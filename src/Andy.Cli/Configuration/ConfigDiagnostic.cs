using System;
using System.Collections.Generic;
using System.Text;

namespace Andy.Cli.Configuration;

/// <summary>Severity of a configuration diagnostic.</summary>
public enum ConfigSeverity
{
    Warning,
    Error,
}

/// <summary>
/// Stable diagnostic identifiers. They are part of the user-facing contract
/// (documented in docs/configuration.md) so scripts can grep for them.
/// </summary>
public static class ConfigDiagnosticCodes
{
    public const string InvalidJson = "ANDYCFG001";
    public const string UnknownKey = "ANDYCFG002";
    public const string InvalidValue = "ANDYCFG003";
    public const string MissingSubstitution = "ANDYCFG004";
    public const string InvalidPath = "ANDYCFG005";
    public const string UnreadableFile = "ANDYCFG006";
    public const string SemanticError = "ANDYCFG007";
}

/// <summary>
/// One problem found while loading configuration, carrying everything needed to
/// find it again: source, line, column, and the dotted key path.
/// </summary>
public sealed record ConfigDiagnostic
{
    public required ConfigSeverity Severity { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public required ConfigSource Source { get; init; }

    /// <summary>1-based line, or 0 when the source has no text (env / CLI layers).</summary>
    public int Line { get; init; }

    /// <summary>1-based column, or 0 when the source has no text.</summary>
    public int Column { get; init; }

    /// <summary>Dotted key path, e.g. "llm.providers.openai.apiKey". Empty for whole-file problems.</summary>
    public string KeyPath { get; init; } = string.Empty;

    public bool IsError => Severity == ConfigSeverity.Error;

    /// <summary>
    /// One-line rendering: "error ANDYCFG002 project:/ws/andy.jsonc:4:5 [llm.nope]: message".
    /// </summary>
    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.Append(Severity == ConfigSeverity.Error ? "error " : "warning ");
        builder.Append(Code).Append(' ');
        builder.Append(Source.Display);
        if (Line > 0)
        {
            builder.Append(':').Append(Line).Append(':').Append(Column);
        }
        if (!string.IsNullOrEmpty(KeyPath))
        {
            builder.Append(" [").Append(KeyPath).Append(']');
        }
        builder.Append(": ").Append(Message);
        return builder.ToString();
    }

    public static ConfigDiagnostic Error(
        string code,
        ConfigSource source,
        string message,
        string keyPath = "",
        int line = 0,
        int column = 0) =>
        new()
        {
            Severity = ConfigSeverity.Error,
            Code = code,
            Source = source,
            Message = message,
            KeyPath = keyPath,
            Line = line,
            Column = column,
        };

    public static ConfigDiagnostic Warning(
        string code,
        ConfigSource source,
        string message,
        string keyPath = "",
        int line = 0,
        int column = 0) =>
        new()
        {
            Severity = ConfigSeverity.Warning,
            Code = code,
            Source = source,
            Message = message,
            KeyPath = keyPath,
            Line = line,
            Column = column,
        };
}
