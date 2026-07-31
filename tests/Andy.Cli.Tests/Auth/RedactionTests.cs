using System;
using Andy.Cli.Auth;
using Xunit;

namespace Andy.Cli.Tests.Auth;

/// <summary>
/// Redaction is the last line of defence for anything that could carry a provider secret into a
/// log line, an exception message, or the transcript.
/// </summary>
[Collection("EnvironmentVariableTests")]
public class RedactionTests
{
    [Fact]
    public void Describe_NeverRevealsAnyPartOfTheValue()
    {
        Assert.Equal("not set", Redaction.Describe(null));
        Assert.Equal("not set", Redaction.Describe(string.Empty));

        var described = Redaction.Describe(AuthTestValues.ApiKey);
        Assert.Equal(Redaction.Mask, described);
        Assert.DoesNotContain(AuthTestValues.ApiKey[..4], described);
        Assert.DoesNotContain(AuthTestValues.ApiKey[^4..], described);
    }

    [Fact]
    public void Scrub_RemovesEverySecretFromArbitraryText()
    {
        var text = $"POST failed for key {AuthTestValues.ApiKey} and token {AuthTestValues.AccessToken}";

        var scrubbed = Redaction.Scrub(text, AuthTestValues.ApiKey, AuthTestValues.AccessToken);

        Assert.DoesNotContain(AuthTestValues.ApiKey, scrubbed);
        Assert.DoesNotContain(AuthTestValues.AccessToken, scrubbed);
        Assert.Contains(Redaction.Mask, scrubbed);
        Assert.Contains("POST failed", scrubbed);
    }

    [Fact]
    public void Scrub_HandlesNullsAndShortValuesWithoutMangling()
    {
        Assert.Equal(string.Empty, Redaction.Scrub(null, AuthTestValues.ApiKey));
        Assert.Equal("abc", Redaction.Scrub("abc", (string?)null));

        // A value too short to be a plausible credential must not turn the message into noise.
        Assert.Equal("the ok path", Redaction.Scrub("the ok path", "ok"));
    }

    [Fact]
    public void Scrub_TakesAllThreeSecretsOffACredential()
    {
        var credential = new StoredCredential
        {
            Kind = CredentialKind.OAuth,
            ApiKey = AuthTestValues.ApiKey,
            AccessToken = AuthTestValues.AccessToken,
            RefreshToken = AuthTestValues.RefreshToken
        };

        var text = string.Join(' ', AuthTestValues.ApiKey, AuthTestValues.AccessToken, AuthTestValues.RefreshToken);
        var scrubbed = Redaction.Scrub(text, credential);

        Assert.DoesNotContain(AuthTestValues.ApiKey, scrubbed);
        Assert.DoesNotContain(AuthTestValues.AccessToken, scrubbed);
        Assert.DoesNotContain(AuthTestValues.RefreshToken, scrubbed);

        Assert.Equal("unchanged", Redaction.Scrub("unchanged", (StoredCredential?)null));
    }

    [Fact]
    public void ScrubEnvironmentValues_RemovesConfiguredKeysFromDiagnostics()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        env.Set("OPENAI_API_KEY", AuthTestValues.EnvApiKey);

        var diagnostics = $"OPENAI_API_KEY={AuthTestValues.EnvApiKey}";
        var scrubbed = Redaction.ScrubEnvironmentValues(diagnostics, new[] { "OPENAI_API_KEY", "GROQ_API_KEY" });

        Assert.DoesNotContain(AuthTestValues.EnvApiKey, scrubbed);
        Assert.Contains("OPENAI_API_KEY=", scrubbed);
    }

    [Fact]
    public void OAuthResponses_AreRedactedWhenRendered()
    {
        var tokens = new OAuthTokenResponse
        {
            AccessToken = AuthTestValues.AccessToken,
            RefreshToken = AuthTestValues.RefreshToken,
            AccountLabel = "person@example.invalid"
        };

        var rendered = tokens.ToString();
        Assert.DoesNotContain(AuthTestValues.AccessToken, rendered);
        Assert.DoesNotContain(AuthTestValues.RefreshToken, rendered);
        Assert.Contains("person@example.invalid", rendered);

        var device = new OAuthDeviceAuthorization
        {
            DeviceCode = "unit-test-device-code",
            UserCode = "ABCD-EFGH",
            VerificationUri = "https://example.invalid/device"
        };

        // The user code is meant to be read aloud; the device code is a bearer secret.
        Assert.Contains("ABCD-EFGH", device.ToString());
        Assert.DoesNotContain("unit-test-device-code", device.ToString());
    }
}
