using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace Andy.Cli.Lsp;

/// <summary>
/// Result of resolving language-server definitions from every available source.
/// </summary>
/// <param name="Servers">Valid, enabled-or-disabled definitions in stable id order.</param>
/// <param name="Errors">Human-readable configuration problems, shown by <c>/lsp status</c>.</param>
/// <param name="Sources">Every source consulted, in precedence order (lowest first).</param>
/// <param name="AllowOutsideWorkspace">
/// Explicit opt-in that lets a language server see files outside the active workspace. Off by
/// default: without it a mutation outside the workspace root is never forwarded to a server.
/// </param>
public sealed record LspConfigurationLoadResult(
    IReadOnlyList<LspServerDefinition> Servers,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Sources,
    bool AllowOutsideWorkspace = false)
{
    public static LspConfigurationLoadResult Empty { get; } = new(
        Array.Empty<LspServerDefinition>(),
        Array.Empty<string>(),
        Array.Empty<string>());
}

/// <summary>
/// Minimal, self-contained configuration source for language servers.
///
/// SEAM (rivoli-ai/andy-cli#280): #280 introduces a layered user/project configuration system
/// (builtin &lt; user &lt; project &lt; local &lt; ...). This loader intentionally implements only the two
/// layers that already exist elsewhere in the CLI - appsettings (<c>Lsp:Servers</c>) and the
/// project-local <c>.andy/lsp-servers.json</c>, mirroring <see cref="Mcp.McpConfigurationLoader"/>
/// - so this feature is usable on its own. When #280 lands, replace the body of
/// <see cref="Load"/> with a read of the layered store and keep
/// <see cref="LspConfigurationLoadResult"/> as the contract; nothing downstream of this type
/// knows where the definitions came from.
///
/// There are deliberately NO built-in server definitions: Andy must never launch or download a
/// toolchain the user did not ask for. See docs/lsp-diagnostics.md for ready-to-paste examples.
/// </summary>
public static class LspConfigurationLoader
{
    public const string ProjectFileName = "lsp-servers.json";

    public static LspConfigurationLoadResult Load(
        IConfiguration? applicationConfiguration,
        string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);

        var servers = new Dictionary<string, LspServerDefinition>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var sources = new List<string>();
        var allowOutside = false;

        LoadFromApplicationConfiguration(applicationConfiguration, servers, sources, ref allowOutside);

        var projectPath = Path.Combine(projectDirectory, ".andy", ProjectFileName);
        if (File.Exists(projectPath))
        {
            sources.Add(projectPath);
            LoadFromProjectFile(projectPath, servers, errors, ref allowOutside);
        }

        var valid = new List<LspServerDefinition>();
        foreach (var server in servers.Values.OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (Validate(server, errors))
            {
                valid.Add(server);
            }
        }

        return new LspConfigurationLoadResult(valid, errors, sources, allowOutside);
    }

    private static void LoadFromProjectFile(
        string projectPath,
        IDictionary<string, LspServerDefinition> servers,
        ICollection<string> errors,
        ref bool allowOutside)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(
                File.ReadAllText(projectPath),
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
        }
        catch (JsonException ex)
        {
            errors.Add($"{projectPath}: invalid JSON ({ex.Message}).");
            return;
        }
        catch (IOException ex)
        {
            errors.Add($"{projectPath}: could not be read ({ex.Message}).");
            return;
        }
        catch (UnauthorizedAccessException)
        {
            errors.Add($"{projectPath}: access denied.");
            return;
        }

        if (root is not JsonObject obj)
        {
            errors.Add($"{projectPath}: expected a JSON object at the top level.");
            return;
        }

        if (obj.TryGetPropertyValue("allowOutsideWorkspace", out var allowNode) &&
            allowNode is JsonValue allowValue &&
            allowValue.TryGetValue<bool>(out var allowParsed))
        {
            allowOutside = allowParsed;
        }

        if (!obj.TryGetPropertyValue("servers", out var serversNode) || serversNode is not JsonObject serverObj)
        {
            errors.Add($"{projectPath}: expected a top-level 'servers' object.");
            return;
        }

        foreach (var (name, node) in serverObj)
        {
            if (node is not JsonObject entry)
            {
                errors.Add($"{projectPath}: server '{name}' must be an object.");
                continue;
            }

            servers[name] = FromJson(name, entry, servers.TryGetValue(name, out var previous) ? previous : null);
        }
    }

    private static LspServerDefinition FromJson(string id, JsonObject entry, LspServerDefinition? existing)
    {
        return new LspServerDefinition
        {
            Id = id,
            Command = ReadString(entry, "command") ?? existing?.Command ?? string.Empty,
            Args = ReadStringArray(entry, "args") ?? existing?.Args ?? Array.Empty<string>(),
            Environment = ReadStringMap(entry, "env") ?? existing?.Environment
                ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Extensions = ReadStringArray(entry, "extensions") ?? existing?.Extensions ?? Array.Empty<string>(),
            RootMarkers = ReadStringArray(entry, "rootMarkers") ?? existing?.RootMarkers ?? Array.Empty<string>(),
            LanguageId = ReadString(entry, "languageId") ?? existing?.LanguageId,
            InitializationOptionsJson = entry.TryGetPropertyValue("initializationOptions", out var init) && init is not null
                ? init.ToJsonString()
                : existing?.InitializationOptionsJson,
            Enabled = ReadBool(entry, "enabled") ?? existing?.Enabled ?? true,
            StartTimeoutMs = ReadInt(entry, "startTimeoutMs") ?? existing?.StartTimeoutMs ?? LspLimits.DefaultStartTimeoutMs,
            DiagnosticsTimeoutMs = ReadInt(entry, "diagnosticsTimeoutMs")
                ?? existing?.DiagnosticsTimeoutMs ?? LspLimits.DefaultDiagnosticsTimeoutMs,
        };
    }

    private static void LoadFromApplicationConfiguration(
        IConfiguration? configuration,
        IDictionary<string, LspServerDefinition> servers,
        ICollection<string> sources,
        ref bool allowOutside)
    {
        if (configuration is null) return;

        var section = configuration.GetSection("Lsp");
        if (!section.Exists()) return;

        if (bool.TryParse(section["AllowOutsideWorkspace"], out var parsedAllow))
        {
            allowOutside = parsedAllow;
        }

        var serverSection = section.GetSection("Servers");
        var children = serverSection.GetChildren().ToList();
        if (children.Count == 0) return;

        sources.Add("appsettings.json (Lsp:Servers)");
        foreach (var child in children)
        {
            servers[child.Key] = new LspServerDefinition
            {
                Id = child.Key,
                Command = child["Command"] ?? string.Empty,
                Args = child.GetSection("Args").GetChildren().Select(v => v.Value ?? string.Empty).ToList(),
                Environment = child.GetSection("Env").GetChildren()
                    .ToDictionary(v => v.Key, v => v.Value ?? string.Empty, StringComparer.Ordinal),
                Extensions = child.GetSection("Extensions").GetChildren().Select(v => v.Value ?? string.Empty).ToList(),
                RootMarkers = child.GetSection("RootMarkers").GetChildren().Select(v => v.Value ?? string.Empty).ToList(),
                LanguageId = child["LanguageId"],
                Enabled = !bool.TryParse(child["Enabled"], out var enabled) || enabled,
                StartTimeoutMs = int.TryParse(child["StartTimeoutMs"], out var start)
                    ? start
                    : LspLimits.DefaultStartTimeoutMs,
                DiagnosticsTimeoutMs = int.TryParse(child["DiagnosticsTimeoutMs"], out var diag)
                    ? diag
                    : LspLimits.DefaultDiagnosticsTimeoutMs,
            };
        }
    }

    private static bool Validate(LspServerDefinition server, ICollection<string> errors)
    {
        var valid = true;
        if (string.IsNullOrWhiteSpace(server.Id))
        {
            errors.Add("Language server ids must not be blank.");
            valid = false;
        }

        if (string.IsNullOrWhiteSpace(server.Command))
        {
            errors.Add($"Language server '{server.Id}' requires a 'command'.");
            valid = false;
        }

        if (server.Extensions.Count == 0)
        {
            errors.Add($"Language server '{server.Id}' requires at least one entry in 'extensions'.");
            valid = false;
        }

        if (server.StartTimeoutMs <= 0)
        {
            errors.Add($"Language server '{server.Id}' has a non-positive 'startTimeoutMs'.");
            valid = false;
        }

        if (server.DiagnosticsTimeoutMs <= 0)
        {
            errors.Add($"Language server '{server.Id}' has a non-positive 'diagnosticsTimeoutMs'.");
            valid = false;
        }

        return valid;
    }

    private static string? ReadString(JsonObject entry, string name) =>
        entry.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static bool? ReadBool(JsonObject entry, string name) =>
        entry.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<bool>(out var flag)
            ? flag
            : null;

    private static int? ReadInt(JsonObject entry, string name) =>
        entry.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<int>(out var number)
            ? number
            : null;

    private static IReadOnlyList<string>? ReadStringArray(JsonObject entry, string name)
    {
        if (!entry.TryGetPropertyValue(name, out var node) || node is not JsonArray array) return null;
        var result = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text))
            {
                result.Add(text);
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string>? ReadStringMap(JsonObject entry, string name)
    {
        if (!entry.TryGetPropertyValue(name, out var node) || node is not JsonObject map) return null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, item) in map)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text))
            {
                result[key] = text;
            }
        }
        return result;
    }
}
