using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Andy.Cli.Auth;

/// <summary>The login mechanisms a provider can offer.</summary>
public enum AuthMethodKind
{
    /// <summary>A long-lived API key typed by the user (masked) or piped in for automation.</summary>
    ApiKey = 0,

    /// <summary>OAuth authorization-code flow with a local loopback callback.</summary>
    OAuthLoopback = 1,

    /// <summary>OAuth device-authorization flow, for machines with no usable browser.</summary>
    OAuthDeviceCode = 2
}

/// <summary>
/// Validation rules for one field of a provider login. Rules are data, not code, so a new
/// provider never needs a change in the TUI or the CLI verb - only a descriptor.
/// </summary>
public sealed class AuthFieldSpec
{
    public required string Name { get; init; }

    /// <summary>Human-readable prompt label. Never contains secret material.</summary>
    public required string Label { get; init; }

    /// <summary>Whether the value must be captured with echo suppressed.</summary>
    public bool IsSecret { get; init; }

    public bool Required { get; init; } = true;

    public int MinLength { get; init; }

    public int MaxLength { get; init; } = 8192;

    /// <summary>
    /// Optional anchored regular expression the value must match. Deliberately expressed as a
    /// character-shape rule rather than a vendor key prefix, so no secret-shaped literal ends
    /// up in this repository.
    /// </summary>
    public string? Pattern { get; init; }

    /// <summary>Short hint shown next to the prompt (for example where to obtain the value).</summary>
    public string? Hint { get; init; }

    /// <summary>
    /// Validates a value. The returned message describes the rule that failed and never
    /// echoes the value itself.
    /// </summary>
    public bool TryValidate(string? value, out string error)
    {
        var trimmed = value?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            if (Required)
            {
                error = $"{Label} is required.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        if (trimmed.Length < MinLength)
        {
            error = $"{Label} looks too short (expected at least {MinLength} characters).";
            return false;
        }

        if (trimmed.Length > MaxLength)
        {
            error = $"{Label} is longer than the {MaxLength}-character limit.";
            return false;
        }

        if (trimmed.Any(char.IsWhiteSpace))
        {
            error = $"{Label} must not contain whitespace - check for a stray newline or a partial paste.";
            return false;
        }

        if (!string.IsNullOrEmpty(Pattern) && !Regex.IsMatch(trimmed, Pattern, RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            error = $"{Label} does not have the expected format for this provider.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

/// <summary>
/// OAuth endpoints and client settings for a provider. Supplied by the built-in catalog or by
/// <c>~/.andy/provider-auth.json</c>, so a provider can gain an OAuth login without a code change.
/// </summary>
public sealed class OAuthEndpointConfig
{
    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("authorizationEndpoint")]
    public string AuthorizationEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("tokenEndpoint")]
    public string TokenEndpoint { get; set; } = string.Empty;

    /// <summary>Required only for the device-code flow.</summary>
    [JsonPropertyName("deviceAuthorizationEndpoint")]
    public string? DeviceAuthorizationEndpoint { get; set; }

    [JsonPropertyName("scopes")]
    public List<string> Scopes { get; set; } = new();

    /// <summary>PKCE is on by default; only a provider that cannot handle S256 should disable it.</summary>
    [JsonPropertyName("usePkce")]
    public bool UsePkce { get; set; } = true;

    /// <summary>
    /// Fixed loopback callback port, when the provider requires a pre-registered redirect URI.
    /// Zero picks a free ephemeral port. The callback always binds to 127.0.0.1.
    /// </summary>
    [JsonPropertyName("callbackPort")]
    public int CallbackPort { get; set; }

    /// <summary>Path component of the loopback redirect URI.</summary>
    [JsonPropertyName("callbackPath")]
    public string CallbackPath { get; set; } = "/andy-cli/callback";

    public bool SupportsDeviceCode => !string.IsNullOrWhiteSpace(DeviceAuthorizationEndpoint);

    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(AuthorizationEndpoint)
        && !string.IsNullOrWhiteSpace(TokenEndpoint);
}

/// <summary>
/// Everything the auth stack needs to know about how a provider is logged in to. One
/// descriptor per provider; the TUI and the CLI verb both read this and nothing else, which is
/// what keeps provider knowledge out of the UI layer.
/// </summary>
public sealed class ProviderAuthDescriptor
{
    public required string ProviderId { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Environment variables that supply this credential, highest priority and never persisted.</summary>
    public IReadOnlyList<string> EnvironmentVariables { get; init; } = Array.Empty<string>();

    /// <summary>Whether the provider needs a credential at all (a local Ollama does not).</summary>
    public bool RequiresCredential { get; init; } = true;

    /// <summary>Fields collected for an API-key login, in prompt order.</summary>
    public IReadOnlyList<AuthFieldSpec> ApiKeyFields { get; init; } = Array.Empty<AuthFieldSpec>();

    public OAuthEndpointConfig? OAuth { get; init; }

    /// <summary>Login methods this provider actually supports, in preference order.</summary>
    public IReadOnlyList<AuthMethodKind> SupportedMethods
    {
        get
        {
            var methods = new List<AuthMethodKind>();
            if (RequiresCredential && ApiKeyFields.Count > 0)
            {
                methods.Add(AuthMethodKind.ApiKey);
            }

            if (OAuth is { IsUsable: true })
            {
                methods.Add(AuthMethodKind.OAuthLoopback);
                if (OAuth.SupportsDeviceCode)
                {
                    methods.Add(AuthMethodKind.OAuthDeviceCode);
                }
            }

            return methods;
        }
    }

    /// <summary>The secret field of an API-key login (the one that must be masked).</summary>
    public AuthFieldSpec? SecretField => ApiKeyFields.FirstOrDefault(f => f.IsSecret);
}
