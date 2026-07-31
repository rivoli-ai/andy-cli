using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Auth;

/// <summary>
/// The real authorization-server client, speaking RFC 6749 / RFC 8628 over HTTPS.
///
/// SECURITY: every secret parameter travels in the POST body, never in the query string,
/// because URLs routinely end up in proxy and server logs. Response bodies are parsed and
/// discarded; they are never logged, and error paths surface only the OAuth error code.
/// </summary>
public sealed class HttpOAuthTokenClient : IOAuthTokenClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public HttpOAuthTokenClient()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, ownsClient: true)
    {
    }

    public HttpOAuthTokenClient(HttpClient httpClient, bool ownsClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsClient = ownsClient;
    }

    public Task<OAuthTokenResponse> ExchangeAuthorizationCodeAsync(
        OAuthEndpointConfig config,
        string code,
        string redirectUri,
        string? codeVerifier,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = config.ClientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri
        };

        if (!string.IsNullOrEmpty(codeVerifier))
        {
            form["code_verifier"] = codeVerifier;
        }

        return PostForTokensAsync(config.TokenEndpoint, form, cancellationToken);
    }

    public async Task<OAuthDeviceAuthorization> StartDeviceAuthorizationAsync(
        OAuthEndpointConfig config,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.DeviceAuthorizationEndpoint))
        {
            throw new OAuthException("This provider does not advertise a device-authorization endpoint.");
        }

        var form = new Dictionary<string, string> { ["client_id"] = config.ClientId };
        if (config.Scopes.Count > 0)
        {
            form["scope"] = string.Join(' ', config.Scopes);
        }

        using var response = await PostAsync(config.DeviceAuthorizationEndpoint!, form, cancellationToken)
            .ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        if (!response.IsSuccessStatusCode)
        {
            throw new OAuthException(
                $"The provider refused the device-authorization request ({DescribeError(root, response.StatusCode.ToString())}).");
        }

        var deviceCode = GetString(root, "device_code")
                         ?? throw new OAuthException("The device-authorization response did not include a device code.");
        var userCode = GetString(root, "user_code")
                       ?? throw new OAuthException("The device-authorization response did not include a user code.");
        var verificationUri = GetString(root, "verification_uri") ?? GetString(root, "verification_url")
                              ?? throw new OAuthException("The device-authorization response did not include a verification URL.");

        return new OAuthDeviceAuthorization
        {
            DeviceCode = deviceCode,
            UserCode = userCode,
            VerificationUri = verificationUri,
            VerificationUriComplete = GetString(root, "verification_uri_complete"),
            Interval = TimeSpan.FromSeconds(GetDouble(root, "interval") ?? 5),
            ExpiresIn = TimeSpan.FromSeconds(GetDouble(root, "expires_in") ?? 300)
        };
    }

    public async Task<DevicePollResult> PollDeviceTokenAsync(
        OAuthEndpointConfig config,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["client_id"] = config.ClientId,
            ["device_code"] = deviceCode
        };

        using var response = await PostAsync(config.TokenEndpoint, form, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        if (response.IsSuccessStatusCode)
        {
            return new DevicePollResult(DevicePollStatus.Complete, ToTokens(root), null);
        }

        var error = GetString(root, "error") ?? response.StatusCode.ToString();
        return error switch
        {
            "authorization_pending" => new DevicePollResult(DevicePollStatus.Pending, null, error),
            "slow_down" => new DevicePollResult(DevicePollStatus.SlowDown, null, error),
            _ => new DevicePollResult(DevicePollStatus.Denied, null, error)
        };
    }

    public Task<OAuthTokenResponse> RefreshAsync(
        OAuthEndpointConfig config,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = config.ClientId,
            ["refresh_token"] = refreshToken
        };

        return PostForTokensAsync(config.TokenEndpoint, form, cancellationToken);
    }

    private async Task<OAuthTokenResponse> PostForTokensAsync(
        string endpoint,
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var response = await PostAsync(endpoint, form, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new OAuthException(
                $"The provider rejected the token request ({DescribeError(document.RootElement, response.StatusCode.ToString())}).");
        }

        return ToTokens(document.RootElement);
    }

    private async Task<HttpResponseMessage> PostAsync(
        string endpoint,
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        try
        {
            return await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // The exception message can quote the request; scrub every secret we sent.
            var scrubbed = Redaction.Scrub(
                ex.Message,
                form.GetValueOrDefault("code"),
                form.GetValueOrDefault("code_verifier"),
                form.GetValueOrDefault("refresh_token"),
                form.GetValueOrDefault("device_code"));
            throw new OAuthException($"Could not reach the provider's OAuth endpoint: {scrubbed}");
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        catch (JsonException)
        {
            // Never surface the body: it may contain tokens on a partially successful response.
            return JsonDocument.Parse("{}");
        }
    }

    private static OAuthTokenResponse ToTokens(JsonElement root)
    {
        var accessToken = GetString(root, "access_token")
                          ?? throw new OAuthException("The token response did not include an access token.");

        var expiresIn = GetDouble(root, "expires_in");
        return new OAuthTokenResponse
        {
            AccessToken = accessToken,
            RefreshToken = GetString(root, "refresh_token"),
            ExpiresIn = expiresIn.HasValue ? TimeSpan.FromSeconds(expiresIn.Value) : null,
            AccountLabel = GetString(root, "account") ?? GetString(root, "email") ?? GetString(root, "sub")
        };
    }

    private static string DescribeError(JsonElement root, string fallback)
    {
        var error = GetString(root, "error");
        var description = GetString(root, "error_description");
        if (string.IsNullOrEmpty(error))
        {
            return fallback;
        }

        return string.IsNullOrEmpty(description) ? error : $"{error}: {description}";
    }

    private static string? GetString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? GetDouble(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
