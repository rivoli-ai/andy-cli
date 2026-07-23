using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Engine;

namespace Andy.Cli.Services.Sessions;

/// <summary>
/// Converts a restored <see cref="TranscriptSnapshot"/> into the ordered list of
/// feed entries shown when a session is resumed, so the user sees the prior
/// conversation. User messages and assistant answers (including mid-turn
/// narration) are replayed verbatim; tool activity is summarized as a single
/// notice per turn instead of re-rendering every tool payload.
/// </summary>
public static class SessionReplayFormatter
{
    public enum EntryKind
    {
        User,
        Assistant,
        Notice
    }

    public sealed record Entry(EntryKind Kind, string Text);

    public static IReadOnlyList<Entry> Format(TranscriptSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var entries = new List<Entry>();
        foreach (var turn in snapshot.Turns ?? Array.Empty<TranscriptTurn>())
        {
            if (!string.IsNullOrWhiteSpace(turn.User?.Content))
            {
                entries.Add(new Entry(EntryKind.User, turn.User.Content));
            }

            var toolCallCount = 0;
            foreach (var message in turn.Interleaved ?? Array.Empty<TranscriptMessage>())
            {
                toolCallCount += message.ToolCalls?.Count ?? 0;
                if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(message.Content))
                {
                    entries.Add(new Entry(EntryKind.Assistant, message.Content));
                }
            }

            if (toolCallCount > 0)
            {
                entries.Add(new Entry(
                    EntryKind.Notice,
                    $"[{toolCallCount} tool call{(toolCallCount == 1 ? "" : "s")} executed]"));
            }

            if (!string.IsNullOrWhiteSpace(turn.FinalAssistant?.Content))
            {
                entries.Add(new Entry(EntryKind.Assistant, turn.FinalAssistant.Content));
            }
            else
            {
                entries.Add(new Entry(EntryKind.Notice, "[turn ended without a final answer]"));
            }
        }

        return entries;
    }
}
