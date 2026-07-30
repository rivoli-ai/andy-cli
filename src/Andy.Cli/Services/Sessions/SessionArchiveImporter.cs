using System;
using System.IO;
using System.Text;

namespace Andy.Cli.Services.Sessions;

/// <summary>
/// Outcome of an import. <see cref="Installed"/> is false for a dry run, in which case the
/// summary describes exactly what a real import WOULD do and nothing has been written.
/// </summary>
public sealed record SessionImportResult(
    bool Installed,
    string SessionId,
    string OriginalSessionId,
    bool IdWasRemapped,
    string Title,
    int TurnCount,
    string Provider,
    string Model,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    SessionLineage? Lineage,
    SessionOrigin? Origin,
    SessionUsage? Usage)
{
    /// <summary>Multi-line summary shown by both the dry run and the real import.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Installed
            ? $"Imported session {SessionId}"
            : $"Dry run: would import session {SessionId}");
        if (IdWasRemapped)
        {
            sb.AppendLine($"  Original id:  {OriginalSessionId} (already in use; a new id was assigned)");
        }
        if (!string.IsNullOrEmpty(Title))
        {
            sb.AppendLine($"  Title:        {Title}");
        }
        sb.AppendLine($"  Turns:        {TurnCount}");
        sb.AppendLine($"  Model:        {Provider}/{Model}");
        if (CreatedUtc != DateTimeOffset.MinValue)
        {
            sb.AppendLine($"  Recorded:     {CreatedUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC");
        }
        if (Lineage is { IsEmpty: false } lineage)
        {
            if (!string.IsNullOrEmpty(lineage.ParentSessionId))
            {
                sb.AppendLine($"  Forked from:  {lineage.ParentSessionId}"
                    + (lineage.ForkedAtTurn is { } turn ? $" before turn {turn}" : " (full session)"));
            }
            if (!string.IsNullOrEmpty(lineage.RootSessionId))
            {
                sb.AppendLine($"  Root session: {lineage.RootSessionId}");
            }
        }
        if (Origin is { IsEmpty: false } origin)
        {
            sb.AppendLine($"  Origin:       {origin.Describe()}");
        }
        if (Usage is { IsEmpty: false } usage)
        {
            sb.AppendLine($"  Usage:        {SessionUsage.FormatTokens(usage.InputTokens)} in / "
                + $"{SessionUsage.FormatTokens(usage.OutputTokens)} out, cost "
                + usage.FormatCost("unknown (no pricing data)"));
        }
        sb.Append(Installed
            ? $"Resume it with: andy-cli --resume {SessionId}"
            : "Re-run without --dry-run to install it.");
        return sb.ToString();
    }
}

/// <summary>
/// Reads a portable archive back into the local session store (issue #285).
///
/// Guarantees:
/// <list type="bullet">
/// <item>Validation is complete BEFORE anything is written - a rejected archive leaves the
/// session directory untouched, so corrupt/truncated/oversized/unsupported/path-traversal
/// archives fail atomically.</item>
/// <item>The archive's session id is reused when it is free, and a fresh conflict-safe id is
/// minted when it is already taken; the original id is preserved in the lineage.</item>
/// <item>Nothing is executed. The importer only parses JSON and writes one session file; it
/// never runs a tool, never replays a tool result, and never touches the workspace path
/// recorded in the archive, which stays informational.</item>
/// </list>
/// </summary>
public static class SessionArchiveImporter
{
    /// <summary>Reads and validates an archive file without installing it.</summary>
    /// <param name="maxBytes">
    /// Overrides <see cref="SessionArchive.MaxArchiveBytes"/> (used by tests to exercise
    /// the oversize guard without a 64 MB fixture).
    /// </param>
    public static SessionArchiveDocument ReadFile(string path, long? maxBytes = null)
    {
        var limit = maxBytes ?? SessionArchive.MaxArchiveBytes;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Archive path is required.", nameof(path));
        }

        var full = Path.GetFullPath(path);
        FileInfo info;
        try
        {
            info = new FileInfo(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new SessionArchiveException($"Cannot read archive '{path}': {ex.Message}", ex);
        }

        if (!info.Exists)
        {
            throw new SessionArchiveException($"Archive '{path}' was not found.");
        }
        // Checked on the file BEFORE reading it, so an oversized archive is never
        // loaded into memory at all.
        if (info.Length > limit)
        {
            throw new SessionArchiveException(
                $"Archive is {info.Length} bytes, over the {limit} byte limit.");
        }

        string json;
        try
        {
            json = File.ReadAllText(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SessionArchiveException($"Cannot read archive '{path}': {ex.Message}", ex);
        }

        return SessionArchive.Parse(json, limit);
    }

    /// <summary>
    /// Imports an archive file into <paramref name="store"/>. With
    /// <paramref name="dryRun"/> the archive is fully validated and summarized but no
    /// session file is written.
    /// </summary>
    public static SessionImportResult ImportFile(
        SessionStore store,
        string path,
        bool dryRun = false,
        string? title = null,
        TimeProvider? clock = null,
        long? maxBytes = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        return Import(store, ReadFile(path, maxBytes), dryRun, title, clock);
    }

    /// <summary>Imports an already-parsed archive.</summary>
    public static SessionImportResult Import(
        SessionStore store,
        SessionArchiveDocument archive,
        bool dryRun = false,
        string? title = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(archive);

        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var summary = archive.Record.Summary;
        var originalId = summary.SessionId;

        // Conflict-safe id: keep the archive's id when it is free, otherwise mint a new
        // one rather than overwriting an unrelated local session.
        var remapped = store.Exists(originalId);
        var targetId = remapped ? store.NewUniqueSessionId(clock) : originalId;

        var lineage = (summary.Lineage ?? new SessionLineage()) with
        {
            ImportedFromSessionId = originalId,
            ImportedUtc = now
        };

        var resolvedTitle = !string.IsNullOrWhiteSpace(title)
            ? title!.Trim()
            : summary.Title;

        var result = new SessionImportResult(
            Installed: !dryRun,
            SessionId: targetId,
            OriginalSessionId: originalId,
            IdWasRemapped: remapped,
            Title: resolvedTitle,
            TurnCount: summary.TurnCount,
            Provider: summary.Provider,
            Model: summary.Model,
            CreatedUtc: summary.CreatedUtc,
            UpdatedUtc: summary.UpdatedUtc,
            Lineage: lineage,
            Origin: summary.Origin,
            Usage: summary.Usage);

        if (dryRun)
        {
            return result;
        }

        var options = new SessionSaveOptions
        {
            Title = resolvedTitle,
            Lineage = lineage,
            // The recording machine's workspace path travels with the session as
            // informational metadata; it is never used to resolve anything locally.
            Origin = summary.Origin,
            Usage = summary.Usage,
            CreatedUtc = summary.CreatedUtc == DateTimeOffset.MinValue ? now : summary.CreatedUtc,
            UpdatedUtc = summary.UpdatedUtc == DateTimeOffset.MinValue ? now : summary.UpdatedUtc,
            CaptureOrigin = false
        };

        var saved = store.Save(
            targetId,
            archive.Record.Snapshot,
            summary.Provider,
            summary.Model,
            options);
        if (!saved)
        {
            throw new SessionArchiveException("Archive transcript contains no turns.");
        }

        return result;
    }
}
