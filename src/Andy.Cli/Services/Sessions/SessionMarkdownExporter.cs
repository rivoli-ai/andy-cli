using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Andy.Engine;

namespace Andy.Cli.Services.Sessions;

/// <summary>Options for the human-readable Markdown transcript.</summary>
public sealed record SessionMarkdownOptions
{
    /// <summary>Include per-tool-call names, arguments, and results.</summary>
    public bool IncludeToolDetails { get; init; }

    /// <summary>Include the provider/model, timestamps, lineage, origin, and usage header.</summary>
    public bool IncludeModelMetadata { get; init; }

    /// <summary>Maximum characters kept from a single tool argument/result block.</summary>
    public int MaxToolPayloadChars { get; init; } = 2000;

    public static SessionMarkdownOptions Default => new();
}

/// <summary>
/// Renders a stored session as a Markdown document (issue #285): a readable transcript for
/// sharing or archiving, as opposed to the machine-readable archive.
///
/// Every string that reaches the output goes through the <see cref="SessionRedactor"/>, so
/// the Markdown export is no more revealing than the archive: bearer tokens, api-key shapes,
/// and key/value secrets are replaced exactly as they are on the machine-readable path.
/// </summary>
public static class SessionMarkdownExporter
{
    public static string Render(
        SessionRecord record,
        SessionMarkdownOptions? options = null,
        SessionRedactor? redactor = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        var opts = options ?? SessionMarkdownOptions.Default;
        var scrub = redactor ?? new SessionRedactor();
        var summary = record.Summary;

        var sb = new StringBuilder();
        var heading = !string.IsNullOrEmpty(summary.Title)
            ? scrub.RedactText(summary.Title)
            : "Session " + summary.SessionId;
        sb.Append("# ").AppendLine(heading);
        sb.AppendLine();

        if (opts.IncludeModelMetadata)
        {
            AppendMetadata(sb, summary, scrub);
        }
        else
        {
            sb.Append("Session: `").Append(summary.SessionId).AppendLine("`");
            sb.AppendLine();
        }

        var turns = record.Snapshot.Turns ?? Array.Empty<TranscriptTurn>();
        for (var i = 0; i < turns.Count; i++)
        {
            AppendTurn(sb, turns[i], i + 1, opts, scrub);
        }

        return sb.ToString();
    }

    private static void AppendMetadata(StringBuilder sb, SessionSummary summary, SessionRedactor scrub)
    {
        sb.AppendLine("## Metadata");
        sb.AppendLine();
        sb.Append("- Session id: `").Append(summary.SessionId).AppendLine("`");
        if (!string.IsNullOrEmpty(summary.Provider) || !string.IsNullOrEmpty(summary.Model))
        {
            sb.Append("- Provider/model: `").Append(summary.Provider).Append('/')
              .Append(summary.Model).AppendLine("`");
        }
        if (summary.CreatedUtc != DateTimeOffset.MinValue)
        {
            sb.Append("- Created (UTC): ").AppendLine(
                summary.CreatedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        }
        if (summary.UpdatedUtc != DateTimeOffset.MinValue)
        {
            sb.Append("- Updated (UTC): ").AppendLine(
                summary.UpdatedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        }
        sb.Append("- Turns: ").AppendLine(summary.TurnCount.ToString(CultureInfo.InvariantCulture));

        if (summary.Lineage is { IsEmpty: false } lineage)
        {
            if (!string.IsNullOrEmpty(lineage.ParentSessionId))
            {
                sb.Append("- Forked from: `").Append(lineage.ParentSessionId).Append('`')
                  .AppendLine(lineage.ForkedAtTurn is { } turn
                      ? $" (before turn {turn})"
                      : " (full session)");
            }
            if (!string.IsNullOrEmpty(lineage.RootSessionId))
            {
                sb.Append("- Root session: `").Append(lineage.RootSessionId).AppendLine("`");
            }
            if (!string.IsNullOrEmpty(lineage.ImportedFromSessionId))
            {
                sb.Append("- Imported from session: `").Append(lineage.ImportedFromSessionId).AppendLine("`");
            }
        }

        if (summary.Origin is { IsEmpty: false } origin)
        {
            // Informational only - the path belongs to the recording machine.
            sb.Append("- Recorded in: ").AppendLine(scrub.RedactText(origin.Describe()));
        }

        if (summary.Usage is { IsEmpty: false } usage)
        {
            sb.Append("- Tokens: ").Append(SessionUsage.FormatTokens(usage.InputTokens))
              .Append(" input, ").Append(SessionUsage.FormatTokens(usage.OutputTokens))
              .Append(" output, ").Append(SessionUsage.FormatTokens(usage.ReasoningTokens))
              .Append(" reasoning, ").Append(SessionUsage.FormatTokens(usage.CacheReadTokens))
              .Append(" cache read, ").Append(SessionUsage.FormatTokens(usage.CacheWriteTokens))
              .AppendLine(" cache write");
            sb.Append("- Estimated cost: ").AppendLine(usage.FormatCost("unknown (no pricing data)"));
        }

        sb.AppendLine();
    }

    private static void AppendTurn(
        StringBuilder sb,
        TranscriptTurn turn,
        int index,
        SessionMarkdownOptions opts,
        SessionRedactor scrub)
    {
        sb.Append("## Turn ").AppendLine(index.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(turn.User?.Content))
        {
            sb.AppendLine("### User");
            sb.AppendLine();
            sb.AppendLine(scrub.RedactText(turn.User!.Content!).TrimEnd());
            sb.AppendLine();
        }

        var toolCallNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var toolCallCount = 0;

        foreach (var message in turn.Interleaved ?? Array.Empty<TranscriptMessage>())
        {
            if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(message.Content))
            {
                sb.AppendLine("### Assistant (in progress)");
                sb.AppendLine();
                sb.AppendLine(scrub.RedactText(message.Content!).TrimEnd());
                sb.AppendLine();
            }

            foreach (var call in message.ToolCalls ?? Array.Empty<TranscriptToolCall>())
            {
                toolCallCount++;
                if (!string.IsNullOrEmpty(call.Id))
                {
                    toolCallNames[call.Id!] = call.Name ?? "tool";
                }
                if (!opts.IncludeToolDetails)
                {
                    continue;
                }
                sb.Append("### Tool call: `").Append(call.Name ?? "tool").AppendLine("`");
                sb.AppendLine();
                if (!string.IsNullOrWhiteSpace(call.ArgumentsJson))
                {
                    sb.AppendLine("Arguments:");
                    sb.AppendLine();
                    AppendFenced(sb, "json", call.ArgumentsJson!, opts, scrub);
                }
            }

            foreach (var toolResult in message.ToolResults ?? Array.Empty<TranscriptToolResult>())
            {
                if (!opts.IncludeToolDetails)
                {
                    continue;
                }
                var name = !string.IsNullOrEmpty(toolResult.Name)
                    ? toolResult.Name!
                    : toolResult.CallId is { Length: > 0 } id
                        && toolCallNames.TryGetValue(id, out var resolved)
                            ? resolved
                            : "tool";
                sb.Append("### Tool result: `").Append(name).Append('`')
                  .AppendLine(toolResult.IsError ? " (error)" : "");
                sb.AppendLine();
                AppendFenced(sb, "json", toolResult.ResultJson ?? "", opts, scrub);
            }
        }

        if (toolCallCount > 0 && !opts.IncludeToolDetails)
        {
            sb.Append("_").Append(toolCallCount).Append(" tool call")
              .Append(toolCallCount == 1 ? "" : "s").AppendLine(" executed (details omitted)._");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(turn.FinalAssistant?.Content))
        {
            sb.AppendLine("### Assistant");
            sb.AppendLine();
            sb.AppendLine(scrub.RedactText(turn.FinalAssistant!.Content!).TrimEnd());
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("_Turn ended without a final answer._");
            sb.AppendLine();
        }
    }

    private static void AppendFenced(
        StringBuilder sb,
        string language,
        string content,
        SessionMarkdownOptions opts,
        SessionRedactor scrub)
    {
        var text = scrub.RedactText(content ?? "");
        if (opts.MaxToolPayloadChars > 0 && text.Length > opts.MaxToolPayloadChars)
        {
            text = text[..opts.MaxToolPayloadChars] + "\n... (truncated)";
        }
        // Widen the fence past any run of backticks inside the payload so the block
        // cannot be broken out of.
        var fence = new string('`', Math.Max(3, LongestBacktickRun(text) + 1));
        sb.Append(fence).AppendLine(language);
        sb.AppendLine(text.TrimEnd());
        sb.AppendLine(fence);
        sb.AppendLine();
    }

    private static int LongestBacktickRun(string text)
    {
        var longest = 0;
        var current = 0;
        foreach (var ch in text)
        {
            if (ch == '`')
            {
                current++;
                longest = Math.Max(longest, current);
            }
            else
            {
                current = 0;
            }
        }
        return longest;
    }
}
