using System;
using System.Collections.Generic;
using System.Reflection;
using Andy.Cli.Widgets;

namespace TestToolResult
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== Testing RunningToolItem Result Display ===\n");

            // Create a tool item with parameters
            var toolItem = new RunningToolItem(
                "test_1",
                "datetime_tool",
                new Dictionary<string, object?> { { "operation", "current_date" } }
            );

            // Get the private GetResultSummary method
            var getResultSummaryMethod = typeof(RunningToolItem).GetMethod(
                "GetResultSummary",
                BindingFlags.NonPublic | BindingFlags.Instance
            );

            if (getResultSummaryMethod == null)
            {
                Console.WriteLine("ERROR: Could not find GetResultSummary method");
                return;
            }

            // Test 1: Tool completed without result being set
            Console.WriteLine("Test 1: Tool completed without SetResult being called");
            toolItem.SetComplete(true, "1.5s");
            var result1 = (string?)getResultSummaryMethod.Invoke(toolItem, null) ?? "null";
            Console.WriteLine($"  Result: '{result1}'");
            if (result1.Contains("Completed") || result1.Contains("Done"))
            {
                Console.WriteLine($"  ✗ FAIL: Shows generic message instead of actual result");
            }
            Console.WriteLine();

            // Test 2: Tool with result set
            Console.WriteLine("Test 2: Tool with SetResult called with actual date");
            var actualDate = "Tuesday, October 15, 2024";
            toolItem.SetResult(actualDate);
            var result2 = (string?)getResultSummaryMethod.Invoke(toolItem, null) ?? "null";
            Console.WriteLine($"  Result: '{result2}'");

            if (result2.Contains(actualDate) || result2.Contains("Tuesday"))
            {
                Console.WriteLine($"  ✓ SUCCESS: Shows actual date result!");
            }
            else if (result2.Contains("Completed") || result2.Contains("Done"))
            {
                Console.WriteLine($"  ✗ FAIL: Still shows generic message");
                Console.WriteLine($"  Expected: '{actualDate}'");
                Console.WriteLine($"  Got: '{result2}'");
            }
            Console.WriteLine();

            // Test 3: Check what happens with null or empty result
            Console.WriteLine("Test 3: Tool with empty result");
            var toolItem2 = new RunningToolItem(
                "test_2",
                "datetime_tool",
                new Dictionary<string, object?> { { "operation", "current_date" } }
            );
            toolItem2.SetComplete(true, "1s");
            toolItem2.SetResult("");
            var result3 = (string?)getResultSummaryMethod.Invoke(toolItem2, null) ?? "null";
            Console.WriteLine($"  Result: '{result3}'");
            Console.WriteLine();

            // Test 4: Check the actual flow - simulate what UiUpdatingToolExecutor should do
            Console.WriteLine("Test 4: Simulate UiUpdatingToolExecutor flow");
            var toolItem3 = new RunningToolItem(
                "datetime_tool_1",
                "datetime_tool",
                new Dictionary<string, object?> { { "operation", "current_date" } }
            );

            // This is what should happen:
            // 1. Tool executes and returns result
            // 2. UiUpdatingToolExecutor extracts the result
            // 3. ToolExecutionTracker.TrackToolComplete is called with the result
            // 4. SimpleAssistantService gets execution info and calls AddToolExecutionComplete
            // 5. FeedView calls SetResult on the RunningToolItem

            Console.WriteLine("  Simulating: toolItem.SetResult('Tuesday, October 15, 2024')");
            toolItem3.SetResult("Tuesday, October 15, 2024");
            toolItem3.SetComplete(true, "0.5s");

            var result4 = (string?)getResultSummaryMethod.Invoke(toolItem3, null) ?? "null";
            Console.WriteLine($"  Final result display: '{result4}'");

            if (!result4.Contains("Tuesday"))
            {
                Console.WriteLine($"  ✗ PROBLEM: The result is not being displayed!");
                Console.WriteLine($"  The issue is that SetResult is not being called with the actual result.");
            }
            else
            {
                Console.WriteLine($"  ✓ When SetResult is called properly, it works!");
            }
        }
    }
}