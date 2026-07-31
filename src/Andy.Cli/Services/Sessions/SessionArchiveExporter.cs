using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Andy.Cli.Services.Sessions;

/// <summary>Result of writing a session out to disk.</summary>
public sealed record SessionExportResult(
    string SessionId,
    string Path,
    int TurnCount,
    long Bytes,
    string? Checksum);

/// <summary>
/// Writes a saved session to a portable archive file (issue #285). The archive is built
/// from the stored - already redacted - session and redacted once more on the way out, so
/// it can be moved between machines without carrying credentials.
/// </summary>
public static class SessionArchiveExporter
{
    /// <summary>
    /// Exports <paramref name="sessionId"/> to <paramref name="path"/> (a file, or a
    /// directory in which the conventional file name is used). The write is atomic:
    /// a temp file next to the target is filled and then moved into place, so a reader
    /// never sees a half-written archive.
    /// </summary>
    public static SessionExportResult Export(
        SessionStore store,
        string sessionId,
        string path,
        SessionRedactor? redactor = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Output path is required.", nameof(path));
        }

        var record = store.Load(sessionId)
            ?? throw new SessionArchiveException($"Session '{sessionId}' was not found.");

        var target = ResolveTarget(path, SessionArchive.DefaultFileName(record.Summary.SessionId));
        var archive = SessionArchive.Build(
            record,
            redactor ?? new SessionRedactor(),
            (clock ?? TimeProvider.System).GetUtcNow());

        var json = archive.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        WriteAtomic(target, json);

        return new SessionExportResult(
            record.Summary.SessionId,
            target,
            record.Summary.TurnCount,
            Encoding.UTF8.GetByteCount(json),
            archive["checksum"]?["value"]?.GetValue<string>());
    }

    /// <summary>
    /// Exports <paramref name="sessionId"/> as human-readable Markdown.
    /// </summary>
    public static SessionExportResult ExportMarkdown(
        SessionStore store,
        string sessionId,
        string path,
        SessionMarkdownOptions? options = null,
        SessionRedactor? redactor = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Output path is required.", nameof(path));
        }

        var record = store.Load(sessionId)
            ?? throw new SessionArchiveException($"Session '{sessionId}' was not found.");

        var target = ResolveTarget(path, SessionArchive.DefaultMarkdownFileName(record.Summary.SessionId));
        var markdown = SessionMarkdownExporter.Render(record, options, redactor);
        WriteAtomic(target, markdown);

        return new SessionExportResult(
            record.Summary.SessionId,
            target,
            record.Summary.TurnCount,
            Encoding.UTF8.GetByteCount(markdown),
            Checksum: null);
    }

    internal static string ResolveTarget(string path, string defaultFileName)
    {
        var full = Path.GetFullPath(path);
        if (Directory.Exists(full))
        {
            return Path.Combine(full, defaultFileName);
        }
        return full;
    }

    internal static void WriteAtomic(string target, string content)
    {
        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = target + ".tmp";
        File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempPath, target, overwrite: true);
    }
}
