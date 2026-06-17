using System;
using System.Linq;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Tools.Core;
using Andy.Tools.Framework;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Cli.Tests.Services;

/// <summary>
/// Verifies the CLI's trim-safe <see cref="ToolCatalog"/> registers the Andy.Tools.Data
/// <c>dataframe_*</c> tools (and their backing DuckDB engine), so the agent — and the <c>/tools</c>
/// command — see them alongside the built-in tools.
/// </summary>
public sealed class ToolCatalogDataFrameTests
{
    private static readonly string[] ExpectedDataFrameIds =
    {
        "dataframe_load_csv", "dataframe_load_json", "dataframe_load_parquet", "dataframe_load_delta",
        "dataframe_schema", "dataframe_profile", "dataframe_preview", "dataframe_value_counts",
        "dataframe_assert", "dataframe_list", "dataframe_select", "dataframe_filter",
        "dataframe_with_column", "dataframe_rename", "dataframe_group_by", "dataframe_window",
        "dataframe_pivot", "dataframe_unpivot", "dataframe_unnest", "dataframe_join", "dataframe_sample",
        "dataframe_sort", "dataframe_distinct", "dataframe_union", "dataframe_fillna", "dataframe_dropna",
        "dataframe_export", "dataframe_drop",
    };

    [Fact]
    public async Task RegisterAllTools_registers_all_28_dataframe_tools_with_their_engine()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        ToolCatalog.RegisterAllTools(services);
        // The dataframe tools are IAsyncDisposable (they own a DuckDB connection), so the provider
        // must be disposed asynchronously.
        await using var provider = services.BuildServiceProvider();

        // The dataframe tools are registered as ToolRegistrationInfo entries, like every catalog tool.
        var dataframeTypes = provider.GetServices<ToolRegistrationInfo>()
            .Select(r => r.ToolType)
            .Where(t => t.Namespace == "Andy.Tools.Data")
            .ToList();

        // Resolving each instance proves the backing DuckDB engine (IDuckDbBackend + IDatasetCatalog)
        // is wired into DI, and exposes the tool ids the registry / LLM will see.
        var ids = dataframeTypes
            .Select(t => ((ITool)provider.GetRequiredService(t)).Metadata.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        var expected = ExpectedDataFrameIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, ids);
        Assert.Equal(28, ids.Length);
        Assert.All(ids, id => Assert.StartsWith("dataframe_", id));
    }
}
