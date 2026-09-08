using System.Text.Json;
using Andy.Cli.Services.Sessions;
using Andy.Engine;

namespace Andy.Cli.ACP;

/// <summary>Atomic, redacted ACP sessions, independent of the in-memory LRU.</summary>
public sealed class AcpSessionStore
{
    private const int MaxBytes = 16 * 1024 * 1024;
    private readonly string _directory;
    private readonly SessionRedactor _redactor = new();
    public AcpSessionStore(string? directory = null) => _directory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".andy", "acp-sessions");

    private string PathFor(string id)
    {
        if (!SessionStore.IsValidSessionId(id)) throw new ArgumentException("Invalid ACP session id.", nameof(id));
        return Path.Combine(_directory, id + ".json");
    }

    public void Save(AcpStoredSession session)
    {
        var path = PathFor(session.SessionId);
        var text = _redactor.RedactJson(JsonSerializer.Serialize(session));
        if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxBytes)
            throw new InvalidDataException("ACP session exceeds the 16 MiB persistence limit.");
        Directory.CreateDirectory(_directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public AcpStoredSession? Load(string id)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("ACP session exceeds the persistence limit.");
        var record = JsonSerializer.Deserialize<AcpStoredSession>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Invalid ACP session.");
        if (record.Version != 1 || record.SessionId != id || !Path.IsPathFullyQualified(record.Cwd))
            throw new InvalidDataException("Invalid ACP session metadata or version.");
        return record;
    }

    public IReadOnlyList<AcpStoredSession> List()
    {
        if (!Directory.Exists(_directory)) return [];
        var records = new List<AcpStoredSession>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try { if (Load(Path.GetFileNameWithoutExtension(path)) is { } record) records.Add(record); }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException) { }
        }
        return records.OrderByDescending(r => r.UpdatedAt).ThenBy(r => r.SessionId, StringComparer.Ordinal).ToArray();
    }

    public bool Delete(string id)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }
}

public sealed record AcpStoredSession
{
    public int Version { get; init; } = 1;
    public required string SessionId { get; init; }
    public required string Cwd { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required string Mode { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public TranscriptSnapshot Snapshot { get; init; } = new();
}
