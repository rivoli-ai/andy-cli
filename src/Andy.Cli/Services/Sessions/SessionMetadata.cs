using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Andy.Cli.Services.Sessions;

/// <summary>
/// Fork/import lineage for a saved session (issue #285). Every field is optional so
/// schema-version-1 session files written before lineage existed keep loading: a
/// session with no lineage simply reports <see cref="IsEmpty"/>.
/// </summary>
public sealed record SessionLineage
{
    /// <summary>The session this one was forked from, when any.</summary>
    public string? ParentSessionId { get; init; }

    /// <summary>The original ancestor of the fork chain (the parent's root, or the parent itself).</summary>
    public string? RootSessionId { get; init; }

    /// <summary>
    /// The 1-based user-turn boundary the fork was taken at: the fork contains the
    /// history strictly BEFORE this turn. Null for a full-session fork.
    /// </summary>
    public int? ForkedAtTurn { get; init; }

    /// <summary>When the fork was created.</summary>
    public DateTimeOffset? ForkedUtc { get; init; }

    /// <summary>The session id recorded in an imported archive, kept even when the local id had to change.</summary>
    public string? ImportedFromSessionId { get; init; }

    /// <summary>When the session was imported from an archive.</summary>
    public DateTimeOffset? ImportedUtc { get; init; }

    public bool IsEmpty =>
        string.IsNullOrEmpty(ParentSessionId)
        && string.IsNullOrEmpty(RootSessionId)
        && ForkedAtTurn is null
        && ForkedUtc is null
        && string.IsNullOrEmpty(ImportedFromSessionId)
        && ImportedUtc is null;

    public JsonObject ToJson()
    {
        var node = new JsonObject();
        if (!string.IsNullOrEmpty(ParentSessionId)) node["parentSessionId"] = ParentSessionId;
        if (!string.IsNullOrEmpty(RootSessionId)) node["rootSessionId"] = RootSessionId;
        if (ForkedAtTurn is { } turn) node["forkedAtTurn"] = turn;
        if (ForkedUtc is { } forked) node["forkedUtc"] = forked.UtcDateTime.ToString("O");
        if (!string.IsNullOrEmpty(ImportedFromSessionId)) node["importedFromSessionId"] = ImportedFromSessionId;
        if (ImportedUtc is { } imported) node["importedUtc"] = imported.UtcDateTime.ToString("O");
        return node;
    }

    public static SessionLineage? FromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var lineage = new SessionLineage
        {
            ParentSessionId = SessionJson.ReadString(element, "parentSessionId"),
            RootSessionId = SessionJson.ReadString(element, "rootSessionId"),
            ForkedAtTurn = SessionJson.ReadNullableInt(element, "forkedAtTurn"),
            ForkedUtc = SessionJson.ReadNullableTimestamp(element, "forkedUtc"),
            ImportedFromSessionId = SessionJson.ReadString(element, "importedFromSessionId"),
            ImportedUtc = SessionJson.ReadNullableTimestamp(element, "importedUtc")
        };
        return lineage.IsEmpty ? null : lineage;
    }
}

/// <summary>
/// Where a session was recorded. Path metadata travels with an exported archive but is
/// purely INFORMATIONAL on another machine: nothing in the import path ever opens,
/// creates, or resolves a file relative to it (see <see cref="ResolveLocalWorkspace"/>).
/// </summary>
public sealed record SessionOrigin
{
    /// <summary>Absolute workspace directory as it existed on the recording machine.</summary>
    public string WorkspacePath { get; init; } = "";

    /// <summary>"windows", "macos", "linux" or "unknown".</summary>
    public string Platform { get; init; } = "";

    public bool IsEmpty => string.IsNullOrEmpty(WorkspacePath) && string.IsNullOrEmpty(Platform);

    public JsonObject ToJson()
    {
        var node = new JsonObject();
        if (!string.IsNullOrEmpty(WorkspacePath)) node["workspacePath"] = WorkspacePath;
        if (!string.IsNullOrEmpty(Platform)) node["platform"] = Platform;
        return node;
    }

    public static SessionOrigin? FromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var origin = new SessionOrigin
        {
            WorkspacePath = SessionJson.ReadString(element, "workspacePath") ?? "",
            Platform = SessionJson.ReadString(element, "platform") ?? ""
        };
        return origin.IsEmpty ? null : origin;
    }

    /// <summary>Origin metadata for the machine running right now.</summary>
    public static SessionOrigin ForCurrentMachine(string? workspacePath = null) => new()
    {
        WorkspacePath = workspacePath ?? SafeCurrentDirectory(),
        Platform = CurrentPlatform()
    };

    public static string CurrentPlatform() =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "macos"
        : OperatingSystem.IsLinux() ? "linux"
        : "unknown";

    /// <summary>
    /// The workspace path ONLY when it is a plain rooted directory that actually exists on
    /// this machine. Anything else - a foreign platform's path, a relative path, or one
    /// containing traversal segments - yields null and stays informational.
    /// </summary>
    public string? ResolveLocalWorkspace()
    {
        if (string.IsNullOrWhiteSpace(WorkspacePath))
        {
            return null;
        }
        if (WorkspacePath.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }
        if (WorkspacePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return null;
        }
        if (!Path.IsPathRooted(WorkspacePath))
        {
            return null;
        }

        try
        {
            return Directory.Exists(WorkspacePath) ? WorkspacePath : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Human-readable one-liner used by the export/import summaries.</summary>
    public string Describe()
    {
        if (IsEmpty)
        {
            return "unknown origin";
        }
        var platform = string.IsNullOrEmpty(Platform) ? "unknown platform" : Platform;
        if (string.IsNullOrEmpty(WorkspacePath))
        {
            return platform;
        }
        var availability = ResolveLocalWorkspace() is null
            ? " (not available on this machine; informational)"
            : "";
        return $"{WorkspacePath} [{platform}]{availability}";
    }

    private static string SafeCurrentDirectory()
    {
        try
        {
            return Directory.GetCurrentDirectory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}

/// <summary>
/// Optional metadata carried alongside a <see cref="SessionStore.Save"/> call. Every
/// property is opt-in; anything left null keeps whatever the existing session file
/// already recorded, so a plain save never erases a title, lineage, or usage totals.
/// </summary>
public sealed record SessionSaveOptions
{
    /// <summary>User-facing title. Empty string clears the stored title.</summary>
    public string? Title { get; init; }

    public SessionLineage? Lineage { get; init; }

    public SessionOrigin? Origin { get; init; }

    public SessionUsage? Usage { get; init; }

    /// <summary>Overrides the created timestamp (used when installing an imported archive).</summary>
    public DateTimeOffset? CreatedUtc { get; init; }

    /// <summary>Overrides the updated timestamp (used when installing an imported archive).</summary>
    public DateTimeOffset? UpdatedUtc { get; init; }

    /// <summary>
    /// When true (the default) and neither this call nor the existing file supplies an
    /// origin, the current machine's workspace/platform is recorded.
    /// </summary>
    public bool CaptureOrigin { get; init; } = true;
}

/// <summary>Small JsonElement readers shared by the session metadata types.</summary>
internal static class SessionJson
{
    public static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static long ReadLong(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var parsed)
            ? parsed
            : 0;

    public static int? ReadNullableInt(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    public static decimal? ReadNullableDecimal(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDecimal(out var parsed)
            ? parsed
            : null;

    public static DateTimeOffset? ReadNullableTimestamp(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.TryGetDateTimeOffset(out var parsed)
            ? parsed
            : null;
}
