using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
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
/// End-to-end verification for rivoli-ai/andy-cli#227: a file write/edit must render in the feed
/// as a git-style diff (removed lines red, added lines green). These tests run the REAL tools
/// (write_file, replace_text) through the real permission-gated executor and the real
/// <see cref="UiUpdatingToolExecutor"/> wiring - before-snapshot capture, UnifiedDiff, and
/// <see cref="FeedView.AddFileDiff"/> - then render the resulting <see cref="FileDiffItem"/> and
/// assert on the actual colors, not a duplicated heuristic.
/// </summary>
[Collection("bash-tool-env")]
public sealed class FileDiffRenderingIntegrationTests
{
    /// <summary>Builds the CLI's real permission-gated executor wiring with an auto-allowing broker.</summary>
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
            var allow = new Andy.Permissions.Model.PermissionDecision(
                true, Andy.Permissions.Model.PersistScope.Session);
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

    /// <summary>
    /// Mirrors what SimpleAssistantService.ToolCalled does before the executor runs: installs the
    /// feed on the process-wide tracker and enqueues the pending UI tool so the executor claims it.
    /// </summary>
    private static (FeedView Feed, string UiToolId) PrepareFeed(string toolName)
    {
        var feed = new FeedView();
        ToolExecutionTracker.Instance.SetFeedView(feed);
        var uiToolId = $"{toolName}_{Guid.NewGuid():N}";
        feed.AddToolExecutionStart(uiToolId, toolName);
        ToolExecutionTracker.Instance.EnqueuePendingTool(toolName, uiToolId);
        return (feed, uiToolId);
    }

    private static FileDiffItem SingleDiffItem(FeedView feed)
        => Assert.Single(feed.GetItemsForTesting().OfType<FileDiffItem>().ToList());

    /// <summary>Renders the item full-height and returns its non-empty text runs.</summary>
    private static List<DL.TextRun> Render(FileDiffItem item)
    {
        var b = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, 120, 0, item.MeasureLineCount(120), new DL.DisplayListBuilder().Build(), b);
        return b.Build().Ops.OfType<DL.TextRun>().Where(r => !string.IsNullOrEmpty(r.Content)).ToList();
    }

    [Fact]
    public async Task WriteFile_UpdatingExistingFile_RendersRedGreenDiff()
    {
        var dir = Directory.CreateTempSubdirectory("diff-write-upd-").FullName;
        var file = Path.Combine(dir, "sample.txt");
        try
        {
            await File.WriteAllTextAsync(file, "alpha\nbravo\ncharlie\n");

            var broker = new PermissionRequestBroker();
            using var driver = new AllowAllDriver(broker);
            using var sp = BuildProvider(broker);
            // Root the session working directory at the temp dir: the executor stamps it into the
            // tool context (the #235 fix), and the tools require targets under that directory.
            var exec = new UiUpdatingToolExecutor(
                sp.GetRequiredService<IToolExecutor>(),
                workingDirectoryTracker: new WorkingDirectoryTracker(dir));

            var (feed, _) = PrepareFeed("write_file");
            var result = await exec.ExecuteAsync("write_file", new Dictionary<string, object?>
            {
                ["file_path"] = file,
                ["content"] = "alpha\nBRAVO\ncharlie\ndelta\n",
            }, new ToolExecutionContext());

            Assert.True(result.IsSuccessful, result.ErrorMessage);

            var item = SingleDiffItem(feed);
            var runs = Render(item);
            var theme = Andy.Cli.Themes.Theme.Current;

            // Git-style header naming the operation and file.
            Assert.Contains(runs, r => r.Content.Contains("Update(") && r.Content.Contains("sample.txt"));
            // Removed line in the error (red) color, added lines in the success (green) color.
            Assert.Contains(runs, r => r.Content.Contains("- bravo") && r.Fg.Equals(theme.Error));
            Assert.Contains(runs, r => r.Content.Contains("+ BRAVO") && r.Fg.Equals(theme.Success));
            Assert.Contains(runs, r => r.Content.Contains("+ delta") && r.Fg.Equals(theme.Success));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WriteFile_CreatingNewFile_RendersCreateDiffWithGreenAdds()
    {
        var dir = Directory.CreateTempSubdirectory("diff-write-new-").FullName;
        var file = Path.Combine(dir, "fresh.txt");
        try
        {
            var broker = new PermissionRequestBroker();
            using var driver = new AllowAllDriver(broker);
            using var sp = BuildProvider(broker);
            var exec = new UiUpdatingToolExecutor(
                sp.GetRequiredService<IToolExecutor>(),
                workingDirectoryTracker: new WorkingDirectoryTracker(dir));

            var (feed, _) = PrepareFeed("write_file");
            var result = await exec.ExecuteAsync("write_file", new Dictionary<string, object?>
            {
                ["file_path"] = file,
                ["content"] = "first\nsecond\n",
            }, new ToolExecutionContext());

            Assert.True(result.IsSuccessful, result.ErrorMessage);

            var item = SingleDiffItem(feed);
            var runs = Render(item);
            var theme = Andy.Cli.Themes.Theme.Current;

            Assert.Contains(runs, r => r.Content.Contains("Create(") && r.Content.Contains("fresh.txt"));
            Assert.Contains(runs, r => r.Content.Contains("+ first") && r.Fg.Equals(theme.Success));
            Assert.Contains(runs, r => r.Content.Contains("+ second") && r.Fg.Equals(theme.Success));
            // A brand-new file has no removed lines.
            Assert.DoesNotContain(runs, r => r.Content.StartsWith("- "));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReplaceText_OnSingleFile_RendersRedGreenDiff()
    {
        // Regression for the #227 gap: replace_text is the CLI's edit-style tool (the model's
        // old_string/new_string edits are aliased onto it), but it was excluded from diff
        // rendering entirely - so edits showed no diff at all.
        var dir = Directory.CreateTempSubdirectory("diff-replace-").FullName;
        var file = Path.Combine(dir, "edit-me.txt");
        try
        {
            await File.WriteAllTextAsync(file, "one\ntwo\nthree\n");

            var broker = new PermissionRequestBroker();
            using var driver = new AllowAllDriver(broker);
            using var sp = BuildProvider(broker);
            var exec = new UiUpdatingToolExecutor(
                sp.GetRequiredService<IToolExecutor>(),
                toolRegistry: sp.GetRequiredService<IToolRegistry>(),
                workingDirectoryTracker: new WorkingDirectoryTracker(dir));

            var (feed, _) = PrepareFeed("replace_text");
            var result = await exec.ExecuteAsync("replace_text", new Dictionary<string, object?>
            {
                ["target_path"] = file,
                ["search_pattern"] = "two",
                ["replacement_text"] = "TWO-EDITED",
            }, new ToolExecutionContext());

            Assert.True(result.IsSuccessful, result.ErrorMessage);
            Assert.Contains("TWO-EDITED", await File.ReadAllTextAsync(file));

            var item = SingleDiffItem(feed);
            var runs = Render(item);
            var theme = Andy.Cli.Themes.Theme.Current;

            Assert.Contains(runs, r => r.Content.Contains("Update(") && r.Content.Contains("edit-me.txt"));
            Assert.Contains(runs, r => r.Content.Contains("- two") && r.Fg.Equals(theme.Error));
            Assert.Contains(runs, r => r.Content.Contains("+ TWO-EDITED") && r.Fg.Equals(theme.Success));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReplaceText_CalledWithModelAliasParameterNames_StillRendersDiff()
    {
        // Models routinely call the edit tool with old_string/new_string/file_path; the
        // ParameterMapper aliases those onto search_pattern/replacement_text/target_path BEFORE
        // the diff capture runs, so the diff must render for this spelling too.
        var dir = Directory.CreateTempSubdirectory("diff-replace-alias-").FullName;
        var file = Path.Combine(dir, "aliased.txt");
        try
        {
            await File.WriteAllTextAsync(file, "red\ngreen\nblue\n");

            var broker = new PermissionRequestBroker();
            using var driver = new AllowAllDriver(broker);
            using var sp = BuildProvider(broker);
            var exec = new UiUpdatingToolExecutor(
                sp.GetRequiredService<IToolExecutor>(),
                toolRegistry: sp.GetRequiredService<IToolRegistry>(),
                workingDirectoryTracker: new WorkingDirectoryTracker(dir));

            var (feed, _) = PrepareFeed("replace_text");
            var result = await exec.ExecuteAsync("replace_text", new Dictionary<string, object?>
            {
                ["file_path"] = file,
                ["old_string"] = "green",
                ["new_string"] = "GREEN",
            }, new ToolExecutionContext());

            Assert.True(result.IsSuccessful, result.ErrorMessage);

            var item = SingleDiffItem(feed);
            var runs = Render(item);
            var theme = Andy.Cli.Themes.Theme.Current;

            Assert.Contains(runs, r => r.Content.Contains("- green") && r.Fg.Equals(theme.Error));
            Assert.Contains(runs, r => r.Content.Contains("+ GREEN") && r.Fg.Equals(theme.Success));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
