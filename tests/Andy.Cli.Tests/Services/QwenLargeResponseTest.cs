using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Andy.Cli.Services;
using Xunit;
using Xunit.Abstractions;

namespace Andy.Cli.Tests.Services;

public class QwenLargeResponseTest
{
    private readonly ModelResponseInterpreter _interpreter;
    private readonly ITestOutputHelper _output;

    public QwenLargeResponseTest(ITestOutputHelper output)
    {
        _interpreter = new ModelResponseInterpreter();
        _output = output;
    }

    [Fact]
    public void QwenModel_VeryLargeDirectoryListing_ShouldFormatProperly()
    {
        // Simulate a very large directory listing result that might cause issues
        var items = new List<string>();
        for (int i = 0; i < 100; i++)
        {
            items.Add($@"{{
                ""name"": ""System.Xml.{i}.dll"",
                ""fullPath"": ""/path/to/System.Xml.{i}.dll"",
                ""type"": ""file"",
                ""size"": {15000 + i * 100},
                ""sizeFormatted"": ""{15 + i * 0.1:F2} KB""
            }}");
        }
        
        var largeResult = $@"{{
            ""items"": [
                {string.Join(",\n                ", items)}
            ],
            ""totalSize"": 2500000,
            ""fileCount"": 100,
            ""directoryCount"": 5
        }}";

        _output.WriteLine($"Result size: {largeResult.Length} characters");

        // Create tool calls and results
        var toolCalls = new List<ModelToolCall>
        {
            new() { ToolId = "list_directory", Parameters = new Dictionary<string, object?> { ["path"] = "src/bin/Debug" } }
        };
        var results = new List<string> { largeResult };

        // Format the results - this should simplify them for Qwen
        var formatted = _interpreter.FormatToolResults(toolCalls, results, "qwen-3-coder-480b", "cerebras");
        
        _output.WriteLine($"Formatted size: {formatted.Length} characters");
        _output.WriteLine($"First 500 chars: {formatted.Substring(0, Math.Min(500, formatted.Length))}");
        
        // The formatted result should be much smaller than the original
        Assert.True(formatted.Length < largeResult.Length, "Formatted result should be smaller than raw JSON");
        
        // Should contain summary information
        Assert.Contains("[list_directory completed", formatted);
        Assert.Contains("Found items:", formatted);
    }

    [Fact]
    public void QwenModel_TokenLimitExceeded_ShouldTruncateGracefully()
    {
        // Create a response that would exceed typical token limits
        var hugeResponse = new StringBuilder();
        hugeResponse.AppendLine("Here are the directory contents:");
        
        // Add many repeated lines (simulating what Qwen might output)
        for (int i = 0; i < 500; i++)
        {
            hugeResponse.AppendLine($"   ├─ System.Xml.XPath.{i}.dll ({15 + i * 0.1:F2} KB)");
        }
        
        var response = hugeResponse.ToString();
        _output.WriteLine($"Response length: {response.Length} characters");
        
        // Clean the response
        var cleaned = _interpreter.CleanResponseForDisplay(response, "qwen-3-coder-480b");
        
        // Should not have duplicates (deduplication should work)
        var lines = cleaned.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var uniqueLines = lines.Distinct().Count();
        
        _output.WriteLine($"Total lines: {lines.Count}, Unique lines: {uniqueLines}");
        
        // Most lines should be unique after deduplication
        Assert.True(uniqueLines > lines.Count * 0.9, "Should have mostly unique lines");
    }

    [Fact]
    public void QwenModel_NestedDirectoryStructure_ShouldSimplify()
    {
        // Test with deeply nested directory structure like in the error
        var nestedJson = @"{
            ""items"": [
                {
                    ""name"": ""bin"",
                    ""type"": ""directory"",
                    ""items"": [
                        {
                            ""name"": ""Debug"",
                            ""type"": ""directory"",
                            ""items"": [
                                {
                                    ""name"": ""net8.0"",
                                    ""type"": ""directory"",
                                    ""items"": [
                                        { ""name"": ""System.Xml.dll"", ""type"": ""file"", ""size"": 15000 },
                                        { ""name"": ""System.Xml.XPath.dll"", ""type"": ""file"", ""size"": 16000 }
                                    ]
                                }
                            ]
                        }
                    ]
                }
            ]
        }";

        var toolCalls = new List<ModelToolCall>
        {
            new() { ToolId = "list_directory", Parameters = new Dictionary<string, object?> { ["path"] = "src" } }
        };
        var results = new List<string> { nestedJson };

        // Format should simplify nested structure
        var formatted = _interpreter.FormatToolResults(toolCalls, results, "qwen-3-coder-480b", "cerebras");
        
        _output.WriteLine($"Formatted result:\n{formatted}");
        
        // Should be simplified
        Assert.DoesNotContain("\"items\":", formatted);
        Assert.Contains("[list_directory completed", formatted);
    }

    [Fact]
    public void QwenModel_ErrorResponse_ShouldHandleGracefully()
    {
        // Test handling of error responses
        var errorResponse = @"LLM Error: Service request failed.
Status: 400 (Bad Request)";

        // This should not crash or throw
        var cleaned = _interpreter.CleanResponseForDisplay(errorResponse, "qwen-3-coder-480b");
        
        _output.WriteLine($"Cleaned error: {cleaned}");
        
        // Should preserve error message
        Assert.Contains("Error", cleaned);
        Assert.Contains("400", cleaned);
    }
}