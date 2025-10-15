#!/usr/bin/env dotnet-script
#r "nuget: Microsoft.Extensions.Logging, 8.0.0"

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

// Load the Andy.Cli assembly
var assemblyPath = Path.Combine(Directory.GetCurrentDirectory(), "src/Andy.Cli/bin/Debug/net8.0/andy-cli.dll");
var assembly = Assembly.LoadFrom(assemblyPath);

// Get the types we need
var runningToolItemType = assembly.GetType("Andy.Cli.Widgets.RunningToolItem");

if (runningToolItemType == null)
{
    Console.WriteLine("ERROR: Could not find RunningToolItem type");
    return;
}

// Create an instance with reflection
var toolItem = Activator.CreateInstance(
    runningToolItemType,
    "test_1",
    "datetime_tool",
    new Dictionary<string, object?> { { "operation", "current_date" } }
);

// Get methods
var setCompleteMethod = runningToolItemType.GetMethod("SetComplete");
var setResultMethod = runningToolItemType.GetMethod("SetResult");
var getResultSummaryMethod = runningToolItemType.GetMethod("GetResultSummary", BindingFlags.NonPublic | BindingFlags.Instance);

if (setCompleteMethod == null || setResultMethod == null || getResultSummaryMethod == null)
{
    Console.WriteLine("ERROR: Could not find required methods");
    Console.WriteLine($"  SetComplete: {setCompleteMethod != null}");
    Console.WriteLine($"  SetResult: {setResultMethod != null}");
    Console.WriteLine($"  GetResultSummary: {getResultSummaryMethod != null}");
    return;
}

Console.WriteLine("=== Testing RunningToolItem Result Display ===");

// Test 1: Without result
setCompleteMethod.Invoke(toolItem, new object[] { true, "1.5s" });
var result1 = (string)getResultSummaryMethod.Invoke(toolItem, null);
Console.WriteLine($"Test 1 (no result set): '{result1}'");

// Test 2: With actual result
setResultMethod.Invoke(toolItem, new object[] { "Tuesday, October 15, 2024" });
var result2 = (string)getResultSummaryMethod.Invoke(toolItem, null);
Console.WriteLine($"Test 2 (with result): '{result2}'");

// Check results
if (result2.Contains("Tuesday"))
{
    Console.WriteLine("✓ SUCCESS: Result shows actual date");
}
else
{
    Console.WriteLine("✗ FAIL: Result does not show actual date");
    Console.WriteLine($"  Expected to contain: 'Tuesday'");
    Console.WriteLine($"  Actual: '{result2}'");
}