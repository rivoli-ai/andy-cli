using System.Collections.Generic;
using Andy.Cli.Services;
using Andy.Tools.Core;
using Xunit;
using Xunit.Abstractions;

namespace Andy.Cli.Tests.Services;

/// <summary>
/// Tests for parameter name mapping functionality
/// </summary>
public class ParameterMapperTests
{
    private readonly ITestOutputHelper _output;

    public ParameterMapperTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void MapParameters_MapsPathToFilePathForReadFile()
    {
        // Arrange
        var toolId = "read_file";
        var inputParams = new Dictionary<string, object?>
        {
            ["path"] = "/Users/test/file.txt"
        };
        var metadata = new ToolMetadata
        {
            Id = toolId,
            Parameters = new[]
            {
                new ToolParameter { Name = "file_path", Required = true },
                new ToolParameter { Name = "encoding", Required = false }
            }
        };

        // Act
        var mapped = ParameterMapper.MapParameters(toolId, inputParams, metadata);

        // Assert
        Assert.True(mapped.ContainsKey("file_path"));
        Assert.Equal("/Users/test/file.txt", mapped["file_path"]);
        Assert.False(mapped.ContainsKey("path"));
        
        _output.WriteLine($"Successfully mapped 'path' to 'file_path': {mapped["file_path"]}");
    }

    [Fact]
    public void MapParameters_MapsMultipleParametersForWriteFile()
    {
        // Arrange
        var toolId = "write_file";
        var inputParams = new Dictionary<string, object?>
        {
            ["path"] = "/tmp/test.txt",
            ["data"] = "Hello World"
        };
        var metadata = new ToolMetadata
        {
            Id = toolId,
            Parameters = new[]
            {
                new ToolParameter { Name = "file_path", Required = true },
                new ToolParameter { Name = "content", Required = true }
            }
        };

        // Act
        var mapped = ParameterMapper.MapParameters(toolId, inputParams, metadata);

        // Assert
        Assert.True(mapped.ContainsKey("file_path"));
        Assert.Equal("/tmp/test.txt", mapped["file_path"]);
        Assert.True(mapped.ContainsKey("content"));
        Assert.Equal("Hello World", mapped["content"]);
        
        _output.WriteLine($"Mapped 'path' -> 'file_path' and 'data' -> 'content'");
    }

    [Fact]
    public void MapParameters_KeepsCorrectParameterNames()
    {
        // Arrange
        var toolId = "read_file";
        var inputParams = new Dictionary<string, object?>
        {
            ["file_path"] = "/correct/path.txt",
            ["encoding"] = "utf-8"
        };
        var metadata = new ToolMetadata
        {
            Id = toolId,
            Parameters = new[]
            {
                new ToolParameter { Name = "file_path", Required = true },
                new ToolParameter { Name = "encoding", Required = false }
            }
        };

        // Act
        var mapped = ParameterMapper.MapParameters(toolId, inputParams, metadata);

        // Assert
        Assert.Equal(2, mapped.Count);
        Assert.Equal("/correct/path.txt", mapped["file_path"]);
        Assert.Equal("utf-8", mapped["encoding"]);
        
        _output.WriteLine("Parameters already correct - no mapping needed");
    }

    [Fact]
    public void MapParameters_HandlesCopyFileAliases()
    {
        // Arrange
        var toolId = "copy_file";
        var inputParams = new Dictionary<string, object?>
        {
            ["src"] = "/source.txt",
            ["dest"] = "/destination.txt"
        };
        var metadata = new ToolMetadata
        {
            Id = toolId,
            Parameters = new[]
            {
                new ToolParameter { Name = "source_path", Required = true },
                new ToolParameter { Name = "destination_path", Required = true }
            }
        };

        // Act
        var mapped = ParameterMapper.MapParameters(toolId, inputParams, metadata);

        // Assert
        Assert.True(mapped.ContainsKey("source_path"));
        Assert.Equal("/source.txt", mapped["source_path"]);
        Assert.True(mapped.ContainsKey("destination_path"));
        Assert.Equal("/destination.txt", mapped["destination_path"]);
        
        _output.WriteLine("Mapped 'src' -> 'source_path' and 'dest' -> 'destination_path'");
    }

    [Fact]
    public void MapParameters_HandlesListDirectoryAliases()
    {
        // Arrange
        var toolId = "list_directory";
        var inputParams = new Dictionary<string, object?>
        {
            ["dir"] = "/tmp"
        };
        var metadata = new ToolMetadata
        {
            Id = toolId,
            Parameters = new[]
            {
                new ToolParameter { Name = "path", Required = true }
            }
        };

        // Act
        var mapped = ParameterMapper.MapParameters(toolId, inputParams, metadata);

        // Assert
        Assert.True(mapped.ContainsKey("path"));
        Assert.Equal("/tmp", mapped["path"]);
        Assert.False(mapped.ContainsKey("dir"));
        
        _output.WriteLine("Mapped 'dir' -> 'path' for list_directory");
    }

    [Fact]
    public void MapParameters_PreservesUnknownParametersForToolsWithoutMapping()
    {
        // Arrange
        var toolId = "unknown_tool";
        var inputParams = new Dictionary<string, object?>
        {
            ["custom_param"] = "value"
        };
        var metadata = new ToolMetadata
        {
            Id = toolId,
            Parameters = new[]
            {
                new ToolParameter { Name = "expected_param", Required = false }
            }
        };

        // Act
        var mapped = ParameterMapper.MapParameters(toolId, inputParams, metadata);

        // Assert
        // The unknown parameter is preserved for the tool to handle
        Assert.True(mapped.ContainsKey("custom_param"));
        Assert.Equal("value", mapped["custom_param"]);
        
        _output.WriteLine("Unknown parameter preserved for tool to handle");
    }

    [Fact]
    public void MapParameters_ConvertsStringToArrayWhenExpected()
    {
        // Arrange
        var toolId = "system_info";
        var inputParams = new Dictionary<string, object?>
        {
            ["categories"] = "repo"  // String instead of array
        };
        var metadata = new ToolMetadata
        {
            Id = toolId,
            Parameters = new[]
            {
                new ToolParameter { Name = "categories", Type = "array", Required = false }
            }
        };

        // Act
        var mapped = ParameterMapper.MapParameters(toolId, inputParams, metadata);

        // Assert
        Assert.True(mapped.ContainsKey("categories"));
        var value = mapped["categories"];
        Assert.NotNull(value);
        Assert.IsType<string[]>(value);
        var array = (string[])value;
        Assert.Single(array);
        Assert.Equal("repo", array[0]);
        
        _output.WriteLine($"Converted string 'repo' to array: [{string.Join(", ", array)}]");
    }

    [Fact]
    public void MapParameters_HandlesCommaSeparatedStringAsArray()
    {
        // Arrange
        var toolId = "test_tool";
        var inputParams = new Dictionary<string, object?>
        {
            ["tags"] = "foo, bar, baz"  // Comma-separated string
        };
        var metadata = new ToolMetadata
        {
            Id = toolId,
            Parameters = new[]
            {
                new ToolParameter { Name = "tags", Type = "array", Required = false }
            }
        };

        // Act
        var mapped = ParameterMapper.MapParameters(toolId, inputParams, metadata);

        // Assert
        Assert.True(mapped.ContainsKey("tags"));
        var value = mapped["tags"];
        Assert.NotNull(value);
        Assert.IsType<string[]>(value);
        var array = (string[])value;
        Assert.Equal(3, array.Length);
        Assert.Equal("foo", array[0]);
        Assert.Equal("bar", array[1]);
        Assert.Equal("baz", array[2]);
        
        _output.WriteLine($"Converted comma-separated string to array: [{string.Join(", ", array)}]");
    }

    [Fact]
    public void MapParameters_PreservesExistingArrays()
    {
        // Arrange
        var toolId = "test_tool";
        var originalArray = new[] { "item1", "item2" };
        var inputParams = new Dictionary<string, object?>
        {
            ["items"] = originalArray
        };
        var metadata = new ToolMetadata
        {
            Id = toolId,
            Parameters = new[]
            {
                new ToolParameter { Name = "items", Type = "array", Required = false }
            }
        };

        // Act
        var mapped = ParameterMapper.MapParameters(toolId, inputParams, metadata);

        // Assert
        Assert.True(mapped.ContainsKey("items"));
        Assert.Same(originalArray, mapped["items"]);
        
        _output.WriteLine("Array parameter preserved as-is");
    }

    [Fact]
    public void MapParameters_ConvertsBooleanTypes()
    {
        // Arrange
        var toolId = "test_tool";
        var inputParams = new Dictionary<string, object?>
        {
            ["flag1"] = "true",
            ["flag2"] = "yes",
            ["flag3"] = 1,
            ["flag4"] = "false"
        };
        var metadata = new ToolMetadata
        {
            Id = toolId,
            Parameters = new[]
            {
                new ToolParameter { Name = "flag1", Type = "boolean", Required = false },
                new ToolParameter { Name = "flag2", Type = "bool", Required = false },
                new ToolParameter { Name = "flag3", Type = "boolean", Required = false },
                new ToolParameter { Name = "flag4", Type = "bool", Required = false }
            }
        };

        // Act
        var mapped = ParameterMapper.MapParameters(toolId, inputParams, metadata);

        // Assert
        Assert.Equal(true, mapped["flag1"]);
        Assert.Equal(true, mapped["flag2"]);
        Assert.Equal(true, mapped["flag3"]);
        Assert.Equal(false, mapped["flag4"]);
        
        _output.WriteLine("Boolean conversions successful");
    }
}