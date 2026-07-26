using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Andy.Cli.Services;

namespace Andy.Cli.Configuration;

/// <summary>
/// Produces the non-file layers (packaged defaults, environment, command line) and
/// finds the file layers on disk.
///
/// The environment and command-line layers exist so that the EXISTING variable and
/// flag names keep working unchanged: they are translated into the same schema the
/// files use, and then merged by the same rules, instead of being special-cased
/// wherever they happen to be read. Nothing here invents a new name for something
/// that already had one.
/// </summary>
public static class ConfigLayerBuilder
{
    /// <summary>
    /// Layer 0. Built-in defaults derived from <see cref="ProviderRegistry"/> (the
    /// existing source of truth for the provider set) and then overlaid with the
    /// packaged appsettings.json, which keeps working exactly as before.
    /// </summary>
    public static ConfigLayer BuildPackagedDefaults(
        string? appSettingsPath,
        string workspaceDirectory,
        ICollection<ConfigDiagnostic> diagnostics)
    {
        var source = ConfigSource.Synthetic(ConfigSourceKind.PackagedDefaults, workspaceDirectory);
        var root = new JsonObject
        {
            ["version"] = ConfigSchema.Version,
        };

        var providers = new JsonObject();
        foreach (var descriptor in ProviderRegistry.All)
        {
            var entry = new JsonObject
            {
                ["provider"] = descriptor.Id,
                ["apiBase"] = descriptor.DefaultEndpoint,
                ["model"] = descriptor.DefaultModel,
                ["enabled"] = true,
            };
            if (descriptor.RequiresApiKey && descriptor.ApiKeyEnvVars.Count > 0)
            {
                entry["apiKey"] = $"{{env:{descriptor.PrimaryApiKeyEnvVar}}}";
            }
            providers[descriptor.Id] = entry;
        }
        root["llm"] = new JsonObject { ["providers"] = providers };
        root["ui"] = new JsonObject
        {
            ["theme"] = "dark",
            ["transparentBackground"] = false,
            ["diffStyle"] = "auto",
        };
        root["permissions"] = new JsonObject { ["mode"] = "ask" };
        root["logging"] = new JsonObject { ["level"] = "warning", ["console"] = false };

        OverlayAppSettings(root, appSettingsPath, source, diagnostics);

        return new ConfigLayer { Source = source, Root = root };
    }

    /// <summary>
    /// Folds the packaged appsettings.json <c>Llm</c> and <c>Mcp</c> sections into the
    /// defaults layer. Keys are matched case-insensitively against the schema so the
    /// existing PascalCase file needs no edit, and empty strings are dropped because
    /// appsettings uses <c>""</c> where the schema means "not set".
    /// </summary>
    private static void OverlayAppSettings(
        JsonObject root,
        string? appSettingsPath,
        ConfigSource source,
        ICollection<ConfigDiagnostic> diagnostics)
    {
        if (string.IsNullOrEmpty(appSettingsPath) || !File.Exists(appSettingsPath))
        {
            return;
        }

        JsonObject? parsed;
        try
        {
            parsed = JsoncDocument.Parse(File.ReadAllText(appSettingsPath)).Root;
        }
        catch (JsoncParseException ex)
        {
            diagnostics.Add(ConfigDiagnostic.Warning(
                ConfigDiagnosticCodes.InvalidJson,
                source,
                $"the packaged {Path.GetFileName(appSettingsPath)} could not be parsed and was ignored: {ex.Message}"));
            return;
        }
        catch (IOException ex)
        {
            diagnostics.Add(ConfigDiagnostic.Warning(
                ConfigDiagnosticCodes.UnreadableFile,
                source,
                $"the packaged {Path.GetFileName(appSettingsPath)} could not be read and was ignored: {ex.Message}"));
            return;
        }

        var translated = new JsonObject();
        foreach (var section in new[] { "llm", "mcp" })
        {
            var value = FindCaseInsensitive(parsed, section);
            if (value is JsonObject sectionObject)
            {
                translated[section] = sectionObject.DeepClone();
            }
        }

        if (translated.Count == 0)
        {
            return;
        }

        SchemaKeyNormalizer.Normalize(translated, ConfigSchema.Node);
        DropEmptyStrings(translated);
        DeepOverlay(root, translated);
    }

    private static JsonNode? FindCaseInsensitive(JsonObject obj, string name) =>
        obj.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static void DeepOverlay(JsonObject target, JsonObject source)
    {
        foreach (var pair in source.ToList())
        {
            if (pair.Value is JsonObject sourceObject && target[pair.Key] is JsonObject targetObject)
            {
                DeepOverlay(targetObject, sourceObject);
            }
            else
            {
                target[pair.Key] = pair.Value?.DeepClone();
            }
        }
    }

    private static void DropEmptyStrings(JsonObject obj)
    {
        foreach (var pair in obj.ToList())
        {
            switch (pair.Value)
            {
                case JsonObject child:
                    DropEmptyStrings(child);
                    break;
                case JsonValue value when value.TryGetValue<string>(out var text) && text.Length == 0:
                    obj.Remove(pair.Key);
                    break;
            }
        }
    }

    /// <summary>
    /// Layer 3. Translates the environment variables the CLI already honours into
    /// schema shape. Values landing on credential fields are added to
    /// <paramref name="secretValues"/> so they are redacted like substituted ones.
    /// </summary>
    public static ConfigLayer BuildEnvironment(
        Func<string, string?> lookup,
        string workspaceDirectory,
        ISet<string> secretValues)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        var source = ConfigSource.Synthetic(ConfigSourceKind.Environment, workspaceDirectory);
        var root = new JsonObject();

        var theme = lookup("ANDY_THEME");
        if (!string.IsNullOrWhiteSpace(theme))
        {
            Ui(root)["theme"] = theme;
        }

        var diffStyle = NormalizeDiffStyle(lookup("ANDY_DIFF_STYLE"));
        if (diffStyle is not null)
        {
            Ui(root)["diffStyle"] = diffStyle;
        }

        var maxTurns = lookup("ANDY_MAX_TURNS");
        if (int.TryParse(maxTurns, NumberStyles.Integer, CultureInfo.InvariantCulture, out var turns) && turns > 0)
        {
            Session(root)["maxTurns"] = turns;
        }

        if (!string.IsNullOrEmpty(lookup("ANDY_AUTO_APPROVE")))
        {
            root["permissions"] = new JsonObject { ["mode"] = "auto" };
        }

        if (string.Equals(lookup("ANDY_DEBUG"), "true", StringComparison.OrdinalIgnoreCase))
        {
            root["logging"] = new JsonObject { ["console"] = true, ["level"] = "information" };
        }

        var providers = new JsonObject();
        foreach (var descriptor in ProviderRegistry.All)
        {
            var entry = new JsonObject();

            foreach (var variable in descriptor.ApiKeyEnvVars)
            {
                var key = lookup(variable);
                if (!string.IsNullOrEmpty(key))
                {
                    entry["apiKey"] = key;
                    secretValues.Add(key);
                    break;
                }
            }

            if (!string.IsNullOrEmpty(descriptor.ApiBaseEnvVar))
            {
                var apiBase = lookup(descriptor.ApiBaseEnvVar);
                if (!string.IsNullOrWhiteSpace(apiBase))
                {
                    entry["apiBase"] = apiBase;
                }
            }

            // Mirrors ModelCommand.GetDefaultModel, which has always let
            // <PROVIDER>_MODEL win over the configured default.
            var model = lookup($"{descriptor.Id.ToUpperInvariant()}_MODEL");
            if (!string.IsNullOrWhiteSpace(model))
            {
                entry["model"] = model;
            }

            if (entry.Count > 0)
            {
                providers[descriptor.Id] = entry;
            }
        }

        if (providers.Count > 0)
        {
            Llm(root)["providers"] = providers;
        }

        return new ConfigLayer { Source = source, Root = root };
    }

    /// <summary>
    /// Layer 4, the highest. Recognises the flags the CLI already accepts plus the
    /// direct equivalents of the settings this layer can express. Unknown arguments
    /// are ignored here; the mode selector and each sub-command still own their own
    /// argument handling.
    /// </summary>
    public static ConfigLayer BuildCommandLine(string[] args, string workspaceDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);

        var source = ConfigSource.Synthetic(ConfigSourceKind.CommandLine, workspaceDirectory);
        var root = new JsonObject();

        for (var i = 0; i < args.Length; i++)
        {
            var (name, inlineValue) = SplitArgument(args[i]);
            switch (name)
            {
                case "--theme":
                    if (TryValue(args, ref i, inlineValue, out var themeValue))
                    {
                        Ui(root)["theme"] = themeValue;
                    }
                    break;

                case "--diff-style":
                    if (TryValue(args, ref i, inlineValue, out var diffValue)
                        && NormalizeDiffStyle(diffValue) is { } normalizedDiff)
                    {
                        Ui(root)["diffStyle"] = normalizedDiff;
                    }
                    break;

                case "--provider":
                    if (TryValue(args, ref i, inlineValue, out var providerValue))
                    {
                        Llm(root)["defaultProvider"] = providerValue;
                    }
                    break;

                case "--model":
                    if (TryValue(args, ref i, inlineValue, out var modelValue))
                    {
                        Llm(root)["defaultModel"] = modelValue;
                    }
                    break;

                case "--max-turns":
                    if (TryValue(args, ref i, inlineValue, out var turnsValue)
                        && int.TryParse(turnsValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var turns)
                        && turns > 0)
                    {
                        Session(root)["maxTurns"] = turns;
                    }
                    break;

                case "--auto":
                case "--yolo":
                    root["permissions"] = new JsonObject { ["mode"] = "auto" };
                    break;

                case "--debug":
                    root["logging"] = new JsonObject { ["console"] = true, ["level"] = "debug" };
                    break;

                case "--verbose":
                    Logging(root)["level"] = "debug";
                    break;

                case "--quiet":
                    Logging(root)["level"] = "none";
                    break;
            }
        }

        return new ConfigLayer { Source = source, Root = root };
    }

    /// <summary>
    /// The andy.jsonc files to look for, lowest precedence first. Project scope has
    /// two locations and <c>.andy/andy.jsonc</c> is read last so the dedicated folder
    /// wins over a repository-root file. Duplicates are dropped with the platform's
    /// own path comparison, so a workspace that happens to BE the user config folder
    /// still produces one deterministic ordering everywhere.
    /// </summary>
    public static IReadOnlyList<(ConfigSourceKind Kind, string Path)> DiscoverFiles(
        string userHomeDirectory,
        string workspaceDirectory)
    {
        var candidates = new List<(ConfigSourceKind, string)>
        {
            (ConfigSourceKind.User, Path.Combine(userHomeDirectory, ".andy", ConfigSchema.FileName)),
            (ConfigSourceKind.Project, Path.Combine(workspaceDirectory, ConfigSchema.FileName)),
            (ConfigSourceKind.Project, Path.Combine(workspaceDirectory, ".andy", ConfigSchema.FileName)),
        };

        var comparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var seen = new HashSet<string>(comparer);
        var result = new List<(ConfigSourceKind, string)>();
        foreach (var (kind, path) in candidates)
        {
            var full = Path.GetFullPath(path);
            if (seen.Add(full))
            {
                result.Add((kind, full));
            }
        }
        return result;
    }

    private static JsonObject Ui(JsonObject root) => Section(root, "ui");

    private static JsonObject Llm(JsonObject root) => Section(root, "llm");

    private static JsonObject Session(JsonObject root) => Section(root, "session");

    private static JsonObject Logging(JsonObject root) => Section(root, "logging");

    private static JsonObject Section(JsonObject root, string name)
    {
        if (root[name] is JsonObject existing)
        {
            return existing;
        }
        var created = new JsonObject();
        root[name] = created;
        return created;
    }

    private static (string Name, string? Value) SplitArgument(string argument)
    {
        var separator = argument.IndexOf('=', StringComparison.Ordinal);
        return separator < 0
            ? (argument, null)
            : (argument[..separator], argument[(separator + 1)..]);
    }

    private static bool TryValue(string[] args, ref int index, string? inlineValue, out string value)
    {
        if (inlineValue is not null)
        {
            value = inlineValue;
            return value.Length > 0;
        }
        if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            index++;
            value = args[index];
            return value.Length > 0;
        }
        value = string.Empty;
        return false;
    }

    /// <summary>Maps the historical ANDY_DIFF_STYLE spellings onto the schema enum.</summary>
    private static string? NormalizeDiffStyle(string? raw) =>
        (raw ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "unified" or "stacked" => "unified",
            "split" or "side-by-side" => "split",
            "auto" => "auto",
            _ => null,
        };
}
