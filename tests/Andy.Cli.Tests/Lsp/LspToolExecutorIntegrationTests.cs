using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Lsp;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Tools.Core;
using Xunit;
using DL = Andy.Tui.DisplayList;

// The stub executors below implement IToolExecutor's events but never raise them.
#pragma warning disable CS0067

namespace Andy.Cli.Tests.Lsp;

/// <summary>
/// The integration point itself: after a successful file mutation, the executor asks the language
/// server about the file and folds the answer into both the model-visible result and the feed.
///
/// The executor is driven with a stub inner executor that performs the write, which is exactly how
/// the real file tools behave from the executor's point of view (they overwrite the file and
/// report neither the old nor the new content).
/// </summary>
[Collection(Andy.Cli.Tests.Services.ToolExecutionTrackerCollection.Name)]
public sealed class LspToolExecutorIntegrationTests : IDisposable
{
    private readonly FeedView _feed = new();

    public LspToolExecutorIntegrationTests()
    {
        ToolExecutionTracker.Instance.Reset();
        ToolExecutionTracker.Instance.SetFeedView(_feed);
    }

    public void Dispose() => ToolExecutionTracker.Instance.Reset();

    /// <summary>An inner executor that writes the requested content, like the real write_file tool.</summary>
    private sealed class WritingExecutor : IToolExecutor
    {
        public event EventHandler<ToolExecutionStartedEventArgs>? ExecutionStarted;
        public event EventHandler<ToolExecutionCompletedEventArgs>? ExecutionCompleted;
        public event EventHandler<SecurityViolationEventArgs>? SecurityViolation;

        public Task<ToolExecutionResult> ExecuteAsync(
            string toolId,
            Dictionary<string, object?> parameters,
            ToolExecutionContext? context = null)
        {
            var path = parameters["file_path"]!.ToString()!;
            File.WriteAllText(path, parameters["content"]!.ToString()!);

            return Task.FromResult(new ToolExecutionResult
            {
                IsSuccessful = true,
                Message = "written",
                Data = new Dictionary<string, object?> { ["file_path"] = path, ["written"] = true },
            });
        }

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request) =>
            ExecuteAsync(request.ToolId, request.Parameters, request.Context);

        public Task<IList<string>> ValidateExecutionRequestAsync(ToolExecutionRequest request) =>
            Task.FromResult<IList<string>>(new List<string>());

        public Task<ToolResourceUsage?> EstimateResourceUsageAsync(string toolId, Dictionary<string, object?> parameters) =>
            Task.FromResult<ToolResourceUsage?>(null);

        public Task<int> CancelExecutionsAsync(string? toolId = null) => Task.FromResult(0);
        public IReadOnlyList<RunningExecutionInfo> GetRunningExecutions() => Array.Empty<RunningExecutionInfo>();
        public ToolExecutionStatistics GetStatistics() => new();
    }

    /// <summary>Renders every feed item and returns the text it actually draws.</summary>
    private string RenderFeed()
    {
        var text = new System.Text.StringBuilder();
        foreach (var item in _feed.GetItemsForTesting())
        {
            var builder = new DL.DisplayListBuilder();
            item.RenderSlice(0, 0, 160, 0, item.MeasureLineCount(160), new DL.DisplayListBuilder().Build(), builder);
            // The markdown renderer emits one run per glyph, so runs are concatenated rather than
            // joined: what matters here is the text the item draws, not how it was chunked.
            foreach (var run in builder.Build().Ops.OfType<DL.TextRun>())
            {
                text.Append(run.Content);
            }
            text.Append('\n');
        }
        return text.ToString();
    }

    private static Dictionary<string, object?> WriteParameters(string path, string content) => new()
    {
        ["file_path"] = path,
        ["content"] = content,
    };

    [Fact]
    public async Task DiagnosticsReachTheModelVisibleToolResult()
    {
        // Acceptance: "Diagnostic results are structured, bounded, and visible in both tool output
        // and the agent context." The model reads ToolExecutionResult.Data.
        using var workspace = new LspTestWorkspace();
        await using var manager = workspace.Manager(LspTestWorkspace.Definition());
        var executor = new UiUpdatingToolExecutor(
            new WritingExecutor(),
            workingDirectoryTracker: new WorkingDirectoryTracker(workspace.Root),
            diagnosticsReporter: new LspDiagnosticsService(manager));

        var path = Path.Combine(workspace.Root, "a.fake");
        var result = await executor.ExecuteAsync(
            "write_file",
            WriteParameters(path, "an ERROR here\na WARN there\n"),
            new ToolExecutionContext());

        var data = Assert.IsType<Dictionary<string, object?>>(result.Data);

        // The tool's own payload survives untouched.
        Assert.Equal(path, (string?)data["file_path"]);

        var payload = Assert.IsType<Dictionary<string, object?>>(data[LspResultAttachment.PayloadKey]);
        Assert.Equal("received", (string?)payload["status"]);
        Assert.Equal(1, (int)payload["error_count"]!);
        Assert.Equal(1, (int)payload["warning_count"]!);

        var diagnostics = Assert.IsType<List<Dictionary<string, object?>>>(payload["diagnostics"]);
        Assert.Contains(diagnostics, d => (string?)d["message"] == "unexpected token ERROR");
    }

    [Fact]
    public async Task DiagnosticsAppearInTheFeed()
    {
        using var workspace = new LspTestWorkspace();
        await using var manager = workspace.Manager(LspTestWorkspace.Definition());
        var executor = new UiUpdatingToolExecutor(
            new WritingExecutor(),
            workingDirectoryTracker: new WorkingDirectoryTracker(workspace.Root),
            diagnosticsReporter: new LspDiagnosticsService(manager));

        var path = Path.Combine(workspace.Root, "a.fake");
        await executor.ExecuteAsync("write_file", WriteParameters(path, "an ERROR here\n"), new ToolExecutionContext());

        var rendered = RenderFeed();

        Assert.Contains("lsp (fake)", rendered);
        Assert.Contains("unexpected token ERROR", rendered);
    }

    [Fact]
    public async Task ACleanFileAddsNothingToTheResultOrTheFeed()
    {
        // Silence is the common case; a clean write must not pay context or screen space for it.
        using var workspace = new LspTestWorkspace();
        await using var manager = workspace.Manager(LspTestWorkspace.Definition());
        var executor = new UiUpdatingToolExecutor(
            new WritingExecutor(),
            workingDirectoryTracker: new WorkingDirectoryTracker(workspace.Root),
            diagnosticsReporter: new LspDiagnosticsService(manager));

        var path = Path.Combine(workspace.Root, "a.fake");
        var result = await executor.ExecuteAsync(
            "write_file", WriteParameters(path, "all good\n"), new ToolExecutionContext());

        var data = Assert.IsType<Dictionary<string, object?>>(result.Data);
        Assert.False(data.ContainsKey(LspResultAttachment.PayloadKey));

        Assert.DoesNotContain("lsp (fake)", RenderFeed());
    }

    [Fact]
    public async Task DiagnosticsDescribeTheContentTheToolLeftOnDisk()
    {
        // The executor reads the file back after the tool ran, which is what makes it safe for a
        // post-mutation formatter (#283) to rewrite the file before this step: whatever ends up on
        // disk is what gets analyzed. Simulated here by an inner executor whose write differs from
        // the content it was handed.
        using var workspace = new LspTestWorkspace();
        await using var manager = workspace.Manager(LspTestWorkspace.Definition());
        var executor = new UiUpdatingToolExecutor(
            new RewritingExecutor("an ERROR here\n"),
            workingDirectoryTracker: new WorkingDirectoryTracker(workspace.Root),
            diagnosticsReporter: new LspDiagnosticsService(manager));

        var path = Path.Combine(workspace.Root, "a.fake");
        var result = await executor.ExecuteAsync(
            "write_file", WriteParameters(path, "the caller thought this was clean\n"), new ToolExecutionContext());

        var data = Assert.IsType<Dictionary<string, object?>>(result.Data);
        var payload = Assert.IsType<Dictionary<string, object?>>(data[LspResultAttachment.PayloadKey]);
        Assert.Equal(1, (int)payload["error_count"]!);
        Assert.Equal("an ERROR here\n", workspace.Transports.Single().Server.ReceivedTexts.Last());
    }

    [Fact]
    public async Task AFailingLanguageServerNeverFailsTheToolCall()
    {
        // Acceptance: nothing about the LSP layer may break the agent loop.
        using var workspace = new LspTestWorkspace();
        var executor = new UiUpdatingToolExecutor(
            new WritingExecutor(),
            workingDirectoryTracker: new WorkingDirectoryTracker(workspace.Root),
            diagnosticsReporter: new ThrowingReporter());

        var path = Path.Combine(workspace.Root, "a.fake");
        var result = await executor.ExecuteAsync(
            "write_file", WriteParameters(path, "an ERROR here\n"), new ToolExecutionContext());

        Assert.True(result.IsSuccessful);
        Assert.Equal("an ERROR here\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task NonMutatingToolsAreNeverSentToALanguageServer()
    {
        using var workspace = new LspTestWorkspace();
        var reporter = new CountingReporter();
        var executor = new UiUpdatingToolExecutor(
            new WritingExecutor(),
            workingDirectoryTracker: new WorkingDirectoryTracker(workspace.Root),
            diagnosticsReporter: reporter);

        var path = Path.Combine(workspace.Root, "a.fake");
        await executor.ExecuteAsync("read_file", WriteParameters(path, "an ERROR here\n"), new ToolExecutionContext());

        Assert.Equal(0, reporter.Calls);
    }

    private sealed class RewritingExecutor : IToolExecutor
    {
        private readonly string _actualContent;
        public RewritingExecutor(string actualContent) => _actualContent = actualContent;

        public event EventHandler<ToolExecutionStartedEventArgs>? ExecutionStarted;
        public event EventHandler<ToolExecutionCompletedEventArgs>? ExecutionCompleted;
        public event EventHandler<SecurityViolationEventArgs>? SecurityViolation;

        public Task<ToolExecutionResult> ExecuteAsync(
            string toolId,
            Dictionary<string, object?> parameters,
            ToolExecutionContext? context = null)
        {
            File.WriteAllText(parameters["file_path"]!.ToString()!, _actualContent);
            return Task.FromResult(new ToolExecutionResult
            {
                IsSuccessful = true,
                Data = new Dictionary<string, object?> { ["written"] = true },
            });
        }

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request) =>
            ExecuteAsync(request.ToolId, request.Parameters, request.Context);

        public Task<IList<string>> ValidateExecutionRequestAsync(ToolExecutionRequest request) =>
            Task.FromResult<IList<string>>(new List<string>());

        public Task<ToolResourceUsage?> EstimateResourceUsageAsync(string toolId, Dictionary<string, object?> parameters) =>
            Task.FromResult<ToolResourceUsage?>(null);

        public Task<int> CancelExecutionsAsync(string? toolId = null) => Task.FromResult(0);
        public IReadOnlyList<RunningExecutionInfo> GetRunningExecutions() => Array.Empty<RunningExecutionInfo>();
        public ToolExecutionStatistics GetStatistics() => new();
    }

    private sealed class ThrowingReporter : IFileMutationDiagnosticsReporter
    {
        public Task<LspDiagnosticsReport?> ReportAsync(string absolutePath, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the language server layer exploded");
    }

    private sealed class CountingReporter : IFileMutationDiagnosticsReporter
    {
        public int Calls;

        public Task<LspDiagnosticsReport?> ReportAsync(string absolutePath, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<LspDiagnosticsReport?>(null);
        }
    }
}

#pragma warning restore CS0067
