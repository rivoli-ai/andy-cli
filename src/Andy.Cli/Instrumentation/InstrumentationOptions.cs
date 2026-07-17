namespace Andy.Cli.Instrumentation;

/// <summary>
/// Configuration for the localhost instrumentation server.
///
/// The instrumentation server exposes live agent activity (user messages, model
/// responses, tool parameters and results) over a local HTTP/SSE endpoint. Because
/// this data is sensitive, the server is DISABLED BY DEFAULT and must be explicitly
/// opted into via a CLI flag or environment variable. When enabled it binds to
/// loopback only, requires a per-process credential, and redacts sensitive fields
/// unless the operator explicitly opts in to including them.
/// </summary>
public sealed class InstrumentationOptions
{
    /// <summary>Default TCP port used when none is configured.</summary>
    public const int DefaultPort = 5555;

    /// <summary>Whether the instrumentation server should be started at all.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Requested TCP port. A value of 0 asks the OS for a free ephemeral port.
    /// </summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>
    /// When false (the default), sensitive event fields (user messages, model
    /// responses, tool parameters and results) are redacted before being streamed.
    /// Set to true only when the operator has explicitly opted in.
    /// </summary>
    public bool IncludeSensitive { get; init; }

    /// <summary>
    /// Build options from environment variables and CLI arguments.
    ///
    /// Enable with any of:
    ///   ANDY_INSTRUMENTATION=1|true|yes|on
    ///   --instrumentation
    /// Include sensitive content (opt-in) with any of:
    ///   ANDY_INSTRUMENTATION_SENSITIVE=1|true|yes|on
    ///   --instrumentation-include-sensitive
    /// Override the port with:
    ///   ANDY_INSTRUMENTATION_PORT=&lt;port&gt;
    /// </summary>
    public static InstrumentationOptions FromEnvironmentAndArgs(IReadOnlyList<string>? args = null)
    {
        args ??= Array.Empty<string>();

        bool enabled = IsTruthy(Environment.GetEnvironmentVariable("ANDY_INSTRUMENTATION"))
            || HasFlag(args, "--instrumentation");

        bool includeSensitive = IsTruthy(Environment.GetEnvironmentVariable("ANDY_INSTRUMENTATION_SENSITIVE"))
            || HasFlag(args, "--instrumentation-include-sensitive");

        int port = DefaultPort;
        var portEnv = Environment.GetEnvironmentVariable("ANDY_INSTRUMENTATION_PORT");
        if (!string.IsNullOrWhiteSpace(portEnv)
            && int.TryParse(portEnv, out var parsedPort)
            && parsedPort >= 0
            && parsedPort <= 65535)
        {
            port = parsedPort;
        }

        return new InstrumentationOptions
        {
            Enabled = enabled,
            Port = port,
            IncludeSensitive = includeSensitive
        };
    }

    private static bool HasFlag(IReadOnlyList<string> args, string flag)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            _ => false
        };
    }
}
