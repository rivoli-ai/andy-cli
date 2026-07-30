using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Andy.Cli.Services.Shell;

namespace Andy.Cli.Services.Sessions;

/// <summary>
/// One command the user ran in shell mode, as persisted for replay, export and audit (issue #286).
/// </summary>
/// <param name="TimestampUtc">When the command was submitted.</param>
/// <param name="Command">The command line, redacted.</param>
/// <param name="ExitCode">Process exit code, when one was reported.</param>
/// <param name="Status">The command's terminal state ("exit 0", "denied", "timed out", ...).</param>
/// <param name="DurationMs">Measured wall-clock duration in milliseconds.</param>
/// <param name="WorkingDirectory">Directory the command ran in.</param>
/// <param name="OutputPreview">Bounded, redacted head of the combined output.</param>
public sealed record UserShellRecord(
    DateTimeOffset TimestampUtc,
    string Command,
    int? ExitCode,
    string Status,
    long DurationMs,
    string WorkingDirectory,
    string OutputPreview)
{
    /// <summary>
    /// Identifies the actor. Always the literal "user": this store only ever holds commands the
    /// person at the keyboard typed in shell mode. Model-requested commands live in the engine
    /// transcript as tool calls and never appear here, which is what keeps the two distinguishable
    /// in replay, export and instrumentation without any heuristics.
    /// </summary>
    public const string Source = "user";

    /// <summary>Entry kind written to the file, so a future reader can tell records apart.</summary>
    public const string Kind = "user_shell";

    /// <summary>
    /// The attributed one-liner used in replay and export. The leading <c>!</c> is the same marker
    /// the composer shows in shell mode, so a transcript reads the way the session looked.
    /// </summary>
    public string ToTranscriptLine()
    {
        var command = Command.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return string.Format(
            CultureInfo.InvariantCulture,
            "[user shell] ! {0}  ({1})",
            command,
            Status);
    }
}

/// <summary>
/// Persists the user's shell-mode commands for a session.
///
/// It is a SEPARATE artifact from the transcript on purpose. The transcript is the engine's
/// conversation - what the model saw - and a user-invoked command is deliberately not part of it
/// (see <see cref="UserShellOutputAttachment"/>: output reaches the model only when the user
/// attaches it). Keeping these commands in their own file means:
///
/// <list type="bullet">
/// <item><description>a user command can never be mistaken for a model tool call in replay or
/// export, because the two never share a container;</description></item>
/// <item><description>resuming a session restores the model's context exactly as it was, without
/// retroactively teaching it what the user did in a side channel;</description></item>
/// <item><description>the engine's <c>TranscriptSnapshot</c> schema (which the CLI does not own)
/// needs no change.</description></item>
/// </list>
///
/// Layout mirrors <see cref="Andy.Cli.Services.SessionApprovalStore"/>: one JSON file per session
/// under ~/.andy/sessions/ named <c>{sessionId}.shell.json</c>, written atomically (temp + move).
/// Everything is redacted by the caller before it arrives here; the store redacts again as a
/// backstop so a caller that forgets cannot write a secret to disk.
/// </summary>
public sealed class UserShellLogStore
{
    /// <summary>Envelope version, hard-checked on load.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Characters of combined output retained per command.</summary>
    public const int MaxOutputPreviewCharacters = 4000;

    /// <summary>Commands retained per session; older ones are dropped as the log grows.</summary>
    public const int MaxRecords = 500;

    private readonly SessionRedactor _redactor;
    private readonly TimeProvider _clock;

    public UserShellLogStore(string? directory = null, SessionRedactor? redactor = null, TimeProvider? clock = null)
    {
        DirectoryPath = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".andy", "sessions");
        _redactor = redactor ?? new SessionRedactor();
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Directory the per-session log files live in.</summary>
    public string DirectoryPath { get; }

    private string PathFor(string sessionId) => Path.Combine(DirectoryPath, sessionId + ".shell.json");

    /// <summary>
    /// Appends one completed command. Best-effort: a failure here must never disturb the composer,
    /// so everything is caught and written to the crash log instead of thrown.
    /// </summary>
    public void Record(string sessionId, UserShellCommandResult result)
    {
        if (!SessionStore.IsValidSessionId(sessionId) || result is null) return;

        try
        {
            var redacted = result.Redact(_redactor);
            var combined = Combine(redacted.StandardOutput, redacted.StandardError);
            var record = new UserShellRecord(
                TimestampUtc: result.StartedAtUtc == default ? _clock.GetUtcNow() : result.StartedAtUtc,
                Command: redacted.Command,
                ExitCode: redacted.ExitCode,
                Status: redacted.StatusLabel,
                DurationMs: (long)redacted.Duration.TotalMilliseconds,
                WorkingDirectory: redacted.WorkingDirectory,
                OutputPreview: combined);

            var all = Load(sessionId).ToList();
            all.Add(record);
            while (all.Count > MaxRecords) all.RemoveAt(0);
            Save(sessionId, all);
        }
        catch (Exception ex)
        {
            CrashLog.Write("usershell.Record", ex);
        }
    }

    /// <summary>
    /// Every command recorded for the session, oldest first. Returns empty for an unknown or
    /// corrupt file: a broken log must never block resuming a session.
    /// </summary>
    public IReadOnlyList<UserShellRecord> Load(string sessionId)
    {
        if (!SessionStore.IsValidSessionId(sessionId)) return Array.Empty<UserShellRecord>();

        var path = PathFor(sessionId);
        if (!File.Exists(path)) return Array.Empty<UserShellRecord>();

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out var versionEl)
                || !versionEl.TryGetInt32(out var version)
                || version != SchemaVersion)
            {
                return Array.Empty<UserShellRecord>();
            }
            if (!root.TryGetProperty("commands", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<UserShellRecord>();
            }

            var list = new List<UserShellRecord>();
            foreach (var el in arr.EnumerateArray())
            {
                if (Read(el) is { } record) list.Add(record);
            }
            return list;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
        {
            return Array.Empty<UserShellRecord>();
        }
    }

    /// <summary>Removes the log for a session (used when a session id is rotated away).</summary>
    public void Delete(string sessionId)
    {
        if (!SessionStore.IsValidSessionId(sessionId)) return;
        try { File.Delete(PathFor(sessionId)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private void Save(string sessionId, IReadOnlyList<UserShellRecord> records)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = PathFor(sessionId);

        var arr = new JsonArray();
        foreach (var r in records)
        {
            arr.Add(new JsonObject
            {
                // "kind" and "source" are written on every record, so an exported log is
                // self-describing: a reader never has to infer that these were the user's own
                // commands rather than the model's tool calls.
                ["kind"] = UserShellRecord.Kind,
                ["source"] = UserShellRecord.Source,
                ["ts"] = r.TimestampUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                ["command"] = r.Command,
                ["exitCode"] = r.ExitCode,
                ["status"] = r.Status,
                ["durationMs"] = r.DurationMs,
                ["workingDirectory"] = r.WorkingDirectory,
                ["output"] = r.OutputPreview,
            });
        }

        var envelope = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["sessionId"] = sessionId,
            ["commands"] = arr,
        };

        var json = _redactor.RedactJson(envelope.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    private static UserShellRecord? Read(JsonElement el)
    {
        if (!el.TryGetProperty("command", out var commandEl)) return null;
        var command = commandEl.GetString();
        if (string.IsNullOrWhiteSpace(command)) return null;

        int? exitCode = el.TryGetProperty("exitCode", out var ec) && ec.ValueKind == JsonValueKind.Number
            && ec.TryGetInt32(out var code) ? code : null;

        return new UserShellRecord(
            TimestampUtc: el.TryGetProperty("ts", out var ts) && ts.TryGetDateTimeOffset(out var t)
                ? t : DateTimeOffset.MinValue,
            Command: command,
            ExitCode: exitCode,
            Status: el.TryGetProperty("status", out var st) ? st.GetString() ?? string.Empty : string.Empty,
            DurationMs: el.TryGetProperty("durationMs", out var d) && d.ValueKind == JsonValueKind.Number
                && d.TryGetInt64(out var ms) ? ms : 0,
            WorkingDirectory: el.TryGetProperty("workingDirectory", out var wd) ? wd.GetString() ?? string.Empty : string.Empty,
            OutputPreview: el.TryGetProperty("output", out var op) ? op.GetString() ?? string.Empty : string.Empty);
    }

    private static string Combine(string stdout, string stderr)
    {
        var text = stdout ?? string.Empty;
        if (!string.IsNullOrEmpty(stderr))
        {
            if (text.Length > 0 && !text.EndsWith('\n')) text += "\n";
            text += stderr;
        }
        return text.Length <= MaxOutputPreviewCharacters ? text : text[..MaxOutputPreviewCharacters];
    }
}
