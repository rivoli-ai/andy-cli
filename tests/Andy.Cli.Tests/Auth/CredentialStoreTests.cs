using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Andy.Cli.Auth;
using Xunit;

namespace Andy.Cli.Tests.Auth;

/// <summary>
/// Credential-store contract tests. Everything here uses the in-memory fake or a temporary
/// directory; no test in this file reads or writes the developer's real credential service.
/// </summary>
[Collection("EnvironmentVariableTests")]
public class CredentialStoreTests
{
    [Fact]
    public async Task InMemoryStore_RoundTripsAndDeletesACredential()
    {
        var store = new InMemoryCredentialStore();
        var key = CredentialKeys.ForProvider("openai");

        Assert.Null(await store.GetAsync(key));

        await store.SetAsync(key, StoredCredential.ForApiKey(AuthTestValues.ApiKey, "workspace-a"));

        var loaded = await store.GetAsync(key);
        Assert.NotNull(loaded);
        Assert.Equal(CredentialKind.ApiKey, loaded!.Kind);
        Assert.Equal(AuthTestValues.ApiKey, loaded.ApiKey);
        Assert.Equal("workspace-a", loaded.AccountLabel);

        Assert.True(await store.DeleteAsync(key));
        Assert.Null(await store.GetAsync(key));
        Assert.False(await store.DeleteAsync(key));
    }

    [Fact]
    public async Task Logout_RemovesAccessAndRefreshTokensTogether()
    {
        // The whole record is a single store entry, which is what makes the removal atomic:
        // there is no window where the refresh token survives the access token.
        var store = new InMemoryCredentialStore();
        var key = CredentialKeys.ForProvider("openai");

        await store.SetAsync(key, new StoredCredential
        {
            Kind = CredentialKind.OAuth,
            AccessToken = AuthTestValues.AccessToken,
            RefreshToken = AuthTestValues.RefreshToken,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
        });

        Assert.True(await store.DeleteAsync(key));
        Assert.Empty(store.Keys);
        Assert.Null(await store.GetAsync(key));
    }

    [Fact]
    public void StoredCredential_ToStringNeverRevealsTheSecret()
    {
        var credential = new StoredCredential
        {
            Kind = CredentialKind.OAuth,
            AccessToken = AuthTestValues.AccessToken,
            RefreshToken = AuthTestValues.RefreshToken,
            ApiKey = AuthTestValues.ApiKey,
            AccountLabel = "team"
        };

        var rendered = credential.ToString();

        Assert.DoesNotContain(AuthTestValues.AccessToken, rendered);
        Assert.DoesNotContain(AuthTestValues.RefreshToken, rendered);
        Assert.DoesNotContain(AuthTestValues.ApiKey, rendered);
        Assert.Contains(Redaction.Mask, rendered);
    }

    [Fact]
    public void StoredCredential_DeserializeReturnsNullForGarbageInsteadOfThrowing()
    {
        // Throwing here would risk putting the raw payload into an exception message.
        Assert.Null(StoredCredential.Deserialize(null));
        Assert.Null(StoredCredential.Deserialize(string.Empty));
        Assert.Null(StoredCredential.Deserialize("not-base64-!!!"));
    }

    [Fact]
    public async Task FileFallbackStore_WritesOwnerOnlyAndRoundTrips()
    {
        using var directory = new TempDirectory();
        var path = directory.File("credentials.json");
        var store = new FileFallbackCredentialStore(path);

        await store.SetAsync(CredentialKeys.ForProvider("groq"), StoredCredential.ForApiKey(AuthTestValues.ApiKey));

        var loaded = await store.GetAsync(CredentialKeys.ForProvider("groq"));
        Assert.Equal(AuthTestValues.ApiKey, loaded?.ApiKey);
        Assert.True(File.Exists(path));

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var mode = File.GetUnixFileMode(path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
    }

    [Fact]
    public async Task FileFallbackStore_StoresNoPlaintextSecretUnderAKeyItDidNotWrite()
    {
        using var directory = new TempDirectory();
        var store = new FileFallbackCredentialStore(directory.File("credentials.json"));

        await store.SetAsync(CredentialKeys.ForProvider("groq"), StoredCredential.ForApiKey(AuthTestValues.ApiKey));

        Assert.Null(await store.GetAsync(CredentialKeys.ForProvider("openai")));
        Assert.True(await store.DeleteAsync(CredentialKeys.ForProvider("groq")));
        Assert.Null(await store.GetAsync(CredentialKeys.ForProvider("groq")));
    }

    [Fact]
    public async Task UnavailableStore_ReadsAsEmptyButRefusesToWrite()
    {
        var store = new UnavailableCredentialStore(CredentialStoreFactory.NoServiceGuidance());

        // Reading must degrade quietly so an environment-variable-only machine still starts.
        Assert.Null(await store.GetAsync(CredentialKeys.ForProvider("openai")));

        // Writing must fail loudly rather than silently persisting a plaintext secret.
        var failure = await Assert.ThrowsAsync<CredentialStoreUnavailableException>(
            () => store.SetAsync(CredentialKeys.ForProvider("openai"), StoredCredential.ForApiKey(AuthTestValues.ApiKey)));

        Assert.Contains("No OS credential service is available", failure.Message);
        Assert.DoesNotContain(AuthTestValues.ApiKey, failure.Message);
    }

    [Fact]
    public void NoServiceGuidance_IsActionableAndSecretFree()
    {
        var guidance = CredentialStoreFactory.NoServiceGuidance();

        Assert.Contains("Nothing was written", guidance);
        Assert.Contains("environment variables", guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(CredentialStoreFactory.OverrideEnvVar + "=file", guidance);
        Assert.Contains("docs/provider-auth.md", guidance);
    }

    [Theory]
    [InlineData("memory", typeof(InMemoryCredentialStore))]
    [InlineData("file", typeof(FileFallbackCredentialStore))]
    [InlineData("keychain", typeof(MacOsKeychainCredentialStore))]
    [InlineData("wincred", typeof(WindowsCredentialManagerStore))]
    [InlineData("secretservice", typeof(SecretServiceCredentialStore))]
    [InlineData("none", typeof(UnavailableCredentialStore))]
    [InlineData("definitely-not-a-backend", typeof(UnavailableCredentialStore))]
    public void Factory_HonoursTheExplicitOverride(string overrideValue, Type expected)
    {
        var store = CredentialStoreFactory.Create(overrideValue);
        Assert.IsType(expected, store);
    }

    [Fact]
    public void Factory_UnknownOverrideExplainsTheSupportedValues()
    {
        var store = Assert.IsType<UnavailableCredentialStore>(CredentialStoreFactory.Create("nonsense"));
        Assert.Contains("Supported values", store.Guidance);
        Assert.Contains("memory", store.Guidance);
    }

    [Fact]
    public void Factory_AutoSelectsAPlatformBackendOrFailsClosed()
    {
        var store = CredentialStoreFactory.Create("auto");

        // The plaintext file store must never be reachable without an explicit opt-in.
        Assert.IsNotType<FileFallbackCredentialStore>(store);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Assert.IsType<MacOsKeychainCredentialStore>(store);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.IsType<WindowsCredentialManagerStore>(store);
        }
    }

    [Fact]
    public void CredentialKeys_AreStableAndCaseInsensitive()
    {
        Assert.Equal("provider:openai", CredentialKeys.ForProvider("OpenAI"));
        Assert.Equal(CredentialKeys.ForProvider("openai"), CredentialKeys.ForProvider(" openai "));
        Assert.Throws<ArgumentException>(() => CredentialKeys.ForProvider("  "));
    }
}
