using System.Security.Cryptography;

namespace Andy.Cli.Instrumentation;

/// <summary>
/// Credential generation and validation for the instrumentation server.
///
/// When instrumentation is enabled a random, unguessable token is generated once
/// per process. Every request to the server must present that token (as a query
/// parameter or header); requests without a valid token are rejected. This prevents
/// arbitrary browser origins or other local processes from reading live agent
/// activity simply by connecting to the loopback port.
/// </summary>
public static class InstrumentationAuth
{
    /// <summary>Query-string parameter carrying the credential (e.g. /events?token=...).</summary>
    public const string TokenQueryParameter = "token";

    /// <summary>HTTP header carrying the credential.</summary>
    public const string TokenHeaderName = "X-Andy-Instrumentation-Token";

    /// <summary>
    /// Generate a cryptographically-random, URL-safe token.
    /// </summary>
    public static string GenerateToken()
    {
        // 32 bytes -> 256 bits of entropy, encoded URL-safe without padding.
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>
    /// Constant-time comparison of a supplied credential against the expected token.
    /// Returns false when either value is missing or the values differ.
    /// </summary>
    public static bool IsAuthorized(string? provided, string? expected)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected))
        {
            return false;
        }

        var providedBytes = System.Text.Encoding.UTF8.GetBytes(provided);
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);

        // FixedTimeEquals also guards against length-based leaks by requiring equal
        // lengths; differing lengths simply return false in constant time.
        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
