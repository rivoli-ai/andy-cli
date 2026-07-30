using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Andy.Cli.Configuration;

/// <summary>
/// Renders the effective configuration for <c>andy-cli config</c>.
///
/// Redaction happens HERE, at the single point where configuration becomes text,
/// rather than being sprinkled over the call sites: every value goes through
/// <see cref="ConfigRedactor"/> before it reaches a StringBuilder, and diagnostic
/// messages are scrubbed of resolved secrets on the way out too.
/// </summary>
public static class ConfigReportFormatter
{
    /// <summary>The effective values, one per line, optionally annotated with their source.</summary>
    public static string FormatEffective(EffectiveConfiguration effective, bool includeSources)
    {
        ArgumentNullException.ThrowIfNull(effective);

        var builder = new StringBuilder();
        builder.AppendLine("Effective configuration");
        builder.AppendLine("-----------------------");

        var leaves = ConfigMerge.Leaves(effective.Merged).ToList();
        var width = leaves.Count == 0 ? 0 : leaves.Max(leaf => leaf.Path.Length);

        foreach (var (path, value) in leaves)
        {
            var rendered = ConfigRedactor.Redact(path, Render(value), effective.SecretValues);
            if (includeSources)
            {
                var origin = effective.OriginOf(path);
                builder.AppendLine(
                    $"  {path.PadRight(width)} = {rendered}    [{origin?.ToString() ?? "unset"}]");
            }
            else
            {
                builder.AppendLine($"  {path.PadRight(width)} = {rendered}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Resolved locations");
        builder.AppendLine("------------------");
        var pathWidth = effective.ResolvedPaths.Count == 0
            ? 0
            : effective.ResolvedPaths.Keys.Max(key => key.Length);
        foreach (var pair in effective.ResolvedPaths.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            builder.AppendLine($"  {pair.Key.PadRight(pathWidth)} = {pair.Value}");
        }
        builder.AppendLine();
        builder.AppendLine(
            "  Permission rules keep their own file format and are not merged into this configuration.");

        if (includeSources)
        {
            builder.AppendLine();
            builder.Append(FormatSources(effective));
        }

        var diagnostics = FormatDiagnostics(effective);
        if (diagnostics.Length > 0)
        {
            builder.AppendLine();
            builder.Append(diagnostics);
        }

        return builder.ToString();
    }

    /// <summary>The layers consulted, lowest precedence first.</summary>
    public static string FormatSources(EffectiveConfiguration effective)
    {
        ArgumentNullException.ThrowIfNull(effective);

        var builder = new StringBuilder();
        builder.AppendLine("Sources (lowest precedence first)");
        builder.AppendLine("---------------------------------");
        var index = 1;
        foreach (var source in effective.Sources)
        {
            var location = source.FilePath ?? "(built in)";
            builder.AppendLine($"  {index}. {source.KindLabel,-17} {location}");
            index++;
        }
        builder.AppendLine();
        builder.AppendLine(
            "  Precedence: packaged defaults < user < project < environment < CLI arguments.");
        return builder.ToString();
    }

    /// <summary>Errors then warnings, one per line, with secrets scrubbed.</summary>
    public static string FormatDiagnostics(EffectiveConfiguration effective)
    {
        ArgumentNullException.ThrowIfNull(effective);

        if (effective.Diagnostics.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Diagnostics");
        builder.AppendLine("-----------");
        foreach (var diagnostic in effective.Diagnostics.OrderByDescending(d => d.IsError))
        {
            builder.AppendLine($"  {ConfigRedactor.Scrub(diagnostic.ToString(), effective.SecretValues)}");
        }
        return builder.ToString();
    }

    /// <summary>Machine-readable form: values, provenance and diagnostics, all redacted.</summary>
    public static string FormatJson(EffectiveConfiguration effective, bool includeSources)
    {
        ArgumentNullException.ThrowIfNull(effective);

        var values = new JsonObject();
        foreach (var (path, value) in ConfigMerge.Leaves(effective.Merged))
        {
            var entry = new JsonObject
            {
                ["value"] = ConfigRedactor.Redact(path, Render(value), effective.SecretValues),
            };
            if (includeSources)
            {
                var origin = effective.OriginOf(path);
                entry["source"] = origin?.Source.KindLabel;
                entry["file"] = origin?.Source.FilePath;
                entry["line"] = origin?.Line ?? 0;
                entry["column"] = origin?.Column ?? 0;
            }
            values[path] = entry;
        }

        var document = new JsonObject
        {
            ["schemaVersion"] = ConfigSchema.Version,
            ["workspace"] = effective.WorkspaceDirectory,
            ["values"] = values,
            ["resolvedPaths"] = new JsonObject(
                effective.ResolvedPaths.Select(p =>
                    new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create(p.Value)))),
            ["sources"] = new JsonArray(effective.Sources
                .Select(s => (JsonNode)new JsonObject
                {
                    ["kind"] = s.KindLabel,
                    ["file"] = s.FilePath,
                })
                .ToArray()),
            ["diagnostics"] = new JsonArray(effective.Diagnostics
                .Select(d => (JsonNode)new JsonObject
                {
                    ["severity"] = d.IsError ? "error" : "warning",
                    ["code"] = d.Code,
                    ["source"] = d.Source.Display,
                    ["line"] = d.Line,
                    ["column"] = d.Column,
                    ["keyPath"] = d.KeyPath,
                    ["message"] = ConfigRedactor.Scrub(d.Message, effective.SecretValues),
                })
                .ToArray()),
        };

        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Render(JsonNode? node) => node switch
    {
        null => "null",
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => node.ToJsonString(),
    };
}
