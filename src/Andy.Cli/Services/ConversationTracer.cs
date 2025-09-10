using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using Andy.Llm.Models;

namespace Andy.Cli.Services;

/// <summary>
/// Traces conversations between the app, LLM, and tools for debugging
/// </summary>
public class ConversationTracer : IDisposable
{
    private readonly string _traceFilePath = "";
    private readonly StreamWriter? _traceWriter;
    private readonly bool _enabled;
    private readonly bool _consoleOutput;
    private readonly object _lock = new();
    private int _sequenceNumber = 0;

    public ConversationTracer(bool enabled = true, bool consoleOutput = false, string? customPath = null)
    {
        _enabled = enabled;
        _consoleOutput = consoleOutput;
        
        if (!_enabled) return;

        // Create trace file in temp directory or custom path
        if (customPath != null)
        {
            _traceFilePath = customPath;
        }
        else
        {
            var tempDir = Path.GetTempPath();
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            _traceFilePath = Path.Combine(tempDir, $"andy-trace-{timestamp}.json");
        }

        try
        {
            _traceWriter = new StreamWriter(_traceFilePath, append: false, encoding: Encoding.UTF8)
            {
                AutoFlush = true
            };
            
            WriteHeader();
            
            if (_consoleOutput)
            {
                Console.WriteLine($"[TRACE] Logging to: {_traceFilePath}");
            }
        }
        catch (Exception ex)
        {
            if (_consoleOutput)
            {
                Console.WriteLine($"[TRACE] Failed to create trace file: {ex.Message}");
            }
            _enabled = false;
        }
    }

    private void WriteHeader()
    {
        var header = new
        {
            type = "trace_start",
            timestamp = DateTime.UtcNow.ToString("O"),
            version = "1.0",
            pid = Environment.ProcessId,
            machine = Environment.MachineName
        };
        
        WriteEntry(header);
    }

    /// <summary>
    /// Log a user message
    /// </summary>
    public void TraceUserMessage(string message)
    {
        if (!_enabled) return;

        var entry = new
        {
            seq = GetNextSequence(),
            type = "user_message",
            timestamp = DateTime.UtcNow.ToString("O"),
            message = message
        };

        WriteEntry(entry);
        
        if (_consoleOutput)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[TRACE] USER: {TruncateForConsole(message)}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Log an LLM request
    /// </summary>
    public void TraceLlmRequest(LlmRequest request)
    {
        if (!_enabled) return;

        var entry = new
        {
            seq = GetNextSequence(),
            type = "llm_request",
            timestamp = DateTime.UtcNow.ToString("O"),
            model = request.Model,
            temperature = request.Temperature,
            max_tokens = request.MaxTokens,
            message_count = request.Messages?.Count ?? 0,
            messages = SimplifyMessages(request.Messages)
        };

        WriteEntry(entry);
        
        if (_consoleOutput)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[TRACE] LLM_REQ: {entry.message_count} messages, model={request.Model}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Log an LLM response
    /// </summary>
    public void TraceLlmResponse(LlmResponse? response, TimeSpan elapsed)
    {
        if (!_enabled) return;

        var entry = new
        {
            seq = GetNextSequence(),
            type = "llm_response",
            timestamp = DateTime.UtcNow.ToString("O"),
            elapsed_ms = elapsed.TotalMilliseconds,
            content_length = response?.Content?.Length ?? 0,
            content_preview = TruncateForTrace(response?.Content),
            finish_reason = response?.FinishReason,
            usage = response?.Usage
        };

        WriteEntry(entry);
        
        if (_consoleOutput)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[TRACE] LLM_RESP: {entry.content_length} chars in {elapsed.TotalMilliseconds:F0}ms");
            if (!string.IsNullOrEmpty(entry.content_preview))
            {
                Console.WriteLine($"         Preview: {TruncateForConsole(entry.content_preview)}");
            }
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Log tool calls extracted from response
    /// </summary>
    public void TraceToolCalls(List<TracedToolCall> toolCalls)
    {
        if (!_enabled || toolCalls.Count == 0) return;

        var entry = new
        {
            seq = GetNextSequence(),
            type = "tool_calls_extracted",
            timestamp = DateTime.UtcNow.ToString("O"),
            count = toolCalls.Count,
            tools = toolCalls.Select(tc => new
            {
                tool_id = tc.ToolId,
                parameters = tc.Parameters
            }).ToList()
        };

        WriteEntry(entry);
        
        if (_consoleOutput)
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[TRACE] TOOLS_FOUND: {toolCalls.Count} tool calls");
            foreach (var tool in toolCalls)
            {
                Console.WriteLine($"         - {tool.ToolId}: {JsonSerializer.Serialize(tool.Parameters)}");
            }
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Log tool execution
    /// </summary>
    public void TraceToolExecution(string toolId, Dictionary<string, object?> parameters, bool success, string? result, TimeSpan elapsed)
    {
        if (!_enabled) return;

        var entry = new
        {
            seq = GetNextSequence(),
            type = "tool_execution",
            timestamp = DateTime.UtcNow.ToString("O"),
            tool_id = toolId,
            parameters = parameters,
            success = success,
            elapsed_ms = elapsed.TotalMilliseconds,
            result_length = result?.Length ?? 0,
            result_preview = TruncateForTrace(result)
        };

        WriteEntry(entry);
        
        if (_consoleOutput)
        {
            Console.ForegroundColor = success ? ConsoleColor.Blue : ConsoleColor.Red;
            Console.WriteLine($"[TRACE] TOOL_EXEC: {toolId} {(success ? "SUCCESS" : "FAILED")} in {elapsed.TotalMilliseconds:F0}ms");
            if (!string.IsNullOrEmpty(result))
            {
                Console.WriteLine($"         Result: {TruncateForConsole(result)}");
            }
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Log assistant's final message
    /// </summary>
    public void TraceAssistantMessage(string message)
    {
        if (!_enabled) return;

        var entry = new
        {
            seq = GetNextSequence(),
            type = "assistant_message",
            timestamp = DateTime.UtcNow.ToString("O"),
            message_length = message.Length,
            message = TruncateForTrace(message)
        };

        WriteEntry(entry);
        
        if (_consoleOutput)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"[TRACE] ASSISTANT: {TruncateForConsole(message)}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Log an error
    /// </summary>
    public void TraceError(string context, Exception ex)
    {
        if (!_enabled) return;

        var entry = new
        {
            seq = GetNextSequence(),
            type = "error",
            timestamp = DateTime.UtcNow.ToString("O"),
            context = context,
            error_type = ex.GetType().Name,
            message = ex.Message,
            stack_trace = ex.StackTrace
        };

        WriteEntry(entry);
        
        if (_consoleOutput)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[TRACE] ERROR in {context}: {ex.Message}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Log iteration info
    /// </summary>
    public void TraceIteration(int iteration, int maxIterations)
    {
        if (!_enabled) return;

        var entry = new
        {
            seq = GetNextSequence(),
            type = "iteration",
            timestamp = DateTime.UtcNow.ToString("O"),
            iteration = iteration,
            max_iterations = maxIterations
        };

        WriteEntry(entry);
        
        if (_consoleOutput)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[TRACE] ITERATION: {iteration}/{maxIterations}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Log context stats
    /// </summary>
    public void TraceContextStats(int messageCount, int tokenEstimate, int toolCallCount)
    {
        if (!_enabled) return;

        var entry = new
        {
            seq = GetNextSequence(),
            type = "context_stats",
            timestamp = DateTime.UtcNow.ToString("O"),
            message_count = messageCount,
            token_estimate = tokenEstimate,
            tool_call_count = toolCallCount
        };

        WriteEntry(entry);
        
        if (_consoleOutput)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[TRACE] CONTEXT: {messageCount} msgs, ~{tokenEstimate} tokens, {toolCallCount} tool calls");
            Console.ResetColor();
        }
    }

    private void WriteEntry(object entry)
    {
        if (!_enabled || _traceWriter == null) return;

        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions
                {
                    WriteIndented = false,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });
                
                _traceWriter.WriteLine(json);
            }
            catch
            {
                // Silently ignore write errors
            }
        }
    }

    private int GetNextSequence()
    {
        lock (_lock)
        {
            return ++_sequenceNumber;
        }
    }

    private List<object> SimplifyMessages(dynamic? messages)
    {
        if (messages == null) return new List<object>();
        
        var result = new List<object>();
        try 
        {
            foreach (var msg in messages)
            {
                result.Add(new
                {
                    role = msg.Role?.ToString() ?? "unknown",
                    message_type = msg.GetType().Name
                });
            }
        }
        catch
        {
            // If we can't process messages, just return count
            result.Add(new { count = messages.Count });
        }
        return result;
    }

    private string TruncateForTrace(string? text, int maxLength = 500)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.Length <= maxLength) return text;
        return text.Substring(0, maxLength) + "...";
    }

    private string TruncateForConsole(string? text, int maxLength = 100)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.Replace('\n', ' ').Replace('\r', ' ');
        if (text.Length <= maxLength) return text;
        return text.Substring(0, maxLength) + "...";
    }

    public void Dispose()
    {
        if (_traceWriter != null)
        {
            try
            {
                var footer = new
                {
                    type = "trace_end",
                    timestamp = DateTime.UtcNow.ToString("O"),
                    total_entries = _sequenceNumber
                };
                WriteEntry(footer);
                
                _traceWriter.Dispose();
                
                if (_consoleOutput && _enabled)
                {
                    Console.WriteLine($"[TRACE] Trace saved to: {_traceFilePath}");
                }
            }
            catch
            {
                // Ignore disposal errors
            }
        }
    }

    public string TraceFilePath => _traceFilePath;
}

/// <summary>
/// Simple tool call structure for tracing
/// </summary>
public class TracedToolCall
{
    public string ToolId { get; set; } = "";
    public Dictionary<string, object?> Parameters { get; set; } = new();
}