using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services.Adapters;
using Andy.Tools.Core;
using Andy.Tools.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Andy.Cli.Tests.Services.Adapters;

public class ToolAdapterTests
{
    private readonly Mock<IToolRegistry> _mockToolRegistry;
    private readonly Mock<IToolExecutor> _mockToolExecutor;
    private readonly Mock<ILogger<ToolAdapter>> _mockLogger;

    public ToolAdapterTests()
    {
        _mockToolRegistry = new Mock<IToolRegistry>();
        _mockToolExecutor = new Mock<IToolExecutor>();
        _mockLogger = new Mock<ILogger<ToolAdapter>>();
    }

    [Fact]
    public void ToolAdapter_ConvertsEnumValuesToJsonSchema()
    {
        // Arrange
        var toolId = "test_tool";
        var tool = new TestToolWithEnum();
        _mockToolRegistry.Setup(r => r.GetTool(toolId)).Returns(tool);

        // Act
        var adapter = new ToolAdapter(toolId, _mockToolRegistry.Object, _mockToolExecutor.Object, _mockLogger.Object);

        // Assert
        Assert.NotNull(adapter.Definition);
        Assert.Equal(toolId, adapter.Definition.Name);
        Assert.NotNull(adapter.Definition.Parameters);

        var parameters = adapter.Definition.Parameters as Dictionary<string, object>;
        Assert.NotNull(parameters);
        Assert.True(parameters.ContainsKey("properties"));

        var properties = parameters["properties"] as Dictionary<string, object>;
        Assert.NotNull(properties);
        Assert.True(properties.ContainsKey("query_type"));

        var queryTypeSchema = properties["query_type"] as Dictionary<string, object>;
        Assert.NotNull(queryTypeSchema);
        Assert.Equal("string", queryTypeSchema["type"]);
        Assert.True(queryTypeSchema.ContainsKey("enum"));

        var enumValues = queryTypeSchema["enum"] as string[];
        Assert.NotNull(enumValues);
        Assert.Contains("symbols", enumValues);
        Assert.Contains("structure", enumValues);
        Assert.Contains("references", enumValues);
        Assert.Contains("hierarchy", enumValues);
    }

    [Fact]
    public void ToolAdapter_ConvertsArrayTypeToJsonSchemaWithItems()
    {
        // Arrange
        var toolId = "test_array_tool";
        var tool = new TestToolWithArray();
        _mockToolRegistry.Setup(r => r.GetTool(toolId)).Returns(tool);

        // Act
        var adapter = new ToolAdapter(toolId, _mockToolRegistry.Object, _mockToolExecutor.Object, _mockLogger.Object);

        // Assert
        Assert.NotNull(adapter.Definition);
        Assert.NotNull(adapter.Definition.Parameters);

        var parameters = adapter.Definition.Parameters as Dictionary<string, object>;
        Assert.NotNull(parameters);
        Assert.True(parameters.ContainsKey("properties"));

        var properties = parameters["properties"] as Dictionary<string, object>;
        Assert.NotNull(properties);
        Assert.True(properties.ContainsKey("file_patterns"));

        var filePatternsSchema = properties["file_patterns"] as Dictionary<string, object>;
        Assert.NotNull(filePatternsSchema);
        Assert.Equal("array", filePatternsSchema["type"]);
        Assert.True(filePatternsSchema.ContainsKey("items"));

        var itemsSchema = filePatternsSchema["items"] as Dictionary<string, object>;
        Assert.NotNull(itemsSchema);
        Assert.Equal("string", itemsSchema["type"]);
    }

    [Fact]
    public void ToolAdapter_HandlesDefaultValues()
    {
        // Arrange
        var toolId = "test_default_tool";
        var tool = new TestToolWithDefaults();
        _mockToolRegistry.Setup(r => r.GetTool(toolId)).Returns(tool);

        // Act
        var adapter = new ToolAdapter(toolId, _mockToolRegistry.Object, _mockToolExecutor.Object, _mockLogger.Object);

        // Assert
        var parameters = adapter.Definition.Parameters as Dictionary<string, object>;
        var properties = parameters["properties"] as Dictionary<string, object>;

        var scopeSchema = properties["scope"] as Dictionary<string, object>;
        Assert.NotNull(scopeSchema);
        Assert.True(scopeSchema.ContainsKey("default"));
        Assert.Equal("all", scopeSchema["default"]);

        var includePrivateSchema = properties["include_private"] as Dictionary<string, object>;
        Assert.NotNull(includePrivateSchema);
        Assert.True(includePrivateSchema.ContainsKey("default"));
        Assert.Equal(false, includePrivateSchema["default"]);
    }

    [Fact]
    public void ToolAdapter_SetsRequiredFieldsCorrectly()
    {
        // Arrange
        var toolId = "test_required_tool";
        var tool = new TestToolWithRequired();
        _mockToolRegistry.Setup(r => r.GetTool(toolId)).Returns(tool);

        // Act
        var adapter = new ToolAdapter(toolId, _mockToolRegistry.Object, _mockToolExecutor.Object, _mockLogger.Object);

        // Assert
        var parameters = adapter.Definition.Parameters as Dictionary<string, object>;
        Assert.True(parameters.ContainsKey("required"));

        var required = parameters["required"] as string[];
        Assert.NotNull(required);
        Assert.Contains("query_type", required);
        Assert.Contains("pattern", required);
        Assert.DoesNotContain("scope", required);
    }

    [Fact]
    public void ToolRegistryAdapter_LimitsToolsForCerebrasProvider()
    {
        // Arrange
        var mockRegistry = new Mock<IToolRegistry>();
        var mockExecutor = new Mock<IToolExecutor>();
        var mockLogger = new Mock<ILogger<ToolRegistryAdapter>>();

        // Create a variety of tools, some essential and some not
        var allTools = new List<ITool>
        {
            new TestTool("list_directory"),
            new TestTool("read_file"),
            new TestTool("bash_command"),
            new TestTool("search_files"),
            new TestTool("write_file"),     // Not essential for Cerebras
            new TestTool("code_index"),     // Not essential for Cerebras
            new TestTool("system_info")     // Not essential for Cerebras
        };

        mockRegistry.Setup(r => r.GetTools(true)).Returns(allTools);

        // Act - Create adapter with Cerebras provider
        var adapter = new ToolRegistryAdapter(mockRegistry.Object, mockExecutor.Object, mockLogger.Object, "cerebras");
        var registry = adapter.GetToolRegistry();

        // Assert - Only 4 essential tools should be registered
        var registeredTools = registry.GetAllTools();
        Assert.Equal(4, registeredTools.Count);

        // Verify only essential tools are present
        var toolNames = registeredTools.Select(t => t.Definition.Name).ToHashSet();
        Assert.Contains("list_directory", toolNames);
        Assert.Contains("read_file", toolNames);
        Assert.Contains("bash_command", toolNames);
        Assert.Contains("search_files", toolNames);
        Assert.DoesNotContain("write_file", toolNames);
        Assert.DoesNotContain("code_index", toolNames);
        Assert.DoesNotContain("system_info", toolNames);
    }

    [Fact]
    public void ToolRegistryAdapter_IncludesAllToolsForNonCerebrasProvider()
    {
        // Arrange
        var mockRegistry = new Mock<IToolRegistry>();
        var mockExecutor = new Mock<IToolExecutor>();

        var allTools = new List<ITool>
        {
            new TestTool("list_directory"),
            new TestTool("read_file"),
            new TestTool("write_file"),
            new TestTool("code_index"),
            new TestTool("system_info")
        };

        mockRegistry.Setup(r => r.GetTools(true)).Returns(allTools);

        // Act - Create adapter with OpenAI provider
        var adapter = new ToolRegistryAdapter(mockRegistry.Object, mockExecutor.Object, null, "openai");
        var registry = adapter.GetToolRegistry();

        // Assert - All tools should be registered
        var registeredTools = registry.GetAllTools();
        Assert.Equal(5, registeredTools.Count);
    }

    // Helper test tool class for provider-specific tests
    private class TestTool : ToolBase
    {
        private readonly string _id;

        public TestTool(string id)
        {
            _id = id;
        }

        public override ToolMetadata Metadata => new()
        {
            Id = _id,
            Name = _id,
            Description = $"Test tool {_id}",
            Parameters = new ToolParameter[] { }
        };

        protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> parameters, ToolExecutionContext context)
        {
            return Task.FromResult(ToolResult.Success("Test execution"));
        }
    }

    // Test tool classes
    private class TestToolWithEnum : ToolBase
    {
        public override ToolMetadata Metadata => new()
        {
            Id = "test_tool",
            Name = "Test Tool",
            Description = "A test tool with enum values",
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

        protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> parameters, ToolExecutionContext context)
        {
            return Task.FromResult(ToolResult.Success("Test execution"));
        }
    }

    private class TestToolWithArray : ToolBase
    {
        public override ToolMetadata Metadata => new()
        {
            Id = "test_array_tool",
            Name = "Test Array Tool",
            Description = "A test tool with array parameter",
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

        protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> parameters, ToolExecutionContext context)
        {
            return Task.FromResult(ToolResult.Success("Test execution"));
        }
    }

    private class TestToolWithDefaults : ToolBase
    {
        public override ToolMetadata Metadata => new()
        {
            Id = "test_default_tool",
            Name = "Test Default Tool",
            Description = "A test tool with default values",
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

        protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> parameters, ToolExecutionContext context)
        {
            return Task.FromResult(ToolResult.Success("Test execution"));
        }
    }

    private class TestToolWithRequired : ToolBase
    {
        public override ToolMetadata Metadata => new()
        {
            Id = "test_required_tool",
            Name = "Test Required Tool",
            Description = "A test tool with required and optional parameters",
            Parameters = new[]
            {
                new ToolParameter
                {
                    Name = "query_type",
                    Type = "string",
                    Description = "Required query type",
                    Required = true
                },
                new ToolParameter
                {
                    Name = "pattern",
                    Type = "string",
                    Description = "Required pattern",
                    Required = true
                },
                new ToolParameter
                {
                    Name = "scope",
                    Type = "string",
                    Description = "Optional scope",
                    Required = false
                }
            }
        };

        protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> parameters, ToolExecutionContext context)
        {
            return Task.FromResult(ToolResult.Success("Test execution"));
        }
    }
}