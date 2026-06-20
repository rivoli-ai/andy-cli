using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using Microsoft.Extensions.Logging;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

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
            var toolItem = new RunningToolItem("test_1", "datetime_tool");
            toolItem.SetParameters(new Dictionary<string, object?>
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

        [Fact]
        public async Task UiUpdatingToolExecutor_Failure_Should_Surface_ErrorMessage_Not_Generic_Fallback()
        {
            // Arrange: a tool failure carries its reason in ErrorMessage, leaving Message null
            // (this is what ToolResult.Failure(...) and the inner ToolExecutor's validation
            // failures actually produce). Reading Message used to drop the reason and emit the
            // useless "Operation failed (params)" fallback.
            var errorReason = "Validation failed: Parameter 'file_patterns' must be an array";
            var mockInnerExecutor = new FailingMockToolExecutor(errorReason);
            var logger = new TestLogger<UiUpdatingToolExecutor>();
            var uiExecutor = new UiUpdatingToolExecutor(mockInnerExecutor, logger);

            ToolExecutionTracker.Instance.RegisterToolMapping("search_text", "search_text_fail_1");

            // Act
            var parameters = new Dictionary<string, object?>
            {
                { "search_pattern", "transparent" },
                { "file_patterns", "*.cs" }
            };
            var result = await uiExecutor.ExecuteAsync("search_text", parameters, null);

            // Assert
            Assert.False(result.IsSuccessful);

            var executionInfo = ToolExecutionTracker.Instance.GetExecutionInfo("search_text_fail_1");
            Assert.NotNull(executionInfo);
            Assert.Equal(errorReason, executionInfo!.Result);
            Assert.DoesNotContain("Operation failed", executionInfo.Result ?? "");
        }

        [Fact]
        public async Task UiUpdatingToolExecutor_Coerces_BareString_To_Array_Before_Dispatch()
        {
            // Arrange: search_text declares file_patterns as an array. The model passes it as a
            // bare string "*.cs". The executor should coerce it to ["*.cs"] before dispatch so the
            // framework validator does not reject it with a type-mismatch error.
            var capturing = new CapturingMockToolExecutor();
            var registry = new Mock<IToolRegistry>();
            registry.Setup(r => r.GetTool("search_text")).Returns(new ToolRegistration
            {
                Metadata = new ToolMetadata
                {
                    Id = "search_text",
                    Name = "Search Text",
                    Parameters = new[]
                    {
                        new ToolParameter { Name = "search_pattern", Type = "string", Required = true },
                        new ToolParameter { Name = "file_patterns", Type = "array", Required = false }
                    }
                },
                IsEnabled = true
            });

            var logger = new TestLogger<UiUpdatingToolExecutor>();
            var uiExecutor = new UiUpdatingToolExecutor(capturing, logger, registry.Object);
            ToolExecutionTracker.Instance.RegisterToolMapping("search_text", "search_text_coerce_1");

            // Act
            await uiExecutor.ExecuteAsync("search_text", new Dictionary<string, object?>
            {
                { "search_pattern", "transparent" },
                { "file_patterns", "*.cs" }
            }, null);

            // Assert: the inner executor received an array for file_patterns.
            Assert.NotNull(capturing.LastParameters);
            var filePatterns = capturing.LastParameters!["file_patterns"];
            Assert.IsType<string[]>(filePatterns);
            Assert.Equal(new[] { "*.cs" }, (string[])filePatterns!);
            // The scalar parameter is unchanged.
            Assert.Equal("transparent", capturing.LastParameters["search_pattern"]);
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

    // Mock that simulates a failed tool execution carrying its reason in ErrorMessage
    // (with Message left null), mirroring ToolResult.Failure(...) / validation failures.
    public class FailingMockToolExecutor : IToolExecutor
    {
        private readonly string _errorMessage;

        public FailingMockToolExecutor(string errorMessage)
        {
            _errorMessage = errorMessage;
        }

        public event EventHandler<ToolExecutionStartedEventArgs>? ExecutionStarted;
        public event EventHandler<ToolExecutionCompletedEventArgs>? ExecutionCompleted;
        public event EventHandler<SecurityViolationEventArgs>? SecurityViolation;

        public Task<ToolExecutionResult> ExecuteAsync(string toolId, Dictionary<string, object?> parameters, ToolExecutionContext? context = null)
        {
            var result = new ToolExecutionResult
            {
                IsSuccessful = false,
                ErrorMessage = _errorMessage,
                Message = null,
                Data = null
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

    // Mock that records the parameters it was dispatched, so tests can assert on coercion.
    public class CapturingMockToolExecutor : IToolExecutor
    {
        public Dictionary<string, object?>? LastParameters { get; private set; }

        public event EventHandler<ToolExecutionStartedEventArgs>? ExecutionStarted;
        public event EventHandler<ToolExecutionCompletedEventArgs>? ExecutionCompleted;
        public event EventHandler<SecurityViolationEventArgs>? SecurityViolation;

        public Task<ToolExecutionResult> ExecuteAsync(string toolId, Dictionary<string, object?> parameters, ToolExecutionContext? context = null)
        {
            LastParameters = parameters;
            return Task.FromResult(new ToolExecutionResult
            {
                IsSuccessful = true,
                Message = "ok",
                Data = "ok"
            });
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