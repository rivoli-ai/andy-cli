using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Diagnostics;

/// <summary>
/// Diagnostic tool to capture and analyze raw Qwen model responses
/// </summary>
public class QwenResponseDiagnostic
{
    private readonly ILogger<QwenResponseDiagnostic>? _logger;
    private readonly string _diagnosticPath;
    private static int _responseCounter = 0;

    public QwenResponseDiagnostic(ILogger<QwenResponseDiagnostic>? logger = null)
    {
        _logger = logger;
        _diagnosticPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".andy",
            "diagnostics",
            DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss")
        );

        Directory.CreateDirectory(_diagnosticPath);

        // Write initialization marker file
        var initFile = Path.Combine(_diagnosticPath, "INITIALIZED.txt");
        File.WriteAllText(initFile, $"Diagnostic initialized at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n");

        _logger?.LogInformation("Diagnostic output directory: {Path}", _diagnosticPath);
    }

    /// <summary>
    /// Capture raw request sent to the model
    /// </summary>
    public async Task CaptureRawRequest(string requestJson)
    {
        var timestamp = DateTime.UtcNow;
        var fileName = $"request_{timestamp:HHmmss_fff}.json";
        var filePath = Path.Combine(_diagnosticPath, fileName);

        var wrapper = new
        {
            Type = "REQUEST",
            Timestamp = timestamp,
            Request = requestJson
        };

        var json = JsonSerializer.Serialize(wrapper, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json);
    }

    /// <summary>
    /// Capture raw response from the model
    /// </summary>
    public async Task CaptureRawResponse(string prompt, string rawResponse, string? parsedResult = null)
    {
        _responseCounter++;
        var timestamp = DateTime.UtcNow;

        // Write a simple marker file first to verify method is called
        var markerFile = Path.Combine(_diagnosticPath, $"CALLED_{_responseCounter:D4}.txt");
        await File.WriteAllTextAsync(markerFile, $"Called at {timestamp:yyyy-MM-dd HH:mm:ss} UTC\nPrompt length: {prompt.Length}\nResponse length: {rawResponse.Length}\n");

        var diagnostic = new
        {
            ResponseId = _responseCounter,
            Timestamp = timestamp,
            Prompt = prompt,
            RawResponse = rawResponse,
            ParsedResult = parsedResult,
            ResponseLength = rawResponse.Length,
            Analysis = AnalyzeResponse(rawResponse)
        };

        // Save to file
        var fileName = $"response_{_responseCounter:D4}_{timestamp:HHmmss}.json";
        var filePath = Path.Combine(_diagnosticPath, fileName);

        var json = JsonSerializer.Serialize(diagnostic, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(filePath, json);

        // Also log key findings
        _logger?.LogInformation("Captured response #{ResponseId}", _responseCounter);
        _logger?.LogInformation("Raw response length: {Length} chars", rawResponse.Length);

        if (diagnostic.Analysis.ContainsToolCallPattern)
        {
            _logger?.LogWarning("FOUND TOOL CALL PATTERN: {Pattern}",
                string.Join(", ", diagnostic.Analysis.ToolCallPatterns));
        }
    }

    /// <summary>
    /// Analyze response for tool call patterns
    /// </summary>
    private ResponseAnalysis AnalyzeResponse(string response)
    {
        var analysis = new ResponseAnalysis();

        // Check for various tool call patterns
        analysis.ContainsToolCallKeyword = response.Contains("tool_call", StringComparison.OrdinalIgnoreCase);
        analysis.ContainsFunctionKeyword = response.Contains("function", StringComparison.OrdinalIgnoreCase);
        analysis.ContainsToolKeyword = response.Contains("\"tool\"", StringComparison.OrdinalIgnoreCase);
        analysis.ContainsParametersKeyword = response.Contains("parameters", StringComparison.OrdinalIgnoreCase);
        analysis.ContainsArgumentsKeyword = response.Contains("arguments", StringComparison.OrdinalIgnoreCase);

        // Check for JSON structures
        analysis.ContainsJsonBraces = response.Contains("{") && response.Contains("}");

        // Look for specific patterns
        var patterns = new[]
        {
            @"\{[^{}]*""tool_call""[^{}]*\}",
            @"\{[^{}]*""function""[^{}]*\}",
            @"\{[^{}]*""tool""[^{}]*\}",
            @"<tool_call>.*?</tool_call>",
            @"<function>.*?</function>",
            @"\[TOOL:.*?\]",
            @"```tool.*?```",
            @"```json.*?tool.*?```"
        };

        foreach (var pattern in patterns)
        {
            var regex = new System.Text.RegularExpressions.Regex(pattern,
                System.Text.RegularExpressions.RegexOptions.Singleline |
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (regex.IsMatch(response))
            {
                analysis.ToolCallPatterns.Add(pattern);

                // Capture the actual matches
                var matches = regex.Matches(response);
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    analysis.MatchedContent.Add(new MatchedPattern
                    {
                        Pattern = pattern,
                        Content = match.Value,
                        Index = match.Index,
                        Length = match.Length
                    });
                }
            }
        }

        analysis.ContainsToolCallPattern = analysis.ToolCallPatterns.Count > 0;

        // Check for specific tool names
        var commonTools = new[] { "list_directory", "read_file", "write_file", "search", "bash_command" };
        foreach (var tool in commonTools)
        {
            if (response.Contains(tool, StringComparison.OrdinalIgnoreCase))
            {
                analysis.MentionedTools.Add(tool);
            }
        }

        return analysis;
    }

    /// <summary>
    /// Generate a summary report of all captured responses
    /// </summary>
    public async Task GenerateSummaryReport()
    {
        var summaryPath = Path.Combine(_diagnosticPath, "SUMMARY.md");
        var summary = new System.Text.StringBuilder();

        summary.AppendLine("# Qwen Response Diagnostic Summary");
        summary.AppendLine($"\nGenerated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        summary.AppendLine($"Total responses captured: {_responseCounter}");
        summary.AppendLine($"\nDiagnostic files location: `{_diagnosticPath}`");

        summary.AppendLine("\n## Key Findings");

        // Analyze all response files
        var files = Directory.GetFiles(_diagnosticPath, "response_*.json");
        var toolCallCount = 0;
        var patterns = new HashSet<string>();

        foreach (var file in files)
        {
            var json = await File.ReadAllTextAsync(file);
            var diagnostic = JsonDocument.Parse(json);
            var analysis = diagnostic.RootElement.GetProperty("Analysis");

            if (analysis.GetProperty("ContainsToolCallPattern").GetBoolean())
            {
                toolCallCount++;

                var toolPatterns = analysis.GetProperty("ToolCallPatterns");
                foreach (var pattern in toolPatterns.EnumerateArray())
                {
                    patterns.Add(pattern.GetString() ?? "");
                }
            }
        }

        summary.AppendLine($"\n- Responses with tool call patterns: {toolCallCount}/{_responseCounter}");
        summary.AppendLine($"- Unique patterns found: {patterns.Count}");

        if (patterns.Count > 0)
        {
            summary.AppendLine("\n### Detected Patterns:");
            foreach (var pattern in patterns)
            {
                summary.AppendLine($"- `{pattern}`");
            }
        }

        summary.AppendLine("\n## Recommendations");
        summary.AppendLine("\n1. Review the individual JSON files for detailed analysis");
        summary.AppendLine("2. Look for consistent patterns in successful tool calls");
        summary.AppendLine("3. Update QwenParser regex patterns based on findings");

        await File.WriteAllTextAsync(summaryPath, summary.ToString());

        _logger?.LogInformation("Summary report generated: {Path}", summaryPath);
    }

    private class ResponseAnalysis
    {
        public bool ContainsToolCallKeyword { get; set; }
        public bool ContainsFunctionKeyword { get; set; }
        public bool ContainsToolKeyword { get; set; }
        public bool ContainsParametersKeyword { get; set; }
        public bool ContainsArgumentsKeyword { get; set; }
        public bool ContainsJsonBraces { get; set; }
        public bool ContainsToolCallPattern { get; set; }
        public List<string> ToolCallPatterns { get; set; } = new();
        public List<string> MentionedTools { get; set; } = new();
        public List<MatchedPattern> MatchedContent { get; set; } = new();
    }

    private class MatchedPattern
    {
        public string Pattern { get; set; } = "";
        public string Content { get; set; } = "";
        public int Index { get; set; }
        public int Length { get; set; }
    }
}