using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using Microsoft.Extensions.Logging;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Andy.Cli.Tests.Services
{
    public class ToolResultDisplayTest
    {
        [Fact]
        public async Task UiUpdatingToolExecutor_Should_Extract_And_Pass_Actual_Result()
        {
            // Arrange
            var actualResult = "Tuesday, October 15, 2024";
            var mockInnerExecutor = new MockToolExecutor(actualResult);
            var logger = new TestLogger<UiUpdatingToolExecutor>();
            var uiExecutor = new UiUpdatingToolExecutor(mockInnerExecutor, logger);

            // Set up the tracker with a test tool ID
            ToolExecutionTracker.Instance.RegisterToolMapping("datetime_tool", "datetime_tool_1");

            // Act
            var parameters = new Dictionary<string, object?>
            {
                { "operation", "current_date" }
            };
            var result = await uiExecutor.ExecuteAsync("datetime_tool", parameters, null);

            // Assert
            Assert.True(result.IsSuccessful);

            // Check that the logger captured the extracted result
            var extractedResultLog = logger.FindLog("[UI_EXECUTOR] Extracted result");
            Assert.NotNull(extractedResultLog);
            Assert.Contains(actualResult, extractedResultLog);

            // Check that the tracker has the correct result
            var executionInfo = ToolExecutionTracker.Instance.GetExecutionInfo("datetime_tool_1");
            Assert.NotNull(executionInfo);
            Assert.Equal(actualResult, executionInfo.Result);
        }

        [Fact]
        public void RunningToolItem_Should_Display_Actual_Result_Not_Generic_Message()
        {
            // Arrange
            var toolItem = new RunningToolItem("test_1", "datetime_tool", new Dictionary<string, object?>
            {
                { "operation", "current_date" }
            });

            // Act - Set the tool as complete with a result
            toolItem.SetComplete(true, "1.5s");
            toolItem.SetResult("Tuesday, October 15, 2024");

            // Use reflection to get the result summary
            var getResultSummary = typeof(RunningToolItem).GetMethod(
                "GetResultSummary",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            var resultSummary = (string?)getResultSummary?.Invoke(toolItem, null);

            // Assert
            Assert.NotNull(resultSummary);
            Assert.DoesNotContain("Completed successfully", resultSummary);
            Assert.DoesNotContain("Completed: current_date", resultSummary);
            Assert.Contains("Tuesday", resultSummary);
        }
    }

    // Mock implementations for testing
    public class MockToolExecutor : IToolExecutor
    {
        private readonly string _resultToReturn;

        public MockToolExecutor(string resultToReturn)
        {
            _resultToReturn = resultToReturn;
        }

        public event EventHandler<ToolExecutionStartedEventArgs>? ExecutionStarted;
        public event EventHandler<ToolExecutionCompletedEventArgs>? ExecutionCompleted;
        public event EventHandler<SecurityViolationEventArgs>? SecurityViolation;

        public Task<ToolExecutionResult> ExecuteAsync(string toolId, Dictionary<string, object?> parameters, ToolExecutionContext? context = null)
        {
            // Simulate what DateTimeTool returns
            var result = new ToolExecutionResult
            {
                IsSuccessful = true,
                Message = "Successfully performed current_date operation",
                Data = _resultToReturn  // The actual date as a string
            };
            return Task.FromResult(result);
        }

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
        {
            return ExecuteAsync(request.ToolId, request.Parameters, request.Context);
        }

        public Task<IList<string>> ValidateExecutionRequestAsync(ToolExecutionRequest request)
        {
            return Task.FromResult<IList<string>>(new List<string>());
        }

        public Task<ToolResourceUsage?> EstimateResourceUsageAsync(string toolId, Dictionary<string, object?> parameters)
        {
            return Task.FromResult<ToolResourceUsage?>(null);
        }

        public Task<int> CancelExecutionsAsync(string correlationId)
        {
            return Task.FromResult(0);
        }

        public IReadOnlyList<RunningExecutionInfo> GetRunningExecutions()
        {
            return new List<RunningExecutionInfo>();
        }

        public ToolExecutionStatistics GetStatistics()
        {
            return new ToolExecutionStatistics();
        }
    }

    public class TestLogger<T> : ILogger<T>
    {
        private readonly List<string> _logs = new();

        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            _logs.Add(message);
        }

        public string? FindLog(string contains)
        {
            return _logs.Find(log => log.Contains(contains));
        }

        private class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new NullScope();
            public void Dispose() { }
        }
    }
}