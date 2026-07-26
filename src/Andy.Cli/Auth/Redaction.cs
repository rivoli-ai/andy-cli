using System;
using System.Collections.Generic;

namespace Andy.Cli.Auth;

/// <summary>
/// Central redaction helpers for anything that might carry provider secrets.
///
/// The policy is deliberately strict: user-facing output never shows any part of a
/// secret - not a prefix, not a suffix, not a hash. Issue #284 requires credential
/// listings and diagnostics to be fully redacted, and a partial reveal (last four
/// characters, or a truncated digest of a low-entropy value) is still a reveal.
/// </summary>
public static class Redaction
{
    /// <summary>The single token used everywhere a secret would otherwise appear.</summary>
    public const string Mask = "****";

    /// <summary>
    /// Renders a secret for display. Any non-empty value becomes <see cref="Mask"/>;
    /// an absent value is reported as "not set" so status output stays useful.
    /// </summary>
    public static string Describe(string? secret)
        => string.IsNullOrEmpty(secret) ? "not set" : Mask;

    /// <summary>
    /// Removes every occurrence of the given secrets from arbitrary text. Used as a last
    /// line of defence before writing an error surfaced by a provider or an OS tool, which
    /// may echo the value we just handed it.
    /// </summary>
    public static string Scrub(string? text, params string?[] secrets)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = text;
        foreach (var secret in secrets)
        {
            // Very short values would turn the whole message into mask noise and are not
            // plausible credentials, so they are left alone.
            if (string.IsNullOrEmpty(secret) || secret.Length < 6)
            {
                continue;
            }

            result = result.Replace(secret, Mask, StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>
    /// Scrubs the secrets carried by a credential out of arbitrary text.
    /// </summary>
    public static string Scrub(string? text, StoredCredential? credential)
    {
        if (credential == null)
        {
            return text ?? string.Empty;
        }

        return Scrub(text, credential.ApiKey, credential.AccessToken, credential.RefreshToken);
    }

    /// <summary>
    /// Scrubs every value of the supplied environment variable names out of text, so a
    /// diagnostic that echoes the process environment cannot leak a configured key.
    /// </summary>
    public static string ScrubEnvironmentValues(string? text, IEnumerable<string> environmentVariableNames)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = text;
        foreach (var name in environmentVariableNames)
        {
            var value = Environment.GetEnvironmentVariable(name);
            result = Scrub(result, value);
        }

        return result;
    }
}
