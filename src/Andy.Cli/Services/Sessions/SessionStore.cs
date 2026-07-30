using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Andy.Engine;

namespace Andy.Cli.Services.Sessions;

/// <summary>
/// Persists interactive conversation sessions so the user can exit the CLI and
/// resume later with the full context restored (issue #231).
///
/// Each session is one JSON file under ~/.andy/sessions/ (the app's existing
/// user-config convention, alongside model-memory.json and theme-memory.json):
/// a small metadata envelope wrapping the engine's own versioned
/// <see cref="TranscriptSnapshot"/> JSON. Writes are atomic (temp file + move)
/// and redacted via <see cref="SessionRedactor"/> before touching disk, following
/// the headless transcript conventions. Restoring feeds the snapshot straight
/// back into <c>SimpleAgent.RestoreTranscript</c>, which re-seeds the complete
/// message history (user, assistant, tool calls and tool results).
/// </summary>
public sealed class SessionStore
{
    public const int SchemaVersion = 1;

    private static readonly Regex s_sessionIdPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
        RegexOptions.Compiled);

    private readonly SessionRedactor _redactor;
    private readonly TimeProvider _clock;

    public SessionStore(
        string? directory = null,
        SessionRedactor? redactor = null,
        TimeProvider? clock = null)
    {
        DirectoryPath = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".andy", "sessions");
        _redactor = redactor ?? new SessionRedactor();
        _clock = clock ?? TimeProvider.System;
    }

    public string DirectoryPath { get; }

    /// <summary>
    /// Generates a new short, filesystem-safe, time-sortable session id, e.g.
    /// "20260723-181530-3fa9".
    /// </summary>
    public static string NewSessionId(TimeProvider? clock = null)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        Span<byte> random = stackalloc byte[2];
        RandomNumberGenerator.Fill(random);
        return $"{now:yyyyMMdd-HHmmss}-{Convert.ToHexString(random).ToLowerInvariant()}";
    }

    /// <summary>True when the id is safe to use as a file name (no path tricks).</summary>
    public static bool IsValidSessionId(string? sessionId) =>
        !string.IsNullOrEmpty(sessionId) && s_sessionIdPattern.IsMatch(sessionId);

    /// <summary>
    /// Saves (or overwrites) the session file for <paramref name="sessionId"/>.
    /// Empty transcripts are skipped so a session that never got a turn does not
    /// clutter the listing (returns false). The transcript is redacted before it
    /// is written; the original creation timestamp survives re-saves.
    ///
    /// <paramref name="options"/> carries the optional title / lineage / origin /
    /// usage metadata added in issue #285. Anything it leaves null is inherited from
    /// the existing file, so an ordinary per-turn save never drops a title or the
    /// lineage of a forked session. All of these envelope fields are ADDITIVE within
    /// schema version 1, which is what keeps sessions written before #285 readable.
    /// </summary>
    /// <param name="mode">
    /// The primary operating mode id (see <c>Andy.Cli.Modes.AgentModeCatalog</c>) the session was in
    /// when it was saved, so <c>--resume</c> / <c>/resume</c> can put the user back in the same mode
    /// instead of silently returning a planning session to a mutation-capable one (issue #278).
    /// Null or empty writes no mode and loads back as <see cref="SessionSummary.Mode"/> = "".
    /// </param>
    public bool Save(
        string sessionId,
        TranscriptSnapshot snapshot,
        string provider,
        string model,
        SessionSaveOptions? options = null,
        string? mode = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!IsValidSessionId(sessionId))
        {
            throw new ArgumentException($"Invalid session id: '{sessionId}'", nameof(sessionId));
        }
        if (snapshot.Turns is null || snapshot.Turns.Count == 0)
        {
            return false;
        }

        Directory.CreateDirectory(DirectoryPath);

        var now = _clock.GetUtcNow();
        var path = PathFor(sessionId);
        var existing = TryReadSummary(path);

        var previousCreatedUtc = existing is { CreatedUtc: var created } && created != DateTimeOffset.MinValue
            ? (DateTimeOffset?)created
            : null;
        var createdUtc = options?.CreatedUtc ?? previousCreatedUtc ?? now;
        var updatedUtc = options?.UpdatedUtc ?? now;
        var title = options?.Title ?? existing?.Title ?? "";
        var lineage = options?.Lineage ?? existing?.Lineage;
        var origin = options?.Origin
            ?? existing?.Origin
            ?? (options?.CaptureOrigin ?? true ? SessionOrigin.ForCurrentMachine() : null);
        var usage = options?.Usage ?? existing?.Usage;

        var transcriptNode = JsonNode.Parse(_redactor.RedactJson(snapshot.ToJson()));
        var firstUserMessage = Snippet(
            _redactor.RedactText(snapshot.Turns[0].User?.Content ?? string.Empty));

        var envelope = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["sessionId"] = sessionId,
            ["createdUtc"] = createdUtc.UtcDateTime.ToString("O"),
            ["updatedUtc"] = updatedUtc.UtcDateTime.ToString("O"),
            ["provider"] = provider ?? string.Empty,
            ["model"] = model ?? string.Empty,
            ["mode"] = mode ?? string.Empty,
            ["turnCount"] = snapshot.Turns.Count,
            ["firstUserMessage"] = firstUserMessage
        };
        if (!string.IsNullOrEmpty(title))
        {
            envelope["title"] = _redactor.RedactText(Snippet(title, 200));
        }
        if (lineage is { IsEmpty: false })
        {
            envelope["lineage"] = lineage.ToJson();
        }
        if (origin is { IsEmpty: false })
        {
            envelope["origin"] = origin.ToJson();
        }
        if (usage is { IsEmpty: false })
        {
            envelope["usage"] = usage.ToJson();
        }
        envelope["transcript"] = transcriptNode;

        WriteAtomic(path, envelope);
        return true;
    }

    /// <summary>True when a session file already exists for this id.</summary>
    public bool Exists(string sessionId) =>
        IsValidSessionId(sessionId) && File.Exists(PathFor(sessionId));

    /// <summary>
    /// Generates a fresh session id that is guaranteed not to collide with an existing
    /// session file. Used when importing an archive whose id is already taken.
    /// </summary>
    public string NewUniqueSessionId(TimeProvider? clock = null)
    {
        var provider = clock ?? _clock;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var candidate = NewSessionId(provider);
            if (!Exists(candidate))
            {
                return candidate;
            }
        }
        throw new InvalidOperationException("Could not generate a free session id.");
    }

    /// <summary>
    /// Sets (or clears, with null/empty) the human-readable title of a saved session.
    /// Returns false when the session does not exist. The rewrite is atomic and leaves
    /// every other envelope field, including the transcript, byte-identical.
    /// </summary>
    public bool Rename(string sessionId, string? title)
    {
        if (!IsValidSessionId(sessionId))
        {
            throw new ArgumentException($"Invalid session id: '{sessionId}'", nameof(sessionId));
        }

        var path = PathFor(sessionId);
        if (!File.Exists(path))
        {
            return false;
        }

        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException("Session file is not a JSON object.");

        var trimmed = title?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            root.Remove("title");
        }
        else
        {
            root["title"] = _redactor.RedactText(Snippet(trimmed, 200));
        }

        WriteAtomic(path, root);
        return true;
    }

    /// <summary>
    /// Loads a saved session. Returns null when no file exists for the id; throws
    /// on a corrupt or incompatible file (the caller reports the reason).
    /// </summary>
    public SessionRecord? Load(string sessionId)
    {
        if (!IsValidSessionId(sessionId))
        {
            throw new ArgumentException($"Invalid session id: '{sessionId}'", nameof(sessionId));
        }

        var path = PathFor(sessionId);
        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var schemaVersion = root.TryGetProperty("schemaVersion", out var sv) ? sv.GetInt32() : -1;
        if (schemaVersion != SchemaVersion)
        {
            throw new NotSupportedException(
                $"Session file schema version {schemaVersion} is not supported (expected {SchemaVersion}).");
        }
        if (!root.TryGetProperty("transcript", out var transcript))
        {
            throw new InvalidDataException("Session file has no transcript.");
        }

        return new SessionRecord(
            ReadSummary(root, sessionId),
            TranscriptSnapshot.FromJson(transcript.GetRawText()));
    }

    /// <summary>Lists saved sessions, most recently updated first. Corrupt files are skipped.</summary>
    public IReadOnlyList<SessionSummary> List()
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return Array.Empty<SessionSummary>();
        }

        var summaries = new List<SessionSummary>();
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var root = document.RootElement;
                var sessionId = root.TryGetProperty("sessionId", out var sid)
                    ? sid.GetString()
                    : null;
                if (!IsValidSessionId(sessionId))
                {
                    continue;
                }
                // Sibling files share this directory and carry a "sessionId" of their own -
                // notably <id>.approvals.json from SessionApprovalStore. Only <id>.json is
                // a transcript, so require the file name to be exactly that; otherwise the
                // listing (and the usage totals built from it) double counts every session.
                if (!string.Equals(Path.GetFileName(file), sessionId + ".json", StringComparison.Ordinal))
                {
                    continue;
                }
                summaries.Add(ReadSummary(root, sessionId!));
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
            {
                // A corrupt or half-written file must not break the listing.
            }
        }

        return summaries
            .OrderByDescending(s => s.UpdatedUtc)
            .ThenByDescending(s => s.SessionId, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The most recently updated session, or null when none exist.</summary>
    public SessionSummary? Latest() => List().FirstOrDefault();

    private string PathFor(string sessionId) => Path.Combine(DirectoryPath, sessionId + ".json");

    private void WriteAtomic(string path, JsonNode content)
    {
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, content.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tempPath, path, overwrite: true);
    }

    // Every field added by issue #285 is optional here: a schema-version-1 file written
    // before those fields existed simply yields nulls/empties.
    private static SessionSummary ReadSummary(JsonElement root, string sessionId) => new()
    {
        SessionId = sessionId,
        CreatedUtc = ReadTimestamp(root, "createdUtc"),
        UpdatedUtc = ReadTimestamp(root, "updatedUtc"),
        Provider = root.TryGetProperty("provider", out var p) ? p.GetString() ?? "" : "",
        Model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "",
        // Absent in files written before modes existed; an empty value means "no recorded mode",
        // which the caller must treat as "leave the current mode alone" rather than as Build.
        Mode = root.TryGetProperty("mode", out var md) ? md.GetString() ?? "" : "",
        TurnCount = root.TryGetProperty("turnCount", out var t) ? t.GetInt32() : 0,
        FirstUserMessage = root.TryGetProperty("firstUserMessage", out var f)
            ? f.GetString() ?? ""
            : "",
        Title = SessionJson.ReadString(root, "title") ?? "",
        Lineage = root.TryGetProperty("lineage", out var lineage)
            ? SessionLineage.FromJson(lineage)
            : null,
        Origin = root.TryGetProperty("origin", out var origin)
            ? SessionOrigin.FromJson(origin)
            : null,
        Usage = root.TryGetProperty("usage", out var usage)
            ? SessionUsage.FromJson(usage)
            : null
    };

    private static DateTimeOffset ReadTimestamp(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetDateTimeOffset(out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    private static SessionSummary? TryReadSummary(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var sessionId = SessionJson.ReadString(root, "sessionId");
            return ReadSummary(root, IsValidSessionId(sessionId) ? sessionId! : "unknown");
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private static string Snippet(string text, int maxLength = 100)
    {
        var collapsed = string.Join(' ',
            text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return collapsed.Length <= maxLength ? collapsed : collapsed[..maxLength] + "...";
    }
}

/// <summary>Listing metadata for one saved session.</summary>
public sealed record SessionSummary
{
    public required string SessionId { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";

    /// <summary>
    /// The operating-mode id recorded with the session ("build", "plan"), or "" for sessions saved
    /// before modes existed.
    /// </summary>
    public string Mode { get; init; } = "";
    public int TurnCount { get; init; }
    public string FirstUserMessage { get; init; } = "";

    /// <summary>Optional user-facing title (issue #285); empty when never set.</summary>
    public string Title { get; init; } = "";

    /// <summary>Fork/import lineage (issue #285); null for an ordinary session.</summary>
    public SessionLineage? Lineage { get; init; }

    /// <summary>Recording machine metadata (issue #285); null on pre-#285 session files.</summary>
    public SessionOrigin? Origin { get; init; }

    /// <summary>Aggregate token usage (issue #285); null when nothing was recorded.</summary>
    public SessionUsage? Usage { get; init; }

    /// <summary>Title when set, otherwise the first user message, otherwise the id.</summary>
    public string DisplayLabel =>
        !string.IsNullOrEmpty(Title) ? Title
        : !string.IsNullOrEmpty(FirstUserMessage) ? FirstUserMessage
        : SessionId;
}

/// <summary>A fully loaded session: metadata plus the restorable engine snapshot.</summary>
public sealed record SessionRecord(SessionSummary Summary, TranscriptSnapshot Snapshot);
