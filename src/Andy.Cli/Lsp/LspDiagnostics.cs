using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Andy.Cli.Lsp;

/// <summary>Severity of a single language-server diagnostic, mirroring the LSP numeric scale.</summary>
public enum LspDiagnosticSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4,
}

/// <summary>
/// One diagnostic, flattened to the fields worth spending model context on.
/// </summary>
/// <param name="Severity">Error, warning, information, or hint.</param>
/// <param name="Line">1-based line number (LSP is 0-based on the wire).</param>
/// <param name="Column">1-based column number.</param>
/// <param name="Message">The diagnostic text, already clamped to <see cref="LspLimits.MaxMessageLength"/>.</param>
/// <param name="Code">The server's rule/error code, when it supplied one.</param>
/// <param name="Source">The analyzer that produced it, when the server supplied one.</param>
public sealed record LspDiagnostic(
    LspDiagnosticSeverity Severity,
    int Line,
    int Column,
    string Message,
    string? Code,
    string? Source)
{
    public string Format()
    {
        var prefix = Severity switch
        {
            LspDiagnosticSeverity.Error => "error",
            LspDiagnosticSeverity.Warning => "warning",
            LspDiagnosticSeverity.Information => "info",
            _ => "hint",
        };

        var code = string.IsNullOrWhiteSpace(Code) ? string.Empty : $" [{Code}]";
        return $"{Line}:{Column} {prefix}{code}: {Message}";
    }
}

/// <summary>How a changed-file diagnostics request ended.</summary>
public enum LspDiagnosticsStatus
{
    /// <summary>The server published diagnostics for the file within the deadline.</summary>
    Received,

    /// <summary>The server was reachable but published nothing before the deadline elapsed.</summary>
    TimedOut,

    /// <summary>No server could be started, or the one that was running has gone away.</summary>
    ServerUnavailable,

    /// <summary>The file is outside the active workspace and no explicit opt-in was given.</summary>
    OutsideWorkspace,

    /// <summary>The file was skipped (too large, unreadable, or deleted by the mutation).</summary>
    Skipped,
}

/// <summary>
/// The bounded, structured result of asking a language server about one changed file.
///
/// Bounded is load-bearing: a single generated file can produce thousands of diagnostics, and both
/// the model context and the terminal feed have to survive that. Whatever is dropped is reported
/// through <see cref="OmittedCount"/> / <see cref="TruncationReason"/> rather than silently lost.
/// </summary>
public sealed record LspDiagnosticsReport
{
    public required string ServerId { get; init; }
    public required string FilePath { get; init; }
    public required LspDiagnosticsStatus Status { get; init; }
    public IReadOnlyList<LspDiagnostic> Diagnostics { get; init; } = Array.Empty<LspDiagnostic>();

    /// <summary>Total diagnostics the server reported, before per-file bounding.</summary>
    public int TotalCount { get; init; }

    /// <summary>How many diagnostics were dropped to stay inside the per-file limits.</summary>
    public int OmittedCount { get; init; }

    /// <summary>Why anything was dropped, or null when the report is complete.</summary>
    public string? TruncationReason { get; init; }

    /// <summary>Explanation for a non-<see cref="LspDiagnosticsStatus.Received"/> status.</summary>
    public string? Detail { get; init; }

    public int ErrorCount => Diagnostics.Count(d => d.Severity == LspDiagnosticSeverity.Error);
    public int WarningCount => Diagnostics.Count(d => d.Severity == LspDiagnosticSeverity.Warning);

    public bool IsTruncated => OmittedCount > 0;

    /// <summary>Nothing worth telling anyone about.</summary>
    public bool IsSilent => Status == LspDiagnosticsStatus.Received && Diagnostics.Count == 0;

    /// <summary>
    /// Applies the per-file bounds to a raw diagnostic list. Errors are kept before warnings before
    /// everything else, so a truncated report still leads with what breaks the build.
    /// </summary>
    public static LspDiagnosticsReport Bounded(
        string serverId,
        string filePath,
        LspDiagnosticsStatus status,
        IReadOnlyList<LspDiagnostic> diagnostics,
        string? detail = null)
    {
        var ordered = diagnostics
            .OrderBy(d => (int)d.Severity)
            .ThenBy(d => d.Line)
            .ThenBy(d => d.Column)
            .ToList();

        var kept = new List<LspDiagnostic>(Math.Min(ordered.Count, LspLimits.MaxDiagnosticsPerFile));
        var renderedChars = 0;
        string? reason = null;

        foreach (var diagnostic in ordered)
        {
            if (kept.Count >= LspLimits.MaxDiagnosticsPerFile)
            {
                reason = $"only the first {LspLimits.MaxDiagnosticsPerFile} diagnostics are reported";
                break;
            }

            var length = diagnostic.Format().Length + 1;
            if (renderedChars + length > LspLimits.MaxRenderedChars && kept.Count > 0)
            {
                reason = $"diagnostics text was capped at {LspLimits.MaxRenderedChars} characters";
                break;
            }

            renderedChars += length;
            kept.Add(diagnostic);
        }

        return new LspDiagnosticsReport
        {
            ServerId = serverId,
            FilePath = filePath,
            Status = status,
            Diagnostics = kept,
            TotalCount = ordered.Count,
            OmittedCount = ordered.Count - kept.Count,
            TruncationReason = ordered.Count > kept.Count ? reason : null,
            Detail = detail,
        };
    }

    public static LspDiagnosticsReport Unavailable(
        string serverId,
        string filePath,
        LspDiagnosticsStatus status,
        string detail) =>
        new()
        {
            ServerId = serverId,
            FilePath = filePath,
            Status = status,
            Detail = detail,
        };

    /// <summary>
    /// The structured payload attached to the mutating tool's result so the model sees the same
    /// diagnostics the user sees.
    /// </summary>
    public Dictionary<string, object?> ToStructuredPayload()
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["server"] = ServerId,
            ["file"] = FilePath,
            ["status"] = Status switch
            {
                LspDiagnosticsStatus.Received => "received",
                LspDiagnosticsStatus.TimedOut => "timed_out",
                LspDiagnosticsStatus.ServerUnavailable => "server_unavailable",
                LspDiagnosticsStatus.OutsideWorkspace => "outside_workspace",
                _ => "skipped",
            },
            ["error_count"] = ErrorCount,
            ["warning_count"] = WarningCount,
            ["diagnostics"] = Diagnostics.Select(d => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["severity"] = d.Severity.ToString().ToLowerInvariant(),
                ["line"] = d.Line,
                ["column"] = d.Column,
                ["message"] = d.Message,
                ["code"] = d.Code,
                ["source"] = d.Source,
            }).ToList(),
        };

        if (Detail is not null) payload["detail"] = Detail;

        if (IsTruncated)
        {
            payload["truncated"] = true;
            payload["reported_count"] = Diagnostics.Count;
            payload["total_count"] = TotalCount;
            payload["omitted_count"] = OmittedCount;
            payload["truncation_reason"] = TruncationReason;
        }

        return payload;
    }

    /// <summary>A compact rendering for the terminal feed.</summary>
    public string ToFeedText()
    {
        var builder = new StringBuilder();
        var name = System.IO.Path.GetFileName(FilePath);

        switch (Status)
        {
            case LspDiagnosticsStatus.Received when Diagnostics.Count == 0:
                return string.Empty;
            case LspDiagnosticsStatus.Received:
                builder.Append($"lsp ({ServerId}) {name}: {ErrorCount} error(s), {WarningCount} warning(s)");
                break;
            default:
                builder.Append($"lsp ({ServerId}) {name}: {Detail ?? Status.ToString()}");
                return builder.ToString();
        }

        foreach (var diagnostic in Diagnostics)
        {
            builder.Append('\n').Append("  ").Append(diagnostic.Format());
        }

        if (IsTruncated)
        {
            builder.Append('\n').Append($"  ... {OmittedCount} more not shown ({TruncationReason})");
        }

        return builder.ToString();
    }
}
