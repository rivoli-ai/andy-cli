using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Tools.Core;
using Andy.Cli.Lsp;
using Andy.Cli.Widgets;
using Andy.Cli.Instrumentation;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services
{
    /// <summary>
    /// Wraps IToolExecutor to update the UI when tools are executed
    /// </summary>
    public partial class UiUpdatingToolExecutor : IToolExecutor
    {
        private readonly IToolExecutor _innerExecutor;
        private readonly ILogger<UiUpdatingToolExecutor>? _logger;
        private readonly IToolRegistry? _toolRegistry;
        private readonly ToolCallLoopDetector _loopDetector = new();
        private readonly WorkingDirectoryTracker _workingDirectory;
        private readonly IFileMutationDiagnosticsReporter? _diagnosticsReporter;

        // The shared post-mutation pipeline (issue #283): runs configured formatters, then computes
        // the displayed diff from the FINAL on-disk bytes. Null falls back to the diff-only pipeline,
        // which behaves exactly as this executor did before formatters existed.
        private readonly Formatting.PostMutationPipeline? _postMutationPipeline;

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

        public UiUpdatingToolExecutor(IToolExecutor innerExecutor, ILogger<UiUpdatingToolExecutor>? logger = null, IToolRegistry? toolRegistry = null, WorkingDirectoryTracker? workingDirectoryTracker = null, Formatting.PostMutationPipeline? postMutationPipeline = null, IFileMutationDiagnosticsReporter? diagnosticsReporter = null)
        {
            _innerExecutor = innerExecutor;
            _logger = logger;
            _toolRegistry = toolRegistry;
            _workingDirectory = workingDirectoryTracker ?? WorkingDirectoryTracker.Instance;
            _postMutationPipeline = postMutationPipeline;
            // Null here means "use whatever the session has installed", resolved per call: the
            // executor is rebuilt on /restart and on model switches, but the LSP session is not.
            _diagnosticsReporter = diagnosticsReporter;
        }

        public async Task<ToolExecutionResult> ExecuteAsync(string toolId, Dictionary<string, object?> parameters, ToolExecutionContext? context = null)
        {
            _logger?.LogWarning("[UI_EXECUTOR] Executing tool {ToolId} with {ParamCount} parameters",
                toolId, parameters?.Count ?? 0);

            // Ensure we have a context with a correlation ID
            context ??= new ToolExecutionContext();

            // Keep tools operating in the session's tracked working directory (rivoli-ai/andy-cli#235).
            // The engine's SimpleAgent stamps a working-directory snapshot FROZEN at agent construction
            // into every context, so once the session cd's (via a standalone `cd` in execute_command,
            // tracked below) that snapshot is stale. The tracker is the live source of truth shared
            // with the header at the top of the UI.
            context.WorkingDirectory = _workingDirectory.Current;

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

            // No row to claim: create one HERE, before the tool runs (rivoli-ai/andy-cli#245).
            //
            // The lookups above assume the UI has already created a row, which is what
            // ToolExecutionTracker.EnqueuePendingTool was written for. But the engine raises its
            // ToolCalled event only AFTER a call finishes, so on the real ordering there is
            // nothing to claim: the executor completed nothing, and the row appeared afterwards
            // with no arguments and no one left to finish it - it spun until the end-of-turn
            // backstop swept it up when the model's final answer arrived. Worse, the next call
            // dequeued the PREVIOUS call's row, so every row lagged one call behind.
            //
            // This executor is the only place that straddles the execution and already holds the
            // arguments, so it is the right place to open the row.
            var feedViewForStart = ToolExecutionTracker.Instance.GetFeedView();

            // Reject a resolved id that does not point at a row still waiting to complete. The
            // last two lookups above are name-keyed fallbacks over maps that are never cleared, so
            // they cheerfully return a row from an EARLIER turn. Adopting it means completing an
            // already-finished row (a no-op) while the real call gets no row of its own - which is
            // how a bare "Running a command" with no arguments ended up spinning forever.
            if (!string.IsNullOrEmpty(uiToolId) && feedViewForStart != null
                && !feedViewForStart.HasIncompleteToolRow(uiToolId))
            {
                _logger?.LogWarning("[UI_EXECUTOR] Discarding stale UI id {UiId} for {ToolId}", uiToolId, toolId);
                uiToolId = null;
            }

            if (string.IsNullOrEmpty(uiToolId) && feedViewForStart != null)
            {
                uiToolId = ToolExecutionTracker.Instance.CreateExecutorRowId(toolId);
                var startParameters = new Dictionary<string, object?>(parameters ?? new Dictionary<string, object?>())
                {
                    // Keep the legacy exact-id convention so RunningToolItem-based tools still match.
                    ["__toolId"] = uiToolId,
                    ["__baseName"] = toolId
                };
                feedViewForStart.AddToolExecutionStart(uiToolId, toolId, startParameters);
                _logger?.LogWarning("[UI_EXECUTOR] Created UI row {UiId} for {ToolId} (no pending row to claim)",
                    uiToolId, toolId);
            }

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

            // Start timing before any executor exit path. The loop guard, exceptions and
            // cancellations must all close the UI row immediately, not wait for the model's
            // end-of-turn fallback.
            var toolStopwatch = System.Diagnostics.Stopwatch.StartNew();

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
                    toolStopwatch.Stop();
                    ToolExecutionTracker.Instance.GetFeedView()?.AddToolExecutionComplete(
                        uiToolId, false, FormatToolDuration(toolStopwatch.Elapsed), guidance);
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

            ToolExecutionResult result;
            try
            {
                result = await _innerExecutor.ExecuteAsync(toolId, dispatchParameters, context);
                toolStopwatch.Stop();
            }
            catch (OperationCanceledException)
            {
                toolStopwatch.Stop();
                CompleteExceptionalTool(uiToolId, "Cancelled", toolStopwatch.Elapsed);
                throw;
            }
            catch (Exception ex)
            {
                toolStopwatch.Stop();
                CompleteExceptionalTool(uiToolId, ex.Message, toolStopwatch.Elapsed);
                throw;
            }

            // A successful standalone `cd` persists for the rest of the session: it moves the
            // tracked working directory, so subsequent tool calls (and the header) follow it.
            if (result.IsSuccessful && string.Equals(toolId, "execute_command", StringComparison.OrdinalIgnoreCase))
            {
                TrackDirectoryChange(dispatchParameters);
            }

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

                // Hand the feed the FULL structured result first (issue #249). Presenters read
                // Data and Metadata directly, so nothing they show has to be recovered from the
                // pre-rendered resultMessage below. When this returns true the call is already
                // complete and the legacy string-based completion path is a no-op for it.
                var feedViewForStructuredCompletion = ToolExecutionTracker.Instance.GetFeedView();

                // A file change is the one piece of display data the tool cannot supply: it
                // overwrites the file and returns neither side, so the diff only exists because
                // the pre-call snapshot above captured "before". Attaching it to the completion
                // keeps the change on the call that made it.
                //
                // The single post-mutation pipeline (issue #283) owns this: it runs the configured
                // formatters first and computes the diff from the FINAL on-disk bytes, so what the
                // user sees is what the file actually contains.
                var postMutation = result.IsSuccessful && diffCapture != null
                    ? await RunPostMutationAsync(diffCapture, toolId, context, dispatchParameters)
                    : null;

                // Never let a formatter failure pass as a clean write: the exit code and bounded,
                // redacted stderr travel back to the model with the tool result.
                var formatterReport = postMutation?.AgentReport;
                if (!string.IsNullOrWhiteSpace(formatterReport))
                {
                    Formatting.FormatterResultReporter.Attach(result, formatterReport);
                    resultMessage = string.IsNullOrWhiteSpace(resultMessage)
                        ? formatterReport!
                        : resultMessage + "\n\n" + formatterReport;
                }

                var fileMutation = ToFileMutationView(postMutation);

                bool renderedStructurally = feedViewForStructuredCompletion?.CompleteToolCall(
                    uiToolId, new ToolResults.ToolCallCompletion
                    {
                        IsSuccessful = result.IsSuccessful,
                        Data = result.Data,
                        Metadata = result.Metadata,
                        ErrorMessage = result.ErrorMessage,
                        Message = result.Message,
                        Duration = toolStopwatch.Elapsed,
                        WasCancelled = result.WasCancelled,
                        WasDenied = IsPermissionDenial(result),
                        FileMutation = fileMutation
                    }) ?? false;

                ToolExecutionTracker.Instance.TrackToolComplete(uiToolId, result.IsSuccessful, resultMessage, result.Data);

                // Stop the spinner immediately now that the tool has returned data. Previously the
                // running-tool item was only marked complete by SimpleAssistantService after the whole
                // agent turn (including the final model response) finished, so the spinner and elapsed
                // timer appeared to hang long after the tool was actually done. AddToolExecutionComplete
                // is idempotent, so the later end-of-turn pass is a harmless no-op for this tool.
                var feedViewForCompletion = ToolExecutionTracker.Instance.GetFeedView();
                feedViewForCompletion?.AddToolExecutionComplete(
                    uiToolId, result.IsSuccessful, FormatToolDuration(toolStopwatch.Elapsed), resultMessage);

                // Render a git-style diff for a successful file write/edit, right under the tool
                // line. Only for tools still on the legacy path: a presenter-backed call already
                // received the same change on its completion and renders it inside the call, so
                // emitting a second item here would show the diff twice. The diff comes from the
                // same pipeline run as the structured view, so both show the final on-disk bytes.
                if (fileMutation != null && feedViewForCompletion != null && !renderedStructurally)
                {
                    feedViewForCompletion.AddFileDiff(fileMutation.DisplayPath, fileMutation.Kind, fileMutation.Diff);
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

            // POST-MUTATION PIPELINE (rivoli-ai/andy-cli#282).
            //
            // Everything that reacts to a file having changed hangs off this one point, and the
            // ORDER is part of the contract:
            //
            //   1. (future, #283) run the post-mutation formatter, rewriting the file on disk
            //   2. notify the language server and collect diagnostics  <- implemented here
            //
            // The formatter must come FIRST because the diagnostics step reads the file back from
            // disk and reports on exactly what it finds. If a formatter ran afterwards, every
            // diagnostic's line and column would describe a version of the file that no longer
            // exists. #283 should insert its call immediately above this one; nothing else needs
            // to move.
            if (result.IsSuccessful && diffCapture != null)
            {
                await ReportLanguageServerDiagnosticsAsync(result, diffCapture, context);
            }

            return result;
        }

        /// <summary>
        /// Sends the changed file's final on-disk content to a configured language server and folds
        /// the resulting diagnostics into the tool result and the feed.
        ///
        /// This runs on the agent's critical path, so it is completely non-fatal: no configured
        /// server, a server that will not start, one that crashed, one that answers with garbage,
        /// and one that is simply slow all end the same way - the tool result is returned unchanged
        /// or with a short status note, and the turn continues.
        /// </summary>
        private async Task ReportLanguageServerDiagnosticsAsync(
            ToolExecutionResult result,
            FileMutationCapture capture,
            ToolExecutionContext? context)
        {
            var reporter = _diagnosticsReporter ?? LspSession.Instance.Reporter;
            if (reporter == null) return;

            try
            {
                var cancellationToken = context?.CancellationToken ?? CancellationToken.None;
                var report = await reporter.ReportAsync(capture.ResolvedPath, cancellationToken);
                if (report == null || report.IsSilent) return;

                // The model reads Data; attaching returns a NEW payload so the snapshot the feed
                // already captured for this call is left exactly as it was.
                LspResultAttachment.AttachTo(result, report);

                var feedText = report.ToFeedText();
                if (!string.IsNullOrWhiteSpace(feedText))
                {
                    ToolExecutionTracker.Instance.GetFeedView()?.AddMarkdownRich("```\n" + feedText + "\n```");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[UI_EXECUTOR] Language server diagnostics failed for {Path}", capture.ResolvedPath);
            }
        }

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (request.Context != null)
            {
                GrantGatedCapabilities(request.Context);
            }

            return ExecuteAsync(request.ToolId, request.Parameters, request.Context);
        }

        /// <summary>
        /// Grants the tool capability flags on the execution context's permission profile. The
        /// Andy.Permissions gate decides actual consent per call; these flags only stop the lower-level
        /// capability checks from blocking a tool before the gate runs.
        /// </summary>
        /// <summary>
        /// Whether a failed result is the permission gate refusing the call rather than the tool
        /// failing on its own terms (#264). The two look identical in the feed otherwise, and they
        /// mean very different things: a denial is something the user can change their mind about.
        ///
        /// The gate short-circuits without running the tool, so a denial is a failure that carries
        /// no data and names permission in its message. A tool's own "Access denied" - a path
        /// outside the permitted roots, say - reaches here having actually run, and is left as an
        /// ordinary failure.
        /// </summary>
        internal static bool IsPermissionDenial(ToolExecutionResult result)
        {
            if (result.IsSuccessful || result.Data is not null) return false;
            var message = result.ErrorMessage;
            return !string.IsNullOrEmpty(message)
                && message.Contains("permission", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Format an elapsed tool duration the same way the feed status line does.</summary>
        private static string FormatToolDuration(TimeSpan elapsed)
        {
            if (elapsed.TotalMilliseconds < 1000)
                return $"{elapsed.TotalMilliseconds:F0}ms";
            if (elapsed.TotalSeconds < 60)
                return $"{elapsed.TotalSeconds:F1}s";
            return $"{elapsed.TotalMinutes:F1}m";
        }

        private static void CompleteExceptionalTool(string? uiToolId, string message, TimeSpan elapsed)
        {
            if (string.IsNullOrEmpty(uiToolId)) return;

            var result = string.IsNullOrWhiteSpace(message) ? "Operation failed" : message;
            ToolExecutionTracker.Instance.TrackToolComplete(uiToolId, false, result, null);
            ToolExecutionTracker.Instance.GetFeedView()?.AddToolExecutionComplete(
                uiToolId, false, FormatToolDuration(elapsed), result);
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

        // --- Session working directory tracking (rivoli-ai/andy-cli#235) --------------------

        /// <summary>
        /// After a successful execute_command, applies a persistent directory change when the
        /// command was a standalone `cd`. Resolution honors an explicit working_directory
        /// parameter when the model supplied one, otherwise the tracked session directory.
        /// </summary>
        private void TrackDirectoryChange(Dictionary<string, object?> parameters)
        {
            try
            {
                if (!parameters.TryGetValue("command", out var cmdObj) || cmdObj is null) return;

                string? baseDir = null;
                if (parameters.TryGetValue("working_directory", out var wdObj) &&
                    wdObj?.ToString() is { Length: > 0 } wd)
                {
                    baseDir = wd;
                }

                var applied = _workingDirectory.ApplyExecutedCommand(cmdObj.ToString(), baseDir);
                if (applied != null)
                {
                    _logger?.LogInformation("[UI_EXECUTOR] Session working directory changed to {Directory}", applied);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[UI_EXECUTOR] Failed to track working directory change");
            }
        }

        // --- File write/edit diff rendering -------------------------------------------------

        // Single-file mutating tools mapped to the parameter that names their target file. Every
        // entry here flows through the ONE shared post-mutation pipeline (issue #283): write and
        // create (write_file), patch (edit_file), replace (replace_text), and rename (move_file).
        // replace_text targets `target_path`, which may also be a whole directory (with
        // file_patterns) - that multi-file mode is skipped in TryCaptureBeforeWrite because a
        // single before/after diff is meaningless there.
        private static readonly Dictionary<string, string> FileDiffToolTargetParams =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["write_file"] = "file_path",
                ["edit_file"] = "file_path",
                ["replace_text"] = "target_path",
                ["move_file"] = "destination_path",
            };

        // Skip diffing files larger than this (either before or after) to keep the UI responsive.
        private const long MaxDiffFileBytes = 512 * 1024;

        private sealed record FileMutationCapture(string ResolvedPath, string DisplayPath, bool Existed, string BeforeText);

        private FileMutationCapture? TryCaptureBeforeWrite(string toolId, Dictionary<string, object?> parameters, ToolExecutionContext? context)
        {
            try
            {
                if (!FileDiffToolTargetParams.TryGetValue(toolId, out var targetParam)) return null;
                if (!parameters.TryGetValue(targetParam, out var fpObj) || fpObj is null) return null;
                var rawPath = fpObj.ToString();
                if (string.IsNullOrWhiteSpace(rawPath)) return null;

                var workingDir = string.IsNullOrEmpty(context?.WorkingDirectory)
                    ? Directory.GetCurrentDirectory()
                    : context!.WorkingDirectory;
                var resolved = Path.IsPathRooted(rawPath) ? rawPath : Path.GetFullPath(Path.Combine(workingDir, rawPath));

                // replace_text pointed at a directory rewrites many files (with file_patterns);
                // a single before/after diff cannot represent that, so skip.
                if (Directory.Exists(resolved)) return null;

                bool existed = File.Exists(resolved);

                // replace_text edits in place and cannot create a file; nothing to diff without one.
                if (!existed && string.Equals(toolId, "replace_text", StringComparison.OrdinalIgnoreCase)) return null;
                string before = "";
                if (existed)
                {
                    var len = new FileInfo(resolved).Length;
                    if (len > MaxDiffFileBytes) return null; // too big to diff cheaply
                    before = File.ReadAllText(resolved);
                    if (before.Contains('\0')) return null;  // binary file
                }

                // A rename moves content that already exists, so its "before" is the SOURCE file,
                // not the (usually absent) destination. Diffing against the source makes a pure
                // rename produce an empty diff and shows only what a formatter then changed - which
                // is exactly what the user needs to see.
                if (string.Equals(toolId, "move_file", StringComparison.OrdinalIgnoreCase))
                {
                    var source = ReadRenameSource(parameters, workingDir);
                    if (source is null) return null;
                    before = source;
                    existed = true;
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

        /// <summary>
        /// Read the rename source's content, so a move can be diffed against what was moved rather
        /// than against an empty destination. Returns null when the source is missing, too large,
        /// or binary - in which case the rename is simply not diffed.
        /// </summary>
        private static string? ReadRenameSource(Dictionary<string, object?> parameters, string workingDir)
        {
            if (!parameters.TryGetValue("source_path", out var srcObj) || srcObj is null) return null;
            var raw = srcObj.ToString();
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var resolved = Path.IsPathRooted(raw) ? raw : Path.GetFullPath(Path.Combine(workingDir, raw));
            if (!File.Exists(resolved)) return null;
            if (new FileInfo(resolved).Length > MaxDiffFileBytes) return null;

            var text = File.ReadAllText(resolved);
            return text.Contains('\0') ? null : text;
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
