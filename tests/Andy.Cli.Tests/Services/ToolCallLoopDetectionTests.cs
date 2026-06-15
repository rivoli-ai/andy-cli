using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Cli.Tests.Services
{
    /// <summary>
    /// Tests for the tool-call loop guard: repeated identical (tool, args) calls within a sliding
    /// window are short-circuited so the agent stops spinning and burning tokens.
    /// </summary>
    public class ToolCallLoopDetectionTests
    {
        [Fact]
        public void Detector_FlagsThirdIdenticalCall_WithDefaults()
        {
            var d = new ToolCallLoopDetector(window: 8, threshold: 3);
            Assert.False(d.RecordAndIsLooping("a")); // 1st
            Assert.False(d.RecordAndIsLooping("a")); // 2nd
            Assert.True(d.RecordAndIsLooping("a"));  // 3rd -> loop
            Assert.True(d.RecordAndIsLooping("a"));  // keeps flagging
        }

        [Fact]
        public void Detector_DistinctSignatures_NeverFlag()
        {
            var d = new ToolCallLoopDetector(window: 8, threshold: 3);
            for (int i = 0; i < 8; i++)
            {
                Assert.False(d.RecordAndIsLooping($"sig_{i}"));
            }
        }

        [Fact]
        public void Detector_OldOccurrencesFallOutOfWindow()
        {
            // window 3, threshold 2: two "a"s separated by enough other calls never coexist.
            var d = new ToolCallLoopDetector(window: 3, threshold: 2);
            Assert.False(d.RecordAndIsLooping("a")); // [a]
            Assert.False(d.RecordAndIsLooping("b")); // [a,b]
            Assert.False(d.RecordAndIsLooping("c")); // [a,b,c]
            Assert.False(d.RecordAndIsLooping("a")); // [b,c,a] - only one "a" in window
        }

        [Fact]
        public void Signature_IsOrderIndependent()
        {
            var p1 = new Dictionary<string, object?> { ["b"] = 2, ["a"] = 1 };
            var p2 = new Dictionary<string, object?> { ["a"] = 1, ["b"] = 2 };
            Assert.Equal(
                ToolCallLoopDetector.Signature("t", p1),
                ToolCallLoopDetector.Signature("t", p2));
        }

        [Fact]
        public void Signature_DiffersByArgs()
        {
            var a = ToolCallLoopDetector.Signature("code_index", new Dictionary<string, object?> { ["q"] = "symbols" });
            var b = ToolCallLoopDetector.Signature("code_index", new Dictionary<string, object?> { ["q"] = "references" });
            Assert.NotEqual(a, b);
        }

        [Fact]
        public async Task Executor_ShortCircuitsRepeatedIdenticalCalls()
        {
            var inner = new CountingToolExecutor();
            var uiExecutor = new UiUpdatingToolExecutor(inner, NullLogger<UiUpdatingToolExecutor>.Instance);

            var feed = new FeedView();
            ToolExecutionTracker.Instance.SetFeedView(feed);

            var toolName = $"loop_tool_{Guid.NewGuid():N}";
            var args = new Dictionary<string, object?> { ["query_type"] = "symbols" };

            int executed = 0, blocked = 0;
            for (int i = 0; i < 5; i++)
            {
                // Mirror SimpleAssistantService.ToolCalled enqueueing a UI id per call.
                ToolExecutionTracker.Instance.EnqueuePendingTool(toolName, $"{toolName}_{i}");
                var r = await uiExecutor.ExecuteAsync(toolName, new Dictionary<string, object?>(args), null);
                if (r.IsSuccessful) executed++;
                else if ((r.Message ?? "").Contains("Loop guard")) blocked++;
            }

            // With threshold 3, the first two run for real; the rest are short-circuited.
            Assert.Equal(2, inner.Calls);
            Assert.Equal(2, executed);
            Assert.Equal(3, blocked);
        }

        private sealed class CountingToolExecutor : IToolExecutor
        {
            public int Calls { get; private set; }

#pragma warning disable CS0067 // required by interface, unused here
            public event EventHandler<ToolExecutionStartedEventArgs>? ExecutionStarted;
            public event EventHandler<ToolExecutionCompletedEventArgs>? ExecutionCompleted;
            public event EventHandler<SecurityViolationEventArgs>? SecurityViolation;
#pragma warning restore CS0067

            public Task<ToolExecutionResult> ExecuteAsync(string toolId, Dictionary<string, object?> parameters, ToolExecutionContext? context = null)
            {
                Calls++;
                return Task.FromResult(new ToolExecutionResult { IsSuccessful = true, Message = "ok", Data = "data" });
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
