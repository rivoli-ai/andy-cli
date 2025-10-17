using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Andy.Tools.Core;
using Andy.Cli.Widgets;
using Andy.Cli.Instrumentation;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services
{
    /// <summary>
    /// Wraps IToolExecutor to update the UI when tools are executed
    /// </summary>
    public class UiUpdatingToolExecutor : IToolExecutor
    {
        private readonly IToolExecutor _innerExecutor;
        private readonly ILogger<UiUpdatingToolExecutor>? _logger;

        public event EventHandler<ToolExecutionStartedEventArgs>? ExecutionStarted
        {
            add { _innerExecutor.ExecutionStarted += value; }
            remove { _innerExecutor.ExecutionStarted -= value; }
        }

        public event EventHandler<ToolExecutionCompletedEventArgs>? ExecutionCompleted
        {
            add { _innerExecutor.ExecutionCompleted += value; }
            remove { _innerExecutor.ExecutionCompleted -= value; }
        }

        public event EventHandler<SecurityViolationEventArgs>? SecurityViolation
        {
            add { _innerExecutor.SecurityViolation += value; }
            remove { _innerExecutor.SecurityViolation -= value; }
        }

        public UiUpdatingToolExecutor(IToolExecutor innerExecutor, ILogger<UiUpdatingToolExecutor>? logger = null)
        {
            _innerExecutor = innerExecutor;
            _logger = logger;
        }

        public async Task<ToolExecutionResult> ExecuteAsync(string toolId, Dictionary<string, object?> parameters, ToolExecutionContext? context = null)
        {
            _logger?.LogWarning("[UI_EXECUTOR] Executing tool {ToolId} with {ParamCount} parameters",
                toolId, parameters?.Count ?? 0);

            // Ensure we have a context with a correlation ID
            context ??= new ToolExecutionContext();

            // If no correlation ID is set, create a unique one for this execution
            if (string.IsNullOrEmpty(context.CorrelationId))
            {
                context.CorrelationId = Guid.NewGuid().ToString("N")[..8];
            }

            _logger?.LogWarning("[UI_EXECUTOR] Using correlation ID {CorrelationId} for {ToolId}",
                context.CorrelationId, toolId);

            // Find the UI tool ID for this tool - try multiple strategies:
            // 1. Dequeue the next pending execution for this tool (handles parallel executions correctly)
            // 2. Check correlation ID mapping (if agent set a correlation ID we registered)
            // 3. Fall back to tool name mapping (last resort, may be wrong for parallel executions)
            var uiToolId = ToolExecutionTracker.Instance.DequeuePendingTool(toolId)
                        ?? ToolExecutionTracker.Instance.GetToolIdForCorrelation(context.CorrelationId)
                        ?? ToolExecutionTracker.Instance.GetToolIdForName(toolId);

            _logger?.LogWarning("[UI_EXECUTOR] Found UI ID {UiId} for tool {ToolId} with correlation {CorrelationId}",
                uiToolId, toolId, context.CorrelationId);

            // CRITICAL: Track the tool start so we can track completion later
            if (!string.IsNullOrEmpty(uiToolId))
            {
                // Track the start of this tool execution
                ToolExecutionTracker.Instance.TrackToolStart(uiToolId, toolId, parameters);
                _logger?.LogWarning("[UI_EXECUTOR] Tracked tool start for {UiId}", uiToolId);

                // INSTRUMENTATION: Publish tool execution start with actual parameters
                var toolExecutionStartEvent = new ToolExecutionStartEvent
                {
                    ToolName = toolId,
                    ToolId = uiToolId,
                    Parameters = parameters ?? new Dictionary<string, object?>()
                };
                InstrumentationHub.Instance.Publish(toolExecutionStartEvent);
            }

            // Update the UI with the actual parameters
            var feedView = ToolExecutionTracker.Instance.GetFeedView();
            if (feedView != null)
            {
                if (!string.IsNullOrEmpty(uiToolId) && parameters != null)
                {
                    _logger?.LogWarning("[UI_EXECUTOR] Updating UI tool {UiToolId} with real parameters", uiToolId);

                    // Update ONLY this specific tool by exact ID (critical for parallel executions)
                    feedView.UpdateToolByExactId(uiToolId, parameters);
                }
            }

            // Execute the actual tool (parameters cannot be null here based on interface contract)
            var result = await _innerExecutor.ExecuteAsync(toolId, parameters ?? new Dictionary<string, object?>(), context);

            // Track completion and update UI with result
            // IMPORTANT: We must track completion BEFORE SimpleAssistantService tries to read it
            // Store the result immediately in the tracker
            // Use the SAME uiToolId we got from the queue - don't look it up again!
            if (!string.IsNullOrEmpty(uiToolId))
            {
                // Format a meaningful result message
                string resultMessage = result.Message ?? "";

                // The actual result is in result.Data for successful operations
                // The Message field often just has a generic success message
                if (result.IsSuccessful && result.Data != null)
                {
                    // First priority: if Data is directly a string, that's likely the result
                    if (result.Data is string strData && !string.IsNullOrEmpty(strData))
                    {
                        resultMessage = strData;
                    }
                    // Check if it's an anonymous type (like datetime tool results)
                    else if (result.Data.GetType().Name.Contains("AnonymousType"))
                    {
                        // Try to extract formatted field from anonymous type
                        var formattedProp = result.Data.GetType().GetProperty("formatted");
                        if (formattedProp != null)
                        {
                            var formattedValue = formattedProp.GetValue(result.Data);
                            if (formattedValue != null)
                            {
                                resultMessage = formattedValue.ToString() ?? "";
                                _logger?.LogWarning("[UI_EXECUTOR] Extracted formatted from anonymous type: {Value}", resultMessage);
                            }
                        }

                        // If no formatted field, try to get any meaningful string representation
                        if (string.IsNullOrEmpty(resultMessage))
                        {
                            // Convert anonymous type to dictionary for easier processing
                            var props = result.Data.GetType().GetProperties();
                            foreach (var prop in props)
                            {
                                if (prop.Name == "formatted" || prop.Name == "output" || prop.Name == "result")
                                {
                                    var value = prop.GetValue(result.Data);
                                    if (value != null)
                                    {
                                        resultMessage = value.ToString() ?? "";
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    // Otherwise try to extract from dictionary
                    else if (result.Data is Dictionary<string, object?> dataDict)
                    {
                        // Tool-specific extraction based on tool ID
                        if (toolId.Contains("read_file"))
                        {
                            if (dataDict.TryGetValue("content", out var content) && content != null)
                            {
                                var lines = content.ToString()?.Split('\n').Length ?? 0;
                                resultMessage = $"{lines} lines read";
                            }
                            else if (dataDict.TryGetValue("metadata", out var meta) && meta is Dictionary<string, object?> metaDict)
                            {
                                if (metaDict.TryGetValue("line_count", out var lineCount))
                                    resultMessage = $"{lineCount} lines read";
                            }
                        }
                        else if (toolId.Contains("search_text") || toolId.Contains("search_files"))
                        {
                            if (dataDict.TryGetValue("count", out var count))
                            {
                                resultMessage = $"{count} matches found";
                            }
                            else if (dataDict.TryGetValue("items", out var items) && items is System.Collections.IList list)
                            {
                                resultMessage = $"{list.Count} matches found";
                            }
                        }
                        else if (toolId.Contains("code_index"))
                        {
                            // Extract detailed information about the code index query
                            var queryType = dataDict.GetValueOrDefault("query_type")?.ToString() ?? "unknown";

                            if (dataDict.TryGetValue("data", out var data) && data != null)
                            {
                                // Try to convert to dictionary if it's not already
                                Dictionary<string, object?>? dataContent = data as Dictionary<string, object?>;

                                if (dataContent == null && data is System.Collections.IDictionary dict)
                                {
                                    dataContent = new Dictionary<string, object?>();
                                    foreach (var key in dict.Keys)
                                    {
                                        dataContent[key.ToString() ?? ""] = dict[key];
                                    }
                                }

                                if (dataContent != null)
                                {

                                    // Different result formats based on query type
                                    switch (queryType)
                                {
                                    case "structure":
                                        // Structure query: show namespace and file counts
                                        var scope = dataContent.GetValueOrDefault("scope")?.ToString() ?? "all";
                                        var structure = dataContent.GetValueOrDefault("structure");

                                        // Handle ProjectStructure type directly
                                        if (structure != null)
                                        {
                                            var structureType = structure.GetType();
                                            var namespaceCount = 0;
                                            var fileCount = 0;

                                            // Try to get Namespaces property
                                            var namespacesProp = structureType.GetProperty("Namespaces");
                                            if (namespacesProp != null)
                                            {
                                                var namespaces = namespacesProp.GetValue(structure);
                                                if (namespaces is System.Collections.IList nsList)
                                                {
                                                    namespaceCount = nsList.Count;
                                                }
                                            }

                                            // Try to get Files property
                                            var filesProp = structureType.GetProperty("Files");
                                            if (filesProp != null)
                                            {
                                                var files = filesProp.GetValue(structure);
                                                if (files is System.Collections.IList filesList)
                                                {
                                                    fileCount = filesList.Count;
                                                }
                                            }

                                            resultMessage = $"Structure indexed: {namespaceCount} namespaces, {fileCount} files (scope: {scope})";
                                        }
                                        else
                                        {
                                            resultMessage = $"Structure indexed for scope: {scope}";
                                        }
                                        break;

                                    case "symbols":
                                        // Symbol search: show count and query pattern
                                        var pattern = dataContent.GetValueOrDefault("query")?.ToString() ?? "*";
                                        var symbolScope = dataContent.GetValueOrDefault("scope")?.ToString() ?? "all";
                                        var count = 0;

                                        if (dataContent.TryGetValue("count", out var countObj) && countObj != null)
                                        {
                                            count = Convert.ToInt32(countObj);
                                        }

                                        resultMessage = $"Found {count} symbols matching '{pattern}' (scope: {symbolScope})";
                                        break;

                                    case "references":
                                        // Reference search: show count and symbol name
                                        var symbol = dataContent.GetValueOrDefault("symbol")?.ToString() ?? "unknown";
                                        var refScope = dataContent.GetValueOrDefault("scope")?.ToString() ?? "all";
                                        var refCount = 0;

                                        if (dataContent.TryGetValue("count", out var refCountObj) && refCountObj != null)
                                        {
                                            refCount = Convert.ToInt32(refCountObj);
                                        }

                                        resultMessage = $"Found {refCount} references to '{symbol}' (scope: {refScope})";
                                        break;

                                    case "hierarchy":
                                        // Class hierarchy: show class name
                                        var className = dataContent.GetValueOrDefault("className")?.ToString() ?? "unknown";
                                        resultMessage = $"Retrieved hierarchy for class '{className}'";
                                        break;

                                    default:
                                        resultMessage = $"Code index query completed: {queryType}";
                                        break;
                                    }
                                }
                            }

                            if (string.IsNullOrEmpty(resultMessage))
                            {
                                if (dataDict.TryGetValue("query_type", out var qt))
                                {
                                    resultMessage = $"Code index query: {qt}";
                                }
                                else
                                {
                                    resultMessage = "Code repository indexed";
                                }
                            }
                        }
                        else if (toolId.Contains("list_directory"))
                        {
                            if (dataDict.TryGetValue("entries", out var entries) && entries is System.Collections.IList entryList)
                            {
                                resultMessage = $"{entryList.Count} items";
                            }
                        }
                        else if (toolId.Contains("datetime"))
                        {
                            // For datetime, the result is often the direct Data if it's a string
                            // But sometimes it's in a nested structure
                            _logger?.LogWarning("[UI_EXECUTOR] datetime tool Data type: {Type}, Data: {Data}",
                                result.Data?.GetType().Name, result.Data);

                            // Check if Data is directly a string (for simple operations)
                            if (result.Data is string dateStr && !string.IsNullOrEmpty(dateStr))
                            {
                                resultMessage = dateStr;
                            }
                            // Otherwise look for specific keys in the dictionary
                            else
                            {
                                // Try multiple possible keys for datetime result
                                string[] dateTimeKeys = { "formatted", "output", "result", "date_time", "value" };
                                foreach (var key in dateTimeKeys)
                                {
                                    if (dataDict.TryGetValue(key, out var val) && val != null)
                                    {
                                        var valStr = val.ToString();
                                        if (!string.IsNullOrEmpty(valStr) && !valStr.StartsWith("System."))
                                        {
                                            resultMessage = valStr;
                                            _logger?.LogWarning("[UI_EXECUTOR] Found datetime result in '{Key}': {Value}",
                                                key, valStr);
                                            break;
                                        }
                                    }
                                }
                            }

                            // If still no result, log what we have
                            if (string.IsNullOrEmpty(resultMessage) || resultMessage == result.Message)
                            {
                                _logger?.LogWarning("[UI_EXECUTOR] No datetime result found. Keys: {Keys}, Values: {Values}",
                                    string.Join(", ", dataDict.Keys),
                                    string.Join(", ", dataDict.Take(5).Select(kvp => $"{kvp.Key}={kvp.Value}")));
                            }
                        }
                        else if (string.IsNullOrEmpty(resultMessage))
                        {
                            // Generic extraction for other tools (only if no result message set yet)
                            string[] resultKeys = { "output", "result", "data", "formatted", "content", "value", "message" };
                            foreach (var key in resultKeys)
                            {
                                if (dataDict.TryGetValue(key, out var val) && val != null)
                                {
                                    var valStr = val.ToString();
                                    if (!string.IsNullOrEmpty(valStr))
                                    {
                                        resultMessage = valStr;
                                        break;
                                    }
                                }
                            }
                        }

                        // If still no result and only one field, use it
                        if ((string.IsNullOrEmpty(resultMessage) || resultMessage == result.Message) && dataDict.Count == 1)
                        {
                            var singleValue = dataDict.Values.FirstOrDefault();
                            if (singleValue != null)
                            {
                                resultMessage = singleValue.ToString() ?? resultMessage;
                            }
                        }
                    }
                    else if (result.Data.GetType().IsValueType)
                    {
                        resultMessage = result.Data.ToString() ?? resultMessage;
                    }
                }
                else if (!result.IsSuccessful)
                {
                    // For failed operations, try to extract detailed error information
                    resultMessage = result.Message ?? "";

                    // Log the raw error data for debugging
                    _logger?.LogError("[UI_EXECUTOR] Tool {ToolId} failed. Message: '{Message}', Data type: {DataType}, Data: {Data}",
                        toolId, result.Message, result.Data?.GetType().Name ?? "null", result.Data);

                    // If no message but we have data, try to extract error details
                    if (string.IsNullOrEmpty(resultMessage) && result.Data != null)
                    {
                        if (result.Data is Dictionary<string, object?> errorDict)
                        {
                            // Log all keys in the error dictionary for debugging
                            _logger?.LogError("[UI_EXECUTOR] Error dictionary keys: {Keys}",
                                string.Join(", ", errorDict.Keys));

                            // Try to extract error message from common error fields
                            string[] errorKeys = { "error", "message", "error_message", "details", "exception", "reason", "description" };
                            foreach (var key in errorKeys)
                            {
                                if (errorDict.TryGetValue(key, out var errorVal) && errorVal != null)
                                {
                                    var errorStr = errorVal.ToString();
                                    if (!string.IsNullOrEmpty(errorStr) && !errorStr.StartsWith("System."))
                                    {
                                        resultMessage = errorStr;
                                        _logger?.LogError("[UI_EXECUTOR] Extracted error from key '{Key}': {Error}", key, errorStr);
                                        break;
                                    }
                                }
                            }

                            // If no standard error field found, try to build a message from available data
                            if (string.IsNullOrEmpty(resultMessage) && errorDict.Count > 0)
                            {
                                var firstEntry = errorDict.First();
                                resultMessage = $"{firstEntry.Key}: {firstEntry.Value}";
                            }
                        }
                        else if (result.Data is string errorStr && !string.IsNullOrEmpty(errorStr))
                        {
                            resultMessage = errorStr;
                        }
                        else
                        {
                            // Try to get a string representation of the data
                            var dataStr = result.Data.ToString();
                            if (!string.IsNullOrEmpty(dataStr) && !dataStr.StartsWith("System."))
                            {
                                resultMessage = dataStr;
                            }
                        }
                    }

                    // If still no message, use a generic fallback
                    if (string.IsNullOrEmpty(resultMessage))
                    {
                        resultMessage = "Operation failed";
                        // Try to add tool-specific context
                        if (parameters != null && parameters.Count > 0)
                        {
                            var paramSummary = string.Join(", ", parameters
                                .Where(p => !p.Key.StartsWith("__"))
                                .Take(2)
                                .Select(p => $"{p.Key}={p.Value}"));
                            if (!string.IsNullOrEmpty(paramSummary))
                            {
                                resultMessage = $"Operation failed ({paramSummary})";
                            }
                        }
                    }

                    _logger?.LogWarning("[UI_EXECUTOR] Tool {ToolId} failed with message: {Message}", toolId, resultMessage);
                }

                // Log what we extracted
                _logger?.LogWarning("[UI_EXECUTOR] Extracted result for {ToolId}: '{Result}' from Data type {DataType}",
                    toolId, resultMessage, result.Data?.GetType().Name ?? "null");

                _logger?.LogWarning("[UI_EXECUTOR] Tracking completion for {UiToolId} with result: '{Result}'",
                    uiToolId, resultMessage);

                ToolExecutionTracker.Instance.TrackToolComplete(uiToolId, result.IsSuccessful, resultMessage, result.Data);

                // INSTRUMENTATION: Publish event when tool result is about to be sent back to LLM
                var toolResultToLlmEvent = new ToolResultToLlmEvent
                {
                    ToolName = toolId,
                    ToolId = uiToolId,
                    Success = result.IsSuccessful,
                    Result = resultMessage,
                    ResultLength = resultMessage?.Length ?? 0,
                    HasStructuredData = result.Data != null,
                    DataType = result.Data?.GetType().Name,
                    StructuredData = result.Data
                };
                InstrumentationHub.Instance.Publish(toolResultToLlmEvent);
            }

            return result;
        }

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
        {
            return _innerExecutor.ExecuteAsync(request);
        }

        public Task<IList<string>> ValidateExecutionRequestAsync(ToolExecutionRequest request)
        {
            return _innerExecutor.ValidateExecutionRequestAsync(request);
        }

        public Task<ToolResourceUsage?> EstimateResourceUsageAsync(string toolId, Dictionary<string, object?> parameters)
        {
            return _innerExecutor.EstimateResourceUsageAsync(toolId, parameters);
        }

        public Task<int> CancelExecutionsAsync(string? toolId = null)
        {
            return _innerExecutor.CancelExecutionsAsync(toolId ?? string.Empty);
        }

        public IReadOnlyList<RunningExecutionInfo> GetRunningExecutions()
        {
            return _innerExecutor.GetRunningExecutions();
        }

        public ToolExecutionStatistics GetStatistics()
        {
            return _innerExecutor.GetStatistics();
        }
    }
}