namespace Andy.Cli.HeadlessConfig;

// rivoli-ai/andy-cli#180: semantic (cross-field and runtime-support) validation
// that the JSON Schema cannot express on its own, or that must fail fast with a
// clear operator-facing message. Runs in HeadlessConfigLoader AFTER schema
// validation and deserialization; any non-null return maps to
// HeadlessExitCode.ConfigError.
//
// The guiding principle for the whole issue: no config field is a silent no-op.
// A field is either applied/verified by the runtime, or the config that carries
// it is rejected here. Nothing carried in the schema is quietly ignored.
public static class HeadlessConfigValidator
{
    // A config's env_vars must never shadow these. Two families, both rejected
    // fail-closed:
    //
    //   Container-runtime identity (Epic Y5) - set by the runtime; shadowing lets
    //   an agent redirect its own egress proxy, spoof its run token, or repoint
    //   the MCP gateway:
    //     ANDY_PROXY_URL, ANDY_TOKEN, ANDY_MCP_URL
    //
    //   Permission-engine controls - read by the Andy.Permissions bootstrap when
    //   the engine is built. env_vars are applied to the process environment
    //   BEFORE the permission engine is constructed, so a config that set any of
    //   these would weaken or disable the fail-closed permission gate from inside
    //   the very config the gate is meant to constrain:
    //     ANDY_PERMISSION_MODE  - 'bypass' turns every Ask into Allow.
    //     ANDY_PERMISSIONS_FILE - path to a rules file loaded as the
    //                             highest-precedence Allow/Ask/Deny layer.
    //     ANDY_PERMISSIONS_JSON - inline rules loaded as that same layer.
    public static readonly IReadOnlyList<string> ReservedEnvVars =
        new[]
        {
            "ANDY_PROXY_URL",
            "ANDY_TOKEN",
            "ANDY_MCP_URL",
            "ANDY_PERMISSION_MODE",
            "ANDY_PERMISSIONS_FILE",
            "ANDY_PERMISSIONS_JSON",
        };

    // Returns null when the config is semantically valid, otherwise a clear,
    // secret-free error message. Never embeds a resolved secret value: only
    // field names, env-var NAMES, and schemes appear in messages.
    public static string? Validate(HeadlessRunConfig config)
    {
        var apiKeyRef = config.Model.ApiKeyRef;
        if (!string.IsNullOrEmpty(apiKeyRef)
            && !TryParseEnvRef(apiKeyRef, out _, out var apiErr))
        {
            return $"model.api_key_ref is invalid: {apiErr}";
        }

        if (config.EnvVars is { Count: > 0 })
        {
            foreach (var key in config.EnvVars.Keys)
            {
                if (ReservedEnvVars.Contains(key, StringComparer.Ordinal))
                {
                    return $"env_vars must not set reserved variable '{key}'; it is a "
                        + "container-runtime identity or permission-engine control and cannot be "
                        + "overridden by a run config.";
                }
            }
        }

        if (string.Equals(config.Output.Stream, "fifo", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(config.EventSink?.Path))
        {
            return "output.stream is 'fifo' but event_sink.path is not set. FIFO event "
                + "streaming requires an absolute FIFO path in event_sink.path.";
        }

        for (var index = 0; index < config.RequiredActions.Count; index++)
        {
            var requirement = config.RequiredActions[index];
            if (requirement.CommandEquals is null)
            {
                continue;
            }

            if (!string.Equals(requirement.ToolName, "execute_command", StringComparison.Ordinal))
            {
                return $"required_actions[{index}].command_equals is only valid when "
                    + "tool_name is 'execute_command'.";
            }

            if (!RequiredCommandMatcher.TryNormalize(
                requirement.CommandEquals,
                out _,
                out var commandError))
            {
                return $"required_actions[{index}].command_equals is invalid: {commandError}";
            }
        }

        return null;
    }

    // Parses an 'env:NAME' API-key reference. 'env:' is the only scheme
    // implemented in v1; 'secret-store:' is reserved and bare values are never
    // accepted. Never returns or logs the resolved secret value - only NAME.
    public static bool TryParseEnvRef(string reference, out string envVarName, out string? error)
    {
        envVarName = string.Empty;
        error = null;

        if (reference.StartsWith("secret-store:", StringComparison.Ordinal))
        {
            error = "the 'secret-store:' scheme is reserved for a future release and is not "
                + "implemented in v1; use 'env:NAME'.";
            return false;
        }

        const string prefix = "env:";
        if (!reference.StartsWith(prefix, StringComparison.Ordinal))
        {
            error = "only the 'env:NAME' form is supported (read the API key from environment "
                + "variable NAME); a bare key value is never accepted.";
            return false;
        }

        var name = reference.Substring(prefix.Length);
        if (name.Length == 0 || !IsValidEnvName(name))
        {
            error = "the env var name after 'env:' is empty or contains invalid characters.";
            return false;
        }

        envVarName = name;
        return true;
    }

    private static bool IsValidEnvName(string name)
    {
        if (!(char.IsLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }
        foreach (var c in name)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }
        return true;
    }
}

public static class RequiredCommandMatcher
{
    private static readonly char[] s_patternCharacters = ['*', '?', '[', ']'];

    public static bool TryNormalize(string command, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(command))
        {
            error = "the exact command must not be empty.";
            return false;
        }

        if (!string.Equals(command, command.Trim(), StringComparison.Ordinal))
        {
            error = "leading or trailing whitespace is not allowed; provide the exact normalized command.";
            return false;
        }

        if (command.Any(char.IsControl))
        {
            error = "control characters and multi-line commands are not allowed.";
            return false;
        }

        if (command.IndexOfAny(s_patternCharacters) >= 0)
        {
            error = "glob pattern characters (*, ?, [, ]) are not allowed; command matching is exact.";
            return false;
        }

        normalized = command;
        return true;
    }

    public static bool IsExactMatch(string expected, object? actual)
    {
        if (!TryNormalize(expected, out var normalizedExpected, out _)
            || actual is not string actualCommand)
        {
            return false;
        }

        return string.Equals(normalizedExpected, actualCommand.Trim(), StringComparison.Ordinal);
    }
}
