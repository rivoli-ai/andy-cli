using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Andy.Cli.Configuration;

/// <summary>
/// Resolves <c>{env:NAME}</c> references inside string values.
///
/// Two spellings are accepted so the existing files migrate without an edit:
/// <c>{env:NAME}</c> (the documented form, per rivoli-ai/andy-cli#280) and
/// <c>${NAME}</c> (the form already used by appsettings.json provider keys and
/// .andy/mcp-servers.json).
///
/// Every resolved value is added to a redaction set. Substitution exists so that
/// secrets stay in the environment; printing one back out in <c>config show</c>
/// would defeat the whole point, so the loader treats ALL substituted values as
/// secret regardless of the variable's name.
/// </summary>
public static partial class ConfigSubstitution
{
    [GeneratedRegex(@"\{env:([A-Za-z_][A-Za-z0-9_]*)\}|\$\{([A-Za-z_][A-Za-z0-9_]*)\}",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();

    /// <summary>
    /// Rewrites every string in <paramref name="layer"/> in place. Unset variables
    /// produce a diagnostic naming the source, line, column, key path and variable;
    /// the placeholder is replaced with an empty string so later stages still see a
    /// well-typed document.
    /// </summary>
    public static void Apply(
        ConfigLayer layer,
        Func<string, string?> environmentLookup,
        ICollection<ConfigDiagnostic> diagnostics,
        ISet<string> secretValues)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(environmentLookup);

        Walk(layer.Root, string.Empty, layer, environmentLookup, diagnostics, secretValues);
    }

    private static void Walk(
        JsonNode? node,
        string path,
        ConfigLayer layer,
        Func<string, string?> lookup,
        ICollection<ConfigDiagnostic> diagnostics,
        ISet<string> secretValues)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in new List<string>(obj.Select(pair => pair.Key)))
                {
                    var childPath = JsoncDocument.Join(path, key);
                    var child = obj[key];
                    if (child is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        obj[key] = Substitute(text, childPath, layer, lookup, diagnostics, secretValues);
                    }
                    else
                    {
                        Walk(child, childPath, layer, lookup, diagnostics, secretValues);
                    }
                }
                break;

            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    var childPath = $"{path}[{index}]";
                    if (array[index] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        array[index] = Substitute(text, childPath, layer, lookup, diagnostics, secretValues);
                    }
                    else
                    {
                        Walk(array[index], childPath, layer, lookup, diagnostics, secretValues);
                    }
                }
                break;
        }
    }

    private static JsonNode Substitute(
        string text,
        string keyPath,
        ConfigLayer layer,
        Func<string, string?> lookup,
        ICollection<ConfigDiagnostic> diagnostics,
        ISet<string> secretValues)
    {
        if (text.Length == 0 || (!text.Contains("{env:", StringComparison.Ordinal) && !text.Contains("${", StringComparison.Ordinal)))
        {
            return JsonValue.Create(text)!;
        }

        var replaced = ReferencePattern().Replace(text, match =>
        {
            var name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            var resolved = lookup(name);
            if (string.IsNullOrEmpty(resolved))
            {
                var (line, column) = layer.ValuePositionOf(keyPath);
                var fatal = layer.SubstitutionFailureIsFatal;
                diagnostics.Add(new ConfigDiagnostic
                {
                    Severity = fatal ? ConfigSeverity.Error : ConfigSeverity.Warning,
                    Code = ConfigDiagnosticCodes.MissingSubstitution,
                    Source = layer.Source,
                    Message = fatal
                        ? $"references environment variable '{name}', which is not set. "
                            + "Export it, or remove the reference."
                        : $"environment variable '{name}' is not set, so this value is empty.",
                    KeyPath = keyPath,
                    Line = line,
                    Column = column,
                });
                return string.Empty;
            }

            secretValues.Add(resolved);
            return resolved;
        });

        return JsonValue.Create(replaced)!;
    }
}
