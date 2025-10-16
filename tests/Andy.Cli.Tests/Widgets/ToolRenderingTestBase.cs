using System;
using System.Collections.Generic;
using System.Reflection;
using Andy.Cli.Widgets;
using Andy.Tools.Core;
using Xunit;

namespace Andy.Cli.Tests.Widgets
{
    /// <summary>
    /// Base class for tool rendering tests providing common helper methods
    /// </summary>
    public abstract class ToolRenderingTestBase
    {
        internal static RunningToolItem CreateToolItem(string toolName, Dictionary<string, object?> parameters)
        {
            var toolId = $"{toolName}_{Guid.NewGuid():N}";
            var toolItem = new RunningToolItem(toolId, toolName);
            if (parameters.Count > 0)
            {
                toolItem.SetParameters(parameters);
            }
            return toolItem;
        }

        internal static string GetParameterDisplay(RunningToolItem toolItem)
        {
            var method = typeof(RunningToolItem).GetMethod(
                "GetParameterDisplay",
                BindingFlags.NonPublic | BindingFlags.Instance);

            return (string)(method?.Invoke(toolItem, null) ?? "");
        }

        internal static string GetResultSummary(RunningToolItem toolItem)
        {
            var method = typeof(RunningToolItem).GetMethod(
                "GetResultSummary",
                BindingFlags.NonPublic | BindingFlags.Instance);

            return (string)(method?.Invoke(toolItem, null) ?? "");
        }

        protected static void AssertParameterDisplayContains(string toolName, Dictionary<string, object?> parameters, params string[] expectedContent)
        {
            var toolItem = CreateToolItem(toolName, parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);

            foreach (var expected in expectedContent)
            {
                Assert.Contains(expected, display);
            }
        }

        protected static void AssertResultDisplayContains(string toolName, Dictionary<string, object?> parameters, string result, params string[] expectedContent)
        {
            var toolItem = CreateToolItem(toolName, parameters);
            toolItem.SetComplete(true, "1.0s");
            toolItem.SetResult(result);

            var summary = GetResultSummary(toolItem);

            Assert.NotNull(summary);

            foreach (var expected in expectedContent)
            {
                Assert.Contains(expected, summary);
            }
        }

        protected static void AssertErrorDisplayContains(string toolName, Dictionary<string, object?> parameters, string errorMessage, params string[] expectedContent)
        {
            var toolItem = CreateToolItem(toolName, parameters);
            toolItem.SetComplete(false, "0.5s");
            toolItem.SetResult(errorMessage);

            var summary = GetResultSummary(toolItem);

            Assert.NotNull(summary);

            foreach (var expected in expectedContent)
            {
                Assert.Contains(expected, summary);
            }
        }

        protected static void AssertParameterDisplayDoesNotContain(string toolName, Dictionary<string, object?> parameters, params string[] unexpectedContent)
        {
            var toolItem = CreateToolItem(toolName, parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);

            foreach (var unexpected in unexpectedContent)
            {
                Assert.DoesNotContain(unexpected, display);
            }
        }

        protected static ToolExecutionResult CreateSuccessResult(object data, string? message = null)
        {
            return new ToolExecutionResult
            {
                IsSuccessful = true,
                Message = message ?? "Operation completed successfully",
                Data = data
            };
        }

        protected static ToolExecutionResult CreateErrorResult(string errorMessage, object? data = null)
        {
            return new ToolExecutionResult
            {
                IsSuccessful = false,
                Message = errorMessage,
                Data = data ?? new Dictionary<string, object?>
                {
                    { "error", errorMessage }
                }
            };
        }

        protected static void AssertResultDisplayMatchesPattern(string toolName, Dictionary<string, object?> parameters, string result, string pattern)
        {
            var toolItem = CreateToolItem(toolName, parameters);
            toolItem.SetComplete(true, "1.0s");
            toolItem.SetResult(result);

            var summary = GetResultSummary(toolItem);

            Assert.NotNull(summary);
            Assert.Matches(pattern, summary);
        }
    }
}
