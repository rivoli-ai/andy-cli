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
    public void NormalizeParameterTypes_CoercesBareStringToArray_ForSearchTextFilePatterns()
    {
        // Reproduces the real failure: the model passed file_patterns as a bare string "*.cs"
        // instead of ["*.cs"], which the framework validator rejects with PARAMETER_TYPE_MISMATCH
        // ("must be an array") before search_text ever runs.
        var inputParams = new Dictionary<string, object?>
        {
            ["search_pattern"] = "transparent",
            ["file_patterns"] = "*.cs"
        };
        var metadata = new ToolMetadata
        {
            Id = "search_text",
            Parameters = new[]
            {
                new ToolParameter { Name = "search_pattern", Type = "string", Required = true },
                new ToolParameter { Name = "file_patterns", Type = "array", Required = false }
            }
        };

        var normalized = ParameterMapper.NormalizeParameterTypes(inputParams, metadata);

        // file_patterns is now an array...
        var filePatterns = normalized["file_patterns"];
        Assert.IsType<string[]>(filePatterns);
        Assert.Equal(new[] { "*.cs" }, (string[])filePatterns!);
        // ...and the scalar string parameter is untouched.
        Assert.Equal("transparent", normalized["search_pattern"]);
    }

    [Fact]
    public void NormalizeParameterTypes_DoesNotRenameOrDropUnknownParameters()
    {
        // Value-only coercion must never rename or fuzzy-match parameter names.
        var inputParams = new Dictionary<string, object?>
        {
            ["totally_unknown"] = "value"
        };
        var metadata = new ToolMetadata
        {
            Id = "search_text",
            Parameters = new[]
            {
                new ToolParameter { Name = "search_pattern", Type = "string", Required = true }
            }
        };

        var normalized = ParameterMapper.NormalizeParameterTypes(inputParams, metadata);

        Assert.True(normalized.ContainsKey("totally_unknown"));
        Assert.Equal("value", normalized["totally_unknown"]);
        Assert.False(normalized.ContainsKey("search_pattern"));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArrayRescue_ConvertsNestedJsonToClrValues(bool normalizeOnly)
    {
        var input = new Dictionary<string, object?>
        {
            ["aggregations"] = """[{"column":"rating","function":"avg","alias":"avg_rating","options":{"values":[1,2.5,true,false,null,{"name":"nested"}]}}]"""
        };
        var metadata = new ToolMetadata
        {
            Id = "dataframe_group_by",
            Parameters = new[] { new ToolParameter { Name = "aggregations", Type = "array" } }
        };

        var result = normalizeOnly
            ? ParameterMapper.NormalizeParameterTypes(input, metadata)
            : ParameterMapper.MapParameters(metadata.Id, input, metadata);

        var aggregation = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            Assert.Single(Assert.IsType<object[]>(result["aggregations"])));
        Assert.Equal("rating", aggregation["column"]);
        Assert.Equal("avg", aggregation["function"]);
        Assert.Equal("avg_rating", aggregation["alias"]);
        var options = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(aggregation["options"]);
        var values = Assert.IsType<object[]>(options["values"]);
        Assert.Equal(1, Assert.IsType<int>(values[0]));
        Assert.Equal(2.5, Assert.IsType<double>(values[1]));
        Assert.True(Assert.IsType<bool>(values[2]));
        Assert.False(Assert.IsType<bool>(values[3]));
        Assert.Null(values[4]);
        Assert.Equal("nested", Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(values[5])["name"]);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""["rating","avg","123","true","{literal}"]""")]
    public void ArrayRescue_PreservesEmptyArraysAndLiteralStrings(string json)
    {
        var result = ParameterMapper.NormalizeParameterTypes(
            new Dictionary<string, object?> { ["items"] = json },
            new ToolMetadata
            {
                Id = "test",
                Parameters = new[] { new ToolParameter { Name = "items", Type = "array" } }
            });

        var expected = json == "[]" ? System.Array.Empty<string>() : new[] { "rating", "avg", "123", "true", "{literal}" };
        var actual = Assert.IsType<object[]>(result["items"]);
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], Assert.IsType<string>(actual[i]));
    }

    [Fact]
    public void ArrayRescue_PreservesMalformedJsonFallback()
    {
        const string malformed = "[{not valid json}]";
        var result = ParameterMapper.NormalizeParameterTypes(
            new Dictionary<string, object?> { ["items"] = malformed },
            new ToolMetadata
            {
                Id = "test",
                Parameters = new[] { new ToolParameter { Name = "items", Type = "array" } }
            });

        Assert.Equal(malformed, Assert.Single(Assert.IsType<object[]>(result["items"])));
    }
}