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
/// as a git-style diff. These tests run the REAL tools (write_file, replace_text) through the
/// real permission-gated executor and the real <see cref="UiUpdatingToolExecutor"/> wiring -
/// before-snapshot capture, UnifiedDiff, and the completion that carries the change - then render
/// the result and assert on the actual colors, not a duplicated heuristic.
///
/// Since #253/#254 the change is rendered INSIDE the tool call rather than as a separate item
/// below it, so a diff stays attached to the call that made it. Diff content is now
/// syntax-highlighted, so "removed is red" is carried by the sign column and the row tint rather
/// than by the color of the text itself - which is what these tests assert.
/// </summary>
[Collection(Andy.Cli.Tests.Services.ToolExecutionTrackerCollection.Name)]
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

    private static Andy.Cli.Widgets.Tools.ToolCallItem SingleToolCall(FeedView feed)
        => Assert.Single(feed.GetItemsForTesting().OfType<Andy.Cli.Widgets.Tools.ToolCallItem>().ToList());

    /// <summary>Renders the item full-height and returns its non-empty text runs.</summary>
    private static List<DL.TextRun> Render(IFeedItem item)
    {
        var b = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, 120, 0, item.MeasureLineCount(120), new DL.DisplayListBuilder().Build(), b);
        return b.Build().Ops.OfType<DL.TextRun>().Where(r => !string.IsNullOrEmpty(r.Content)).ToList();
    }

    /// <summary>True when some run on the same row carries the given background tint.</summary>
    private static bool HasRowWith(List<DL.TextRun> runs, string content, DL.Rgb24 background)
    {
        var rows = runs.Where(r => r.Content.Contains(content)).Select(r => r.Y).Distinct();
        return rows.Any(y => runs.Any(r => r.Y == y && r.Bg.HasValue && r.Bg.Value.Equals(background)));
    }

    /// <summary>True when a sign column of the given color sits on the same row as the content.</summary>
    private static bool HasSign(List<DL.TextRun> runs, string content, string sign, DL.Rgb24 color)
    {
        var rows = runs.Where(r => r.Content.Contains(content)).Select(r => r.Y).Distinct();
        return rows.Any(y => runs.Any(r => r.Y == y && r.Content.TrimEnd() == sign && r.Fg.Equals(color)));
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

            var item = SingleToolCall(feed);
            var runs = Render(item);
            var theme = Andy.Cli.Themes.Theme.Current;

            // The header names the operation and the file, and reports the change counts.
            Assert.Contains(runs, r => r.Content.Contains("Wrote") || r.Content.Contains("sample.txt"));
            Assert.Contains(runs, r => r.Content.Contains("+2") && r.Content.Contains("-1"));
            // Removed row: red sign, removed tint. Added rows: green sign, added tint.
            Assert.True(HasSign(runs, "bravo", "-", theme.Error), "removed line needs a red sign");
            Assert.True(HasRowWith(runs, "bravo", theme.DiffRemovedBackground), "removed line needs the removed tint");
            Assert.True(HasSign(runs, "BRAVO", "+", theme.Success), "added line needs a green sign");
            Assert.True(HasRowWith(runs, "BRAVO", theme.DiffAddedBackground), "added line needs the added tint");
            Assert.True(HasRowWith(runs, "delta", theme.DiffAddedBackground), "added line needs the added tint");
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

            var item = SingleToolCall(feed);
            var runs = Render(item);
            var theme = Andy.Cli.Themes.Theme.Current;

            // A creation reads as a creation, and shows the new file as numbered content rather
            // than as a diff in which every line happens to be an addition.
            Assert.Contains(runs, r => r.Content.Contains("Created"));
            Assert.Contains(runs, r => r.Content.Contains("fresh.txt"));
            Assert.Contains(runs, r => r.Content.Contains("first"));
            Assert.Contains(runs, r => r.Content.Contains("second"));
            Assert.Contains(runs, r => r.Content.Trim() == "1");   // line-number gutter
            Assert.Contains(runs, r => r.Content.Trim() == "2");
            // A brand-new file has no removed lines.
            Assert.DoesNotContain(runs, r => r.Bg.HasValue && r.Bg.Value.Equals(theme.DiffRemovedBackground));
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

            var item = SingleToolCall(feed);
            var runs = Render(item);
            var theme = Andy.Cli.Themes.Theme.Current;

            Assert.Contains(runs, r => r.Content.Contains("Edited"));
            Assert.Contains(runs, r => r.Content.Contains("edit-me.txt"));
            Assert.True(HasRowWith(runs, "two", theme.DiffRemovedBackground), "removed line needs the removed tint");
            Assert.True(HasRowWith(runs, "TWO-EDITED", theme.DiffAddedBackground), "added line needs the added tint");
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

            var item = SingleToolCall(feed);
            var runs = Render(item);
            var theme = Andy.Cli.Themes.Theme.Current;

            Assert.True(HasRowWith(runs, "green", theme.DiffRemovedBackground), "removed line needs the removed tint");
            Assert.True(HasRowWith(runs, "GREEN", theme.DiffAddedBackground), "added line needs the added tint");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
