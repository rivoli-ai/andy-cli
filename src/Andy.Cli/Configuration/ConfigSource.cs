using System;
using System.IO;

namespace Andy.Cli.Configuration;

/// <summary>
/// The layers Andy configuration is assembled from, lowest precedence first.
/// The numeric order is load-bearing: <see cref="ConfigLayerBuilder"/> sorts by it and
/// <c>andy-cli config show --sources</c> prints it, so the documented precedence
/// (packaged defaults &lt; user &lt; project &lt; environment &lt; CLI arguments) is
/// expressed exactly once.
/// </summary>
public enum ConfigSourceKind
{
    /// <summary>Values baked into the binary plus the packaged appsettings.json.</summary>
    PackagedDefaults = 0,

    /// <summary>~/.andy/andy.jsonc</summary>
    User = 1,

    /// <summary>&lt;workspace&gt;/andy.jsonc, then &lt;workspace&gt;/.andy/andy.jsonc.</summary>
    Project = 2,

    /// <summary>Legacy environment variables (ANDY_THEME, OPENAI_API_KEY, ...).</summary>
    Environment = 3,

    /// <summary>Command-line arguments (--theme, --model, --auto, ...).</summary>
    CommandLine = 4,
}

/// <summary>
/// Identifies one configuration source precisely enough to point a user at it:
/// which layer it belongs to, and (for file layers) the absolute path plus the
/// directory that relative paths declared inside it resolve against.
/// </summary>
public sealed record ConfigSource
{
    public required ConfigSourceKind Kind { get; init; }

    /// <summary>Absolute path of the file, or null for the synthetic env/CLI/built-in layers.</summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// Directory that a relative path declared in this source resolves against.
    /// The declaring file's directory for file layers; the workspace for the rest.
    /// </summary>
    public required string BaseDirectory { get; init; }

    /// <summary>Short label used in diagnostics and <c>config show</c> output.</summary>
    public string Display => FilePath is null ? KindLabel : $"{KindLabel}:{FilePath}";

    /// <summary>The layer name on its own ("packaged defaults", "user", ...).</summary>
    public string KindLabel => Kind switch
    {
        ConfigSourceKind.PackagedDefaults => "packaged defaults",
        ConfigSourceKind.User => "user",
        ConfigSourceKind.Project => "project",
        ConfigSourceKind.Environment => "environment",
        ConfigSourceKind.CommandLine => "cli",
        _ => Kind.ToString().ToLowerInvariant(),
    };

    public static ConfigSource File(ConfigSourceKind kind, string path)
    {
        var full = Path.GetFullPath(path);
        return new ConfigSource
        {
            Kind = kind,
            FilePath = full,
            BaseDirectory = Path.GetDirectoryName(full) ?? full,
        };
    }

    public static ConfigSource Synthetic(ConfigSourceKind kind, string baseDirectory) =>
        new() { Kind = kind, BaseDirectory = baseDirectory };
}

/// <summary>
/// Where one effective value came from: its source plus the exact line and column
/// inside that source (0 for synthetic layers, which have no text).
/// </summary>
public sealed record ConfigOrigin(ConfigSource Source, int Line, int Column)
{
    /// <summary>"project:/ws/andy.jsonc:12:5" or just "environment" for synthetic layers.</summary>
    public override string ToString() =>
        Source.FilePath is null ? Source.KindLabel : $"{Source.Display}:{Line}:{Column}";
}
