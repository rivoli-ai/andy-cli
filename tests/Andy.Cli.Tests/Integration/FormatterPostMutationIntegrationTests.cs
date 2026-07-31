using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Cli.Services.Formatting;
using Andy.Cli.Tests.Services.Formatting;
using Andy.Cli.Widgets;
using Andy.Permissions.Model;
using Andy.Permissions.Store;
using Andy.Tools.Core;
using Andy.Tools.Core.OutputLimiting;
using Andy.Tools.Discovery;
using Andy.Tools.Execution;
using Andy.Tools.Framework;
using Andy.Tools.Registry;
using Andy.Tools.Validation;
using Microsoft.Extensions.DependencyInjection;
using DL = Andy.Tui.DisplayList;
using Xunit;

namespace Andy.Cli.Tests.Integration;

/// <summary>
/// End-to-end verification of issue #283 through the REAL executor: a successful file mutation runs
/// the configured formatters, and the diff the user sees is computed from the file's final on-disk
/// bytes. Permission denial is exercised through the real Andy.Permissions gate rather than a stub,
/// because "Plan mode and normal permissions can deny formatter execution before the process
/// starts" is only meaningful if the production gate is the thing being consulted.
/// </summary>
[Collection(Andy.Cli.Tests.Services.ToolExecutionTrackerCollection.Name)]
public sealed class FormatterPostMutationIntegrationTests
{
    private static ServiceProvider BuildProvider(PermissionRequestBroker broker)
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

        ToolCatalog.RegisterAllTools(services);

        services.AddAndyCliPermissions(broker, o =>
        {
            o.UserFilePath = null;
            o.ProjectFilePath = null;
            o.LocalFilePath = null;
            o.ManagedFilePath = null;
        });

        var sp = services.BuildServiceProvider();

        var registry = sp.GetRequiredService<IToolRegistry>();
        foreach (var reg in sp.GetServices<ToolRegistrationInfo>())
        {
            registry.RegisterTool(reg.ToolType, reg.Configuration);
        }

        return sp;
    }

    /// <summary>Answers every permission prompt with session-Allow until disposed.</summary>
    private sealed class AllowAllDriver : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public AllowAllDriver(PermissionRequestBroker broker)
        {
            var allow = new PermissionDecision(true, PersistScope.Session);
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    if (broker.TryDequeue(out var pending) && pending != null)
                    {
                        pending.Completion.TrySetResult(allow);
                    }
                    else
                    {
                        await Task.Delay(5);
                    }
                }
            });
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _loop.Wait(2000); } catch { /* best-effort shutdown */ }
            _cts.Dispose();
        }
    }

    private static (FeedView Feed, string UiToolId) PrepareFeed(string toolName)
    {
        var feed = new FeedView();
        ToolExecutionTracker.Instance.SetFeedView(feed);
        var uiToolId = $"{toolName}_{Guid.NewGuid():N}";
        feed.AddToolExecutionStart(uiToolId, toolName);
        ToolExecutionTracker.Instance.EnqueuePendingTool(toolName, uiToolId);
        return (feed, uiToolId);
    }

    private static List<DL.TextRun> Render(IFeedItem item)
    {
        var b = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, 120, 0, item.MeasureLineCount(120), new DL.DisplayListBuilder().Build(), b);
        return b.Build().Ops.OfType<DL.TextRun>().Where(r => !string.IsNullOrEmpty(r.Content)).ToList();
    }

    /// <summary>
    /// The rendered rows as text. Diff content is syntax-highlighted, so a single source line
    /// arrives as several runs; reassembling per row is what makes an assertion about the line the
    /// user actually reads possible.
    /// </summary>
    private static List<string> Rows(List<DL.TextRun> runs)
        => runs
            .GroupBy(r => r.Y)
            .OrderBy(g => g.Key)
            .Select(g => string.Concat(g.OrderBy(r => r.X).Select(r => r.Content)))
            .ToList();

    private static PostMutationPipeline PipelineFor(
        IFormatterProcessRunner processRunner,
        IFormatterPermissionGate gate,
        params FormatterDefinition[] definitions)
    {
        var catalog = new FormatterCatalog(definitions, command => "/usr/bin/" + command);
        return new PostMutationPipeline(new FormatterRunner(catalog, processRunner, gate));
    }

    private static FormatterDefinition CsFormatter(string command = "csfmt")
        => new() { Name = "cs", Command = command, Extensions = new[] { ".cs" }, Order = 10 };

    [Fact]
    public async Task WriteFile_ShowsTheFormattedResult_NotWhatTheToolWrote()
    {
        var dir = Directory.CreateTempSubdirectory("fmt-write-").FullName;
        var file = Path.Combine(dir, "Sample.cs");
        try
        {
            await File.WriteAllTextAsync(file, "class A{}\n");

            var broker = new PermissionRequestBroker();
            using var driver = new AllowAllDriver(broker);
            using var sp = BuildProvider(broker);

            // The formatter rewrites what write_file produced.
            var process = new FakeFormatterProcessRunner().OnCommand("csfmt", _ =>
            {
                File.WriteAllText(file, "class B\n{\n}\n");
                return FakeFormatterProcessRunner.Success();
            });

            var exec = new UiUpdatingToolExecutor(
                sp.GetRequiredService<IToolExecutor>(),
                workingDirectoryTracker: new WorkingDirectoryTracker(dir),
                postMutationPipeline: PipelineFor(process, UngatedFormatterPermission.Instance, CsFormatter()));

            var (feed, _) = PrepareFeed("write_file");
            var result = await exec.ExecuteAsync("write_file", new Dictionary<string, object?>
            {
                ["file_path"] = file,
                ["content"] = "class B{}\n",
            }, new ToolExecutionContext());

            Assert.True(result.IsSuccessful, result.ErrorMessage);

            // The formatter ran on exactly the file that was written.
            var invocation = Assert.Single(process.Invocations);
            Assert.Contains(file, invocation.Arguments);

            // The file on disk holds the FORMATTED text, and that is what the feed shows.
            Assert.Equal("class B\n{\n}\n", await File.ReadAllTextAsync(file));

            var item = Assert.Single(feed.GetItemsForTesting().OfType<Andy.Cli.Widgets.Tools.ToolCallItem>().ToList());
            var rows = Rows(Render(item));
            Assert.Contains(rows, row => row.Contains("class B"));
            // The unformatted intermediate ("class B{}") must never reach the user.
            Assert.DoesNotContain(rows, row => row.Contains("class B{}"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AFailedFormatter_ReturnsTheExitCodeAndStderrToTheAgent()
    {
        var dir = Directory.CreateTempSubdirectory("fmt-fail-").FullName;
        var file = Path.Combine(dir, "Sample.cs");
        try
        {
            var broker = new PermissionRequestBroker();
            using var driver = new AllowAllDriver(broker);
            using var sp = BuildProvider(broker);

            var process = new FakeFormatterProcessRunner().OnCommand("csfmt",
                _ => FakeFormatterProcessRunner.Failure(7, "Sample.cs(1,7): unexpected '{'"));

            var exec = new UiUpdatingToolExecutor(
                sp.GetRequiredService<IToolExecutor>(),
                workingDirectoryTracker: new WorkingDirectoryTracker(dir),
                postMutationPipeline: PipelineFor(process, UngatedFormatterPermission.Instance, CsFormatter()));

            var (_, _) = PrepareFeed("write_file");
            var result = await exec.ExecuteAsync("write_file", new Dictionary<string, object?>
            {
                ["file_path"] = file,
                ["content"] = "class B{}\n",
            }, new ToolExecutionContext());

            // The write itself succeeded; the formatter did not, and the agent is told so.
            Assert.True(result.IsSuccessful, result.ErrorMessage);
            Assert.NotNull(result.Metadata);
            var report = Assert.IsType<string>(result.Metadata![FormatterResultReporter.ReportKey]);
            Assert.Contains("exited with code 7", report);
            Assert.Contains("unexpected", report);
            Assert.Contains("NOT formatter-clean", report);
            Assert.Contains("exited with code 7", result.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TheRealPermissionGate_DeniesAFormatterBeforeItsProcessStarts()
    {
        var dir = Directory.CreateTempSubdirectory("fmt-deny-").FullName;
        var file = Path.Combine(dir, "Sample.cs");
        try
        {
            var broker = new PermissionRequestBroker();
            using var driver = new AllowAllDriver(broker);
            using var sp = BuildProvider(broker);

            // Deny command execution through the ordinary rule mechanism. This is the same lever a
            // future Plan-mode overlay (#278) pulls, so the formatter is denied without any
            // formatter-specific policy.
            var store = sp.GetRequiredService<IPermissionStore>();
            store.SetInjectedRules(new[]
            {
                PermissionRule.Parse("execute_command(*)", PermissionOutcome.Deny, PermissionLayer.Injected),
            });

            var gate = sp.GetService<IToolPermissionGate>();
            Assert.NotNull(gate);

            var process = new FakeFormatterProcessRunner();
            var exec = new UiUpdatingToolExecutor(
                sp.GetRequiredService<IToolExecutor>(),
                workingDirectoryTracker: new WorkingDirectoryTracker(dir),
                postMutationPipeline: PipelineFor(
                    process,
                    new ToolGateFormatterPermission(gate!, sp.GetService<IToolRegistry>()),
                    CsFormatter()));

            var (_, _) = PrepareFeed("write_file");
            var result = await exec.ExecuteAsync("write_file", new Dictionary<string, object?>
            {
                ["file_path"] = file,
                ["content"] = "class B{}\n",
            }, new ToolExecutionContext());

            Assert.True(result.IsSuccessful, result.ErrorMessage);

            // Nothing was launched, and the agent was told the file is not formatter-clean.
            Assert.Empty(process.Invocations);
            Assert.NotNull(result.Metadata);
            var report = Assert.IsType<string>(result.Metadata![FormatterResultReporter.ReportKey]);
            Assert.Contains("permission denied before the process started", report);

            // The write itself is untouched by the denial.
            Assert.Equal("class B{}\n", await File.ReadAllTextAsync(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TheRealPermissionGate_AllowsAFormatterWhenTheRulesAllowCommands()
    {
        var dir = Directory.CreateTempSubdirectory("fmt-allow-").FullName;
        var file = Path.Combine(dir, "Sample.cs");
        try
        {
            var broker = new PermissionRequestBroker();
            using var driver = new AllowAllDriver(broker);
            using var sp = BuildProvider(broker);

            var store = sp.GetRequiredService<IPermissionStore>();
            store.SetInjectedRules(new[]
            {
                PermissionRule.Parse("execute_command(*)", PermissionOutcome.Allow, PermissionLayer.Injected),
            });

            var gate = sp.GetRequiredService<IToolPermissionGate>();
            var process = new FakeFormatterProcessRunner().OnCommand("csfmt", _ =>
            {
                File.WriteAllText(file, "formatted\n");
                return FakeFormatterProcessRunner.Success();
            });

            var exec = new UiUpdatingToolExecutor(
                sp.GetRequiredService<IToolExecutor>(),
                workingDirectoryTracker: new WorkingDirectoryTracker(dir),
                postMutationPipeline: PipelineFor(
                    process,
                    new ToolGateFormatterPermission(gate, sp.GetService<IToolRegistry>()),
                    CsFormatter()));

            var (_, _) = PrepareFeed("write_file");
            var result = await exec.ExecuteAsync("write_file", new Dictionary<string, object?>
            {
                ["file_path"] = file,
                ["content"] = "unformatted\n",
            }, new ToolExecutionContext());

            Assert.True(result.IsSuccessful, result.ErrorMessage);
            Assert.Single(process.Invocations);
            Assert.Equal("formatted\n", await File.ReadAllTextAsync(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AFileWithNoConfiguredFormatter_IsNeverHandedToAProcess()
    {
        var dir = Directory.CreateTempSubdirectory("fmt-none-").FullName;
        var file = Path.Combine(dir, "notes.txt");
        try
        {
            var broker = new PermissionRequestBroker();
            using var driver = new AllowAllDriver(broker);
            using var sp = BuildProvider(broker);

            var process = new FakeFormatterProcessRunner();
            var exec = new UiUpdatingToolExecutor(
                sp.GetRequiredService<IToolExecutor>(),
                workingDirectoryTracker: new WorkingDirectoryTracker(dir),
                postMutationPipeline: PipelineFor(process, UngatedFormatterPermission.Instance, CsFormatter()));

            var (_, _) = PrepareFeed("write_file");
            var result = await exec.ExecuteAsync("write_file", new Dictionary<string, object?>
            {
                ["file_path"] = file,
                ["content"] = "plain text\n",
            }, new ToolExecutionContext());

            Assert.True(result.IsSuccessful, result.ErrorMessage);
            Assert.Empty(process.Invocations);
            Assert.Null(result.Metadata is null ? null : result.Metadata.GetValueOrDefault(FormatterResultReporter.ReportKey));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReplaceText_UsesTheSamePostMutationPipeline()
    {
        var dir = Directory.CreateTempSubdirectory("fmt-replace-").FullName;
        var file = Path.Combine(dir, "Sample.cs");
        try
        {
            await File.WriteAllTextAsync(file, "var a = OLD;\n");

            var broker = new PermissionRequestBroker();
            using var driver = new AllowAllDriver(broker);
            using var sp = BuildProvider(broker);

            var process = new FakeFormatterProcessRunner().OnCommand("csfmt", _ =>
            {
                File.WriteAllText(file, "var a = NEW; // formatted\n");
                return FakeFormatterProcessRunner.Success();
            });

            var exec = new UiUpdatingToolExecutor(
                sp.GetRequiredService<IToolExecutor>(),
                workingDirectoryTracker: new WorkingDirectoryTracker(dir),
                postMutationPipeline: PipelineFor(process, UngatedFormatterPermission.Instance, CsFormatter()));

            var (feed, _) = PrepareFeed("replace_text");
            var result = await exec.ExecuteAsync("replace_text", new Dictionary<string, object?>
            {
                ["target_path"] = file,
                ["search_pattern"] = "OLD",
                ["replacement_text"] = "NEW",
            }, new ToolExecutionContext());

            Assert.True(result.IsSuccessful, result.ErrorMessage);
            Assert.Single(process.Invocations);

            var rows = Rows(Render(Assert.Single(
                feed.GetItemsForTesting().OfType<Andy.Cli.Widgets.Tools.ToolCallItem>().ToList())));
            Assert.Contains(rows, row => row.Contains("// formatted"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
