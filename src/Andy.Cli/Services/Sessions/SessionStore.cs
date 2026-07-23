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
    /// </summary>
    public bool Save(string sessionId, TranscriptSnapshot snapshot, string provider, string model)
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
        var createdUtc = TryReadCreatedUtc(path) ?? now;

        var transcriptNode = JsonNode.Parse(_redactor.RedactJson(snapshot.ToJson()));
        var firstUserMessage = Snippet(
            _redactor.RedactText(snapshot.Turns[0].User?.Content ?? string.Empty));

        var envelope = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["sessionId"] = sessionId,
            ["createdUtc"] = createdUtc.UtcDateTime.ToString("O"),
            ["updatedUtc"] = now.UtcDateTime.ToString("O"),
            ["provider"] = provider ?? string.Empty,
            ["model"] = model ?? string.Empty,
            ["turnCount"] = snapshot.Turns.Count,
            ["firstUserMessage"] = firstUserMessage,
            ["transcript"] = transcriptNode
        };

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, envelope.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tempPath, path, overwrite: true);
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

    private static SessionSummary ReadSummary(JsonElement root, string sessionId) => new()
    {
        SessionId = sessionId,
        CreatedUtc = ReadTimestamp(root, "createdUtc"),
        UpdatedUtc = ReadTimestamp(root, "updatedUtc"),
        Provider = root.TryGetProperty("provider", out var p) ? p.GetString() ?? "" : "",
        Model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "",
        TurnCount = root.TryGetProperty("turnCount", out var t) ? t.GetInt32() : 0,
        FirstUserMessage = root.TryGetProperty("firstUserMessage", out var f)
            ? f.GetString() ?? ""
            : ""
    };

    private static DateTimeOffset ReadTimestamp(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetDateTimeOffset(out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    private static DateTimeOffset? TryReadCreatedUtc(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var created = ReadTimestamp(document.RootElement, "createdUtc");
            return created == DateTimeOffset.MinValue ? null : created;
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
    public int TurnCount { get; init; }
    public string FirstUserMessage { get; init; } = "";
}

/// <summary>A fully loaded session: metadata plus the restorable engine snapshot.</summary>
public sealed record SessionRecord(SessionSummary Summary, TranscriptSnapshot Snapshot);
