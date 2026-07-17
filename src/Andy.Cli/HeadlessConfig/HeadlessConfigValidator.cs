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
    // Set by the container runtime (Epic Y5). A config's env_vars must never
    // shadow these, or an agent could redirect its own egress proxy, spoof its
    // run token, or repoint the MCP gateway. Rejected fail-closed.
    public static readonly IReadOnlyList<string> ReservedEnvVars =
        new[] { "ANDY_PROXY_URL", "ANDY_TOKEN", "ANDY_MCP_URL" };

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
                    return $"env_vars must not set reserved variable '{key}'; it is injected by "
                        + "the container runtime and cannot be overridden by a run config.";
                }
            }
        }

        if (string.Equals(config.Output.Stream, "fifo", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(config.EventSink?.Path))
        {
            return "output.stream is 'fifo' but event_sink.path is not set. FIFO event "
                + "streaming requires an absolute FIFO path in event_sink.path.";
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
