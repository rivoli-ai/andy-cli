using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Andy.Engine;

namespace Andy.Cli.Services.Sessions;

/// <summary>
/// Raised when an archive cannot be trusted: malformed JSON, a truncated file, a bad
/// checksum, an oversized payload, a hostile session id, or missing required fields.
/// Callers surface the message; nothing has been written when this is thrown.
/// </summary>
public sealed class SessionArchiveException : Exception
{
    public SessionArchiveException(string message) : base(message) { }
    public SessionArchiveException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Portable, versioned session archive (issue #285).
///
/// Layout - a single JSON object:
/// <code>
/// {
///   "format": "andy-session-archive",
///   "schemaVersion": 1,
///   "exportedUtc": "...",
///   "exportedBy": "andy-cli/2026.7.25",
///   "checksum": { "algorithm": "sha256", "value": "&lt;hex&gt;" },
///   "session": { sessionId, title, createdUtc, updatedUtc, provider, model,
///                turnCount, firstUserMessage, lineage?, origin?, usage?, transcript }
/// }
/// </code>
///
/// The checksum covers the compact serialization of the "session" object only, so it is
/// stable across pretty-printing of the outer document and detects any tampering or
/// truncation of the payload.
///
/// Security: the payload is built exclusively from the already-redacted stored session
/// and is redacted again on the way out, so no API key, OAuth token, injected header, or
/// other secret the <see cref="SessionRedactor"/> removes can reach an archive. The
/// archive carries conversation content and identifiers only - never credentials,
/// permission grants, or provider configuration.
/// </summary>
public static class SessionArchive
{
    public const string FormatId = "andy-session-archive";

    /// <summary>The archive schema version this build writes and is able to read.</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// Hard ceiling on an archive file. A session transcript is text; anything larger is
    /// either a mistake or an attempt to exhaust memory, and is rejected before parsing.
    /// </summary>
    public const long MaxArchiveBytes = 64L * 1024 * 1024;

    public const string ChecksumAlgorithm = "sha256";

    /// <summary>Conventional file name for an exported archive.</summary>
    public static string DefaultFileName(string sessionId) => $"andy-session-{sessionId}.json";

    /// <summary>Conventional file name for a Markdown export.</summary>
    public static string DefaultMarkdownFileName(string sessionId) => $"andy-session-{sessionId}.md";

    /// <summary>
    /// Builds the archive document for a stored session. The transcript is passed through
    /// the redactor once more (it was already redacted on save) so an archive produced from
    /// an older session file still benefits from the current redaction rules.
    /// </summary>
    public static JsonObject Build(
        SessionRecord record,
        SessionRedactor redactor,
        DateTimeOffset exportedUtc,
        string? exportedBy = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(redactor);

        var summary = record.Summary;
        var transcriptNode = JsonNode.Parse(redactor.RedactJson(record.Snapshot.ToJson()))
            ?? new JsonObject();

        var session = new JsonObject
        {
            ["sessionId"] = summary.SessionId,
            ["createdUtc"] = FormatTimestamp(summary.CreatedUtc, exportedUtc),
            ["updatedUtc"] = FormatTimestamp(summary.UpdatedUtc, exportedUtc),
            ["provider"] = summary.Provider ?? "",
            ["model"] = summary.Model ?? "",
            ["turnCount"] = summary.TurnCount,
            ["firstUserMessage"] = redactor.RedactText(summary.FirstUserMessage ?? "")
        };
        if (!string.IsNullOrEmpty(summary.Title))
        {
            session["title"] = redactor.RedactText(summary.Title);
        }
        if (summary.Lineage is { IsEmpty: false } lineage)
        {
            session["lineage"] = lineage.ToJson();
        }
        if (summary.Origin is { IsEmpty: false } origin)
        {
            session["origin"] = origin.ToJson();
        }
        if (summary.Usage is { IsEmpty: false } usage)
        {
            session["usage"] = usage.ToJson();
        }
        session["transcript"] = transcriptNode;

        return new JsonObject
        {
            ["format"] = FormatId,
            ["schemaVersion"] = SchemaVersion,
            ["exportedUtc"] = exportedUtc.UtcDateTime.ToString("O"),
            ["exportedBy"] = exportedBy ?? DefaultExportedBy(),
            ["checksum"] = new JsonObject
            {
                ["algorithm"] = ChecksumAlgorithm,
                ["value"] = ComputeChecksum(session)
            },
            ["session"] = session
        };
    }

    /// <summary>SHA-256 (lowercase hex) of the compact serialization of the payload.</summary>
    public static string ComputeChecksum(JsonNode payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var canonical = payload.ToJsonString();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Parses and fully validates an archive. Throws <see cref="SessionArchiveException"/>
    /// for corrupt, truncated, oversized, or hostile content, and
    /// <see cref="NotSupportedException"/> for a schema version this build does not know -
    /// a future archive must fail before anything is installed rather than be half read.
    /// </summary>
    /// <param name="maxBytes">
    /// Overrides <see cref="MaxArchiveBytes"/>. Exists so the oversize guard can be
    /// exercised without materializing a 64 MB fixture.
    /// </param>
    public static SessionArchiveDocument Parse(string json, long? maxBytes = null)
    {
        var limit = maxBytes ?? MaxArchiveBytes;
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new SessionArchiveException("Archive is empty.");
        }
        if (Encoding.UTF8.GetByteCount(json) > limit)
        {
            throw new SessionArchiveException(
                $"Archive exceeds the {limit} byte limit.");
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new SessionArchiveException(
                "Archive is not valid JSON (corrupt or truncated).", ex);
        }

        if (root is not JsonObject document)
        {
            throw new SessionArchiveException("Archive root is not a JSON object.");
        }

        var format = document["format"]?.GetValue<string>();
        if (!string.Equals(format, FormatId, StringComparison.Ordinal))
        {
            throw new SessionArchiveException(
                $"Not an Andy session archive (format '{format ?? "missing"}').");
        }

        var version = ReadInt(document, "schemaVersion");
        if (version is null || version < 1)
        {
            throw new SessionArchiveException("Archive has no usable schema version.");
        }
        if (version > SchemaVersion)
        {
            // Fail before touching the store: a newer archive may carry fields whose
            // meaning this build cannot guess, and a partial install would be worse
            // than a clean refusal.
            throw new NotSupportedException(
                $"Archive schema version {version} is newer than this build supports "
                + $"(max {SchemaVersion}). Upgrade andy-cli to import it.");
        }

        if (document["session"] is not JsonObject session)
        {
            throw new SessionArchiveException("Archive has no session payload.");
        }

        if (document["checksum"] is not JsonObject checksum)
        {
            throw new SessionArchiveException("Archive has no integrity checksum.");
        }
        var algorithm = checksum["algorithm"]?.GetValue<string>();
        if (!string.Equals(algorithm, ChecksumAlgorithm, StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionArchiveException(
                $"Unsupported checksum algorithm '{algorithm ?? "missing"}'.");
        }
        var declared = checksum["value"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(declared))
        {
            throw new SessionArchiveException("Archive checksum is missing.");
        }
        var actual = ComputeChecksum(session);
        if (!string.Equals(declared, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionArchiveException(
                "Archive checksum does not match its contents (corrupt or tampered).");
        }

        var sessionId = session["sessionId"]?.GetValue<string>();
        if (!SessionStore.IsValidSessionId(sessionId))
        {
            // Guards against ids such as "../../etc/passwd": the id becomes a file name
            // in the session directory, so an invalid one is refused outright.
            throw new SessionArchiveException(
                $"Archive session id '{sessionId ?? "missing"}' is not a safe session id.");
        }

        var transcriptNode = session["transcript"];
        if (transcriptNode is null)
        {
            throw new SessionArchiveException("Archive has no transcript.");
        }

        TranscriptSnapshot snapshot;
        try
        {
            snapshot = TranscriptSnapshot.FromJson(transcriptNode.ToJsonString());
        }
        catch (JsonException ex)
        {
            throw new SessionArchiveException("Archive transcript is malformed.", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new NotSupportedException(
                "Archive transcript uses an unsupported snapshot version: " + ex.Message, ex);
        }

        if (snapshot.Turns is null || snapshot.Turns.Count == 0)
        {
            throw new SessionArchiveException("Archive transcript contains no turns.");
        }

        using var element = JsonDocument.Parse(session.ToJsonString());
        var payload = element.RootElement;

        var summary = new SessionSummary
        {
            SessionId = sessionId!,
            CreatedUtc = SessionJson.ReadNullableTimestamp(payload, "createdUtc") ?? DateTimeOffset.MinValue,
            UpdatedUtc = SessionJson.ReadNullableTimestamp(payload, "updatedUtc") ?? DateTimeOffset.MinValue,
            Provider = SessionJson.ReadString(payload, "provider") ?? "",
            Model = SessionJson.ReadString(payload, "model") ?? "",
            TurnCount = snapshot.Turns.Count,
            FirstUserMessage = SessionJson.ReadString(payload, "firstUserMessage") ?? "",
            Title = SessionJson.ReadString(payload, "title") ?? "",
            Lineage = payload.TryGetProperty("lineage", out var lineage)
                ? SessionLineage.FromJson(lineage)
                : null,
            Origin = payload.TryGetProperty("origin", out var origin)
                ? SessionOrigin.FromJson(origin)
                : null,
            Usage = payload.TryGetProperty("usage", out var usage)
                ? SessionUsage.FromJson(usage)
                : null
        };

        return new SessionArchiveDocument(
            version.Value,
            ReadTimestamp(document, "exportedUtc"),
            document["exportedBy"]?.GetValue<string>() ?? "",
            declared!,
            new SessionRecord(summary, snapshot));
    }

    private static string FormatTimestamp(DateTimeOffset value, DateTimeOffset fallback) =>
        (value == DateTimeOffset.MinValue ? fallback : value).UtcDateTime.ToString("O");

    private static int? ReadInt(JsonObject document, string name)
    {
        var node = document[name];
        if (node is null)
        {
            return null;
        }
        try
        {
            return node.GetValue<int>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static DateTimeOffset ReadTimestamp(JsonObject document, string name)
    {
        var text = document[name]?.GetValue<string>();
        return DateTimeOffset.TryParse(text, out var parsed) ? parsed : DateTimeOffset.MinValue;
    }

    private static string DefaultExportedBy()
    {
        var version = typeof(SessionArchive).Assembly.GetName().Version?.ToString() ?? "unknown";
        return "andy-cli/" + version;
    }
}

/// <summary>A parsed, checksum-verified archive.</summary>
public sealed record SessionArchiveDocument(
    int SchemaVersion,
    DateTimeOffset ExportedUtc,
    string ExportedBy,
    string Checksum,
    SessionRecord Record);
