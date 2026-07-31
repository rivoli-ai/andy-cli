using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Andy.Cli.Configuration;

/// <summary>
/// The result of a load: the typed configuration, where every value came from,
/// which sources were consulted, everything that went wrong, and the set of
/// resolved secrets that must never be printed.
/// </summary>
public sealed class EffectiveConfiguration
{
    public required AndyConfiguration Config { get; init; }

    /// <summary>The merged JSON, after substitution and path resolution.</summary>
    public required JsonObject Merged { get; init; }

    /// <summary>Winning origin per dotted leaf path.</summary>
    public required IReadOnlyDictionary<string, ConfigOrigin> Provenance { get; init; }

    /// <summary>Sources actually consulted, lowest precedence first.</summary>
    public required IReadOnlyList<ConfigSource> Sources { get; init; }

    /// <summary>Everything found wrong, in discovery order.</summary>
    public required IReadOnlyList<ConfigDiagnostic> Diagnostics { get; init; }

    /// <summary>
    /// Values substituted from the environment. Treated as secret wholesale, because
    /// substitution exists precisely so that secrets stay out of the file.
    /// </summary>
    public required IReadOnlySet<string> SecretValues { get; init; }

    /// <summary>
    /// Locations Andy computed rather than read: the permission rule files (a
    /// dedicated security format that is deliberately NOT merged here), the session
    /// directory, and the config file paths that were looked for.
    /// </summary>
    public required IReadOnlyDictionary<string, string> ResolvedPaths { get; init; }

    /// <summary>The workspace the project layer was discovered from.</summary>
    public required string WorkspaceDirectory { get; init; }

    public bool HasErrors => Diagnostics.Any(d => d.IsError);

    public IEnumerable<ConfigDiagnostic> Errors => Diagnostics.Where(d => d.IsError);

    public IEnumerable<ConfigDiagnostic> Warnings => Diagnostics.Where(d => !d.IsError);

    /// <summary>Origin of a dotted key path, or null when nothing declared it.</summary>
    public ConfigOrigin? OriginOf(string keyPath) =>
        Provenance.TryGetValue(keyPath, out var origin) ? origin : null;
}
