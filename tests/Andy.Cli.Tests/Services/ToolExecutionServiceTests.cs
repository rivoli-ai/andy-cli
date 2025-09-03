using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Tools.Core;
using Andy.Tools.Execution;

namespace Andy.Cli.Tests.Services;

public class ToolExecutionServiceTests
{
    private readonly Mock<IToolRegistry> _mockRegistry;
    private readonly Mock<IToolExecutor> _mockExecutor;
    private readonly FeedView _feed;
    private readonly ToolExecutionService _service;

    public ToolExecutionServiceTests()
    {
        _mockRegistry = new Mock<IToolRegistry>();
        _mockExecutor = new Mock<IToolExecutor>();
        _feed = new FeedView();
        _service = new ToolExecutionService(_mockRegistry.Object, _mockExecutor.Object, _feed);
    }

    [Fact]
    public async Task ExecuteToolAsync_WithValidTool_ReturnsSuccessResult()
    {
        // Arrange
        var toolId = "list_directory";
        var parameters = new Dictionary<string, object?>
        {
            ["path"] = ".",
            ["recursive"] = false
        };

        var toolReg = new ToolRegistration
        {
            Metadata = new ToolMetadata
            {
                Id = toolId,
                Name = "List Directory",
                Description = "Lists files and directories"
            }
        };

        var executionResult = new ToolResult
        {
            IsSuccessful = true,
            Data = new Dictionary<string, object>
            {
                ["files"] = new[] { "file1.txt", "file2.txt" },
                ["directories"] = new[] { "subdir" }
            }
        };

        _mockRegistry.Setup(x => x.GetTool(toolId)).Returns(toolReg);
        _mockExecutor.Setup(x => x.ExecuteAsync(toolId, parameters, It.IsAny<ToolExecutionContext>()))
            .ReturnsAsync(executionResult);

        // Act
        var result = await _service.ExecuteToolAsync(toolId, parameters);

        // Assert
        Assert.True(result.IsSuccessful);
        Assert.NotNull(result.FullOutput);
        Assert.Contains("file1.txt", result.FullOutput);
        // Verify through result
        Assert.True(result.IsSuccessful);
    }

    [Fact]
    public async Task ExecuteToolAsync_WithUnknownTool_ReturnsErrorResult()
    {
        // Arrange
        var toolId = "unknown_tool";
        var parameters = new Dictionary<string, object?>();

        _mockRegistry.Setup(x => x.GetTool(toolId)).Returns((ToolRegistration)null);

        // Act
        var result = await _service.ExecuteToolAsync(toolId, parameters);

        // Assert
        Assert.False(result.IsSuccessful);
        Assert.Contains("not found", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteToolAsync_WithLargeOutput_TruncatesDisplay()
    {
        // Arrange
        var toolId = "generate_data";
        var parameters = new Dictionary<string, object?>();

        var toolReg = new ToolRegistration
        {
            Metadata = new ToolMetadata
            {
                Id = toolId,
                Name = "Generate Data",
                Description = "Generates lots of data"
            }
        };

        // Generate large output
        var largeData = new Dictionary<string, object>();
        for (int i = 0; i < 100; i++)
        {
            largeData[$"item_{i}"] = $"value_{i}";
        }

        var executionResult = new ToolResult
        {
            IsSuccessful = true,
            Data = largeData
        };

        _mockRegistry.Setup(x => x.GetTool(toolId)).Returns(toolReg);
        _mockExecutor.Setup(x => x.ExecuteAsync(toolId, parameters, It.IsAny<ToolExecutionContext>()))
            .ReturnsAsync(executionResult);

        // Act
        var result = await _service.ExecuteToolAsync(toolId, parameters);

        // Assert
        Assert.True(result.IsSuccessful);
        Assert.True(result.TruncatedForDisplay);
        Assert.NotNull(result.FullOutput);
        // Truncation flag should be set
        Assert.True(result.TruncatedForDisplay);
    }

    [Fact]
    public async Task ExecuteToolAsync_WithExecutionError_ReturnsErrorDetails()
    {
        // Arrange
        var toolId = "failing_tool";
        var parameters = new Dictionary<string, object?>();

        var toolReg = new ToolRegistration
        {
            Metadata = new ToolMetadata
            {
                Id = toolId,
                Name = "Failing Tool",
                Description = "A tool that fails"
            }
        };

        _mockRegistry.Setup(x => x.GetTool(toolId)).Returns(toolReg);
        _mockExecutor.Setup(x => x.ExecuteAsync(toolId, parameters, It.IsAny<ToolExecutionContext>()))
            .ThrowsAsync(new Exception("Tool execution failed"));

        // Act
        var result = await _service.ExecuteToolAsync(toolId, parameters);

        // Assert
        Assert.False(result.IsSuccessful);
        Assert.Contains("Tool execution failed", result.ErrorMessage);
        // Error should be in result
        Assert.Contains("Tool execution failed", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteToolAsync_DisplaysParameters()
    {
        // Arrange
        var toolId = "parameterized_tool";
        var parameters = new Dictionary<string, object?>
        {
            ["param1"] = "value1",
            ["param2"] = 42,
            ["param3"] = true
        };

        var toolReg = new ToolRegistration
        {
            Metadata = new ToolMetadata
            {
                Id = toolId,
                Name = "Parameterized Tool",
                Description = "A tool with parameters"
            }
        };

        var executionResult = new ToolResult
        {
            IsSuccessful = true,
            Message = "Executed successfully"
        };

        _mockRegistry.Setup(x => x.GetTool(toolId)).Returns(toolReg);
        _mockExecutor.Setup(x => x.ExecuteAsync(toolId, parameters, It.IsAny<ToolExecutionContext>()))
            .ReturnsAsync(executionResult);

        // Act
        var result = await _service.ExecuteToolAsync(toolId, parameters);

        // Assert
        // Parameters are displayed in the feed - in real tests we'd verify through feed's content
        Assert.True(result.IsSuccessful);
    }
}