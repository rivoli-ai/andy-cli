using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Parsing.Validation;

/// <summary>
/// Detects when LLM is hallucinating tool results instead of actually executing tools
/// </summary>
public class HallucinationDetector
{
    private readonly ILogger<HallucinationDetector>? _logger;

    // Patterns that indicate fake tool results
    private static readonly Regex FakeToolResultPattern = new(
        @"\[Tool Results?\]|\[Tool Execution\]|\[Output\]|\[Result\]|<<<.*?>>>|```tool.*?```",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Pattern for fake tool JSON that looks like the model is pretending to call tools
    private static readonly Regex FakeToolJsonPattern = new(
        @"^\s*\[Tool Results?\]\s*\n\s*\{.*?""tool""\s*:\s*"".*?""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

    // Pattern for detecting code blocks that claim to be file contents but weren't from tool calls
    private static readonly Regex FakeFileContentPattern = new(
        @"(?:Here(?:'s| is) (?:the )?(?:content|code)|The (?:file|code) contains?|File contents?:).*?```[\s\S]*?```",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

    // Pattern for responses that claim to have read/executed something without tool calls
    private static readonly Regex ClaimsWithoutActionPattern = new(
        @"(?:I've |I have |I |Let me )(?:read|checked|looked at|examined|found|executed|ran|listed)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Pattern for fake directory listings
    private static readonly Regex FakeDirectoryPattern = new(
        @"(?:├──|└──|│\s+|Directory listing:|Files? found:)",
        RegexOptions.Compiled);

    public HallucinationDetector(ILogger<HallucinationDetector>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Check if the response contains hallucinated tool results
    /// </summary>
    public HallucinationCheckResult CheckForHallucination(string response, bool hadToolCalls)
    {
        var result = new HallucinationCheckResult();

        if (string.IsNullOrWhiteSpace(response))
        {
            return result;
        }

        // Check for fake tool result markers
        if (FakeToolResultPattern.IsMatch(response) || FakeToolJsonPattern.IsMatch(response))
        {
            result.HasFakeToolResults = true;
            result.Issues.Add("Response contains fake tool result markers like [Tool Results]");
            _logger?.LogWarning("Detected fake tool result markers in response");
        }

        // Check for fake file content
        if (FakeFileContentPattern.IsMatch(response) && !hadToolCalls)
        {
            result.HasFakeFileContent = true;
            result.Issues.Add("Response claims to show file content without actual tool calls");
            _logger?.LogWarning("Detected fake file content without tool calls");
        }

        // Check for claims without corresponding tool calls
        if (ClaimsWithoutActionPattern.IsMatch(response) && !hadToolCalls)
        {
            var claims = ClaimsWithoutActionPattern.Matches(response);
            if (claims.Count > 0)
            {
                result.HasUnsubstantiatedClaims = true;
                result.Issues.Add($"Response claims to have performed {claims.Count} action(s) without tool calls");
                _logger?.LogWarning("Detected {Count} unsubstantiated claims", claims.Count);
            }
        }

        // Check for fake directory listings
        if (FakeDirectoryPattern.IsMatch(response) && !hadToolCalls)
        {
            result.HasFakeDirectoryListing = true;
            result.Issues.Add("Response contains directory listing without list_directory tool call");
            _logger?.LogWarning("Detected fake directory listing");
        }

        // Check for suspicious code blocks claiming to be from files
        var codeBlocks = Regex.Matches(response, @"```(?<lang>\w+)?\s*(?<code>[\s\S]*?)```");
        foreach (Match block in codeBlocks)
        {
            var code = block.Groups["code"].Value;
            var lang = block.Groups["lang"].Value;

            // If it looks like a complete class/module and we didn't have tool calls, it's suspicious
            if (!hadToolCalls && LooksLikeCompleteCode(code))
            {
                result.HasSuspiciousCodeBlocks = true;
                result.Issues.Add($"Response contains complete {lang} code without read_file tool call");
                _logger?.LogWarning("Detected suspicious code block without tool call");
            }
        }

        result.IsHallucinating = result.HasFakeToolResults ||
                                result.HasFakeFileContent ||
                                result.HasFakeDirectoryListing ||
                                result.HasSuspiciousCodeBlocks ||
                                (result.HasUnsubstantiatedClaims && result.Issues.Count > 1);

        if (result.IsHallucinating)
        {
            result.SuggestedAction = "The model appears to be hallucinating. Request should be retried with stricter prompting.";
            _logger?.LogError("Hallucination detected with {IssueCount} issues", result.Issues.Count);
        }

        return result;
    }

    /// <summary>
    /// Clean hallucinated content from response
    /// </summary>
    public string CleanHallucinatedContent(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return response;

        var cleaned = response;

        // Remove fake tool result markers
        cleaned = FakeToolResultPattern.Replace(cleaned, "");

        // Remove fake bracketed outputs
        cleaned = Regex.Replace(cleaned, @"\[(?:Tool |Output|Result|File).*?\][\s\S]*?(?=\n\n|\z)", "",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        // Remove directory tree drawings
        cleaned = Regex.Replace(cleaned, @"(?:├──|└──|│).*\n", "", RegexOptions.Multiline);

        // Clean up extra whitespace
        cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n");

        return cleaned.Trim();
    }

    private bool LooksLikeCompleteCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length < 100)
            return false;

        // Check for class/interface/namespace declarations (C#)
        if (Regex.IsMatch(code, @"^\s*(public|private|internal)?\s*(class|interface|namespace)\s+\w+",
            RegexOptions.Multiline))
            return true;

        // Check for function/class definitions (Python)
        if (Regex.IsMatch(code, @"^\s*(def|class)\s+\w+", RegexOptions.Multiline))
            return true;

        // Check for module exports (JavaScript/TypeScript)
        if (Regex.IsMatch(code, @"^\s*(export|module\.exports)", RegexOptions.Multiline))
            return true;

        return false;
    }
}

/// <summary>
/// Result of hallucination check
/// </summary>
public class HallucinationCheckResult
{
    public bool IsHallucinating { get; set; }
    public bool HasFakeToolResults { get; set; }
    public bool HasFakeFileContent { get; set; }
    public bool HasFakeDirectoryListing { get; set; }
    public bool HasUnsubstantiatedClaims { get; set; }
    public bool HasSuspiciousCodeBlocks { get; set; }
    public List<string> Issues { get; set; } = new();
    public string? SuggestedAction { get; set; }
}