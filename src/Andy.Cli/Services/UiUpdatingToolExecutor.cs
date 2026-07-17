using System;
using System.Collections.Generic;
using System.IO;
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
        private readonly IToolRegistry? _toolRegistry;
        private readonly ToolCallLoopDetector _loopDetector = new();

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

        public UiUpdatingToolExecutor(IToolExecutor innerExecutor, ILogger<UiUpdatingToolExecutor>? logger = null, IToolRegistry? toolRegistry = null)
        {
            _innerExecutor = innerExecutor;
            _logger = logger;
            _toolRegistry = toolRegistry;
        }

        public async Task<ToolExecutionResult> ExecuteAsync(string toolId, Dictionary<string, object?> parameters, ToolExecutionContext? context = null)
        {
            _logger?.LogWarning("[UI_EXECUTOR] Executing tool {ToolId} with {ParamCount} parameters",
                toolId, parameters?.Count ?? 0);

            // Ensure we have a context with a correlation ID
            context ??= new ToolExecutionContext();

            // The Andy.Permissions gate is the CLI's consent authority (allow/ask/deny per call). Grant the
            // capability flags on the profile so the lower-level capability checks
            // (SecurityManager.ValidateExecution + ToolBase.CanExecuteWithPermissions) don't pre-empt the
            // gate. Without this, tools that declare ProcessExecution (execute_command) are blocked before
            // the gate runs, because the engine builds the context with the restrictive default profile.
            GrantGatedCapabilities(context);

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

            // Loop guard: if the model keeps issuing the same call with identical arguments, it is
            // almost certainly stuck (and burning tokens). Short-circuit with guidance instead of
            // re-running the tool, so it stops repeating and changes approach.
            var loopSignature = ToolCallLoopDetector.Signature(toolId, parameters);
            if (_loopDetector.RecordAndIsLooping(loopSignature))
            {
                var guidance =
                    $"Loop guard: the tool '{toolId}' has already been called repeatedly with identical " +
                    "arguments and returned the same result. Stop repeating this call - use the results you " +
                    "already have, or take a different approach to make progress.";
                _logger?.LogWarning("[UI_EXECUTOR] Loop detected for {ToolId}; short-circuiting. Signature={Signature}",
                    toolId, loopSignature);

                if (!string.IsNullOrEmpty(uiToolId))
                {
                    ToolExecutionTracker.Instance.TrackToolComplete(uiToolId, false, guidance, null);
                }

                return new ToolExecutionResult
                {
                    IsSuccessful = false,
                    Message = guidance
                };
            }

            // Execute the actual tool (parameters cannot be null here based on interface contract).
            // Time it so the UI can show the tool's real duration the moment it returns, rather
            // than the whole-turn elapsed measured later by SimpleAssistantService.
            var toolStopwatch = System.Diagnostics.Stopwatch.StartNew();
            // Map parameter names via the curated per-tool alias table and coerce values to the
            // types the tool declares before dispatching. Models routinely (a) call a tool with
            // names from a different tool family (e.g. old_string/new_string for replace_text,
            // whose real parameters are search_pattern/replacement_text) and (b) pass an
            // array-typed parameter as a bare scalar (file_patterns="*.cs" instead of ["*.cs"]).
            // Either makes the framework validator reject the call for a reason unrelated to what
            // the tool does. Mapping uses ONLY exact + hand-vetted aliases (no fuzzy guessing), so
            // it cannot mis-route a call.
            var dispatchParameters = parameters ?? new Dictionary<string, object?>();
            var toolMetadata = _toolRegistry?.GetTool(toolId)?.Metadata;
            if (toolMetadata != null)
            {
                try
                {
                    dispatchParameters = ParameterMapper.MapAndNormalize(toolId, dispatchParameters, toolMetadata);
                }
                catch (Exception ex)
                {
                    // Normalization is best-effort; never let it block execution.
                    _logger?.LogWarning(ex, "[UI_EXECUTOR] Parameter normalization failed for {ToolId}; dispatching as-is", toolId);
                }
            }

            // For file write/edit tools, snapshot the target file's current content BEFORE the call:
            // the tool overwrites the file and returns neither the old nor new content, so a diff can
            // only be reconstructed by capturing "before" here and reading "after" once it completes.
            var diffCapture = TryCaptureBeforeWrite(toolId, dispatchParameters, context);

            // The framework executor (Andy.Tools) cancels EVERY tool after
            // context.ResourceLimits.MaxExecutionTimeMs - which the engine leaves at its 30s default
            // (SimpleAgent only overrides MaxMemoryBytes). That blanket cap overrides each tool's own
            // timeout (notably execute_command's timeout_seconds) and kills legitimate long-running
            // operations - builds, test runs, code indexing - well before they finish. Raise the cap
            // so the tool's own timeout governs: a generous safety-net backstop, and never shorter
            // than an explicit timeout_seconds the caller asked for.
            if (context.ResourceLimits != null && context.ResourceLimits.MaxExecutionTimeMs > 0)
            {
                long backstopMs = 30L * 60 * 1000; // 30-minute safety net for tools without their own timeout
                if (dispatchParameters.TryGetValue("timeout_seconds", out var ts) && ts != null
                    && int.TryParse(ts.ToString(), out var secs) && secs > 0)
                    backstopMs = Math.Max(backstopMs, (long)secs * 1000 + 5000);
                if (context.ResourceLimits.MaxExecutionTimeMs < backstopMs)
                    context.ResourceLimits.MaxExecutionTimeMs = (int)Math.Min(backstopMs, int.MaxValue);
            }

            var result = await _innerExecutor.ExecuteAsync(toolId, dispatchParameters, context);
            toolStopwatch.Stop();

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
                    // For failed operations, the reason lives in ErrorMessage, not Message.
                    // ToolResult.Failure(...) and the inner ToolExecutor's validation failures
                    // populate ErrorMessage and leave Message null, so reading Message here used
                    // to discard the real reason and fall through to the generic "Operation failed"
                    // fallback below. Prefer ErrorMessage, then Message.
                    resultMessage = !string.IsNullOrEmpty(result.ErrorMessage)
                        ? result.ErrorMessage
                        : (result.Message ?? "");

                    // Log the raw error data for debugging
                    _logger?.LogError("[UI_EXECUTOR] Tool {ToolId} failed. ErrorMessage: '{ErrorMessage}', Message: '{Message}', Data type: {DataType}, Data: {Data}",
                        toolId, result.ErrorMessage, result.Message, result.Data?.GetType().Name ?? "null", result.Data);

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

                // Stop the spinner immediately now that the tool has returned data. Previously the
                // running-tool item was only marked complete by SimpleAssistantService after the whole
                // agent turn (including the final model response) finished, so the spinner and elapsed
                // timer appeared to hang long after the tool was actually done. AddToolExecutionComplete
                // is idempotent, so the later end-of-turn pass is a harmless no-op for this tool.
                var feedViewForCompletion = ToolExecutionTracker.Instance.GetFeedView();
                feedViewForCompletion?.AddToolExecutionComplete(
                    uiToolId, result.IsSuccessful, FormatToolDuration(toolStopwatch.Elapsed), resultMessage);

                // Render a git-style diff for a successful file write/edit, right under the tool line.
                if (result.IsSuccessful && diffCapture != null && feedViewForCompletion != null)
                {
                    TryRenderFileDiff(diffCapture, feedViewForCompletion);
                }

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
            ArgumentNullException.ThrowIfNull(request);

            if (request.Context != null)
            {
                GrantGatedCapabilities(request.Context);
            }

            return _innerExecutor.ExecuteAsync(request);
        }

        /// <summary>
        /// Grants the tool capability flags on the execution context's permission profile. The
        /// Andy.Permissions gate decides actual consent per call; these flags only stop the lower-level
        /// capability checks from blocking a tool before the gate runs.
        /// </summary>
        /// <summary>Format an elapsed tool duration the same way the feed status line does.</summary>
        private static string FormatToolDuration(TimeSpan elapsed)
        {
            if (elapsed.TotalMilliseconds < 1000)
                return $"{elapsed.TotalMilliseconds:F0}ms";
            if (elapsed.TotalSeconds < 60)
                return $"{elapsed.TotalSeconds:F1}s";
            return $"{elapsed.TotalMinutes:F1}m";
        }

        private static void GrantGatedCapabilities(ToolExecutionContext context)
        {
            context.Permissions.FileSystemAccess = true;
            context.Permissions.NetworkAccess = true;
            context.Permissions.ProcessExecution = true;
            context.Permissions.EnvironmentAccess = true;
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

        // --- File write/edit diff rendering -------------------------------------------------

        // Single-file mutating tools whose target is the `file_path` parameter. replace_text is
        // intentionally excluded: it can rewrite many files across a directory tree, so capturing
        // a meaningful "before" for all of them is out of scope here.
        private static readonly HashSet<string> FileDiffToolIds =
            new(StringComparer.OrdinalIgnoreCase) { "write_file", "edit_file" };

        // Skip diffing files larger than this (either before or after) to keep the UI responsive.
        private const long MaxDiffFileBytes = 512 * 1024;

        private sealed record FileMutationCapture(string ResolvedPath, string DisplayPath, bool Existed, string BeforeText);

        private FileMutationCapture? TryCaptureBeforeWrite(string toolId, Dictionary<string, object?> parameters, ToolExecutionContext? context)
        {
            try
            {
                if (!FileDiffToolIds.Contains(toolId)) return null;
                if (!parameters.TryGetValue("file_path", out var fpObj) || fpObj is null) return null;
                var rawPath = fpObj.ToString();
                if (string.IsNullOrWhiteSpace(rawPath)) return null;

                var workingDir = string.IsNullOrEmpty(context?.WorkingDirectory)
                    ? Directory.GetCurrentDirectory()
                    : context!.WorkingDirectory;
                var resolved = Path.IsPathRooted(rawPath) ? rawPath : Path.GetFullPath(Path.Combine(workingDir, rawPath));

                bool existed = File.Exists(resolved);
                string before = "";
                if (existed)
                {
                    var len = new FileInfo(resolved).Length;
                    if (len > MaxDiffFileBytes) return null; // too big to diff cheaply
                    before = File.ReadAllText(resolved);
                    if (before.Contains('\0')) return null;  // binary file
                }

                var display = ToDisplayPath(resolved, workingDir, rawPath!);
                return new FileMutationCapture(resolved, display, existed, before);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[UI_EXECUTOR] Failed to capture pre-write content for {ToolId}", toolId);
                return null;
            }
        }

        private void TryRenderFileDiff(FileMutationCapture capture, FeedView feedView)
        {
            try
            {
                if (!File.Exists(capture.ResolvedPath)) return; // e.g. tool deleted it
                if (new FileInfo(capture.ResolvedPath).Length > MaxDiffFileBytes) return;

                var after = File.ReadAllText(capture.ResolvedPath);
                if (after.Contains('\0')) return; // binary

                var diff = UnifiedDiff.Compute(capture.BeforeText, after);
                if (diff.IsEmpty) return; // no visible change (e.g. identical write / no-op edit)

                var kind = capture.Existed ? FileChangeKind.Update : FileChangeKind.Create;
                feedView.AddFileDiff(capture.DisplayPath, kind, diff);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[UI_EXECUTOR] Failed to render file diff for {Path}", capture.ResolvedPath);
            }
        }

        // Show a path relative to the working directory when the file is under it; otherwise fall
        // back to the path the caller supplied.
        private static string ToDisplayPath(string resolved, string workingDir, string rawPath)
        {
            try
            {
                var full = Path.GetFullPath(workingDir);
                var rel = Path.GetRelativePath(full, resolved);
                if (!rel.StartsWith("..") && !Path.IsPathRooted(rel))
                {
                    return rel.Replace('\\', '/');
                }
            }
            catch
            {
                // fall through
            }
            return rawPath;
        }
    }
}