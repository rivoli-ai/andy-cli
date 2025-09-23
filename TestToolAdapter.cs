using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services.Adapters;
using Andy.Tools.Core;
using Andy.Tools.Library;
using Andy.Tools.Registry;
using Microsoft.Extensions.Logging;

// Simple test program for ToolAdapter
public class TestToolAdapterProgram
{
    public static void Main()
    {
        Console.WriteLine("Testing ToolAdapter schema conversion...");

        var success = true;
        success &= TestEnumValues();
        success &= TestArrayTypes();
        success &= TestDefaultValues();

        if (success)
        {
            Console.WriteLine("✅ All tests passed!");
        }
        else
        {
            Console.WriteLine("❌ Some tests failed!");
            Environment.Exit(1);
        }
    }

    static bool TestEnumValues()
    {
        Console.Write("Testing enum values conversion... ");

        var toolRegistry = new InMemoryToolRegistry();
        var tool = new TestToolWithEnum();
        toolRegistry.RegisterTool(tool);

        var toolExecutor = new StandardToolExecutor(toolRegistry, null);
        var adapter = new ToolAdapter("test_enum_tool", toolRegistry, toolExecutor, null);

        var parameters = adapter.Definition.Parameters as Dictionary<string, object>;
        if (parameters == null)
        {
            Console.WriteLine("FAILED: Parameters is null");
            return false;
        }

        var properties = parameters["properties"] as Dictionary<string, object>;
        if (properties == null)
        {
            Console.WriteLine("FAILED: Properties is null");
            return false;
        }

        if (!properties.ContainsKey("query_type"))
        {
            Console.WriteLine("FAILED: query_type not found");
            return false;
        }

        var queryTypeSchema = properties["query_type"] as Dictionary<string, object>;
        if (queryTypeSchema == null)
        {
            Console.WriteLine("FAILED: query_type schema is null");
            return false;
        }

        if (!queryTypeSchema.ContainsKey("enum"))
        {
            Console.WriteLine("FAILED: enum property not found");
            return false;
        }

        var enumValues = queryTypeSchema["enum"] as string[];
        if (enumValues == null)
        {
            Console.WriteLine("FAILED: enum values is null");
            return false;
        }

        if (!enumValues.Contains("symbols") || !enumValues.Contains("structure"))
        {
            Console.WriteLine("FAILED: enum values don't match expected");
            return false;
        }

        Console.WriteLine("PASSED");
        return true;
    }

    static bool TestArrayTypes()
    {
        Console.Write("Testing array type conversion... ");

        var toolRegistry = new InMemoryToolRegistry();
        var tool = new TestToolWithArray();
        toolRegistry.RegisterTool(tool);

        var toolExecutor = new StandardToolExecutor(toolRegistry, null);
        var adapter = new ToolAdapter("test_array_tool", toolRegistry, toolExecutor, null);

        var parameters = adapter.Definition.Parameters as Dictionary<string, object>;
        var properties = parameters?["properties"] as Dictionary<string, object>;
        var filePatternsSchema = properties?["file_patterns"] as Dictionary<string, object>;

        if (filePatternsSchema == null)
        {
            Console.WriteLine("FAILED: file_patterns schema is null");
            return false;
        }

        if (filePatternsSchema["type"]?.ToString() != "array")
        {
            Console.WriteLine("FAILED: type is not array");
            return false;
        }

        if (!filePatternsSchema.ContainsKey("items"))
        {
            Console.WriteLine("FAILED: items property not found");
            return false;
        }

        var itemsSchema = filePatternsSchema["items"] as Dictionary<string, object>;
        if (itemsSchema == null || itemsSchema["type"]?.ToString() != "string")
        {
            Console.WriteLine("FAILED: items schema incorrect");
            return false;
        }

        Console.WriteLine("PASSED");
        return true;
    }

    static bool TestDefaultValues()
    {
        Console.Write("Testing default values... ");

        var toolRegistry = new InMemoryToolRegistry();
        var tool = new TestToolWithDefaults();
        toolRegistry.RegisterTool(tool);

        var toolExecutor = new StandardToolExecutor(toolRegistry, null);
        var adapter = new ToolAdapter("test_defaults_tool", toolRegistry, toolExecutor, null);

        var parameters = adapter.Definition.Parameters as Dictionary<string, object>;
        var properties = parameters?["properties"] as Dictionary<string, object>;
        var scopeSchema = properties?["scope"] as Dictionary<string, object>;

        if (scopeSchema == null)
        {
            Console.WriteLine("FAILED: scope schema is null");
            return false;
        }

        if (!scopeSchema.ContainsKey("default") || scopeSchema["default"]?.ToString() != "all")
        {
            Console.WriteLine("FAILED: default value not correct");
            return false;
        }

        Console.WriteLine("PASSED");
        return true;
    }

    // Test tools
    class TestToolWithEnum : ToolBase
    {
        public override ToolMetadata Metadata => new()
        {
            Id = "test_enum_tool",
            Name = "Test Enum Tool",
            Description = "Test tool with enum values",
            Parameters = new[]
            {
                new ToolParameter
                {
                    Name = "query_type",
                    Type = "string",
                    Description = "Type of query",
                    Required = true,
                    AllowedValues = new[] { "symbols", "structure", "references", "hierarchy" }
                }
            }
        };

        protected override async System.Threading.Tasks.Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> parameters, ToolExecutionContext context)
        {
            return ToolResult.Success("Test execution");
        }
    }

    class TestToolWithArray : ToolBase
    {
        public override ToolMetadata Metadata => new()
        {
            Id = "test_array_tool",
            Name = "Test Array Tool",
            Description = "Test tool with array parameter",
            Parameters = new[]
            {
                new ToolParameter
                {
                    Name = "file_patterns",
                    Type = "array",
                    Description = "File patterns to match",
                    Required = false
                }
            }
        };

        protected override async System.Threading.Tasks.Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> parameters, ToolExecutionContext context)
        {
            return ToolResult.Success("Test execution");
        }
    }

    class TestToolWithDefaults : ToolBase
    {
        public override ToolMetadata Metadata => new()
        {
            Id = "test_defaults_tool",
            Name = "Test Defaults Tool",
            Description = "Test tool with default values",
            Parameters = new[]
            {
                new ToolParameter
                {
                    Name = "scope",
                    Type = "string",
                    Description = "Scope to search in",
                    Required = false,
                    DefaultValue = "all"
                },
                new ToolParameter
                {
                    Name = "include_private",
                    Type = "boolean",
                    Description = "Include private members",
                    Required = false,
                    DefaultValue = false
                }
            }
        };

        protected override async System.Threading.Tasks.Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> parameters, ToolExecutionContext context)
        {
            return ToolResult.Success("Test execution");
        }
    }
}