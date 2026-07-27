using System;
using System.Text.Json.Nodes;
using Andy.Cli.Configuration;

namespace Andy.Cli.HeadlessConfig;

/// <summary>
/// Translates the parts of a validated <see cref="HeadlessRunConfig"/> that overlap
/// with the layered andy.jsonc schema into a configuration layer.
///
/// A headless run happens in a workspace folder that is carried across several
/// agentic sessions, so project-scope settings in that folder have to apply to it
/// (rivoli-ai/andy-cli#280). The run config file still wins over them: it is the
/// caller's explicit, per-run instruction, so this layer sits above user and
/// project.
///
/// Two rules keep the strict headless contract intact:
///
/// 1. This is a PROJECTION, never a source of truth. The runtime still reads
///    <c>limits</c>, <c>permissions.allowed_tools</c>, <c>tools</c>,
///    <c>workspace</c>, <c>output</c> and the rest straight from
///    <see cref="HeadlessRunConfig"/>. Nothing here can weaken schema validation,
///    the fail-closed permission gate, or the exit-code contract.
/// 2. <c>permissions.mode</c> is pinned to <c>"ask"</c>. Headless permissions are
///    governed exclusively by the run config's <c>allowed_tools</c> against a
///    fail-closed default; pinning the mode here means a checked-in project
///    andy.jsonc setting <c>"mode": "auto"</c> is overridden rather than obeyed,
///    and <c>config show</c> tells the truth about which one won.
/// </summary>
public static class HeadlessConfigLayer
{
    /// <summary>
    /// Builds the layer for <paramref name="config"/>. <paramref name="configPath"/>
    /// is recorded as the source so provenance and diagnostics name the actual run
    /// config file; it also becomes the base directory for any relative path this
    /// layer contributes.
    /// </summary>
    public static ConfigLayer Build(HeadlessRunConfig config, string configPath)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        var root = new JsonObject();

        var providerId = config.Model.Provider;
        if (!string.IsNullOrWhiteSpace(providerId))
        {
            var entry = new JsonObject
            {
                ["provider"] = providerId,
                ["enabled"] = true,
            };
            if (!string.IsNullOrWhiteSpace(config.Model.Id))
            {
                entry["model"] = config.Model.Id;
            }

            // model.api_key_ref is validated at load time to be exactly "env:NAME",
            // which is the same thing {env:NAME} means here. Expressing it this way
            // puts the resolved value into the redaction set, so it cannot be
            // printed by config show even though the run config named it.
            if (!string.IsNullOrWhiteSpace(config.Model.ApiKeyRef)
                && config.Model.ApiKeyRef.StartsWith("env:", StringComparison.Ordinal))
            {
                entry["apiKey"] = $"{{env:{config.Model.ApiKeyRef["env:".Length..]}}}";
            }

            var llm = new JsonObject
            {
                ["defaultProvider"] = providerId,
                ["providers"] = new JsonObject { [providerId] = entry },
            };
            if (!string.IsNullOrWhiteSpace(config.Model.Id))
            {
                llm["defaultModel"] = config.Model.Id;
            }
            root["llm"] = llm;
        }

        // The headless loop enforces limits.max_iterations. Projecting it here keeps
        // `config show` honest about the cap that is actually in force, and stops a
        // project andy.jsonc from appearing to change it.
        if (config.Limits.MaxIterations > 0)
        {
            root["session"] = new JsonObject { ["maxTurns"] = config.Limits.MaxIterations };
        }

        // Pinned, see the class remarks. Never derived from the run config.
        root["permissions"] = new JsonObject { ["mode"] = "ask" };

        return new ConfigLayer
        {
            Source = ConfigSource.File(ConfigSourceKind.HeadlessConfig, configPath),
            Root = root,
        };
    }
}
