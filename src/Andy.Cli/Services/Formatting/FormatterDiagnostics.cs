using System;
using System.Collections.Generic;
using System.Text;
using Andy.Cli.Services.Sessions;

namespace Andy.Cli.Services.Formatting;

/// <summary>
/// Builds the text Andy shows and hands back to the model when a formatter misbehaves.
///
/// Two rules govern everything here:
/// <list type="number">
/// <item><b>Bounded.</b> A formatter that floods stderr must not flood the model's context; the
/// report is truncated to a small budget with an explicit marker.</item>
/// <item><b>Redacted.</b> Formatter output can echo environment variables, config files, or the
/// contents of the file being formatted, any of which may carry a token. Every diagnostic string is
/// passed through <see cref="SessionRedactor"/> before it leaves this class.</item>
/// </list>
/// </summary>
public static class FormatterDiagnostics
{
    /// <summary>Per-formatter diagnostic budget, in characters, after redaction.</summary>
    public const int MaxDiagnosticChars = 1200;

    /// <summary>Total budget for the combined report handed back to the model.</summary>
    public const int MaxReportChars = 3000;

    private static readonly Lazy<SessionRedactor> s_redactor = new(() => new SessionRedactor());

    /// <summary>
    /// Redact and bound one formatter's output. Stderr leads because it is where formatters put the
    /// reason; stdout is appended only when stderr is empty.
    /// </summary>
    public static string Summarize(string? standardError, string? standardOutput, SessionRedactor? redactor = null)
    {
        var text = !string.IsNullOrWhiteSpace(standardError)
            ? standardError!
            : standardOutput ?? string.Empty;

        return Bound(Redact(text, redactor), MaxDiagnosticChars);
    }

    /// <summary>Redact a single diagnostic string without bounding it.</summary>
    public static string Redact(string? text, SessionRedactor? redactor = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        try
        {
            return (redactor ?? s_redactor.Value).RedactText(text);
        }
        catch (Exception)
        {
            // Redaction must never be the reason a diagnostic is lost - but an unredacted secret is
            // worse than no diagnostic, so the raw text is dropped rather than passed through.
            return "[diagnostic withheld: redaction failed]";
        }
    }

    /// <summary>Truncate to <paramref name="limit"/> characters with an explicit marker.</summary>
    public static string Bound(string text, int limit)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= limit)
        {
            return text ?? string.Empty;
        }

        return text[..limit] + $"\n[truncated, {text.Length - limit} more characters]";
    }

    /// <summary>
    /// The report handed back to the model when at least one formatter failed. It always names the
    /// formatter, its exit code, and the bounded stderr, so the agent can never conclude that the
    /// file was formatted when it was not.
    /// </summary>
    public static string? BuildAgentReport(string displayPath, IReadOnlyList<FormatterRunResult> results)
    {
        var builder = new StringBuilder();
        foreach (var result in results)
        {
            if (!result.IsFailure)
            {
                continue;
            }

            if (builder.Length == 0)
            {
                builder.Append("Formatting did not complete for ").Append(displayPath).Append(". ")
                    .Append("The file was written, but its contents are NOT formatter-clean:\n");
            }

            builder.Append("- ").Append(result.FormatterName).Append(": ").Append(result.Describe()).Append('\n');
        }

        if (builder.Length == 0)
        {
            return null;
        }

        return Bound(builder.ToString().TrimEnd('\n'), MaxReportChars);
    }
}
