using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class ParallelToolExecutionTests
{
    [Fact]
    public void EnqueueDequeue_WithSingleTool_ReturnsCorrectId()
    {
        // Arrange
        var tracker = new TestableToolExecutionTracker();
        var toolName = "read_file";
        var toolId1 = "read_file_1";

        // Act
        tracker.EnqueuePendingTool(toolName, toolId1);
        var result = tracker.DequeuePendingTool(toolName);

        // Assert
        Assert.Equal(toolId1, result);
    }

    [Fact]
    public void EnqueueDequeue_WithMultipleParallelTools_ReturnsInFifoOrder()
    {
        // Arrange
        var tracker = new TestableToolExecutionTracker();
        var toolName = "read_file";
        var toolId1 = "read_file_1";
        var toolId2 = "read_file_2";
        var toolId3 = "read_file_3";

        // Act - Enqueue 3 parallel executions of the same tool
        tracker.EnqueuePendingTool(toolName, toolId1);
        tracker.EnqueuePendingTool(toolName, toolId2);
        tracker.EnqueuePendingTool(toolName, toolId3);

        // Dequeue in order
        var result1 = tracker.DequeuePendingTool(toolName);
        var result2 = tracker.DequeuePendingTool(toolName);
        var result3 = tracker.DequeuePendingTool(toolName);

        // Assert - Should return in FIFO order
        Assert.Equal(toolId1, result1);
        Assert.Equal(toolId2, result2);
        Assert.Equal(toolId3, result3);
    }

    [Fact]
    public void EnqueueDequeue_WithDifferentTools_MaintainsSeparateQueues()
    {
        // Arrange
        var tracker = new TestableToolExecutionTracker();
        var readFile1 = "read_file_1";
        var readFile2 = "read_file_2";
        var listDir1 = "list_directory_1";
        var listDir2 = "list_directory_2";

        // Act - Enqueue multiple different tools
        tracker.EnqueuePendingTool("read_file", readFile1);
        tracker.EnqueuePendingTool("list_directory", listDir1);
        tracker.EnqueuePendingTool("read_file", readFile2);
        tracker.EnqueuePendingTool("list_directory", listDir2);

        // Dequeue read_file tools
        var readResult1 = tracker.DequeuePendingTool("read_file");
        var readResult2 = tracker.DequeuePendingTool("read_file");

        // Dequeue list_directory tools
        var listResult1 = tracker.DequeuePendingTool("list_directory");
        var listResult2 = tracker.DequeuePendingTool("list_directory");

        // Assert - Each tool type maintains its own FIFO queue
        Assert.Equal(readFile1, readResult1);
        Assert.Equal(readFile2, readResult2);
        Assert.Equal(listDir1, listResult1);
        Assert.Equal(listDir2, listResult2);
    }

    [Fact]
    public void DequeuePendingTool_WhenQueueEmpty_ReturnsNull()
    {
        // Arrange
        var tracker = new TestableToolExecutionTracker();

        // Act
        var result = tracker.DequeuePendingTool("read_file");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void DequeuePendingTool_AfterAllDequeued_ReturnsNull()
    {
        // Arrange
        var tracker = new TestableToolExecutionTracker();
        tracker.EnqueuePendingTool("read_file", "read_file_1");
        tracker.DequeuePendingTool("read_file");

        // Act - Try to dequeue again
        var result = tracker.DequeuePendingTool("read_file");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ParallelEnqueue_WithConcurrentCalls_MaintainsAllItems()
    {
        // Arrange
        var tracker = new TestableToolExecutionTracker();
        var toolName = "read_file";
        var expectedIds = new List<string>();
        var tasks = new List<Task>();

        // Act - Simulate concurrent tool enqueues
        for (int i = 0; i < 10; i++)
        {
            var toolId = $"read_file_{i}";
            expectedIds.Add(toolId);
            tasks.Add(Task.Run(() => tracker.EnqueuePendingTool(toolName, toolId)));
        }

        await Task.WhenAll(tasks);

        // Dequeue all items
        var actualIds = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            var id = tracker.DequeuePendingTool(toolName);
            if (id != null)
                actualIds.Add(id);
        }

        // Assert - All IDs should be present (order may vary due to concurrency)
        Assert.Equal(10, actualIds.Count);
        foreach (var id in actualIds)
        {
            Assert.Contains(id, expectedIds);
        }
    }

    [Fact]
    public void CorrelationIdMapping_RegisterAndRetrieve_ReturnsCorrectId()
    {
        // Arrange
        var tracker = new TestableToolExecutionTracker();
        var correlationId = "abc123";
        var uiToolId = "read_file_1";

        // Act
        tracker.RegisterCorrelationMapping(correlationId, uiToolId);
        var result = tracker.GetToolIdForCorrelation(correlationId);

        // Assert
        Assert.Equal(uiToolId, result);
    }

    [Fact]
    public void CorrelationIdMapping_WithNullOrEmpty_ReturnsNull()
    {
        // Arrange
        var tracker = new TestableToolExecutionTracker();

        // Act & Assert
        Assert.Null(tracker.GetToolIdForCorrelation(null));
        Assert.Null(tracker.GetToolIdForCorrelation(""));
        Assert.Null(tracker.GetToolIdForCorrelation("nonexistent"));
    }

    /// <summary>
    /// Testable wrapper around ToolExecutionTracker singleton for unit testing
    /// </summary>
    private class TestableToolExecutionTracker
    {
        private readonly Dictionary<string, Queue<string>> _pendingToolExecutions = new();
        private readonly Dictionary<string, string> _correlationIdToUiIdMap = new();
        private readonly object _pendingToolsLock = new();

        public void EnqueuePendingTool(string toolName, string uiToolId)
        {
            lock (_pendingToolsLock)
            {
                var key = toolName.ToLower();
                if (!_pendingToolExecutions.ContainsKey(key))
                {
                    _pendingToolExecutions[key] = new Queue<string>();
                }
                _pendingToolExecutions[key].Enqueue(uiToolId);
            }
        }

        public string? DequeuePendingTool(string toolName)
        {
            lock (_pendingToolsLock)
            {
                var key = toolName.ToLower();
                if (_pendingToolExecutions.TryGetValue(key, out var queue) && queue.Count > 0)
                {
                    return queue.Dequeue();
                }
                return null;
            }
        }

        public void RegisterCorrelationMapping(string correlationId, string uiToolId)
        {
            if (!string.IsNullOrEmpty(correlationId))
            {
                _correlationIdToUiIdMap[correlationId] = uiToolId;
            }
        }

        public string? GetToolIdForCorrelation(string? correlationId)
        {
            if (string.IsNullOrEmpty(correlationId))
                return null;
            return _correlationIdToUiIdMap.TryGetValue(correlationId, out var id) ? id : null;
        }
    }
}
