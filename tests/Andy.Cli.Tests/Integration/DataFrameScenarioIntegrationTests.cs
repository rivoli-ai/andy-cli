using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Data;
using Andy.Engine;
using Andy.Model.Llm;
using Andy.Model.Model;
using Andy.Tools.Core;
using Andy.Tools.Core.OutputLimiting;
using Andy.Tools.Discovery;
using Andy.Tools.Execution;
using Andy.Tools.Framework;
using Andy.Tools.Registry;
using Andy.Tools.Validation;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Andy.Cli.Tests.Integration;

/// <summary>
/// End-to-end scenario through the real CLI tool stack: a scripted model drives the dataframe_* tools
/// (registered via the CLI's <see cref="ToolCatalog"/>) through the engine's SimpleAgent over three
/// related CSVs — load, group-by (array-of-objects aggregations), and join. Regression for the
/// tool-argument bug where structured params arrived as raw JSON strings; requires Andy.Engine with
/// the recursive tool-argument deserialization. Asserts on the shared dataset catalog.
/// </summary>
public sealed class DataFrameScenarioIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"andy_cli_df_scn_{Guid.NewGuid():N}");

    public DataFrameScenarioIntegrationTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "sales.csv"),
            "order_id,region,product_id,amount\n" +
            "1,NA,P1,100\n2,EU,P2,200\n3,APAC,P1,50\n4,LATAM,P3,300\n" +
            "5,NA,P2,150\n6,EU,P1,80\n7,APAC,P3,120\n8,LATAM,P2,90\n");
        File.WriteAllText(Path.Combine(_dir, "products.csv"),
            "product_id,product_name,category\nP1,Widget,Hardware\nP2,Gadget,Hardware\nP3,Gizmo,Electronics\n");
        File.WriteAllText(Path.Combine(_dir, "regions.csv"),
            "region,country\nNA,USA\nEU,Germany\nAPAC,Japan\nLATAM,Brazil\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Path_(string f) => Path.Combine(_dir, f).Replace("\\", "\\\\");

    // Mirrors Program.cs: core Andy.Tools services + the CLI ToolCatalog + permission engine, then
    // drains the catalog markers into the registry.
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IToolValidator, ToolValidator>();
        services.AddSingleton<IToolRegistry, Andy.Tools.Registry.ToolRegistry>();
        services.AddSingleton<IToolDiscovery, ToolDiscoveryService>();
        services.AddSingleton<ISecurityManager, SecurityManager>();
        services.AddSingleton<IResourceMonitor, ResourceMonitor>();
        services.AddSingleton<IToolOutputLimiter, ToolOutputLimiter>();
        services.AddSingleton<IToolExecutor, ToolExecutor>();
        services.AddSingleton<IPermissionProfileService, PermissionProfileService>();
        services.AddSingleton(new ToolFrameworkOptions
        {
            RegisterBuiltInTools = false,
            EnableObservability = false,
            AutoDiscoverTools = false,
        });

        ToolCatalog.RegisterAllTools(services); // registers the dataframe_* tools + their DuckDB engine

        // NOTE: the CLI permission engine (AddAndyCliPermissions) is intentionally NOT wired here.
        // This scenario exercises the dataframe tool registration + engine tool-argument marshaling;
        // permission gating (and its interactive prompt) is covered by the dedicated permission tests.
        var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<IToolRegistry>();
        foreach (var reg in sp.GetServices<ToolRegistrationInfo>())
        {
            registry.RegisterTool(reg.ToolType, reg.Configuration);
        }
        return sp;
    }

    private static LlmResponse ToolCall(string name, string argsJson) => new()
    {
        AssistantMessage = new Message
        {
            Role = Role.Assistant,
            Content = "",
            ToolCalls = new List<ToolCall> { new() { Id = "c_" + name, Name = name, ArgumentsJson = argsJson } },
        },
    };

    private static LlmResponse Stop() => new()
    {
        AssistantMessage = new Message { Role = Role.Assistant, Content = "Done." },
        FinishReason = "stop",
    };

    [Fact]
    public async Task Three_dataset_scenario_runs_through_the_cli_tool_stack()
    {
        using var sp = BuildProvider();
        var registry = sp.GetRequiredService<IToolRegistry>();
        var executor = sp.GetRequiredService<IToolExecutor>();
        var catalog = sp.GetRequiredService<IDatasetCatalog>();

        var llm = new Mock<ILlmProvider>();
        llm.SetupSequence(p => p.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolCall("dataframe_load_csv", $"{{\"path\":\"{Path_("sales.csv")}\",\"dataset_id\":\"sales\"}}"))
            .ReturnsAsync(ToolCall("dataframe_load_csv", $"{{\"path\":\"{Path_("products.csv")}\",\"dataset_id\":\"products\"}}"))
            .ReturnsAsync(ToolCall("dataframe_load_csv", $"{{\"path\":\"{Path_("regions.csv")}\",\"dataset_id\":\"regions\"}}"))
            .ReturnsAsync(ToolCall("dataframe_group_by",
                "{\"dataset_id\":\"sales\",\"into\":\"by_region\",\"group_by\":[\"region\"]," +
                "\"aggregations\":[{\"column\":\"amount\",\"function\":\"sum\",\"alias\":\"total\"}," +
                "{\"column\":\"*\",\"function\":\"count\",\"alias\":\"orders\"}]}"))
            .ReturnsAsync(ToolCall("dataframe_join",
                "{\"left\":\"sales\",\"right\":\"products\",\"into\":\"enriched\",\"how\":\"inner\",\"on\":[\"product_id\"]}"))
            .ReturnsAsync(Stop());

        var agent = new SimpleAgent(llm.Object, registry, executor, systemPrompt: "system", maxTurns: 10);
        await agent.ProcessMessageAsync("Load the three CSVs, total/average amount by region, and join sales with products.");

        Assert.Equal(8L, catalog.Get("sales")?.RowCount);
        Assert.Equal(3L, catalog.Get("products")?.RowCount);
        Assert.Equal(4L, catalog.Get("regions")?.RowCount);

        // group_by with array-of-objects aggregations succeeded → one row per region with total+orders.
        var byRegion = catalog.Get("by_region");
        Assert.NotNull(byRegion);
        Assert.Equal(4L, byRegion!.RowCount);
        var cols = byRegion.Schema.Select(c => c.Name).ToList();
        Assert.Contains("total", cols);
        Assert.Contains("orders", cols);

        Assert.Equal(8L, catalog.Get("enriched")?.RowCount);
    }
}
