using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Andy.Tools.Core;
using Andy.Cli.Widgets;
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

            // Find the UI tool ID for this tool
            var uiToolId = ToolExecutionTracker.Instance.GetToolIdForName(toolId);
            _logger?.LogWarning("[UI_EXECUTOR] Found UI ID {UiId} for tool {ToolId}", uiToolId, toolId);

            // CRITICAL: Track the tool start so we can track completion later
            if (!string.IsNullOrEmpty(uiToolId))
            {
                // Track the start of this tool execution
                ToolExecutionTracker.Instance.TrackToolStart(uiToolId, toolId, parameters);
                _logger?.LogWarning("[UI_EXECUTOR] Tracked tool start for {UiId}", uiToolId);
            }

            // Update the UI with the actual parameters
            var feedView = ToolExecutionTracker.Instance.GetFeedView();
            if (feedView != null)
            {
                if (!string.IsNullOrEmpty(uiToolId) && parameters != null)
                {
                    _logger?.LogWarning("[UI_EXECUTOR] Updating UI with real parameters");

                    // Update the UI with real parameters
                    feedView.UpdateToolByExactId(uiToolId, parameters);
                    feedView.ForceUpdateAllMatchingTools(uiToolId, toolId, parameters);
                }
            }

            // Execute the actual tool (parameters cannot be null here based on interface contract)
            var result = await _innerExecutor.ExecuteAsync(toolId, parameters ?? new Dictionary<string, object?>(), context);

            // DEBUG: Write the raw result to file
            try
            {
                var debugInfo = $"[{DateTime.Now:HH:mm:ss.fff}] Tool {toolId} raw result:\n";
                debugInfo += $"  IsSuccessful: {result.IsSuccessful}\n";
                debugInfo += $"  Message: '{result.Message}'\n";
                debugInfo += $"  Data type: {result.Data?.GetType().Name ?? "null"}\n";
                if (result.Data != null)
                {
                    debugInfo += $"  Data ToString(): '{result.Data}'\n";
                    if (result.Data is Dictionary<string, object?> dict)
                    {
                        foreach (var kvp in dict.Take(10))
                        {
                            debugInfo += $"    {kvp.Key}: {kvp.Value}\n";
                        }
                    }
                }
                System.IO.File.AppendAllText("/tmp/tool_executor_debug.txt", debugInfo + "\n");
            }
            catch { }

            // Track completion and update UI with result
            // The toolId parameter is the actual tool name (e.g., "datetime_tool")
            // We need to find the UI ID that was registered for this execution
            var uiId = ToolExecutionTracker.Instance.GetToolIdForName(toolId);

            // IMPORTANT: We must track completion BEFORE SimpleAssistantService tries to read it
            // Store the result immediately in the tracker
            if (!string.IsNullOrEmpty(uiId))
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
                            if (dataDict.TryGetValue("data", out var data) && data != null)
                            {
                                resultMessage = $"Indexed: {data}";
                            }
                            else if (dataDict.TryGetValue("query_type", out var queryType))
                            {
                                resultMessage = $"Query type: {queryType}";
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
                        else
                        {
                            // Generic extraction for other tools
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
                    // For failed operations, use the error message
                    resultMessage = result.Message ?? "Failed";
                    _logger?.LogWarning("[UI_EXECUTOR] Tool {ToolId} failed with message: {Message}", toolId, resultMessage);
                }

                // Log what we extracted
                _logger?.LogWarning("[UI_EXECUTOR] Extracted result for {ToolId}: '{Result}' from Data type {DataType}",
                    toolId, resultMessage, result.Data?.GetType().Name ?? "null");

                _logger?.LogWarning("[UI_EXECUTOR] Tracking completion for {ToolId} with result: '{Result}'",
                    uiId, resultMessage);

                ToolExecutionTracker.Instance.TrackToolComplete(uiId, result.IsSuccessful, resultMessage, result.Data);
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