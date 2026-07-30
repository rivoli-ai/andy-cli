using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Andy.Cli.Services.Formatting;

/// <summary>
/// Loads formatter definitions from the user and project configuration files and merges them with
/// the locally detected well-known formatters.
///
/// INTEGRATION SEAM (issue #280 - unified configuration): this is a deliberately minimal, local
/// configuration source so the formatter feature is self-contained on its own branch. When #280
/// lands, replace <see cref="LoadLayer"/> with a read of the unified config object; everything
/// downstream consumes <see cref="FormatterDefinition"/> and does not care where it came from. The
/// merge semantics implemented here (project over user over detected, keyed by name) are the
/// contract to preserve.
///
/// File format (both layers use the same shape):
/// <code>
/// {
///   "formatters": {
///     "csharpier": {
///       "command": "csharpier",
///       "arguments": ["format", "$FILE"],
///       "extensions": [".cs"],
///       "workingDirectory": null,
///       "timeoutSeconds": 60,
///       "enabled": true,
///       "order": 10
///     }
///   }
/// }
/// </code>
/// </summary>
public static class FormatterConfigLoader
{
    /// <summary>File name used for both the user- and project-level formatter config.</summary>
    public const string FileName = "formatters.json";

    /// <summary>Project-level config path: <c>&lt;projectRoot&gt;/.andy/formatters.json</c>.</summary>
    public static string ProjectPath(string projectRoot) => Path.Combine(projectRoot, ".andy", FileName);

    /// <summary>User-level config path: <c>~/.andy/formatters.json</c>.</summary>
    public static string UserPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".andy", FileName);

    /// <summary>
    /// Well-known formatters that are used only when their command is already installed. They are
    /// the lowest precedence layer, so any user or project definition of the same name wins, and a
    /// project can disable one outright with <c>"enabled": false</c>.
    ///
    /// Every entry here is a formatter that rewrites the file in place when given a path. Nothing
    /// is installed on the user's behalf; an absent binary simply means the entry never runs.
    /// </summary>
    public static IReadOnlyList<FormatterDefinition> DetectedDefaults { get; } = new[]
    {
        new FormatterDefinition
        {
            Name = "dotnet-format",
            Command = "dotnet",
            Arguments = new[] { "format", "--include", FormatterDefinition.FilePlaceholder },
            Extensions = new[] { ".cs" },
            TimeoutSeconds = 120,
            Order = 20,
            Source = FormatterSource.Detected,
        },
        new FormatterDefinition
        {
            Name = "gofmt",
            Command = "gofmt",
            Arguments = new[] { "-w", FormatterDefinition.FilePlaceholder },
            Extensions = new[] { ".go" },
            Order = 20,
            Source = FormatterSource.Detected,
        },
        new FormatterDefinition
        {
            Name = "rustfmt",
            Command = "rustfmt",
            Arguments = new[] { "--emit", "files", FormatterDefinition.FilePlaceholder },
            Extensions = new[] { ".rs" },
            Order = 20,
            Source = FormatterSource.Detected,
        },
        new FormatterDefinition
        {
            Name = "black",
            Command = "black",
            Arguments = new[] { "--quiet", FormatterDefinition.FilePlaceholder },
            Extensions = new[] { ".py" },
            Order = 20,
            Source = FormatterSource.Detected,
        },
        new FormatterDefinition
        {
            Name = "prettier",
            Command = "prettier",
            Arguments = new[] { "--write", FormatterDefinition.FilePlaceholder },
            Extensions = new[] { ".js", ".jsx", ".ts", ".tsx", ".json", ".css", ".scss", ".html", ".md", ".yaml", ".yml" },
            Order = 30,
            Source = FormatterSource.Detected,
        },
    };

    /// <summary>
    /// Load and merge every layer for a project. Precedence, lowest to highest: detected defaults,
    /// user config, project config. Merging is by <see cref="FormatterDefinition.Name"/>, and a
    /// higher layer replaces the definition wholesale (it is not a field-level merge, so a partial
    /// override cannot silently inherit a command the user never wrote).
    /// </summary>
    public static IReadOnlyList<FormatterDefinition> Load(string projectRoot, bool includeDetectedDefaults = true)
    {
        var layers = new List<IReadOnlyList<FormatterDefinition>>();
        if (includeDetectedDefaults)
        {
            layers.Add(DetectedDefaults);
        }

        layers.Add(LoadLayer(SafeUserPath(), FormatterSource.User));
        layers.Add(LoadLayer(ProjectPath(projectRoot), FormatterSource.Project));
        return Merge(layers);
    }

    /// <summary>Merge ordered layers (lowest precedence first) by definition name.</summary>
    public static IReadOnlyList<FormatterDefinition> Merge(IEnumerable<IReadOnlyList<FormatterDefinition>> layers)
    {
        var merged = new Dictionary<string, FormatterDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in layers)
        {
            foreach (var definition in layer)
            {
                if (string.IsNullOrWhiteSpace(definition.Name))
                {
                    continue;
                }

                merged[definition.Name] = definition;
            }
        }

        // Deterministic output order, independent of dictionary enumeration.
        return merged.Values
            .OrderBy(d => d.Order)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Read one config file. A missing file yields no definitions; a malformed one is reported via
    /// <paramref name="error"/> and likewise yields none, so a typo in a config file can never make
    /// Andy run something unintended.
    /// </summary>
    public static IReadOnlyList<FormatterDefinition> LoadLayer(string? path, FormatterSource source)
        => LoadLayer(path, source, out _);

    /// <inheritdoc cref="LoadLayer(string?, FormatterSource)"/>
    public static IReadOnlyList<FormatterDefinition> LoadLayer(string? path, FormatterSource source, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Array.Empty<FormatterDefinition>();
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Could not read {path}: {ex.Message}";
            return Array.Empty<FormatterDefinition>();
        }

        return Parse(json, source, out error);
    }

    /// <summary>Parse a formatter config document. Never throws; malformed input yields no definitions.</summary>
    public static IReadOnlyList<FormatterDefinition> Parse(string json, FormatterSource source, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<FormatterDefinition>();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            error = "Invalid JSON: " + ex.Message;
            return Array.Empty<FormatterDefinition>();
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Expected a JSON object at the document root.";
                return Array.Empty<FormatterDefinition>();
            }

            if (!root.TryGetProperty("formatters", out var formatters) || formatters.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<FormatterDefinition>();
            }

            var list = new List<FormatterDefinition>();
            foreach (var property in formatters.EnumerateObject())
            {
                var definition = ReadDefinition(property.Name, property.Value, source, out var entryError);
                if (definition is not null)
                {
                    list.Add(definition);
                }
                else if (entryError is not null)
                {
                    error = error is null ? entryError : error + "; " + entryError;
                }
            }

            return list
                .OrderBy(d => d.Order)
                .ThenBy(d => d.Name, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private static FormatterDefinition? ReadDefinition(
        string name, JsonElement element, FormatterSource source, out string? error)
    {
        error = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            error = $"Formatter '{name}': expected an object.";
            return null;
        }

        var command = ReadString(element, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            error = $"Formatter '{name}': 'command' is required.";
            return null;
        }

        return new FormatterDefinition
        {
            Name = name,
            Command = command!,
            Arguments = ReadStringArray(element, "arguments"),
            Extensions = ReadStringArray(element, "extensions"),
            WorkingDirectory = ReadString(element, "workingDirectory"),
            TimeoutSeconds = ReadInt(element, "timeoutSeconds", 30),
            Enabled = ReadBool(element, "enabled", true),
            Order = ReadInt(element, "order", 100),
            Source = source,
        };
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return Array.Empty<string>();
        }

        // A bare string is accepted as a one-element list; hand-written config routinely does this.
        if (value.ValueKind == JsonValueKind.String)
        {
            var single = value.GetString();
            return single is null ? Array.Empty<string>() : new[] { single };
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (text is not null)
                {
                    list.Add(text);
                }
            }
        }

        return list;
    }

    private static int ReadInt(JsonElement element, string name, int fallback)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
            ? parsed
            : fallback;

    private static bool ReadBool(JsonElement element, string name, bool fallback)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    private static string? SafeUserPath()
    {
        try
        {
            return UserPath();
        }
        catch (Exception)
        {
            // No resolvable user profile (some sandboxes): the user layer is simply absent.
            return null;
        }
    }
}
