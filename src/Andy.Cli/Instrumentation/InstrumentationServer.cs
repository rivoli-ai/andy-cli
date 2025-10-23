using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Instrumentation;

/// <summary>
/// HTTP server that exposes instrumentation events via Server-Sent Events (SSE).
/// Allows real-time visualization of all Engine/LLM activity in a separate window.
/// </summary>
public class InstrumentationServer : IDisposable
{
    private readonly int _port;
    private readonly ILogger? _logger;
    private WebApplication? _app;
    private Task? _runTask;
    private readonly ConcurrentBag<StreamWriter> _activeStreams = new();

    public InstrumentationServer(int port = 5555, ILogger? logger = null)
    {
        _port = port;
        _logger = logger;
    }

    /// <summary>
    /// Start the HTTP server
    /// </summary>
    public void Start()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            WebRootPath = null
        });

        // Configure logging
        builder.Logging.ClearProviders();
        if (_logger != null)
        {
            builder.Logging.AddProvider(new DelegateLoggerProvider(_logger));
        }

        // Configure Kestrel
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenLocalhost(_port);
        });

        _app = builder.Build();

        // Endpoint: SSE stream of events
        _app.MapGet("/events", async (HttpContext context) =>
        {
            context.Response.Headers["Content-Type"] = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-cache";
            context.Response.Headers["Connection"] = "keep-alive";
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";

            var writer = new StreamWriter(context.Response.Body, Encoding.UTF8, leaveOpen: true);
            _activeStreams.Add(writer);

            try
            {
                // Send initial connection event
                await SendEvent(writer, new DiagnosticEvent
                {
                    Level = "Info",
                    Source = "InstrumentationServer",
                    Message = "Connected to instrumentation stream"
                });

                // Send event history
                foreach (var evt in InstrumentationHub.Instance.GetEventHistory())
                {
                    await SendEvent(writer, evt);
                }

                // Keep connection alive until client disconnects
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    await Task.Delay(1000, context.RequestAborted);
                    // Send heartbeat
                    await writer.WriteLineAsync(": heartbeat");
                    await writer.FlushAsync();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in SSE stream");
            }
            finally
            {
                _activeStreams.TryTake(out _);
            }
        });

        // Endpoint: Get event history as JSON
        _app.MapGet("/history", () =>
        {
            var events = InstrumentationHub.Instance.GetEventHistory();
            return Results.Json(events, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
        });

        // Endpoint: Clear event history
        _app.MapPost("/clear", () =>
        {
            InstrumentationHub.Instance.Clear();
            return Results.Ok(new { message = "Event history cleared" });
        });

        // Endpoint: Get system prompt
        _app.MapGet("/system-prompt", () =>
        {
            var systemPrompt = InstrumentationHub.Instance.GetSystemPrompt();
            return Results.Json(new { systemPrompt = systemPrompt ?? string.Empty });
        });

        // Endpoint: Serve the dashboard HTML
        _app.MapGet("/", () => Results.Content(GetDashboardHtml(), "text/html"));

        // Subscribe to instrumentation hub
        InstrumentationHub.Instance.Subscribe(async evt =>
        {
            foreach (var stream in _activeStreams)
            {
                try
                {
                    await SendEvent(stream, evt);
                }
                catch
                {
                    // Client disconnected - ignore
                }
            }
        });

        // Start server in background
        _runTask = Task.Run(async () =>
        {
            try
            {
                await _app.RunAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error running instrumentation server");
            }
        });

        _logger?.LogInformation("Instrumentation server started on http://localhost:{Port}", _port);
        _logger?.LogInformation("Open http://localhost:{Port} in your browser to view real-time instrumentation", _port);
    }

    /// <summary>
    /// Stop the HTTP server
    /// </summary>
    public async Task StopAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_runTask != null)
        {
            await _runTask;
        }
    }

    private async Task SendEvent(StreamWriter writer, InstrumentationEvent evt)
    {
        var json = InstrumentationHub.Instance.SerializeEvent(evt);
        await writer.WriteLineAsync($"event: {evt.EventType}");
        await writer.WriteLineAsync($"data: {json}");
        await writer.WriteLineAsync();
        await writer.FlushAsync();
    }

    private static string GetDashboardHtml()
    {
        return @"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Andy Engine Instrumentation</title>
    <script src=""https://cdn.jsdelivr.net/npm/marked@11.1.1/marked.min.js""></script>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body {
            font-family: 'SF Mono', 'Monaco', 'Menlo', 'Consolas', monospace;
            background: linear-gradient(135deg, #0f0f23 0%, #1a1a2e 100%);
            color: #e4e4e7;
            padding: 30px;
            min-height: 100vh;
        }
        h1 {
            background: linear-gradient(135deg, #00d4ff 0%, #7c3aed 100%);
            -webkit-background-clip: text;
            -webkit-text-fill-color: transparent;
            background-clip: text;
            margin-bottom: 20px;
            font-size: 24px;
            font-weight: 800;
            letter-spacing: -0.5px;
        }
        .controls {
            margin-bottom: 25px;
            display: flex;
            gap: 12px;
        }
        button {
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            color: white;
            border: none;
            padding: 10px 20px;
            border-radius: 8px;
            cursor: pointer;
            font-family: inherit;
            font-weight: 600;
            transition: all 0.2s ease;
            box-shadow: 0 4px 12px rgba(102, 126, 234, 0.3);
        }
        button:hover {
            transform: translateY(-2px);
            box-shadow: 0 6px 20px rgba(102, 126, 234, 0.4);
        }
        button:active {
            transform: translateY(0);
        }
        .stats {
            margin-bottom: 25px;
            padding: 20px;
            background: rgba(30, 30, 46, 0.7);
            backdrop-filter: blur(10px);
            border-radius: 12px;
            border: 1px solid rgba(255, 255, 255, 0.1);
            display: flex;
            gap: 40px;
            box-shadow: 0 8px 32px rgba(0, 0, 0, 0.3);
        }
        .stat {
            display: flex;
            flex-direction: column;
            gap: 4px;
        }
        .stat-label {
            color: #a1a1aa;
            font-size: 11px;
            font-weight: 600;
            text-transform: uppercase;
            letter-spacing: 0.5px;
        }
        .stat-value {
            background: linear-gradient(135deg, #00d4ff 0%, #7c3aed 100%);
            -webkit-background-clip: text;
            -webkit-text-fill-color: transparent;
            background-clip: text;
            font-size: 24px;
            font-weight: 800;
        }
        .event-list {
            max-height: calc(100vh - 280px);
            overflow-y: auto;
            scrollbar-width: thin;
            scrollbar-color: #667eea #1a1a2e;
        }
        .event-list::-webkit-scrollbar {
            width: 8px;
        }
        .event-list::-webkit-scrollbar-track {
            background: #1a1a2e;
            border-radius: 4px;
        }
        .event-list::-webkit-scrollbar-thumb {
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            border-radius: 4px;
        }
        .event {
            background: rgba(30, 30, 46, 0.6);
            backdrop-filter: blur(10px);
            border-left: 4px solid #00d4ff;
            margin-bottom: 6px;
            padding: 10px 14px;
            border-radius: 8px;
            border: 1px solid rgba(255, 255, 255, 0.05);
            border-left-width: 4px;
            transition: all 0.2s ease;
            box-shadow: 0 2px 8px rgba(0, 0, 0, 0.2);
            max-width: 85%;
            font-size: 13px;
            cursor: pointer;
        }
        .event:hover {
            transform: translateX(4px);
            box-shadow: 0 4px 12px rgba(0, 0, 0, 0.3);
            border-color: rgba(255, 255, 255, 0.1);
        }
        .event-compact {
            display: flex;
            align-items: center;
            gap: 12px;
        }
        .event-expanded {
            display: none;
            margin-top: 12px;
            padding-top: 12px;
            border-top: 1px solid rgba(255, 255, 255, 0.1);
        }
        .event.expanded .event-compact {
            margin-bottom: 0;
        }
        .event.expanded .event-expanded {
            display: block;
        }
        .event-detail {
            margin-bottom: 6px;
            font-size: 12px;
            line-height: 1.6;
        }
        /* Events SENT TO LLM - aligned right */
        .event.LlmRequest {
            border-left-color: #ff6b35;
            background: rgba(255, 107, 53, 0.05);
            margin-left: auto;
            border-left: none;
            border-right: 4px solid #ff6b35;
        }
        .event.LlmRequest:hover {
            transform: translateX(-4px);
        }
        .event.ToolResultToLlm {
            border-left-color: #ff9e00;
            background: rgba(255, 158, 0, 0.05);
            margin-left: auto;
            border-left: none;
            border-right: 4px solid #ff9e00;
        }
        .event.ToolResultToLlm:hover {
            transform: translateX(-4px);
        }
        /* Events RECEIVED FROM LLM - aligned left */
        .event.LlmResponse {
            border-left-color: #00d4ff;
            background: rgba(0, 212, 255, 0.05);
        }
        /* Tool events - centered/left */
        .event.ToolCall {
            border-left-color: #ffd60a;
            background: rgba(255, 214, 10, 0.05);
        }
        .event.ToolExecutionStart {
            border-left-color: #ffb347;
            background: rgba(255, 179, 71, 0.05);
        }
        .event.ToolComplete {
            border-left-color: #06ffa5;
            background: rgba(6, 255, 165, 0.05);
        }
        .event.Diagnostic {
            border-left-color: #a78bfa;
            background: rgba(167, 139, 250, 0.05);
        }
        .event-seq {
            color: #71717a;
            font-weight: 600;
            font-size: 11px;
            min-width: 28px;
        }
        .event-type {
            font-weight: 700;
            font-size: 13px;
            min-width: 140px;
            letter-spacing: 0.3px;
        }
        .event.LlmRequest .event-type { color: #ff6b35; }
        .event.LlmResponse .event-type { color: #00d4ff; }
        .event.ToolCall .event-type { color: #ffd60a; }
        .event.ToolExecutionStart .event-type { color: #ffb347; }
        .event.ToolComplete .event-type { color: #06ffa5; }
        .event.ToolResultToLlm .event-type { color: #ff9e00; }
        .event.Diagnostic .event-type { color: #a78bfa; }
        .event-time {
            color: #71717a;
            font-size: 11px;
            font-weight: 500;
            min-width: 95px;
        }
        .event-data {
            flex: 1;
            color: #e4e4e7;
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
        }
        .event-key {
            color: #a1a1aa;
            font-weight: 600;
            margin-right: 4px;
        }
        .event-value {
            color: #fafafa;
            font-weight: 400;
        }
        .connection-status {
            position: fixed;
            top: 30px;
            right: 30px;
            padding: 10px 16px;
            border-radius: 20px;
            font-size: 12px;
            font-weight: 700;
            text-transform: uppercase;
            letter-spacing: 0.5px;
            box-shadow: 0 4px 12px rgba(0, 0, 0, 0.3);
            backdrop-filter: blur(10px);
        }
        .connection-status.connected {
            background: linear-gradient(135deg, #06ffa5 0%, #00d4ff 100%);
            color: #0f0f23;
        }
        .connection-status.disconnected {
            background: linear-gradient(135deg, #ff6b6b 0%, #ff4757 100%);
            color: white;
        }
        .system-prompt-section {
            margin-bottom: 25px;
            background: rgba(30, 30, 46, 0.7);
            backdrop-filter: blur(10px);
            border-radius: 12px;
            border: 1px solid rgba(255, 255, 255, 0.1);
            box-shadow: 0 8px 32px rgba(0, 0, 0, 0.3);
            overflow: hidden;
        }
        .system-prompt-header {
            padding: 15px 20px;
            display: flex;
            align-items: center;
            justify-content: space-between;
            background: rgba(124, 58, 237, 0.1);
            border-bottom: 1px solid rgba(255, 255, 255, 0.05);
        }
        .system-prompt-header-left {
            display: flex;
            align-items: center;
            gap: 12px;
            cursor: pointer;
            user-select: none;
            flex: 1;
        }
        .system-prompt-header-left:hover {
            opacity: 0.8;
        }
        .system-prompt-title {
            background: linear-gradient(135deg, #00d4ff 0%, #7c3aed 100%);
            -webkit-background-clip: text;
            -webkit-text-fill-color: transparent;
            background-clip: text;
            font-weight: 700;
            font-size: 14px;
            letter-spacing: 0.5px;
            text-transform: uppercase;
        }
        .system-prompt-toggle {
            color: #a1a1aa;
            font-size: 20px;
            transition: transform 0.2s ease;
        }
        .system-prompt-section.expanded .system-prompt-toggle {
            transform: rotate(180deg);
        }
        .copy-btn {
            background: linear-gradient(135deg, #06ffa5 0%, #00d4ff 100%);
            color: #0f0f23;
            border: none;
            padding: 6px 12px;
            border-radius: 6px;
            cursor: pointer;
            font-family: inherit;
            font-weight: 600;
            font-size: 11px;
            transition: all 0.2s ease;
            text-transform: uppercase;
            letter-spacing: 0.5px;
            box-shadow: 0 2px 8px rgba(6, 255, 165, 0.3);
        }
        .copy-btn:hover {
            transform: translateY(-1px);
            box-shadow: 0 4px 12px rgba(6, 255, 165, 0.4);
        }
        .copy-btn:active {
            transform: translateY(0);
        }
        .copy-btn.copied {
            background: linear-gradient(135deg, #7c3aed 0%, #667eea 100%);
            color: white;
        }
        .system-prompt-content {
            display: none;
            padding: 20px;
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', 'Roboto', sans-serif;
            font-size: 13px;
            line-height: 1.7;
            color: #e4e4e7;
            background: rgba(0, 0, 0, 0.2);
            max-height: 500px;
            overflow-y: auto;
            scrollbar-width: thin;
            scrollbar-color: #667eea #1a1a2e;
        }
        .system-prompt-content::-webkit-scrollbar {
            width: 6px;
        }
        .system-prompt-content::-webkit-scrollbar-track {
            background: #1a1a2e;
            border-radius: 3px;
        }
        .system-prompt-content::-webkit-scrollbar-thumb {
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            border-radius: 3px;
        }
        .system-prompt-section.expanded .system-prompt-content {
            display: block;
        }
        /* Markdown styling */
        .system-prompt-content h1,
        .system-prompt-content h2,
        .system-prompt-content h3 {
            background: linear-gradient(135deg, #00d4ff 0%, #7c3aed 100%);
            -webkit-background-clip: text;
            -webkit-text-fill-color: transparent;
            background-clip: text;
            margin-top: 20px;
            margin-bottom: 12px;
            font-weight: 700;
        }
        .system-prompt-content h1 { font-size: 20px; }
        .system-prompt-content h2 { font-size: 17px; }
        .system-prompt-content h3 { font-size: 15px; }
        .system-prompt-content p {
            margin-bottom: 12px;
        }
        .system-prompt-content ul,
        .system-prompt-content ol {
            margin-left: 20px;
            margin-bottom: 12px;
        }
        .system-prompt-content li {
            margin-bottom: 6px;
        }
        .system-prompt-content strong {
            color: #00d4ff;
            font-weight: 700;
        }
        .system-prompt-content code {
            background: rgba(255, 255, 255, 0.1);
            padding: 2px 6px;
            border-radius: 4px;
            font-family: 'SF Mono', 'Monaco', 'Menlo', 'Consolas', monospace;
            font-size: 12px;
            color: #ffd60a;
        }
        .system-prompt-content pre {
            background: rgba(0, 0, 0, 0.4);
            padding: 12px;
            border-radius: 6px;
            overflow-x: auto;
            margin-bottom: 12px;
            border-left: 3px solid #7c3aed;
            font-family: 'SF Mono', 'Monaco', 'Menlo', 'Consolas', monospace;
            white-space: pre;
        }
        .system-prompt-content pre code {
            background: transparent;
            padding: 0;
            color: #e4e4e7;
            font-family: 'SF Mono', 'Monaco', 'Menlo', 'Consolas', monospace;
            font-size: 12px;
            display: block;
            line-height: 1.5;
        }
        .system-prompt-content blockquote {
            border-left: 4px solid #667eea;
            padding-left: 16px;
            margin-left: 0;
            margin-bottom: 12px;
            color: #a1a1aa;
            font-style: italic;
        }
        .filter-section {
            margin-bottom: 25px;
            padding: 20px;
            background: rgba(30, 30, 46, 0.7);
            backdrop-filter: blur(10px);
            border-radius: 12px;
            border: 1px solid rgba(255, 255, 255, 0.1);
            box-shadow: 0 8px 32px rgba(0, 0, 0, 0.3);
        }
        .filter-title {
            background: linear-gradient(135deg, #00d4ff 0%, #7c3aed 100%);
            -webkit-background-clip: text;
            -webkit-text-fill-color: transparent;
            background-clip: text;
            font-weight: 700;
            font-size: 13px;
            letter-spacing: 0.5px;
            text-transform: uppercase;
            margin-bottom: 12px;
        }
        .filter-controls {
            display: flex;
            flex-wrap: wrap;
            gap: 12px;
            align-items: center;
        }
        .filter-checkbox {
            display: flex;
            align-items: center;
            gap: 6px;
            padding: 6px 12px;
            background: rgba(0, 0, 0, 0.2);
            border-radius: 6px;
            cursor: pointer;
            transition: all 0.2s ease;
            user-select: none;
        }
        .filter-checkbox:hover {
            background: rgba(0, 0, 0, 0.3);
        }
        .filter-checkbox input[type=""checkbox""] {
            cursor: pointer;
            width: 16px;
            height: 16px;
        }
        .filter-checkbox label {
            cursor: pointer;
            font-size: 12px;
            font-weight: 600;
        }
        .filter-checkbox.LlmRequest label { color: #ff6b35; }
        .filter-checkbox.LlmResponse label { color: #00d4ff; }
        .filter-checkbox.ToolCall label { color: #ffd60a; }
        .filter-checkbox.ToolExecutionStart label { color: #ffb347; }
        .filter-checkbox.ToolComplete label { color: #06ffa5; }
        .filter-checkbox.ToolResultToLlm label { color: #ff9e00; }
        .filter-checkbox.Diagnostic label { color: #a78bfa; }
        .filter-actions {
            display: flex;
            gap: 8px;
        }
        .filter-btn {
            background: rgba(124, 58, 237, 0.3);
            color: #e4e4e7;
            border: 1px solid rgba(124, 58, 237, 0.5);
            padding: 6px 12px;
            border-radius: 6px;
            cursor: pointer;
            font-family: inherit;
            font-weight: 600;
            font-size: 11px;
            transition: all 0.2s ease;
        }
        .filter-btn:hover {
            background: rgba(124, 58, 237, 0.5);
            border-color: rgba(124, 58, 237, 0.7);
        }
        .event-list.filtered .event {
            max-width: 95%;
            padding: 14px 18px;
            font-size: 14px;
        }
        .event-list.filtered .event-detail {
            font-size: 13px;
        }
        .event-list.filtered .event.LlmRequest .event-data,
        .event-list.filtered .event.LlmResponse .event-data {
            font-size: 14px;
            font-weight: 600;
        }
        .event-list.filtered .event.LlmRequest .event-detail,
        .event-list.filtered .event.LlmResponse .event-detail {
            font-size: 13px;
        }
        .event.LlmRequest .event-detail .event-value,
        .event.LlmResponse .event-detail .event-value {
            color: #fafafa;
            font-weight: 500;
        }
        /* Enhanced LLM Request/Response message visibility */
        .event.LlmRequest .user-message-display,
        .event.LlmResponse .response-message-display {
            background: rgba(255, 255, 255, 0.05);
            padding: 12px;
            border-radius: 6px;
            margin-top: 8px;
            font-size: 14px;
            line-height: 1.6;
            color: #fafafa;
            font-weight: 500;
            border-left: 3px solid;
        }
        .event.LlmRequest .user-message-display {
            border-left-color: #ff6b35;
        }
        .event.LlmResponse .response-message-display {
            border-left-color: #00d4ff;
        }
        .event-list.filtered .event.LlmRequest .user-message-display,
        .event-list.filtered .event.LlmResponse .response-message-display {
            font-size: 15px;
            padding: 16px;
            font-weight: 600;
        }
    </style>
</head>
<body>
    <div id=""status"" class=""connection-status disconnected"">Disconnected</div>
    <h1>Andy Engine Instrumentation</h1>

    <div class=""system-prompt-section"" id=""systemPromptSection"">
        <div class=""system-prompt-header"">
            <div class=""system-prompt-header-left"" onclick=""toggleSystemPrompt()"">
                <span class=""system-prompt-title"">System Prompt</span>
                <span class=""system-prompt-toggle"">▼</span>
            </div>
            <button class=""copy-btn"" id=""copyPromptBtn"" onclick=""copySystemPrompt(event)"">Copy</button>
        </div>
        <div class=""system-prompt-content"" id=""systemPromptContent"">Loading...</div>
    </div>

    <div class=""filter-section"">
        <div class=""filter-title"">Event Filters</div>
        <div class=""filter-controls"">
            <div class=""filter-checkbox LlmRequest"">
                <input type=""checkbox"" id=""filter-LlmRequest"" checked onchange=""updateFilters()"">
                <label for=""filter-LlmRequest"">LLM Request</label>
            </div>
            <div class=""filter-checkbox LlmResponse"">
                <input type=""checkbox"" id=""filter-LlmResponse"" checked onchange=""updateFilters()"">
                <label for=""filter-LlmResponse"">LLM Response</label>
            </div>
            <div class=""filter-checkbox ToolCall"">
                <input type=""checkbox"" id=""filter-ToolCall"" checked onchange=""updateFilters()"">
                <label for=""filter-ToolCall"">Tool Call</label>
            </div>
            <div class=""filter-checkbox ToolExecutionStart"">
                <input type=""checkbox"" id=""filter-ToolExecutionStart"" checked onchange=""updateFilters()"">
                <label for=""filter-ToolExecutionStart"">Tool Execution Start</label>
            </div>
            <div class=""filter-checkbox ToolComplete"">
                <input type=""checkbox"" id=""filter-ToolComplete"" checked onchange=""updateFilters()"">
                <label for=""filter-ToolComplete"">Tool Complete</label>
            </div>
            <div class=""filter-checkbox ToolResultToLlm"">
                <input type=""checkbox"" id=""filter-ToolResultToLlm"" checked onchange=""updateFilters()"">
                <label for=""filter-ToolResultToLlm"">Tool Result to LLM</label>
            </div>
            <div class=""filter-checkbox Diagnostic"">
                <input type=""checkbox"" id=""filter-Diagnostic"" checked onchange=""updateFilters()"">
                <label for=""filter-Diagnostic"">Diagnostic</label>
            </div>
            <div class=""filter-actions"">
                <button class=""filter-btn"" onclick=""selectAllFilters()"">Select All</button>
                <button class=""filter-btn"" onclick=""deselectAllFilters()"">Deselect All</button>
            </div>
        </div>
    </div>

    <div class=""stats"">
        <div class=""stat"">
            <span class=""stat-label"">TOTAL EVENTS</span>
            <span class=""stat-value"" id=""totalEvents"">0</span>
        </div>
        <div class=""stat"">
            <span class=""stat-label"">LLM REQUESTS</span>
            <span class=""stat-value"" id=""llmRequests"">0</span>
        </div>
        <div class=""stat"">
            <span class=""stat-label"">TOOL CALLS</span>
            <span class=""stat-value"" id=""toolCalls"">0</span>
        </div>
        <div class=""stat"">
            <span class=""stat-label"">AVG RESPONSE TIME</span>
            <span class=""stat-value"" id=""avgResponseTime"">-</span>
        </div>
    </div>

    <div class=""controls"">
        <button onclick=""clearEvents()"">Clear Events</button>
        <button onclick=""toggleAutoscroll()"" id=""autoscrollBtn"">Autoscroll: ON</button>
    </div>

    <div class=""event-list"" id=""eventList""></div>

    <script>
        let eventCount = 0;
        let llmRequestCount = 0;
        let toolCallCount = 0;
        let responseTimes = [];
        let autoscroll = true;
        let activeFilters = new Set(['LlmRequest', 'LlmResponse', 'ToolCall', 'ToolExecutionStart', 'ToolComplete', 'ToolResultToLlm', 'Diagnostic']);

        const eventSource = new EventSource('/events');

        eventSource.onopen = () => {
            document.getElementById('status').className = 'connection-status connected';
            document.getElementById('status').textContent = 'Connected';
        };

        eventSource.onerror = () => {
            document.getElementById('status').className = 'connection-status disconnected';
            document.getElementById('status').textContent = 'Disconnected';
        };

        eventSource.addEventListener('LlmRequest', (e) => handleEvent(JSON.parse(e.data)));
        eventSource.addEventListener('LlmResponse', (e) => handleEvent(JSON.parse(e.data)));
        eventSource.addEventListener('ToolCall', (e) => handleEvent(JSON.parse(e.data)));
        eventSource.addEventListener('ToolExecutionStart', (e) => handleEvent(JSON.parse(e.data)));
        eventSource.addEventListener('ToolComplete', (e) => handleEvent(JSON.parse(e.data)));
        eventSource.addEventListener('ToolResultToLlm', (e) => handleEvent(JSON.parse(e.data)));
        eventSource.addEventListener('Diagnostic', (e) => handleEvent(JSON.parse(e.data)));

        function handleEvent(event) {
            eventCount++;

            if (event.eventType === 'LlmRequest') {
                llmRequestCount++;
            } else if (event.eventType === 'ToolCall') {
                toolCallCount++;
            } else if (event.eventType === 'LlmResponse' && event.duration) {
                const ms = parseDuration(event.duration);
                responseTimes.push(ms);
                if (responseTimes.length > 10) responseTimes.shift();
            }

            updateStats();
            renderEvent(event);
        }

        function parseDuration(duration) {
            // Parse ""00:00:01.234"" format to milliseconds
            const parts = duration.split(':');
            if (parts.length === 3) {
                const seconds = parseFloat(parts[2]);
                return seconds * 1000;
            }
            return 0;
        }

        function updateStats() {
            document.getElementById('totalEvents').textContent = eventCount;
            document.getElementById('llmRequests').textContent = llmRequestCount;
            document.getElementById('toolCalls').textContent = toolCallCount;

            if (responseTimes.length > 0) {
                const avg = responseTimes.reduce((a, b) => a + b, 0) / responseTimes.length;
                document.getElementById('avgResponseTime').textContent = `${(avg / 1000).toFixed(2)}s`;
            }
        }

        function renderEvent(event) {
            const eventList = document.getElementById('eventList');
            const eventDiv = document.createElement('div');
            eventDiv.className = `event ${event.eventType}`;
            eventDiv.dataset.eventType = event.eventType;

            // Check if event should be visible based on filters
            if (!activeFilters.has(event.eventType)) {
                eventDiv.style.display = 'none';
            }

            const time = new Date(event.timestamp).toLocaleTimeString('en-US', {
                hour12: false,
                hour: '2-digit',
                minute: '2-digit',
                second: '2-digit',
                fractionalSecondDigits: 3
            });

            const timestamp = new Date(event.timestamp).toISOString();

            let dataText = '';
            let expandedHtml = '';

            if (event.eventType === 'LlmRequest') {
                dataText = `${event.provider}/${event.model} • ${escapeHtml(event.userMessage.substring(0, 80))}${event.userMessage.length > 80 ? '...' : ''} • ${event.estimatedInputTokens} tokens`;
                expandedHtml = `
                    <div class=""event-detail""><span class=""event-key"">Timestamp:</span> ${timestamp}</div>
                    <div class=""event-detail""><span class=""event-key"">Provider:</span> ${event.provider}</div>
                    <div class=""event-detail""><span class=""event-key"">Model:</span> ${event.model}</div>
                    <div class=""event-detail""><span class=""event-key"">Conversation Turns:</span> ${event.conversationTurns}</div>
                    <div class=""event-detail""><span class=""event-key"">Input Tokens:</span> ${event.estimatedInputTokens}</div>
                    <div class=""event-detail""><span class=""event-key"">User Message:</span></div>
                    <div class=""user-message-display"">${escapeHtml(event.userMessage)}</div>
                `;
            } else if (event.eventType === 'LlmResponse') {
                const preview = event.response?.substring(0, 80) || '';
                dataText = `${event.duration} • ${event.estimatedOutputTokens} tokens • ${escapeHtml(preview)}${event.response?.length > 80 ? '...' : ''}`;
                expandedHtml = `
                    <div class=""event-detail""><span class=""event-key"">Timestamp:</span> ${timestamp}</div>
                    <div class=""event-detail""><span class=""event-key"">Success:</span> ${event.success}</div>
                    <div class=""event-detail""><span class=""event-key"">Duration:</span> ${event.duration}</div>
                    <div class=""event-detail""><span class=""event-key"">Stop Reason:</span> ${event.stopReason || 'N/A'}</div>
                    <div class=""event-detail""><span class=""event-key"">Output Tokens:</span> ${event.estimatedOutputTokens}</div>
                    <div class=""event-detail""><span class=""event-key"">Response:</span></div>
                    <div class=""response-message-display"">${escapeHtml(event.response || '')}</div>
                `;
            } else if (event.eventType === 'ToolCall') {
                const paramCount = event.parameters ? Object.keys(event.parameters).length : 0;
                dataText = `${event.toolName}${paramCount > 0 ? ` • ${paramCount} param${paramCount !== 1 ? 's' : ''}` : ''}`;

                let paramsHtml = '';
                if (event.parameters && Object.keys(event.parameters).length > 0) {
                    paramsHtml = '<div class=""event-detail""><span class=""event-key"">Parameters:</span></div>';
                    for (const [key, value] of Object.entries(event.parameters)) {
                        const valueStr = typeof value === 'object' ? JSON.stringify(value, null, 2) : String(value);
                        paramsHtml += `<div class=""event-detail"" style=""margin-left: 20px; margin-top: 4px;""><span class=""event-key"">${escapeHtml(key)}:</span><div style=""margin-left: 20px; white-space: pre-wrap; font-family: monospace; background: rgba(0,0,0,0.3); padding: 6px; border-radius: 4px; margin-top: 2px;"">${escapeHtml(valueStr)}</div></div>`;
                    }
                } else {
                    paramsHtml = '<div class=""event-detail""><span class=""event-key"">Parameters:</span> <span style=""color: #71717a; font-style: italic;"">Not yet available</span></div>';
                }

                expandedHtml = `
                    <div class=""event-detail""><span class=""event-key"">Timestamp:</span> ${timestamp}</div>
                    <div class=""event-detail""><span class=""event-key"">Tool Name:</span> ${event.toolName}</div>
                    <div class=""event-detail""><span class=""event-key"">Tool ID:</span> ${event.toolId}</div>
                    ${paramsHtml}
                `;
            } else if (event.eventType === 'ToolExecutionStart') {
                const paramCount = event.parameters ? Object.keys(event.parameters).length : 0;
                dataText = `${event.toolName} • ${paramCount} param${paramCount !== 1 ? 's' : ''}`;

                let paramsHtml = '';
                if (event.parameters && Object.keys(event.parameters).length > 0) {
                    paramsHtml = '<div class=""event-detail""><span class=""event-key"">Parameters:</span></div>';
                    for (const [key, value] of Object.entries(event.parameters)) {
                        const valueStr = typeof value === 'object' ? JSON.stringify(value, null, 2) : String(value);
                        paramsHtml += `<div class=""event-detail"" style=""margin-left: 20px; margin-top: 4px;""><span class=""event-key"">${escapeHtml(key)}:</span><div style=""margin-left: 20px; white-space: pre-wrap; font-family: monospace; background: rgba(0,0,0,0.3); padding: 6px; border-radius: 4px; margin-top: 2px;"">${escapeHtml(valueStr)}</div></div>`;
                    }
                } else {
                    paramsHtml = '<div class=""event-detail""><span class=""event-key"">Parameters:</span> <span style=""color: #71717a; font-style: italic;"">None</span></div>';
                }

                expandedHtml = `
                    <div class=""event-detail""><span class=""event-key"">Timestamp:</span> ${timestamp}</div>
                    <div class=""event-detail""><span class=""event-key"">Tool Name:</span> ${event.toolName}</div>
                    <div class=""event-detail""><span class=""event-key"">Tool ID:</span> ${event.toolId}</div>
                    ${paramsHtml}
                `;
            } else if (event.eventType === 'ToolComplete') {
                const result = event.result || '';
                dataText = `${event.toolName} • ${event.duration} • ${escapeHtml(result.substring(0, 60))}${result.length > 60 ? '...' : ''}`;
                expandedHtml = `
                    <div class=""event-detail""><span class=""event-key"">Timestamp:</span> ${timestamp}</div>
                    <div class=""event-detail""><span class=""event-key"">Tool Name:</span> ${event.toolName}</div>
                    <div class=""event-detail""><span class=""event-key"">Tool ID:</span> ${event.toolId}</div>
                    <div class=""event-detail""><span class=""event-key"">Success:</span> ${event.success}</div>
                    <div class=""event-detail""><span class=""event-key"">Duration:</span> ${event.duration}</div>
                    <div class=""event-detail""><span class=""event-key"">Result:</span> ${escapeHtml(event.result || '')}</div>
                `;
            } else if (event.eventType === 'ToolResultToLlm') {
                const result = event.result || '';
                const dataInfo = event.hasStructuredData ? `📦 ${event.dataType}` : `${event.resultLength} chars`;
                dataText = `${event.toolName} • ${dataInfo} • ${escapeHtml(result.substring(0, 50))}${result.length > 50 ? '...' : ''}`;

                let structuredDataHtml = '';
                if (event.structuredData) {
                    const dataStr = JSON.stringify(event.structuredData, null, 2);
                    structuredDataHtml = `<div class=""event-detail""><span class=""event-key"">Structured Data Sent to LLM:</span></div><div class=""event-detail"" style=""margin-left: 20px; white-space: pre-wrap; font-family: monospace; background: rgba(0,0,0,0.3); padding: 8px; border-radius: 4px; margin-top: 4px;"">${escapeHtml(dataStr)}</div>`;
                }

                expandedHtml = `
                    <div class=""event-detail""><span class=""event-key"">Timestamp:</span> ${timestamp}</div>
                    <div class=""event-detail""><span class=""event-key"">Tool Name:</span> ${event.toolName}</div>
                    <div class=""event-detail""><span class=""event-key"">Tool ID:</span> ${event.toolId}</div>
                    <div class=""event-detail""><span class=""event-key"">Success:</span> ${event.success}</div>
                    <div class=""event-detail""><span class=""event-key"">Has Structured Data:</span> ${event.hasStructuredData ? 'Yes (📦 ' + event.dataType + ')' : 'No'}</div>
                    <div class=""event-detail""><span class=""event-key"">Result Summary Length:</span> ${event.resultLength} chars</div>
                    <div class=""event-detail""><span class=""event-key"">Result Summary:</span> ${escapeHtml(event.result || '')}</div>
                    ${structuredDataHtml}
                `;
            } else if (event.eventType === 'Diagnostic') {
                dataText = `${event.level} • ${event.source} • ${escapeHtml(event.message)}`;
                expandedHtml = `
                    <div class=""event-detail""><span class=""event-key"">Timestamp:</span> ${timestamp}</div>
                    <div class=""event-detail""><span class=""event-key"">Level:</span> ${event.level}</div>
                    <div class=""event-detail""><span class=""event-key"">Source:</span> ${event.source}</div>
                    <div class=""event-detail""><span class=""event-key"">Message:</span> ${escapeHtml(event.message)}</div>
                `;
            }

            eventDiv.innerHTML = `
                <div class=""event-compact"">
                    <span class=""event-seq"">#${event.sequenceNumber}</span>
                    <span class=""event-type"">${event.eventType}</span>
                    <span class=""event-time"">${time}</span>
                    <span class=""event-data"">${dataText}</span>
                </div>
                <div class=""event-expanded"">
                    ${expandedHtml}
                </div>
            `;

            eventDiv.addEventListener('click', () => {
                eventDiv.classList.toggle('expanded');
            });

            eventList.insertBefore(eventDiv, eventList.firstChild);

            if (autoscroll) {
                eventList.scrollTop = 0;
            }
        }

        function escapeHtml(text) {
            const div = document.createElement('div');
            div.textContent = text;
            return div.innerHTML;
        }

        function clearEvents() {
            fetch('/clear', { method: 'POST' })
                .then(() => {
                    document.getElementById('eventList').innerHTML = '';
                    eventCount = 0;
                    llmRequestCount = 0;
                    toolCallCount = 0;
                    responseTimes = [];
                    updateStats();
                });
        }

        function toggleAutoscroll() {
            autoscroll = !autoscroll;
            document.getElementById('autoscrollBtn').textContent = `Autoscroll: ${autoscroll ? 'ON' : 'OFF'}`;
        }

        function toggleSystemPrompt() {
            document.getElementById('systemPromptSection').classList.toggle('expanded');
        }

        function updateFilters() {
            const eventTypes = ['LlmRequest', 'LlmResponse', 'ToolCall', 'ToolExecutionStart', 'ToolComplete', 'ToolResultToLlm', 'Diagnostic'];
            activeFilters.clear();

            eventTypes.forEach(type => {
                const checkbox = document.getElementById(`filter-${type}`);
                if (checkbox.checked) {
                    activeFilters.add(type);
                }
            });

            // Update visibility of all events
            const allEvents = document.querySelectorAll('.event');
            allEvents.forEach(eventDiv => {
                const eventType = eventDiv.dataset.eventType;
                if (activeFilters.has(eventType)) {
                    eventDiv.style.display = '';
                } else {
                    eventDiv.style.display = 'none';
                }
            });

            // Add/remove filtered class to event-list for enhanced styling
            const eventList = document.getElementById('eventList');
            if (activeFilters.size < 7) {
                eventList.classList.add('filtered');
            } else {
                eventList.classList.remove('filtered');
            }
        }

        function selectAllFilters() {
            const eventTypes = ['LlmRequest', 'LlmResponse', 'ToolCall', 'ToolExecutionStart', 'ToolComplete', 'ToolResultToLlm', 'Diagnostic'];
            eventTypes.forEach(type => {
                document.getElementById(`filter-${type}`).checked = true;
            });
            updateFilters();
        }

        function deselectAllFilters() {
            const eventTypes = ['LlmRequest', 'LlmResponse', 'ToolCall', 'ToolExecutionStart', 'ToolComplete', 'ToolResultToLlm', 'Diagnostic'];
            eventTypes.forEach(type => {
                document.getElementById(`filter-${type}`).checked = false;
            });
            updateFilters();
        }

        // Store raw markdown for copying
        let rawSystemPrompt = '';

        // Configure marked.js for proper rendering
        marked.setOptions({
            breaks: true,
            gfm: true,
            headerIds: false,
            mangle: false
        });

        // Fetch system prompt on page load
        fetch('/system-prompt')
            .then(response => response.json())
            .then(data => {
                const content = document.getElementById('systemPromptContent');
                if (data.systemPrompt && data.systemPrompt.length > 0) {
                    rawSystemPrompt = data.systemPrompt;
                    // Render markdown to HTML
                    content.innerHTML = marked.parse(data.systemPrompt);
                } else {
                    content.textContent = 'No system prompt configured';
                    content.style.fontStyle = 'italic';
                    content.style.color = '#71717a';
                }
            })
            .catch(error => {
                console.error('Failed to fetch system prompt:', error);
                document.getElementById('systemPromptContent').textContent = 'Failed to load system prompt';
            });

        // Copy system prompt to clipboard
        function copySystemPrompt(event) {
            event.stopPropagation(); // Prevent toggling expand/collapse

            if (!rawSystemPrompt) {
                return;
            }

            navigator.clipboard.writeText(rawSystemPrompt).then(() => {
                const btn = document.getElementById('copyPromptBtn');
                const originalText = btn.textContent;
                btn.textContent = 'Copied!';
                btn.classList.add('copied');

                setTimeout(() => {
                    btn.textContent = originalText;
                    btn.classList.remove('copied');
                }, 2000);
            }).catch(err => {
                console.error('Failed to copy:', err);
                alert('Failed to copy to clipboard');
            });
        }
    </script>
</body>
</html>";
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    private class DelegateLoggerProvider : ILoggerProvider
    {
        private readonly ILogger _logger;

        public DelegateLoggerProvider(ILogger logger)
        {
            _logger = logger;
        }

        public ILogger CreateLogger(string categoryName) => _logger;
        public void Dispose() { }
    }
}
