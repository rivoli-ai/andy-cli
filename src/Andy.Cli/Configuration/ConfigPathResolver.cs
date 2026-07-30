using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace Andy.Cli.Configuration;

/// <summary>
/// Turns the relative paths in the merged document into absolute ones.
///
/// The base directory is the directory of the file that DECLARED the value, not the
/// process working directory: a project file saying <c>"directory": "sessions"</c>
/// means the sessions folder next to that file, and it has to keep meaning that
/// however the CLI was launched. Provenance is what makes this possible, so path
/// resolution runs after the merge rather than per layer.
///
/// Values from the environment and CLI layers, which have no file, resolve against
/// the workspace.
/// </summary>
public static class ConfigPathResolver
{
    /// <summary>
    /// Dotted key paths holding a filesystem path. '*' matches exactly one segment,
    /// so map keys (server names) do not have to be enumerated.
    /// </summary>
    private static readonly string[] PathPatterns =
    {
        "session.directory",
        "mcp.servers.*.workingDirectory",
    };

    public static void Resolve(
        JsonObject merged,
        IReadOnlyDictionary<string, ConfigOrigin> provenance,
        string workspaceDirectory,
        ICollection<ConfigDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(merged);

        foreach (var (path, node) in ConfigMerge.Leaves(merged).ToList())
        {
            if (!IsPathField(path))
            {
                continue;
            }
            if (node is not JsonValue value || !value.TryGetValue<string>(out var raw))
            {
                continue;
            }

            var origin = provenance.TryGetValue(path, out var found) ? found : null;
            var baseDirectory = origin?.Source.FilePath is not null
                ? origin.Source.BaseDirectory
                : workspaceDirectory;

            if (TryResolve(raw, baseDirectory, out var absolute, out var reason))
            {
                Set(merged, path, absolute);
            }
            else if (origin is not null)
            {
                diagnostics.Add(ConfigDiagnostic.Error(
                    ConfigDiagnosticCodes.InvalidPath,
                    origin.Source,
                    $"is not a usable path: {reason}",
                    path,
                    origin.Line,
                    origin.Column));
            }
        }
    }

    /// <summary>True when this dotted key path is declared to hold a filesystem path.</summary>
    public static bool IsPathField(string keyPath) =>
        PathPatterns.Any(pattern => Matches(pattern, keyPath));

    private static bool Matches(string pattern, string keyPath)
    {
        var patternSegments = pattern.Split('.');
        var pathSegments = keyPath.Split('.');
        if (patternSegments.Length != pathSegments.Length)
        {
            return false;
        }
        for (var i = 0; i < patternSegments.Length; i++)
        {
            if (patternSegments[i] == "*")
            {
                continue;
            }
            if (!string.Equals(patternSegments[i], pathSegments[i], StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Expands a leading <c>~</c>, then makes the path absolute against
    /// <paramref name="baseDirectory"/>. Returns false with a reason when the value
    /// cannot denote a path at all.
    /// </summary>
    public static bool TryResolve(
        string raw,
        string baseDirectory,
        out string absolute,
        out string reason)
    {
        absolute = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            reason = "the value is empty.";
            return false;
        }

        var candidate = raw.Trim();
        if (candidate == "~" || candidate.StartsWith("~/", StringComparison.Ordinal)
            || candidate.StartsWith(@"~\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidate = candidate.Length <= 1 ? home : Path.Combine(home, candidate[2..]);
        }

        if (candidate.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            reason = "it contains characters that are not legal in a path.";
            return false;
        }

        try
        {
            absolute = Path.GetFullPath(candidate, baseDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = ex.Message;
            return false;
        }

        return true;
    }

    private static void Set(JsonObject root, string dottedPath, string value)
    {
        var segments = dottedPath.Split('.');
        JsonObject? current = root;
        for (var i = 0; i < segments.Length - 1 && current is not null; i++)
        {
            current = current[segments[i]] as JsonObject;
        }
        if (current is not null)
        {
            current[segments[^1]] = value;
        }
    }
}
