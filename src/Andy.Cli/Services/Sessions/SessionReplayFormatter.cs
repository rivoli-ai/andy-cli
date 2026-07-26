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
        Notice,

        /// <summary>
        /// A command the user ran themselves in shell mode (issue #286). Distinct from
        /// <see cref="Notice"/> so replay and export can attribute it explicitly instead of letting
        /// it read like something the model did.
        /// </summary>
        UserShell
    }

    public sealed record Entry(EntryKind Kind, string Text);

    /// <summary>
    /// Replays the conversation together with the user's own shell-mode commands, interleaved by
    /// timestamp. The two come from separate stores by design (the transcript is what the model
    /// saw; <see cref="UserShellLogStore"/> is what the user did on the side), so this is the one
    /// place they are merged - and every shell entry is tagged <see cref="EntryKind.UserShell"/>,
    /// never folded into the tool-call notice, so the attribution survives the merge.
    /// </summary>
    public static IReadOnlyList<Entry> Format(
        TranscriptSnapshot snapshot,
        IReadOnlyList<UserShellRecord>? userShellCommands)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (userShellCommands is null || userShellCommands.Count == 0)
        {
            return Format(snapshot);
        }

        var entries = new List<Entry>();
        var pending = userShellCommands.OrderBy(c => c.TimestampUtc).ToList();
        var next = 0;

        foreach (var turn in snapshot.Turns ?? Array.Empty<TranscriptTurn>())
        {
            // Everything the user ran before this turn started belongs above it.
            var boundary = turn.User?.Timestamp;
            while (next < pending.Count
                   && (boundary is null || pending[next].TimestampUtc <= boundary.Value))
            {
                entries.Add(new Entry(EntryKind.UserShell, pending[next].ToTranscriptLine()));
                next++;
            }

            entries.AddRange(FormatTurn(turn));
        }

        for (; next < pending.Count; next++)
        {
            entries.Add(new Entry(EntryKind.UserShell, pending[next].ToTranscriptLine()));
        }

        return entries;
    }

    public static IReadOnlyList<Entry> Format(TranscriptSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var entries = new List<Entry>();
        foreach (var turn in snapshot.Turns ?? Array.Empty<TranscriptTurn>())
        {
            entries.AddRange(FormatTurn(turn));
        }

        return entries;
    }

    private static IEnumerable<Entry> FormatTurn(TranscriptTurn turn)
    {
        var entries = new List<Entry>();

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
            // Deliberately says "tool call": these are the MODEL's invocations. The user's own
            // shell-mode commands are never counted here - they arrive as EntryKind.UserShell.
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

        return entries;
    }
}
