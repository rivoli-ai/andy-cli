using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.Cli.Auth;

/// <summary>
/// The kind of secret material held by a <see cref="StoredCredential"/>.
/// </summary>
public enum CredentialKind
{
    /// <summary>A long-lived provider API key.</summary>
    ApiKey = 0,

    /// <summary>An OAuth token pair (access token plus optional refresh token).</summary>
    OAuth = 1
}

/// <summary>
/// Where a resolved credential came from. Used for user-facing status output; it
/// never carries secret material.
/// </summary>
public enum CredentialSource
{
    /// <summary>No credential is available for the provider.</summary>
    None = 0,

    /// <summary>Supplied by an environment variable. Highest priority and never persisted.</summary>
    Environment = 1,

    /// <summary>Read from the operating system credential service (Keychain / Credential Manager / Secret Service).</summary>
    CredentialStore = 2,

    /// <summary>Read from the explicitly opted-in file fallback store (see docs/provider-auth.md).</summary>
    FileFallback = 3,

    /// <summary>The provider needs no credential (for example a local Ollama endpoint).</summary>
    NotRequired = 4
}

/// <summary>
/// The credential record persisted for a single provider. One record per provider keeps
/// logout atomic: deleting the single store entry removes the API key, the refresh token,
/// and any cached access token in one operation.
///
/// SECURITY: this type carries secret material. It deliberately overrides
/// <see cref="ToString"/> so an accidental interpolation into a log line, an exception
/// message, or the event stream cannot leak the secret. Never add secret fields to any
/// serializer used for transcripts, telemetry, or effective-config output.
/// </summary>
public sealed class StoredCredential
{
    /// <summary>Serialized record version, so a future format change can be detected.</summary>
    [JsonPropertyName("v")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("kind")]
    public CredentialKind Kind { get; set; } = CredentialKind.ApiKey;

    /// <summary>The API key, when <see cref="Kind"/> is <see cref="CredentialKind.ApiKey"/>.</summary>
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    /// <summary>The OAuth access token, when <see cref="Kind"/> is <see cref="CredentialKind.OAuth"/>.</summary>
    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; set; }

    /// <summary>The OAuth refresh token, if the provider issued one.</summary>
    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }

    /// <summary>Absolute expiry of <see cref="AccessToken"/>, when known.</summary>
    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAtUtc { get; set; }

    /// <summary>
    /// A non-secret label identifying the account (an email, org id, or key nickname).
    /// Shown in status output, so it must never contain secret material.
    /// </summary>
    [JsonPropertyName("account")]
    public string? AccountLabel { get; set; }

    /// <summary>The auth method that produced this record ("api_key", "oauth_loopback", "oauth_device").</summary>
    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The secret this credential presents to the provider: the API key or the OAuth
    /// access token depending on <see cref="Kind"/>.
    /// </summary>
    [JsonIgnore]
    public string? Secret => Kind == CredentialKind.OAuth ? AccessToken : ApiKey;

    /// <summary>Whether the access token is expired (or expires within <paramref name="skew"/>).</summary>
    public bool IsExpired(DateTimeOffset now, TimeSpan skew)
        => ExpiresAtUtc.HasValue && ExpiresAtUtc.Value - skew <= now;

    public static StoredCredential ForApiKey(string apiKey, string? accountLabel = null) => new()
    {
        Kind = CredentialKind.ApiKey,
        ApiKey = apiKey,
        AccountLabel = accountLabel,
        Method = "api_key"
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>
    /// Serializes to the opaque, single-line payload written to the credential store.
    /// Base64 keeps the blob free of newlines and non-UTF8 bytes, which matters because
    /// the macOS and Linux backends move the value over stdin/stdout pipes.
    /// </summary>
    public string Serialize()
    {
        var json = JsonSerializer.Serialize(this, SerializerOptions);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// Parses a payload produced by <see cref="Serialize"/>. Returns null for anything
    /// unparseable; the caller treats that as "no usable credential" rather than throwing,
    /// because throwing risks putting the raw payload into an exception message.
    /// </summary>
    public static StoredCredential? Deserialize(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload.Trim()));
            return JsonSerializer.Deserialize<StoredCredential>(json, SerializerOptions);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>Redacted by design; see the class remarks.</summary>
    public override string ToString()
        => $"StoredCredential(kind={Kind}, account={AccountLabel ?? "unknown"}, secret={Redaction.Mask})";
}
