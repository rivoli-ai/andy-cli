using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Cli.Widgets.Tools;
using Andy.Tools.Core;
using Xunit;

namespace Andy.Cli.Tests.Services;

/// <summary>
/// Regression for the spinner that never stops (rivoli-ai/andy-cli#245).
///
/// The CLI created a tool's feed row from the engine's ToolCalled event and expected the executor
/// to claim that row and complete it. But SimpleAgent raises ToolCalled AFTER the call finishes -
/// "raised AFTER execution so subscribers see the actual result" - so the ordering was inverted:
///
///   engine: execute the tool  ->  executor looks for a row, finds none, completes nothing
///   engine: raise ToolCalled  ->  the CLI creates a row that is already stale
///
/// The row therefore spun with no arguments (the header fell back to "Reading file" / "Running a
/// command") until the end-of-turn backstop swept it up, which is why tools appeared to run until
/// the model's final answer arrived.
///
/// These tests drive the executor in the engine's real order: nothing enqueued beforehand.
/// </summary>
public class ToolRowLifecycleTests : IDisposable
{
    private sealed class StubExecutor : IToolExecutor
    {
        private readonly ToolExecutionResult _result;
        public StubExecutor(ToolExecutionResult result) => _result = result;

        public event EventHandler<ToolExecutionStartedEventArgs>? ExecutionStarted;
        public event EventHandler<ToolExecutionCompletedEventArgs>? ExecutionCompleted;
        public event EventHandler<SecurityViolationEventArgs>? SecurityViolation;

        public Task<ToolExecutionResult> ExecuteAsync(string toolId, Dictionary<string, object?> parameters,
            ToolExecutionContext? context = null) => Task.FromResult(_result);

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request) => Task.FromResult(_result);

        public Task<IList<string>> ValidateExecutionRequestAsync(ToolExecutionRequest request)
            => Task.FromResult<IList<string>>(new List<string>());

        public Task<ToolResourceUsage?> EstimateResourceUsageAsync(string toolId, Dictionary<string, object?> parameters)
            => Task.FromResult<ToolResourceUsage?>(null);

        public Task<int> CancelExecutionsAsync(string? toolId = null) => Task.FromResult(0);
        public IReadOnlyList<RunningExecutionInfo> GetRunningExecutions() => Array.Empty<RunningExecutionInfo>();
        public ToolExecutionStatistics GetStatistics() => new();
    }

    private readonly FeedView _feed = new();

    public ToolRowLifecycleTests()
    {
        ToolExecutionTracker.Instance.Reset();
        ToolExecutionTracker.Instance.SetFeedView(_feed);
    }

    public void Dispose() => ToolExecutionTracker.Instance.Reset();

    private static ToolExecutionResult Success(object? data) => new()
    {
        IsSuccessful = true,
        Data = data,
        Message = "ok"
    };

    private UiUpdatingToolExecutor Executor(ToolExecutionResult result)
        => new(new StubExecutor(result), workingDirectoryTracker: new WorkingDirectoryTracker(Environment.CurrentDirectory));

    private ToolCallItem SingleRow()
        => _feed.GetItemsForTesting().OfType<ToolCallItem>().Single();

    [Fact]
    public async Task RowIsCompleteAsSoonAsTheToolReturns()
    {
        // Nothing is enqueued: the engine has not raised ToolCalled yet, because it does not
        // raise it until the call is over.
        var executor = Executor(Success(new Dictionary<string, object?>
        {
            ["command"] = "echo hi",
            ["exit_code"] = 0,
            ["stdout"] = "hi"
        }));

        await executor.ExecuteAsync("execute_command",
            new Dictionary<string, object?> { ["command"] = "echo hi" }, new ToolExecutionContext());

        var row = SingleRow();
        Assert.True(row.Snapshot.IsComplete, "the row must be complete the moment the tool returns");
        Assert.True(row.Snapshot.IsSuccessful);
    }

    [Fact]
    public async Task RowCarriesTheArgumentsTheToolWasCalledWith()
    {
        // The stale-row ordering also starved the header of arguments, which is why it read
        // "Running a command" rather than naming the command.
        var executor = Executor(Success(new Dictionary<string, object?> { ["exit_code"] = 0, ["stdout"] = "hi" }));

        await executor.ExecuteAsync("execute_command",
            new Dictionary<string, object?> { ["command"] = "git status" }, new ToolExecutionContext());

        Assert.Equal("git status", SingleRow().Snapshot.Parameters["command"]);
        Assert.Contains("git status", SingleRow().DebugRows(80)[0]);
    }

    [Fact]
    public async Task StructuredResultReachesTheRow()
    {
        var executor = Executor(Success(new Dictionary<string, object?>
        {
            ["items"] = Array.Empty<object>(),
            ["exit_code"] = 0
        }));

        await executor.ExecuteAsync("list_directory",
            new Dictionary<string, object?> { ["path"] = "src" }, new ToolExecutionContext());

        Assert.NotNull(SingleRow().Snapshot.Data);
        Assert.True(SingleRow().Snapshot.IsComplete);
    }

    [Fact]
    public async Task EachCallGetsItsOwnRow()
    {
        // With the inverted ordering, the second call dequeued the FIRST call's row and completed
        // that one instead, so every row lagged one call behind and the last never completed.
        var executor = Executor(Success(new Dictionary<string, object?> { ["exit_code"] = 0 }));

        await executor.ExecuteAsync("read_file",
            new Dictionary<string, object?> { ["file_path"] = "a.cs" }, new ToolExecutionContext());
        await executor.ExecuteAsync("read_file",
            new Dictionary<string, object?> { ["file_path"] = "b.cs" }, new ToolExecutionContext());

        var rows = _feed.GetItemsForTesting().OfType<ToolCallItem>().ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.Snapshot.IsComplete));
        Assert.Equal("a.cs", rows[0].Snapshot.Parameters["file_path"]);
        Assert.Equal("b.cs", rows[1].Snapshot.Parameters["file_path"]);
    }

    [Fact]
    public async Task AFailedCallIsCompletedToo()
    {
        var executor = Executor(new ToolExecutionResult { IsSuccessful = false, ErrorMessage = "boom" });

        await executor.ExecuteAsync("read_file",
            new Dictionary<string, object?> { ["file_path"] = "gone.cs" }, new ToolExecutionContext());

        var row = SingleRow();
        Assert.True(row.Snapshot.IsComplete);
        Assert.False(row.Snapshot.IsSuccessful);
    }

    [Fact]
    public async Task ARowCreatedInAdvanceIsStillUsedRatherThanDuplicated()
    {
        // If a future engine raises its event BEFORE execution, the pre-created row must be
        // claimed as it always was - not left spinning beside a second one.
        _feed.AddToolExecutionStart("execute_command_1", "execute_command",
            new Dictionary<string, object?> { ["__toolId"] = "execute_command_1" });
        ToolExecutionTracker.Instance.EnqueuePendingTool("execute_command", "execute_command_1");

        var executor = Executor(Success(new Dictionary<string, object?> { ["exit_code"] = 0, ["stdout"] = "hi" }));
        await executor.ExecuteAsync("execute_command",
            new Dictionary<string, object?> { ["command"] = "ls" }, new ToolExecutionContext());

        var row = SingleRow();     // exactly one row, not two
        Assert.Equal("execute_command_1", row.ToolId);
        Assert.True(row.Snapshot.IsComplete);
    }

    [Fact]
    public async Task TheLateEventAdoptsTheRowInsteadOfCreatingASecondOne()
    {
        // This is what SimpleAssistantService's ToolCalled handler does once the executor has
        // already created the row: it must adopt it rather than append a duplicate.
        var executor = Executor(Success(new Dictionary<string, object?> { ["exit_code"] = 0 }));
        await executor.ExecuteAsync("read_file",
            new Dictionary<string, object?> { ["file_path"] = "a.cs" }, new ToolExecutionContext());

        var adopted = ToolExecutionTracker.Instance.DequeueExecutorCreatedRow("read_file");

        Assert.NotNull(adopted);
        Assert.Equal(SingleRow().ToolId, adopted);
        // And only once: a second call of the same event must not adopt the same row again.
        Assert.Null(ToolExecutionTracker.Instance.DequeueExecutorCreatedRow("read_file"));
    }
}
