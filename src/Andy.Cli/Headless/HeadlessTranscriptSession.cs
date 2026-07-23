using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Andy.Cli.HeadlessConfig;

namespace Andy.Cli.Headless;

/// <summary>
/// Captures the canonical headless event stream into one bounded, redacted,
/// atomically published transcript. Failures are retained as status instead of
/// escaping into the primary event/output path.
/// </summary>
public sealed class HeadlessTranscriptSession : IDisposable
{
    private const int TerminalReserveOverhead = 512;
    private static readonly TimeSpan s_staleTempAge = TimeSpan.FromHours(1);
    private static readonly Regex s_bearerPattern = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.Compiled);
    private static readonly Regex s_keyValuePattern = new(
        @"(?i)\b(api[_-]?key|access[_-]?token|token|secret|password)\b[""']?"
            + @"(\s*[:=]\s*)[""']?([^\s,""'}]+)",
        RegexOptions.Compiled);
    private static readonly Regex s_apiKeyPattern = new(
        @"\b(sk-(?:or-)?[A-Za-z0-9_-]{8,})\b",
        RegexOptions.Compiled);
    private static int s_instanceSequence;

    private readonly HeadlessTranscript _options;
    private readonly FileStream _stream;
    private readonly IReadOnlyList<string> _secretValues;
    private readonly TimeProvider _clock;
    private readonly object _sync = new();
    private long _bytesWritten;
    private bool _limitReached;
    private bool _completed;
    private bool _disposed;

    private HeadlessTranscriptSession(
        HeadlessTranscript options,
        string directory,
        string tempPath,
        string finalPath,
        FileStream stream,
        IReadOnlyList<string> secretValues,
        TimeProvider clock)
    {
        _options = options;
        DirectoryPath = directory;
        TempPath = tempPath;
        FinalPath = finalPath;
        _stream = stream;
        _secretValues = secretValues;
        _clock = clock;
    }

    public string DirectoryPath { get; }
    public string TempPath { get; }
    public string FinalPath { get; }
    public string? Failure { get; private set; }

    public static TranscriptCreationResult TryCreate(
        HeadlessRunConfig config,
        TimeProvider? clock = null)
    {
        if (config.Transcript is null)
        {
            return new TranscriptCreationResult(null, null);
        }

        clock ??= TimeProvider.System;
        var options = config.Transcript;
        var directory = string.IsNullOrWhiteSpace(options.Directory)
            ? Path.Combine(config.Workspace.Root, ".andy", "transcripts")
            : Path.GetFullPath(options.Directory);

        try
        {
            Directory.CreateDirectory(directory);
            CleanupStaleTempFiles(directory, clock.GetUtcNow());

            var now = clock.GetUtcNow();
            var sequence = Interlocked.Increment(ref s_instanceSequence);
            var stem = $"{config.RunId:D}.{now:yyyyMMddTHHmmssfffffffZ}."
                + $"{Environment.ProcessId:D6}.{sequence:D6}";
            var finalPath = Path.Combine(directory, stem + ".ndjson");
            var tempPath = Path.Combine(directory, "." + stem + ".tmp");
            var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);

            return new TranscriptCreationResult(
                new HeadlessTranscriptSession(
                    options,
                    directory,
                    tempPath,
                    finalPath,
                    stream,
                    ResolveSecretValues(config),
                    clock),
                null);
        }
        catch (Exception ex)
        {
            return new TranscriptCreationResult(
                null,
                $"Transcript initialization failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Captures a non-terminal event. The primary event writer remains healthy if
    /// persistence fails; <see cref="Failure"/> is reported before its finished event.
    /// </summary>
    public void Capture(string eventLine)
    {
        lock (_sync)
        {
            if (_completed || _disposed || Failure is not null || _limitReached)
            {
                return;
            }

            try
            {
                var record = PrepareRecord(eventLine);
                var terminalReserve = _options.MaxRecordBytes + TerminalReserveOverhead;
                if (_bytesWritten + record.Length + 1 > _options.MaxRunBytes - terminalReserve)
                {
                    WriteLimitMarker();
                    _limitReached = true;
                    return;
                }

                WriteBytes(record);
            }
            catch (Exception ex)
            {
                SetFailure("write", ex);
            }
        }
    }

    /// <summary>
    /// Appends the terminal event, fsyncs, and atomically publishes the transcript.
    /// Returns a diagnostic on failure and never throws.
    /// </summary>
    public string? Complete(string terminalEventLine)
    {
        lock (_sync)
        {
            if (_completed)
            {
                return Failure;
            }

            _completed = true;
            if (Failure is not null)
            {
                AbortTempFile();
                return Failure;
            }

            try
            {
                var terminalRecord = PrepareRecord(terminalEventLine);
                if (_bytesWritten + terminalRecord.Length + 1 > _options.MaxRunBytes)
                {
                    throw new IOException(
                        "The bounded transcript has no room for its terminal record.");
                }

                WriteBytes(terminalRecord);
                _stream.Flush(flushToDisk: true);
                _stream.Dispose();
                File.Move(TempPath, FinalPath, overwrite: false);
                CleanupCompletedTranscripts();
            }
            catch (Exception ex)
            {
                SetFailure("completion", ex);
                AbortTempFile();
            }

            return Failure;
        }
    }

    private byte[] PrepareRecord(string eventLine)
    {
        var redacted = RedactEvent(eventLine);
        var bytes = Encoding.UTF8.GetBytes(redacted);
        if (bytes.Length <= _options.MaxRecordBytes)
        {
            return bytes;
        }

        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var kind = TryGetEventKind(redacted);
        var previewBudget = Math.Max(64, _options.MaxRecordBytes / 4);
        var preview = TruncateUtf8(redacted, previewBudget);
        var bounded = JsonSerializer.Serialize(new
        {
            schema_version = 1,
            ts = _clock.GetUtcNow(),
            kind,
            data = new
            {
                transcript_truncated = true,
                original_bytes = bytes.Length,
                digest,
                preview
            }
        });

        var boundedBytes = Encoding.UTF8.GetBytes(bounded);
        if (boundedBytes.Length > _options.MaxRecordBytes)
        {
            throw new IOException("The transcript record limit is too small for truncation metadata.");
        }

        return boundedBytes;
    }

    private string RedactEvent(string eventLine)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(eventLine);
        }
        catch (JsonException)
        {
            return RedactText(eventLine);
        }

        if (root is null)
        {
            return string.Empty;
        }

        RedactNode(root, propertyName: null);
        return root.ToJsonString();
    }

    private void RedactNode(JsonNode node, string? propertyName)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (IsSensitiveProperty(property.Key))
                {
                    obj[property.Key] = "[REDACTED]";
                }
                else if (property.Value is not null)
                {
                    RedactNode(property.Value, property.Key);
                }
            }
            return;
        }

        if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                if (child is not null)
                {
                    RedactNode(child, propertyName);
                }
            }
            return;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            value.ReplaceWith(JsonValue.Create(RedactText(text)));
        }
    }

    private string RedactText(string text)
    {
        var redacted = text;
        foreach (var secret in _secretValues)
        {
            redacted = redacted.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        redacted = s_bearerPattern.Replace(redacted, "Bearer [REDACTED]");
        redacted = s_keyValuePattern.Replace(redacted, "$1$2[REDACTED]");
        redacted = s_apiKeyPattern.Replace(redacted, "[REDACTED]");
        return redacted;
    }

    private static bool IsSensitiveProperty(string propertyName)
    {
        var normalized = propertyName.Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
        return normalized is "api_key" or "apikey" or "authorization" or "access_token"
            or "token" or "secret" or "password";
    }

    private static IReadOnlyList<string> ResolveSecretValues(HeadlessRunConfig config)
    {
        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            "ANDY_TOKEN"
        };

        if (!string.IsNullOrWhiteSpace(config.Model.ApiKeyRef)
            && HeadlessConfigValidator.TryParseEnvRef(
                config.Model.ApiKeyRef,
                out var apiKeyName,
                out _))
        {
            names.Add(apiKeyName);
        }

        if (config.Transcript is { } transcript)
        {
            foreach (var name in transcript.RedactEnvVars)
            {
                names.Add(name);
            }
        }

        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var value = config.EnvVars is not null && config.EnvVars.TryGetValue(name, out var configured)
                ? configured
                : Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value) && value.Length >= 4)
            {
                values.Add(value);
            }
        }

        return values.OrderByDescending(value => value.Length).ToArray();
    }

    private void WriteLimitMarker()
    {
        var marker = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = 1,
            ts = _clock.GetUtcNow(),
            kind = "transcript_limit_reached",
            data = new
            {
                max_run_bytes = _options.MaxRunBytes,
                message = "Intermediate transcript records were omitted; the terminal record is retained."
            }
        });

        if (_bytesWritten + marker.Length + 1 <= _options.MaxRunBytes - _options.MaxRecordBytes)
        {
            WriteBytes(marker);
        }
    }

    private void WriteBytes(byte[] bytes)
    {
        _stream.Write(bytes);
        _stream.WriteByte((byte)'\n');
        _stream.Flush();
        _bytesWritten += bytes.Length + 1;
    }

    private void CleanupCompletedTranscripts()
    {
        var now = _clock.GetUtcNow();
        var cutoff = now - TimeSpan.FromDays(_options.MaxAgeDays);
        var files = new DirectoryInfo(DirectoryPath)
            .EnumerateFiles("*.ndjson", SearchOption.TopDirectoryOnly)
            .OrderBy(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var file in files.Where(file => file.LastWriteTimeUtc < cutoff.UtcDateTime).ToList())
        {
            TryDelete(file.FullName);
            files.Remove(file);
        }

        var totalBytes = files.Sum(file => file.Exists ? file.Length : 0);
        while (files.Count > _options.MaxFiles || totalBytes > _options.MaxTotalBytes)
        {
            var oldest = files[0];
            var length = oldest.Exists ? oldest.Length : 0;
            TryDelete(oldest.FullName);
            files.RemoveAt(0);
            totalBytes -= length;
        }
    }

    private static void CleanupStaleTempFiles(string directory, DateTimeOffset now)
    {
        var cutoff = now - s_staleTempAge;
        foreach (var file in new DirectoryInfo(directory)
            .EnumerateFiles(".*.tmp", SearchOption.TopDirectoryOnly)
            .OrderBy(file => file.Name, StringComparer.Ordinal))
        {
            if (file.LastWriteTimeUtc < cutoff.UtcDateTime)
            {
                TryDelete(file.FullName);
            }
        }
    }

    private static string TryGetEventKind(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.TryGetProperty("kind", out var kind)
                ? kind.GetString() ?? "transcript_record"
                : "transcript_record";
        }
        catch (JsonException)
        {
            return "transcript_record";
        }
    }

    private static string TruncateUtf8(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (bytes + runeBytes > maxBytes)
            {
                break;
            }
            builder.Append(rune.ToString());
            bytes += runeBytes;
        }
        return builder.ToString();
    }

    private void SetFailure(string stage, Exception ex)
    {
        Failure ??= $"Transcript {stage} failed: {ex.GetType().Name}: {ex.Message}";
    }

    private void AbortTempFile()
    {
        try
        {
            _stream.Dispose();
        }
        catch
        {
            // Best effort; preserve the original failure.
        }
        TryDelete(TempPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Retention and crash cleanup are best effort.
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (!_completed)
            {
                AbortTempFile();
            }
        }
    }
}

public sealed record TranscriptCreationResult(
    HeadlessTranscriptSession? Session,
    string? Error);
