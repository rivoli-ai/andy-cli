using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Auth;

/// <summary>
/// Raised for any OAuth protocol failure. Messages describe the protocol condition only -
/// never a token, a code, or a client secret.
/// </summary>
public sealed class OAuthException : Exception
{
    public OAuthException(string message) : base(message)
    {
    }

    public OAuthException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>A token response from an authorization server. Carries secret material.</summary>
public sealed class OAuthTokenResponse
{
    public required string AccessToken { get; init; }

    public string? RefreshToken { get; init; }

    /// <summary>Lifetime of the access token, when the server reported one.</summary>
    public TimeSpan? ExpiresIn { get; init; }

    /// <summary>Non-secret account identifier the server reported (email, subject, org).</summary>
    public string? AccountLabel { get; init; }

    /// <summary>Redacted by design.</summary>
    public override string ToString()
        => $"OAuthTokenResponse(account={AccountLabel ?? "unknown"}, accessToken={Redaction.Mask}, refreshToken={Redaction.Mask})";
}

/// <summary>A pending device-authorization grant. <see cref="DeviceCode"/> is secret.</summary>
public sealed class OAuthDeviceAuthorization
{
    public required string DeviceCode { get; init; }

    /// <summary>The short code the user types into the verification page. Shown to the user.</summary>
    public required string UserCode { get; init; }

    public required string VerificationUri { get; init; }

    public string? VerificationUriComplete { get; init; }

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan ExpiresIn { get; init; } = TimeSpan.FromMinutes(5);

    public override string ToString()
        => $"OAuthDeviceAuthorization(userCode={UserCode}, deviceCode={Redaction.Mask})";
}

/// <summary>Outcome of one device-token poll.</summary>
public enum DevicePollStatus
{
    /// <summary>The user has not finished approving yet; poll again after the interval.</summary>
    Pending = 0,

    /// <summary>The server asked us to back off; the interval is increased.</summary>
    SlowDown = 1,

    /// <summary>Tokens were issued.</summary>
    Complete = 2,

    /// <summary>The user declined, or the grant expired.</summary>
    Denied = 3
}

/// <summary>Result of one device-token poll.</summary>
public sealed record DevicePollResult(DevicePollStatus Status, OAuthTokenResponse? Tokens, string? Error);

/// <summary>
/// The authorization-server calls the OAuth flows make. Abstracted so the flows can be unit
/// tested deterministically (state validation, cancellation, timeout, refresh) with no network.
/// </summary>
public interface IOAuthTokenClient
{
    Task<OAuthTokenResponse> ExchangeAuthorizationCodeAsync(
        OAuthEndpointConfig config,
        string code,
        string redirectUri,
        string? codeVerifier,
        CancellationToken cancellationToken);

    Task<OAuthDeviceAuthorization> StartDeviceAuthorizationAsync(
        OAuthEndpointConfig config,
        CancellationToken cancellationToken);

    Task<DevicePollResult> PollDeviceTokenAsync(
        OAuthEndpointConfig config,
        string deviceCode,
        CancellationToken cancellationToken);

    Task<OAuthTokenResponse> RefreshAsync(
        OAuthEndpointConfig config,
        string refreshToken,
        CancellationToken cancellationToken);
}

/// <summary>
/// Generates the anti-forgery state value and the PKCE pair used by the loopback flow.
/// Both use a cryptographic RNG; the state is what makes a hijacked callback detectable.
/// </summary>
public static class OAuthSecurity
{
    /// <summary>Creates a 256-bit URL-safe random value.</summary>
    public static string CreateRandomToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    /// <summary>Derives the S256 PKCE challenge for a verifier.</summary>
    public static string CreateCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64Url(hash);
    }

    /// <summary>
    /// Constant-time comparison of the returned state against the expected one. A plain
    /// string comparison would leak timing information about the expected value.
    /// </summary>
    public static bool StateMatches(string? expected, string? actual)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    /// <summary>Builds the provider authorization URL for the loopback flow.</summary>
    public static string BuildAuthorizationUrl(
        OAuthEndpointConfig config,
        string redirectUri,
        string state,
        string? codeChallenge)
    {
        var parameters = new List<string>
        {
            "response_type=code",
            "client_id=" + Uri.EscapeDataString(config.ClientId),
            "redirect_uri=" + Uri.EscapeDataString(redirectUri),
            "state=" + Uri.EscapeDataString(state)
        };

        if (config.Scopes.Count > 0)
        {
            parameters.Add("scope=" + Uri.EscapeDataString(string.Join(' ', config.Scopes)));
        }

        if (!string.IsNullOrEmpty(codeChallenge))
        {
            parameters.Add("code_challenge=" + Uri.EscapeDataString(codeChallenge));
            parameters.Add("code_challenge_method=S256");
        }

        var separator = config.AuthorizationEndpoint.Contains('?') ? "&" : "?";
        return config.AuthorizationEndpoint + separator + string.Join('&', parameters);
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
