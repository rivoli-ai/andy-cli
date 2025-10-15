using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging;

class TestToolResultFlow
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("=== Testing Tool Result Flow ===\n");

        // Simulate the flow
        var tracker = ToolExecutionTracker.Instance;

        // Step 1: SimpleAssistantService registers tool mapping when ToolCalled event fires
        Console.WriteLine("Step 1: Register tool mapping (datetime_tool -> datetime_tool_1)");
        var uiToolId = "datetime_tool_1";
        var actualToolName = "datetime_tool";
        tracker.RegisterToolMapping(actualToolName, uiToolId);

        // Step 2: UiUpdatingToolExecutor starts execution
        Console.WriteLine("\nStep 2: UiUpdatingToolExecutor tracks tool start");
        var parameters = new Dictionary<string, object?> { { "operation", "current_date" } };

        // This is what we just added - track the tool start
        tracker.TrackToolStart(uiToolId, actualToolName, parameters);

        // Step 3: Tool executes and returns result
        Console.WriteLine("\nStep 3: Tool executes and returns result");
        var toolResult = "Tuesday, October 15, 2024";

        // Step 4: UiUpdatingToolExecutor tracks completion
        Console.WriteLine("\nStep 4: UiUpdatingToolExecutor tracks completion with result");
        tracker.TrackToolComplete(uiToolId, true, toolResult);

        // Step 5: SimpleAssistantService tries to get execution info
        Console.WriteLine("\nStep 5: SimpleAssistantService retrieves execution info");
        var executionInfo = tracker.GetExecutionInfo(uiToolId);

        if (executionInfo != null)
        {
            Console.WriteLine($"  ✓ SUCCESS: Found execution info!");
            Console.WriteLine($"    Tool Name: {executionInfo.ToolName}");
            Console.WriteLine($"    Result: {executionInfo.Result}");
            Console.WriteLine($"    Parameters: {string.Join(", ", executionInfo.Parameters?.Select(p => $"{p.Key}={p.Value}") ?? new string[0])}");
        }
        else
        {
            Console.WriteLine($"  ✗ FAIL: Execution info is NULL!");
        }

        // Test alternate lookup by base tool name
        Console.WriteLine("\nStep 6: Test lookup by base tool name");
        var infoByBaseName = tracker.GetExecutionInfo(actualToolName);
        if (infoByBaseName != null)
        {
            Console.WriteLine($"  ✓ Found by base name: {infoByBaseName.Result}");
        }
        else
        {
            Console.WriteLine($"  ✗ Not found by base name");
        }
    }
}