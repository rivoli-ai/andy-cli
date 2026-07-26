using System;
using System.Collections.Generic;
using System.Linq;

namespace Andy.Cli.Lsp;

/// <summary>
/// One explicitly configured language server: how to launch it, which files it claims, and how
/// its project root is discovered.
///
/// Andy NEVER downloads a language server. A definition names a command that must already be on
/// PATH (or be an absolute path); when it is missing the server is reported as unavailable and
/// the agent loop continues untouched.
/// </summary>
public sealed class LspServerDefinition
{
    /// <summary>Stable identifier used by <c>/lsp status</c> and <c>/lsp restart</c>.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Executable to launch. Resolved through PATH when not rooted.</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>Arguments passed to <see cref="Command"/>.</summary>
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    /// <summary>Extra environment variables for the server process (merged over the inherited environment).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>File extensions this server claims, including the leading dot (".cs", ".ts").</summary>
    public IReadOnlyList<string> Extensions { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Files or directories that mark the server's project root (for example "*.sln",
    /// "package.json", "go.mod"). The first ancestor of the changed file that contains one wins;
    /// the search never walks above the workspace root.
    /// </summary>
    public IReadOnlyList<string> RootMarkers { get; init; } = Array.Empty<string>();

    /// <summary>Language id sent in textDocument/didOpen. Defaults to <see cref="Id"/>.</summary>
    public string? LanguageId { get; init; }

    /// <summary>Raw JSON passed through as the initialize request's initializationOptions.</summary>
    public string? InitializationOptionsJson { get; init; }

    /// <summary>Whether the definition participates at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How long to wait for the initialize handshake before giving up on the server.</summary>
    public int StartTimeoutMs { get; init; } = LspLimits.DefaultStartTimeoutMs;

    /// <summary>How long a single changed-file diagnostics wait may block the tool call.</summary>
    public int DiagnosticsTimeoutMs { get; init; } = LspLimits.DefaultDiagnosticsTimeoutMs;

    /// <summary>Resolved language id for didOpen.</summary>
    public string EffectiveLanguageId =>
        string.IsNullOrWhiteSpace(LanguageId) ? Id : LanguageId!;

    /// <summary>Whether this definition claims the given file path by extension.</summary>
    public bool Matches(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var extension = System.IO.Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension)) return false;
        return Extensions.Any(candidate =>
            string.Equals(Normalize(candidate), extension, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string extension) =>
        extension.StartsWith('.') ? extension : "." + extension;
}

/// <summary>
/// Bounds applied to changed-file diagnostics so a noisy file can never flood the model context
/// or the feed. Every limit is a hard cap; whatever is dropped is reported as truncation metadata.
/// </summary>
public static class LspLimits
{
    public const int DefaultStartTimeoutMs = 15_000;
    public const int DefaultDiagnosticsTimeoutMs = 3_000;

    /// <summary>Maximum diagnostics reported for a single changed file.</summary>
    public const int MaxDiagnosticsPerFile = 20;

    /// <summary>Maximum characters kept from a single diagnostic message.</summary>
    public const int MaxMessageLength = 240;

    /// <summary>Maximum characters of rendered diagnostics text per file.</summary>
    public const int MaxRenderedChars = 2_000;

    /// <summary>Files larger than this are not synced to a language server.</summary>
    public const long MaxSyncedFileBytes = 2L * 1024 * 1024;
}
