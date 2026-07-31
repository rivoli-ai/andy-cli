using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Andy.Cli.Configuration;

/// <summary>
/// Validates one layer against the versioned schema.
///
/// Two passes, because they answer different questions and only one of them can be
/// answered well by a JSON Schema engine:
///
/// 1. Unknown keys are found by walking the schema tree ourselves. A generic
///    <c>additionalProperties</c> failure reports the PARENT object, which would
///    make the diagnostic point at the wrong line and lose the offending key name.
///    Walking gives us the exact key, its own line and column, and lets us suggest
///    the closest legal key.
/// 2. Types, enums and ranges are checked by JsonSchema.Net, whose instance
///    locations we translate back into the dotted key paths used everywhere else.
/// </summary>
public static class ConfigSchemaValidator
{
    /// <summary>
    /// Keywords whose failure message is only a roll-up of a child's. Reported on
    /// the parent, they would triple every diagnostic and point at the wrong line.
    /// </summary>
    private static readonly HashSet<string> AggregateKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "properties",
        "additionalProperties",
        "patternProperties",
        "items",
        "prefixItems",
        "contains",
        "allOf",
        "anyOf",
        "oneOf",
        "if",
        "then",
        "else",
        "not",
        "dependentSchemas",
        "propertyNames",
        "false",
    };

    public static void Validate(ConfigLayer layer, ICollection<ConfigDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var unknownKeys = new HashSet<string>(StringComparer.Ordinal);
        CheckUnknownKeys(ConfigSchema.Node, layer.Root, string.Empty, layer, diagnostics, unknownKeys);
        CheckValues(layer, diagnostics, unknownKeys);
    }

    private static void CheckUnknownKeys(
        JsonObject schema,
        JsonNode? instance,
        string path,
        ConfigLayer layer,
        ICollection<ConfigDiagnostic> diagnostics,
        ISet<string> unknownKeys)
    {
        switch (instance)
        {
            case JsonObject obj:
                {
                    var properties = schema["properties"] as JsonObject;
                    var additional = schema["additionalProperties"];
                    var additionalSchema = additional as JsonObject;
                    var closed = additional is JsonValue flag
                        && flag.TryGetValue<bool>(out var allowed)
                        && !allowed;

                    foreach (var pair in obj.ToList())
                    {
                        var childPath = JsoncDocument.Join(path, pair.Key);
                        if (properties?[pair.Key] is JsonObject declared)
                        {
                            CheckUnknownKeys(declared, pair.Value, childPath, layer, diagnostics, unknownKeys);
                        }
                        else if (additionalSchema is not null)
                        {
                            CheckUnknownKeys(additionalSchema, pair.Value, childPath, layer, diagnostics, unknownKeys);
                        }
                        else if (closed)
                        {
                            var known = properties?.Select(p => p.Key).ToList() ?? new List<string>();
                            unknownKeys.Add(childPath);
                            var (line, column) = layer.PositionOf(childPath);
                            diagnostics.Add(ConfigDiagnostic.Error(
                                ConfigDiagnosticCodes.UnknownKey,
                                layer.Source,
                                BuildUnknownKeyMessage(pair.Key, known),
                                childPath,
                                line,
                                column));
                        }
                    }
                    break;
                }

            case JsonArray array:
                {
                    if (schema["items"] is JsonObject itemSchema)
                    {
                        for (var index = 0; index < array.Count; index++)
                        {
                            CheckUnknownKeys(
                                itemSchema, array[index], $"{path}[{index}]", layer, diagnostics, unknownKeys);
                        }
                    }
                    break;
                }
        }
    }

    private static string BuildUnknownKeyMessage(string key, IReadOnlyList<string> known)
    {
        var suggestion = Closest(key, known);
        var message = $"unknown key '{key}'.";
        if (suggestion is not null)
        {
            message += $" Did you mean '{suggestion}'?";
        }
        else if (known.Count > 0)
        {
            message += $" Allowed keys here: {string.Join(", ", known.OrderBy(k => k, StringComparer.Ordinal))}.";
        }
        return message;
    }

    private static void CheckValues(
        ConfigLayer layer,
        ICollection<ConfigDiagnostic> diagnostics,
        IReadOnlySet<string> unknownKeys)
    {
        using var document = JsonDocument.Parse(layer.Root.ToJsonString());
        var results = ConfigSchema.Compiled.Evaluate(document.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        if (results.IsValid)
        {
            return;
        }

        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var detail in Flatten(results))
        {
            if (detail.IsValid || detail.Errors is null || detail.Errors.Count == 0)
            {
                continue;
            }

            var keyPath = PointerToKeyPath(detail.InstanceLocation.ToString());

            // A key the walk already rejected as unknown is also rejected by the
            // schema's own `additionalProperties: false`, which surfaces as an
            // opaque "All values fail against the false schema" on the CHILD. One
            // clear error per mistake, not two.
            if (IsUnderAnUnknownKey(keyPath, unknownKeys))
            {
                continue;
            }

            // Applicator keywords only roll their children up ("Some properties did
            // not match the required schema") and are attached to the PARENT object,
            // so reporting them would bury the real message under two useless ones
            // pointing at the wrong lines. The children carry the actual assertion.
            // additionalProperties is dropped for the same reason plus a better one:
            // the unknown-key walk above already reported it, with the offending key.
            var message = string.Join("; ", detail.Errors
                .Where(pair => !AggregateKeywords.Contains(pair.Key))
                .Select(pair => pair.Value));
            if (string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            if (!reported.Add($"{keyPath}|{message}"))
            {
                continue;
            }

            var (line, column) = layer.Document?.ValuePosition(keyPath) ?? layer.PositionOf(keyPath);
            diagnostics.Add(ConfigDiagnostic.Error(
                ConfigDiagnosticCodes.InvalidValue,
                layer.Source,
                $"invalid value: {message}",
                keyPath,
                line,
                column));
        }
    }

    private static bool IsUnderAnUnknownKey(string keyPath, IReadOnlySet<string> unknownKeys)
    {
        foreach (var unknown in unknownKeys)
        {
            if (keyPath == unknown
                || keyPath.StartsWith(unknown + ".", StringComparison.Ordinal)
                || keyPath.StartsWith(unknown + "[", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults results)
    {
        yield return results;
        if (results.Details is null)
        {
            yield break;
        }
        foreach (var child in results.Details)
        {
            foreach (var nested in Flatten(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>Converts an RFC 6901 pointer ("/llm/providers/x") into "llm.providers.x".</summary>
    internal static string PointerToKeyPath(string pointer)
    {
        if (string.IsNullOrEmpty(pointer) || pointer == "/")
        {
            return string.Empty;
        }

        var path = string.Empty;
        foreach (var raw in pointer.TrimStart('/').Split('/'))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            path = int.TryParse(segment, out _) && path.Length > 0
                ? $"{path}[{segment}]"
                : JsoncDocument.Join(path, segment);
        }
        return path;
    }

    /// <summary>Nearest known key within a small edit distance, or null when nothing is close.</summary>
    internal static string? Closest(string key, IReadOnlyList<string> candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in candidates)
        {
            var distance = EditDistance(key.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        var threshold = Math.Max(2, key.Length / 3);
        return bestDistance <= threshold ? best : null;
    }

    private static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
