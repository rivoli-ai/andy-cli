using System;
using System.Linq;
using Andy.Engine;

namespace Andy.Cli.Services.Sessions;

/// <summary>Details of a newly created fork.</summary>
public sealed record SessionForkResult(
    string SessionId,
    string SourceSessionId,
    string RootSessionId,
    int TurnCount,
    int? ForkedAtTurn,
    string Title)
{
    public string Describe()
    {
        var boundary = ForkedAtTurn is { } turn
            ? $"the history before turn {turn}"
            : "the full history";
        return $"Forked {SourceSessionId} into {SessionId} with {boundary} "
            + $"({TurnCount} turn{(TurnCount == 1 ? "" : "s")}).\n"
            + $"Resume it with: andy-cli --resume {SessionId}";
    }
}

/// <summary>
/// Creates an independent copy of a saved session, either whole or truncated at a user-turn
/// boundary (issue #285).
///
/// Boundary semantics: <c>--at N</c> uses 1-based user-turn numbering and keeps the history
/// STRICTLY BEFORE turn N, i.e. turns 1..N-1. That is the state the assistant was in just
/// before the user sent turn N, which is what you want in order to take the conversation in a
/// different direction from there. Omitting the boundary forks the whole session.
///
/// The fork is a deep copy (the snapshot is round-tripped through JSON), gets a brand-new
/// session id, and is written as its own file, so continuing either session cannot mutate the
/// other's transcript.
/// </summary>
public static class SessionForker
{
    public static SessionForkResult Fork(
        SessionStore store,
        string sourceSessionId,
        int? atTurn = null,
        string? title = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        var source = store.Load(sourceSessionId)
            ?? throw new SessionArchiveException($"Session '{sourceSessionId}' was not found.");

        var sourceTurns = source.Snapshot.Turns ?? Array.Empty<TranscriptTurn>();
        var totalTurns = sourceTurns.Count;
        if (totalTurns == 0)
        {
            throw new SessionArchiveException($"Session '{sourceSessionId}' has no turns to fork.");
        }

        int keep;
        int? boundary;
        if (atTurn is null || atTurn.Value > totalTurns)
        {
            keep = totalTurns;
            boundary = null;
        }
        else
        {
            if (atTurn.Value < 2)
            {
                throw new SessionArchiveException(
                    $"--at must be between 2 and {totalTurns}: forking before turn 1 would "
                    + "produce an empty session, which is just a new session.");
            }
            keep = atTurn.Value - 1;
            boundary = atTurn.Value;
        }

        // Deep copy: a JSON round-trip guarantees the fork shares no object graph with the
        // source, so later edits to either snapshot cannot reach the other.
        var copy = TranscriptSnapshot.FromJson(source.Snapshot.ToJson());
        var forkedSnapshot = new TranscriptSnapshot
        {
            Version = copy.Version,
            Turns = (copy.Turns ?? Array.Empty<TranscriptTurn>()).Take(keep).ToArray(),
            Plan = copy.Plan
        };

        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var newId = store.NewUniqueSessionId(clock);
        var rootId = string.IsNullOrEmpty(source.Summary.Lineage?.RootSessionId)
            ? source.Summary.SessionId
            : source.Summary.Lineage!.RootSessionId!;

        var resolvedTitle = !string.IsNullOrWhiteSpace(title)
            ? title!.Trim()
            : BuildDefaultTitle(source.Summary, boundary);

        var options = new SessionSaveOptions
        {
            Title = resolvedTitle,
            Lineage = new SessionLineage
            {
                ParentSessionId = source.Summary.SessionId,
                RootSessionId = rootId,
                ForkedAtTurn = boundary,
                ForkedUtc = now
            },
            Origin = source.Summary.Origin,
            // A partial fork covers only part of the source's traffic, so the source's
            // aggregate usage would be wrong for it: leave usage unrecorded (null, which
            // reads as "unknown", not "zero") and let it accumulate from here.
            Usage = boundary is null ? source.Summary.Usage : null,
            CreatedUtc = now,
            UpdatedUtc = now,
            CaptureOrigin = source.Summary.Origin is null
        };

        if (!store.Save(newId, forkedSnapshot, source.Summary.Provider, source.Summary.Model, options))
        {
            throw new SessionArchiveException("Fork produced an empty transcript.");
        }

        return new SessionForkResult(
            newId,
            source.Summary.SessionId,
            rootId,
            keep,
            boundary,
            resolvedTitle);
    }

    private static string BuildDefaultTitle(SessionSummary source, int? boundary)
    {
        var label = !string.IsNullOrEmpty(source.Title) ? source.Title : source.SessionId;
        return boundary is { } turn
            ? $"Fork of {label} (before turn {turn})"
            : $"Fork of {label}";
    }
}
