using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Andy.Cli.Configuration;

/// <summary>
/// One source after parsing: its identity, its JSON body, and (for file layers)
/// the position map used to point diagnostics at the exact line and column.
/// </summary>
public sealed class ConfigLayer
{
    public required ConfigSource Source { get; init; }

    /// <summary>The layer's contribution. Mutated in place by substitution.</summary>
    public required JsonObject Root { get; init; }

    /// <summary>Position map, or null for synthetic (environment / CLI) layers.</summary>
    public JsoncDocument? Document { get; init; }

    /// <summary>
    /// Missing <c>{env:NAME}</c> references are fatal for user and project files —
    /// the user asked for a value that is not there. They are only a warning in the
    /// packaged-defaults layer, whose provider placeholders intentionally cover
    /// providers the machine has no credentials for.
    /// </summary>
    public bool SubstitutionFailureIsFatal => Source.Kind is ConfigSourceKind.User
        or ConfigSourceKind.Project or ConfigSourceKind.CommandLine;

    /// <summary>Best-known position for a key path in this layer.</summary>
    public (int Line, int Column) PositionOf(string keyPath) =>
        Document?.BestPosition(keyPath) ?? (0, 0);

    /// <summary>Position of a value (not its key) in this layer.</summary>
    public (int Line, int Column) ValuePositionOf(string keyPath) =>
        Document?.ValuePosition(keyPath) ?? Document?.KeyPosition(keyPath) ?? (0, 0);

    public ConfigOrigin OriginOf(string keyPath)
    {
        var (line, column) = ValuePositionOf(keyPath);
        return new ConfigOrigin(Source, line, column);
    }
}
