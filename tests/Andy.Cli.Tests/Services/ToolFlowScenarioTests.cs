using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Cli.Widgets.Tools;
using Andy.Tools.Core;
using Xunit;

namespace Andy.Cli.Tests.Services;

/// <summary>
/// Whole-flow tests over the tool feed, driven the way the engine really drives it: the executor
/// runs first and the ToolCalled event arrives afterwards.
///
/// Three properties are asserted for every tool and every flow, because these are what actually
/// went wrong in use:
///
///   1. WHILE RUNNING, the row names what it is doing, with its arguments.
///   2. THE MOMENT the tool returns, the row is complete - not at end of turn.
///   3. WHEN COMPLETE, the row says something meaningful about the result.
/// </summary>
[Collection(Andy.Cli.Tests.Services.ToolExecutionTrackerCollection.Name)]
public class ToolFlowScenarioTests : IDisposable
{
    /// <summary>A tool that blocks until the test releases it, so "while running" can be observed.</summary>
    private sealed class GatedExecutor : IToolExecutor
    {
        private readonly Func<string, Dictionary<string, object?>, ToolExecutionResult> _resultFor;
        private readonly SemaphoreSlim? _gate;
        public GatedExecutor(Func<string, Dictionary<string, object?>, ToolExecutionResult> resultFor,
            SemaphoreSlim? gate = null)
        {
            _resultFor = resultFor;
            _gate = gate;
        }

        public event EventHandler<ToolExecutionStartedEventArgs>? ExecutionStarted;
        public event EventHandler<ToolExecutionCompletedEventArgs>? ExecutionCompleted;
        public event EventHandler<SecurityViolationEventArgs>? SecurityViolation;

        public async Task<ToolExecutionResult> ExecuteAsync(string toolId, Dictionary<string, object?> parameters,
            ToolExecutionContext? context = null)
        {
            if (_gate != null) await _gate.WaitAsync();
            return _resultFor(toolId, parameters);
        }

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
            => ExecuteAsync(request.ToolId, request.Parameters ?? new(), request.Context);

        public Task<IList<string>> ValidateExecutionRequestAsync(ToolExecutionRequest request)
            => Task.FromResult<IList<string>>(new List<string>());
        public Task<ToolResourceUsage?> EstimateResourceUsageAsync(string toolId, Dictionary<string, object?> parameters)
            => Task.FromResult<ToolResourceUsage?>(null);
        public Task<int> CancelExecutionsAsync(string? toolId = null) => Task.FromResult(0);
        public IReadOnlyList<RunningExecutionInfo> GetRunningExecutions() => Array.Empty<RunningExecutionInfo>();
        public ToolExecutionStatistics GetStatistics() => new();
    }

    private readonly FeedView _feed = new();

    public ToolFlowScenarioTests()
    {
        ToolExecutionTracker.Instance.Reset();
        ToolExecutionTracker.Instance.SetFeedView(_feed);
    }

    public void Dispose() => ToolExecutionTracker.Instance.Reset();

    private UiUpdatingToolExecutor Executor(
        Func<string, Dictionary<string, object?>, ToolExecutionResult> resultFor, SemaphoreSlim? gate = null)
        => new(new GatedExecutor(resultFor, gate),
            workingDirectoryTracker: new WorkingDirectoryTracker(Environment.CurrentDirectory));

    private static ToolExecutionResult Ok(object? data, string? message = null)
        => new() { IsSuccessful = true, Data = data, Message = message };

    private List<ToolCallItem> Rows() => _feed.GetItemsForTesting().OfType<ToolCallItem>().ToList();

    private string RowText(ToolCallItem row) => string.Join("\n", row.DebugRows(100));

    /// <summary>
    /// Mimics what SimpleAssistantService does when the engine's post-execution event arrives:
    /// adopt the row the executor opened rather than appending a second one.
    /// </summary>
    private void RaiseLateToolCalledEvent(string toolName)
    {
        var adopted = ToolExecutionTracker.Instance.DequeueExecutorCreatedRow(toolName);
        if (adopted == null) _feed.AddToolExecutionStart($"{toolName}_late", toolName, new Dictionary<string, object?>());
    }

    // ---- every tool: running shows arguments, completion is immediate and meaningful ----------

    public static IEnumerable<object[]> EveryTool() => new List<object[]>
    {
        new object[] { "execute_command", new Dictionary<string, object?> { ["command"] = "dotnet build" },
            new Dictionary<string, object?> { ["command"] = "dotnet build", ["exit_code"] = 0, ["stdout"] = "Build succeeded", ["duration_ms"] = 1500.0 },
            "dotnet build", "Build succeeded" },

        new object[] { "read_file", new Dictionary<string, object?> { ["file_path"] = "src/Program.cs" },
            new Dictionary<string, object?> { ["content"] = "x", ["line_count"] = 240, ["file_size_formatted"] = "8.4 KB" },
            "src/Program.cs", "240 lines" },

        new object[] { "write_file", new Dictionary<string, object?> { ["file_path"] = "out.txt" },
            new Dictionary<string, object?> { ["bytes_written"] = 12 },
            "out.txt", "out.txt" },

        new object[] { "replace_text", new Dictionary<string, object?> { ["target_path"] = "a.cs", ["search_pattern"] = "x" },
            new Dictionary<string, object?> { ["total_replacements"] = 3 },
            "a.cs", "3 replacements" },

        new object[] { "search_text", new Dictionary<string, object?> { ["search_pattern"] = "TODO", ["search_path"] = "src" },
            new Dictionary<string, object?> { ["items"] = Array.Empty<object>(), ["total_matches"] = 12, ["files_with_matches"] = 4 },
            "TODO", "12 matches" },

        new object[] { "list_directory", new Dictionary<string, object?> { ["path"] = "src" },
            new Dictionary<string, object?> { ["items"] = Array.Empty<object>(), ["file_count"] = 12, ["directory_count"] = 20 },
            "src", "12 files" },

        new object[] { "create_directory", new Dictionary<string, object?> { ["path"] = "build/out" },
            new Dictionary<string, object?> { ["created"] = true },
            "build/out", "build/out" },

        new object[] { "delete_file", new Dictionary<string, object?> { ["file_path"] = "old.txt" },
            new Dictionary<string, object?> { ["deleted"] = true },
            "old.txt", "old.txt" },

        new object[] { "copy_file", new Dictionary<string, object?> { ["source_path"] = "a.txt", ["destination_path"] = "b.txt" },
            new Dictionary<string, object?> { ["copied"] = true },
            "a.txt", "b.txt" },

        new object[] { "move_file", new Dictionary<string, object?> { ["source_path"] = "a.txt", ["destination_path"] = "b.txt" },
            new Dictionary<string, object?> { ["moved"] = true },
            "a.txt", "b.txt" },

        new object[] { "git_diff", new Dictionary<string, object?> { ["path"] = "src" },
            "📄 **src/a.cs** (2 modifications)\n   **+1** additions, **-1** deletions\n```diff\n+   1: new\n-   2: old\n```",
            "src", "+1 -1" },

        new object[] { "todo_management", new Dictionary<string, object?> { ["action"] = "add_batch" },
            new Dictionary<string, object?> { ["todos"] = new object[] { new { text = "step one", status = "pending" } } },
            "plan", "0/1 done" },

        new object[] { "http_request", new Dictionary<string, object?> { ["url"] = "https://api.test/v1" },
            new Dictionary<string, object?> { ["url"] = "https://api.test/v1", ["status_code"] = 200, ["content_length"] = 120 },
            "api.test", "200" },

        new object[] { "json_processor", new Dictionary<string, object?> { ["operation"] = "query" },
            new object[] { 1, 2, 3 },
            "JSON", "3 items" },

        new object[] { "code_index", new Dictionary<string, object?> { ["query_type"] = "structure" },
            new Dictionary<string, object?> { ["query_type"] = "structure", ["data"] = new Dictionary<string, object?> { ["file_count"] = 307 } },
            "structure", "307 files" },

        new object[] { "date_time", new Dictionary<string, object?> { ["operation"] = "now" },
            "2026-07-25 09:00:00",
            "Date/time", "2026-07-25" },

        new object[] { "encoding_tool", new Dictionary<string, object?> { ["operation"] = "base64_encode" },
            "aGVsbG8=",
            "Encode", "aGVsbG8=" },

        new object[] { "system_info", new Dictionary<string, object?>(),
            new Dictionary<string, object?> { ["os_description"] = "Darwin 24.6.0" },
            "System info", "Darwin" },

        new object[] { "process_info", new Dictionary<string, object?>(),
            new Dictionary<string, object?> { ["items"] = new object[] { new { name = "dotnet" } } },
            "Processes", "1 process" },

        new object[] { "format_text", new Dictionary<string, object?> { ["operation"] = "json" },
            "{\n  \"a\": 1\n}",
            "Format text", "Format text" },

        new object[] { "skill", new Dictionary<string, object?> { ["name"] = "code-review" },
            new Dictionary<string, object?> { ["loaded"] = true },
            "code-review", "code-review" },

        new object[] { "dataframe_preview", new Dictionary<string, object?> { ["dataset_id"] = "sales" },
            new Dictionary<string, object?> { ["row_count"] = 1204, ["schema"] = new object[] { new Dictionary<string, object?> { ["name"] = "id", ["type"] = "INT" } }, ["preview_rows"] = Array.Empty<object>() },
            "sales", "1,204 rows" },

        new object[] { "pdf_extract_text", new Dictionary<string, object?> { ["path"] = "10-K.pdf" },
            new Dictionary<string, object?> { ["page_count"] = 312, ["text"] = "..." },
            "10-K.pdf", "312 pages" },

        new object[] { "vendor_unknown_tool", new Dictionary<string, object?> { ["target"] = "thing" },
            new Dictionary<string, object?> { ["ok"] = true },
            "thing", "ok" },
    };

    [Theory]
    [MemberData(nameof(EveryTool))]
    public async Task EveryToolNamesItselfWhileRunningAndReportsAMeaningfulResult(
        string toolName,
        Dictionary<string, object?> parameters,
        object resultData,
        string expectedWhileRunning,
        string expectedWhenComplete)
    {
        var gate = new SemaphoreSlim(0, 1);
        var executor = Executor((_, _) => Ok(resultData), gate);

        var call = executor.ExecuteAsync(toolName, parameters, new ToolExecutionContext());

        // 1. While running, the row exists and says what it is doing - with its arguments.
        await WaitUntil(() => Rows().Count == 1);
        var row = Rows().Single();
        Assert.False(row.Snapshot.IsComplete);
        Assert.Contains(expectedWhileRunning, RowText(row), StringComparison.OrdinalIgnoreCase);

        gate.Release();
        await call;

        // 2. Complete the instant the tool returned - before any end-of-turn pass.
        Assert.True(row.Snapshot.IsComplete, $"{toolName} row must complete when the tool returns");

        // 3. And it says something meaningful about the result.
        Assert.Contains(expectedWhenComplete, RowText(row), StringComparison.OrdinalIgnoreCase);

        // The late event must adopt this row, not append an empty second one.
        RaiseLateToolCalledEvent(toolName);
        Assert.Single(Rows());
    }

    // ---- flows -------------------------------------------------------------------------------

    [Fact]
    public async Task SeveralCallsOfTheSameToolEachGetTheirOwnCompletedRow()
    {
        // The name-keyed fallback used to hand call N the row of call N-1, so rows lagged one
        // behind and the last one never finished.
        var executor = Executor((_, p) => Ok(new Dictionary<string, object?>
        {
            ["command"] = p["command"],
            ["exit_code"] = 0,
            ["stdout"] = $"output of {p["command"]}"
        }));

        foreach (var command in new[] { "git status", "git log", "git diff" })
        {
            await executor.ExecuteAsync("execute_command",
                new Dictionary<string, object?> { ["command"] = command }, new ToolExecutionContext());
            RaiseLateToolCalledEvent("execute_command");
        }

        var rows = Rows();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.True(r.Snapshot.IsComplete, "every row must be complete"));
        Assert.Contains("git status", RowText(rows[0]));
        Assert.Contains("git log", RowText(rows[1]));
        Assert.Contains("git diff", RowText(rows[2]));
        Assert.Contains("output of git diff", RowText(rows[2]));
    }

    [Fact]
    public async Task CallsAcrossSeparateTurnsDoNotReuseAnEarlierRow()
    {
        // The tracker's name-to-id map is never cleared, so a second turn used to resolve the
        // FIRST turn's row, leaving the new call with no row of its own.
        var executor = Executor((_, _) => Ok(new Dictionary<string, object?> { ["exit_code"] = 0, ["stdout"] = "ok" }));

        await executor.ExecuteAsync("execute_command",
            new Dictionary<string, object?> { ["command"] = "first turn" }, new ToolExecutionContext());
        RaiseLateToolCalledEvent("execute_command");

        // ... conversation happens ...
        _feed.AddMarkdown("Some assistant prose between the turns.");

        await executor.ExecuteAsync("execute_command",
            new Dictionary<string, object?> { ["command"] = "second turn" }, new ToolExecutionContext());
        RaiseLateToolCalledEvent("execute_command");

        var rows = Rows();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.Snapshot.IsComplete));
        Assert.Contains("second turn", RowText(rows[1]));
    }

    [Fact]
    public async Task AFastCallCompletesWhileASlowOneIsStillRunning()
    {
        // Different durations interleaved: the quick call must not wait for the slow one.
        var slowGate = new SemaphoreSlim(0, 1);
        var slowExecutor = Executor((_, _) => Ok(new Dictionary<string, object?> { ["exit_code"] = 0, ["stdout"] = "slow done" }), slowGate);
        var fastExecutor = Executor((_, _) => Ok(new Dictionary<string, object?> { ["exit_code"] = 0, ["stdout"] = "fast done" }));

        var slow = slowExecutor.ExecuteAsync("execute_command",
            new Dictionary<string, object?> { ["command"] = "sleep 30" }, new ToolExecutionContext());
        await WaitUntil(() => Rows().Count == 1);

        await fastExecutor.ExecuteAsync("execute_command",
            new Dictionary<string, object?> { ["command"] = "echo hi" }, new ToolExecutionContext());

        var rows = Rows();
        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].Snapshot.IsComplete);   // the slow one is still going
        Assert.True(rows[1].Snapshot.IsComplete);    // the quick one already finished
        Assert.Contains("sleep 30", RowText(rows[0]));

        slowGate.Release();
        await slow;
        Assert.True(rows[0].Snapshot.IsComplete);
    }

    [Fact]
    public async Task ParallelCallsEachCompleteTheirOwnRow()
    {
        var gate = new SemaphoreSlim(0, 3);
        var executor = Executor((_, p) => Ok(new Dictionary<string, object?>
        {
            ["content"] = "x",
            ["line_count"] = 10,
        }), gate);

        var calls = new[] { "a.cs", "b.cs", "c.cs" }
            .Select(f => executor.ExecuteAsync("read_file",
                new Dictionary<string, object?> { ["file_path"] = f }, new ToolExecutionContext()))
            .ToArray();

        await WaitUntil(() => Rows().Count == 3);
        gate.Release(3);
        await Task.WhenAll(calls);

        var rows = Rows();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.True(r.Snapshot.IsComplete));
        Assert.Equal(new[] { "a.cs", "b.cs", "c.cs" },
            rows.Select(r => r.Snapshot.Parameters["file_path"]?.ToString()));
    }

    [Fact]
    public async Task MixedToolsInterleavedWithConversationAllComplete()
    {
        var executor = Executor((tool, p) => tool switch
        {
            "read_file" => Ok(new Dictionary<string, object?> { ["line_count"] = 42 }),
            "search_text" => Ok(new Dictionary<string, object?> { ["total_matches"] = 7 }),
            _ => Ok(new Dictionary<string, object?> { ["exit_code"] = 0, ["stdout"] = "done" })
        });

        _feed.AddMarkdown("Let me look at the code.");
        await executor.ExecuteAsync("read_file",
            new Dictionary<string, object?> { ["file_path"] = "a.cs" }, new ToolExecutionContext());
        RaiseLateToolCalledEvent("read_file");

        _feed.AddMarkdown("Now searching.");
        await executor.ExecuteAsync("search_text",
            new Dictionary<string, object?> { ["search_pattern"] = "TODO" }, new ToolExecutionContext());
        RaiseLateToolCalledEvent("search_text");

        _feed.AddMarkdown("And building.");
        await executor.ExecuteAsync("execute_command",
            new Dictionary<string, object?> { ["command"] = "dotnet build" }, new ToolExecutionContext());
        RaiseLateToolCalledEvent("execute_command");

        var rows = Rows();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.True(r.Snapshot.IsComplete, "no row may still be running once its tool returned"));
        Assert.Contains("a.cs", RowText(rows[0]));
        Assert.Contains("TODO", RowText(rows[1]));
        Assert.Contains("dotnet build", RowText(rows[2]));
    }

    [Fact]
    public async Task AFailingCallCompletesAndExplainsItself()
    {
        var executor = Executor((_, _) => new ToolExecutionResult
        {
            IsSuccessful = false,
            ErrorMessage = "fatal: not a git repository"
        });

        await executor.ExecuteAsync("execute_command",
            new Dictionary<string, object?> { ["command"] = "git log" }, new ToolExecutionContext());

        var row = Rows().Single();
        Assert.True(row.Snapshot.IsComplete);
        Assert.False(row.Snapshot.IsSuccessful);
        Assert.Contains("not a git repository", RowText(row));
    }

    [Fact]
    public async Task TheElapsedClockAdvancesWhileACallIsRunning()
    {
        // The elapsed time used to be baked into the cached row plan, which is only rebuilt when
        // the width, the mode or the snapshot changes - so a call that ran for thirty seconds kept
        // reporting the few milliseconds it had taken on the first frame.
        var gate = new SemaphoreSlim(0, 1);
        var executor = Executor((_, _) => Ok(new Dictionary<string, object?> { ["exit_code"] = 0 }), gate);

        var call = executor.ExecuteAsync("execute_command",
            new Dictionary<string, object?> { ["command"] = "sleep 5" }, new ToolExecutionContext());
        await WaitUntil(() => Rows().Count == 1);
        var row = Rows().Single();

        var first = RenderedText(row);
        await Task.Delay(1200);
        var later = RenderedText(row);

        Assert.NotEqual(first, later);

        gate.Release();
        await call;
    }

    // Renders through the real display list, which is where the frozen clock showed up.
    private static string RenderedText(ToolCallItem row)
    {
        var b = new Andy.Tui.DisplayList.DisplayListBuilder();
        row.RenderSlice(0, 0, 100, 0, row.MeasureLineCount(100),
            new Andy.Tui.DisplayList.DisplayListBuilder().Build(), b);
        return string.Concat(b.Build().Ops.OfType<Andy.Tui.DisplayList.TextRun>().Select(t => t.Content));
    }

    [Fact]
    public async Task TheGutterBesideTheStatusGlyphIsAlwaysPainted()
    {
        // An unpainted column between the spinner and the header left whatever the terminal had
        // there on screen - including a parked cursor.
        var gate = new SemaphoreSlim(0, 1);
        var executor = Executor((_, _) => Ok(new Dictionary<string, object?> { ["exit_code"] = 0 }), gate);

        var call = executor.ExecuteAsync("execute_command",
            new Dictionary<string, object?> { ["command"] = "ls" }, new ToolExecutionContext());
        await WaitUntil(() => Rows().Count == 1);

        var b = new Andy.Tui.DisplayList.DisplayListBuilder();
        var row = Rows().Single();
        row.RenderSlice(0, 0, 100, 0, row.MeasureLineCount(100),
            new Andy.Tui.DisplayList.DisplayListBuilder().Build(), b);

        var header = b.Build().Ops.OfType<Andy.Tui.DisplayList.TextRun>()
            .Where(t => t.Y == 0)
            .OrderBy(t => t.X)
            .ToList();

        // Columns 0 and 1 are both covered by the first run.
        Assert.Equal(0, header[0].X);
        Assert.True(header[0].Content.Length >= 2, "the glyph column must be padded to the gutter width");

        gate.Release();
        await call;
    }

    [Fact]
    public async Task ASlowCallShowsASpinnerWhileItRunsAndAMarkerWhenItIsDone()
    {
        // Directly checks what a user watching the screen sees: an animated spinner glyph in the
        // status column for as long as the tool is working, replaced by the completion marker the
        // moment it returns. A fast tool passes through the running state in milliseconds, which
        // is why quick commands look instant - that is correct, not a missing spinner.
        var gate = new SemaphoreSlim(0, 1);
        var executor = Executor((_, _) => Ok(new Dictionary<string, object?> { ["exit_code"] = 0, ["stdout"] = "done" }), gate);

        var call = executor.ExecuteAsync("execute_command",
            new Dictionary<string, object?> { ["command"] = "sleep 3" }, new ToolExecutionContext());

        await WaitUntil(() => Rows().Count == 1);
        var row = Rows().Single();

        // While running: a braille spinner frame, and the command is already named.
        var running = row.DebugRows(100)[0];
        Assert.Contains(running[0], "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏");
        Assert.Contains("sleep 3", running);

        // The frame advances over time rather than standing still.
        var firstFrame = row.DebugRows(100)[0][0];
        await WaitUntil(() => row.DebugRows(100)[0][0] != firstFrame, timeoutMs: 1500);

        gate.Release();
        await call;

        // Done: the completion marker replaces the spinner, and the result is there.
        var finished = row.DebugRows(100)[0];
        Assert.StartsWith("*", finished);
        Assert.Contains("done", string.Join("\n", row.DebugRows(100)));
    }

    [Fact]
    public async Task ACallBlockedOnConsentSaysSoInsteadOfSpinning()
    {
        // Two tools running in parallel where one is queued behind an approval used to look like
        // two commands running and neither finishing: a waiting call had the same spinner and the
        // same ticking clock as a working one.
        var gate = new SemaphoreSlim(0, 1);
        var executor = Executor((_, _) => Ok(new Dictionary<string, object?> { ["exit_code"] = 0, ["stdout"] = "hi" }), gate);

        var call = executor.ExecuteAsync("execute_command",
            new Dictionary<string, object?> { ["command"] = "rm -rf build" }, new ToolExecutionContext());
        await WaitUntil(() => Rows().Count == 1);
        var row = Rows().Single();

        // Running: a spinner frame and an elapsed clock.
        Assert.Contains(row.DebugRows(100)[0][0], "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏");

        // Blocked on consent: no spinner, and it says what it is waiting for.
        Assert.True(_feed.MarkAwaitingApproval("execute_command", awaiting: true));
        var waiting = RenderedRow(row);
        Assert.StartsWith("?", waiting);
        Assert.Contains("waiting for approval", waiting);
        Assert.DoesNotContain(waiting[0], "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏");

        // Released: back to the running presentation.
        Assert.True(_feed.MarkAwaitingApproval("execute_command", awaiting: false));
        Assert.Contains(row.DebugRows(100)[0][0], "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏");

        gate.Release();
        await call;
        Assert.True(row.Snapshot.IsComplete);
    }

    [Fact]
    public async Task OnlyTheWaitingCallIsMarkedWhenTwoRunInParallel()
    {
        var gate = new SemaphoreSlim(0, 2);
        var executor = Executor((_, _) => Ok(new Dictionary<string, object?> { ["exit_code"] = 0 }), gate);

        var first = executor.ExecuteAsync("read_file",
            new Dictionary<string, object?> { ["file_path"] = "a.cs" }, new ToolExecutionContext());
        await WaitUntil(() => Rows().Count == 1);
        var second = executor.ExecuteAsync("execute_command",
            new Dictionary<string, object?> { ["command"] = "ls" }, new ToolExecutionContext());
        await WaitUntil(() => Rows().Count == 2);

        _feed.MarkAwaitingApproval("execute_command", awaiting: true);

        Assert.False(Rows()[0].Snapshot.IsAwaitingApproval);   // the read is genuinely working
        Assert.True(Rows()[1].Snapshot.IsAwaitingApproval);    // the command is blocked on the user

        gate.Release(2);
        await Task.WhenAll(first, second);
    }

    [Fact]
    public void MarkingAnUnknownToolChangesNothing()
    {
        Assert.False(_feed.MarkAwaitingApproval("no_such_tool", awaiting: true));
    }

    // Renders through the real display list, then flattens row 0.
    private static string RenderedRow(ToolCallItem row)
    {
        var b = new Andy.Tui.DisplayList.DisplayListBuilder();
        row.RenderSlice(0, 0, 100, 0, row.MeasureLineCount(100),
            new Andy.Tui.DisplayList.DisplayListBuilder().Build(), b);
        return string.Concat(b.Build().Ops.OfType<Andy.Tui.DisplayList.TextRun>()
            .Where(t => t.Y == 0).OrderBy(t => t.X).Select(t => t.Content));
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "condition was not met before the timeout");
    }
}
