using Andy.Cli.Services;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Andy.Cli.Tests.Services;

public class StreamingToolCallAccumulatorTests
{
    private readonly JsonRepairService _jsonRepair = new();
    private readonly StreamingToolCallAccumulator _accumulator;

    public StreamingToolCallAccumulatorTests()
    {
        _accumulator = new StreamingToolCallAccumulator(_jsonRepair, NullLogger<StreamingToolCallAccumulator>.Instance);
    }

    [Fact]
    public void AccumulateChunk_SingleCompleteCall_ReturnsToolCall()
    {
        // Arrange
        var chunk1 = new StreamChunk
        {
            ToolCallIndex = 0,
            ToolCallName = "test_tool",
            ToolCallArguments = """{"param": "value"}"""
        };

        var chunk2 = new StreamChunk
        {
            IsFinished = true
        };

        // Act
        _accumulator.AccumulateChunk(chunk1);
        _accumulator.AccumulateChunk(chunk2);
        var result = _accumulator.GetCompletedCalls();

        // Assert
        Assert.Single(result);
        Assert.Equal("test_tool", result[0].ToolId);
        Assert.Equal("value", result[0].Parameters["param"]?.ToString());
    }

    [Fact]
    public void AccumulateChunk_FragmentedCall_AccumulatesCorrectly()
    {
        // Arrange
        var chunks = new[]
        {
            new StreamChunk { ToolCallIndex = 0, ToolCallName = "test_tool" },
            new StreamChunk { ToolCallIndex = 0, ToolCallArguments = """{"param":""" },
            new StreamChunk { ToolCallIndex = 0, ToolCallArguments = """ "value"}""" },
            new StreamChunk { IsFinished = true }
        };

        // Act
        foreach (var chunk in chunks)
        {
            _accumulator.AccumulateChunk(chunk);
        }
        var result = _accumulator.GetCompletedCalls();

        // Assert
        Assert.Single(result);
        Assert.Equal("test_tool", result[0].ToolId);
        Assert.Equal("value", result[0].Parameters["param"]?.ToString());
    }

    [Fact]
    public void AccumulateChunk_MultipleToolCalls_ReturnsAll()
    {
        // Arrange
        var chunks = new[]
        {
            new StreamChunk { ToolCallIndex = 0, ToolCallName = "tool1", ToolCallArguments = """{"a": 1}""" },
            new StreamChunk { ToolCallIndex = 1, ToolCallName = "tool2", ToolCallArguments = """{"b": 2}""" },
            new StreamChunk { IsFinished = true }
        };

        // Act
        foreach (var chunk in chunks)
        {
            _accumulator.AccumulateChunk(chunk);
        }
        var result = _accumulator.GetCompletedCalls();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.ToolId == "tool1");
        Assert.Contains(result, r => r.ToolId == "tool2");
    }

    [Fact]
    public void AccumulateChunk_MalformedJson_ReturnsRepairedCall()
    {
        // Arrange
        var chunk1 = new StreamChunk
        {
            ToolCallIndex = 0,
            ToolCallName = "test_tool",
            ToolCallArguments = """{param: "value", extra: 123,}""" // Missing quotes, trailing comma
        };

        var chunk2 = new StreamChunk
        {
            IsFinished = true
        };

        // Act
        _accumulator.AccumulateChunk(chunk1);
        _accumulator.AccumulateChunk(chunk2);
        var result = _accumulator.GetCompletedCalls();

        // Assert
        Assert.Single(result);
        Assert.Equal("test_tool", result[0].ToolId);
        Assert.Equal("value", result[0].Parameters["param"]?.ToString());
        // Handle JsonElement for numeric values
        if (result[0].Parameters["extra"] is System.Text.Json.JsonElement element)
        {
            Assert.Equal(123, element.GetInt32());
        }
        else
        {
            Assert.Equal(123L, Convert.ToInt64(result[0].Parameters["extra"]));
        }
    }

    [Fact]
    public void Clear_RemovesAllAccumulatedCalls()
    {
        // Arrange
        _accumulator.AccumulateChunk(new StreamChunk
        {
            ToolCallIndex = 0,
            ToolCallName = "test_tool",
            ToolCallArguments = """{"param": "value"}"""
        });

        // Act
        _accumulator.Clear();
        var result = _accumulator.GetCompletedCalls();

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void GetStats_ReturnsCorrectStatistics()
    {
        // Arrange
        _accumulator.AccumulateChunk(new StreamChunk
        {
            ToolCallIndex = 0,
            ToolCallName = "tool1",
            ToolCallArguments = """{"a": 1}"""
        });
        _accumulator.AccumulateChunk(new StreamChunk
        {
            ToolCallIndex = 1,
            ToolCallName = "tool2"
        });
        _accumulator.AccumulateChunk(new StreamChunk { IsFinished = true });

        // Act
        var stats = _accumulator.GetStats();

        // Assert
        Assert.Equal(2, stats.TotalCalls);
        Assert.Equal(2, stats.CompleteCalls);
        Assert.Equal(0, stats.IncompleteCalls);
        Assert.Equal(2, stats.TotalChunks); // Only tool chunks count, not the finish chunk
    }

    [Fact]
    public void GetAllCalls_IncludeIncomplete_ReturnsAllCalls()
    {
        // Arrange
        _accumulator.AccumulateChunk(new StreamChunk
        {
            ToolCallIndex = 0,
            ToolCallName = "complete_tool",
            ToolCallArguments = """{"param": "value"}"""
        });
        _accumulator.AccumulateChunk(new StreamChunk
        {
            ToolCallIndex = 1,
            ToolCallName = "incomplete_tool"
            // No arguments yet - this is incomplete
        });
        _accumulator.AccumulateChunk(new StreamChunk { IsFinished = true }); // Mark stream as finished

        // Act
        var allCalls = _accumulator.GetAllCalls(includeIncomplete: true);
        var completeCalls = _accumulator.GetAllCalls(includeIncomplete: false);

        // Assert
        Assert.Equal(2, allCalls.Count);
        Assert.Equal(2, completeCalls.Count); // Both have minimum data (name) and are marked complete after finish
    }
}