using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Cli.Tests.Services
{
    /// <summary>
    /// Regression tests for the tool-execution spinner lifecycle.
    ///
    /// Bug: the running-tool spinner (and its elapsed timer) only stopped when
    /// SimpleAssistantService ran its end-of-turn completion loop, which happens
    /// after the whole agent turn — including the final model response — finishes.
    /// So a tool that returned in 12ms appeared to hang for the rest of the turn.
    /// The fix moves UI completion into UiUpdatingToolExecutor, the instant the
    /// tool returns its data.
    /// </summary>
    public class ToolSpinnerCompletionTests
    {
        private static RunningToolItem? FindTool(FeedView feed, string toolId)
        {
            return feed.GetItemsForTesting()
                .OfType<RunningToolItem>()
                .FirstOrDefault(t => t.ToolId == toolId);
        }

        private static string? ReadDuration(RunningToolItem item)
        {
            var field = typeof(RunningToolItem).GetField("_duration",
                BindingFlags.NonPublic | BindingFlags.Instance);
            return field?.GetValue(item) as string;
        }

        [Fact]
        public async Task Spinner_StopsWhenToolReturns_WithoutEndOfTurnLoop()
        {
            // Arrange: a unique tool id/name so we don't collide with the
            // process-wide ToolExecutionTracker singleton across tests.
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var toolName = $"unit_tool_{suffix}";
            var uiToolId = $"{toolName}_1";

            var feed = new FeedView();
            ToolExecutionTracker.Instance.SetFeedView(feed);

            // Mirror what SimpleAssistantService.ToolCalled does: create the UI
            // spinner and enqueue the pending tool so the executor can claim it.
            feed.AddToolExecutionStart(uiToolId, toolName);
            ToolExecutionTracker.Instance.EnqueuePendingTool(toolName, uiToolId);

            var gate = new TaskCompletionSource();
            var inner = new GatedToolExecutor(gate.Task, "all done");
            var uiExecutor = new UiUpdatingToolExecutor(inner, NullLogger<UiUpdatingToolExecutor>.Instance);

            // Act: start execution but keep the tool blocked.
            var execTask = uiExecutor.ExecuteAsync(toolName, new Dictionary<string, object?>(), null);

            // While the tool runs, the spinner must still be spinning.
            var duringRun = FindTool(feed, uiToolId);
            Assert.NotNull(duringRun);
            Assert.False(duringRun!.IsComplete);

            // Let the tool return.
            gate.SetResult();
            var result = await execTask;

            // Assert: completion happened at tool-return time — no end-of-turn
            // loop was ever run in this test.
            Assert.True(result.IsSuccessful);
            var afterRun = FindTool(feed, uiToolId);
            Assert.NotNull(afterRun);
            Assert.True(afterRun!.IsComplete);
        }

        [Fact]
        public void EndOfTurnPass_DoesNotOverwrite_ExecutorCompletion()
        {
            // The executor stops the spinner with the tool's real duration; the
            // later end-of-turn pass calls AddToolExecutionComplete again with the
            // whole-turn elapsed. That second call must be a no-op so the accurate
            // duration survives.
            var feed = new FeedView();
            var toolId = $"idemp_{Guid.NewGuid():N}";

            feed.AddToolExecutionStart(toolId, "idemp_tool");
            feed.AddToolExecutionComplete(toolId, success: true, duration: "12ms", result: "done");
            feed.AddToolExecutionComplete(toolId, success: true, duration: "9.9s", result: "stale");

            var item = FindTool(feed, toolId);
            Assert.NotNull(item);
            Assert.True(item!.IsComplete);
            Assert.Equal("12ms", ReadDuration(item));
        }

        [Fact]
        public async Task ConcurrentStartAndComplete_DoesNotThrow()
        {
            // Guards the shared running-tool surface against the kind of cross-thread
            // access that previously raced an unsynchronized dictionary into a
            // NullReferenceException mid-turn.
            var feed = new FeedView();
            const int count = 200;

            var tasks = Enumerable.Range(0, count).Select(i => Task.Run(() =>
            {
                var id = $"race_{i}";
                feed.AddToolExecutionStart(id, "race_tool");
                feed.AddToolExecutionComplete(id, success: true, duration: "1ms", result: "ok");
            }));

            // Should complete without throwing.
            await Task.WhenAll(tasks);

            var completed = feed.GetItemsForTesting()
                .OfType<RunningToolItem>()
                .Count(t => t.ToolId.StartsWith("race_") && t.IsComplete);
            Assert.Equal(count, completed);
        }

        /// <summary>Inner executor whose completion is gated on an external task.</summary>
        private sealed class GatedToolExecutor : IToolExecutor
        {
            private readonly Task _gate;
            private readonly string _data;

            public GatedToolExecutor(Task gate, string data)
            {
                _gate = gate;
                _data = data;
            }

#pragma warning disable CS0067 // events required by IToolExecutor but unused by this fake
            public event EventHandler<ToolExecutionStartedEventArgs>? ExecutionStarted;
            public event EventHandler<ToolExecutionCompletedEventArgs>? ExecutionCompleted;
            public event EventHandler<SecurityViolationEventArgs>? SecurityViolation;
#pragma warning restore CS0067

            public async Task<ToolExecutionResult> ExecuteAsync(string toolId, Dictionary<string, object?> parameters, ToolExecutionContext? context = null)
            {
                await _gate;
                return new ToolExecutionResult
                {
                    IsSuccessful = true,
                    Message = "Successfully executed",
                    Data = _data
                };
            }

            public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
                => ExecuteAsync(request.ToolId, request.Parameters, request.Context);

            public Task<IList<string>> ValidateExecutionRequestAsync(ToolExecutionRequest request)
                => Task.FromResult<IList<string>>(new List<string>());

            public Task<ToolResourceUsage?> EstimateResourceUsageAsync(string toolId, Dictionary<string, object?> parameters)
                => Task.FromResult<ToolResourceUsage?>(null);

            public Task<int> CancelExecutionsAsync(string correlationId) => Task.FromResult(0);

            public IReadOnlyList<RunningExecutionInfo> GetRunningExecutions() => new List<RunningExecutionInfo>();

            public ToolExecutionStatistics GetStatistics() => new ToolExecutionStatistics();
        }
    }
}
