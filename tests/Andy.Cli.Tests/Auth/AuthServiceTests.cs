using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Auth;
using Xunit;

namespace Andy.Cli.Tests.Auth;

/// <summary>
/// The list / login / status / logout use cases, driven entirely against the fake credential
/// store so they are deterministic on CI.
/// </summary>
[Collection("EnvironmentVariableTests")]
public class AuthServiceTests
{
    private static AuthService CreateService(
        ICredentialStore store,
        Func<ProviderAuthDescriptor, IProviderAuthHandler>? handlerFactory = null,
        ProviderCredentialResolver? resolver = null)
        => new(
            store,
            resolver ?? new ProviderCredentialResolver(store, catalogOverlayPath: AuthTestValues.NoOverlay),
            handlerFactory,
            clock: null,
            catalogOverlayPath: AuthTestValues.NoOverlay);

    [Fact]
    public async Task Login_ConfiguresAnApiKeyProviderWithoutExportingAnEnvironmentVariable()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        var store = new InMemoryCredentialStore();
        var service = CreateService(store);

        var result = await service.LoginAsync(
            "openai", methodName: null, ScriptedAuthPrompt.ForApiKey(AuthTestValues.ApiKey, "team-a"));

        Assert.True(result.Success);
        Assert.DoesNotContain(AuthTestValues.ApiKey, result.Message);

        var stored = await store.GetAsync(CredentialKeys.ForProvider("openai"));
        Assert.Equal(AuthTestValues.ApiKey, stored?.ApiKey);
        Assert.Equal("team-a", stored?.AccountLabel);
        Assert.Null(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));

        // The provider must now resolve for every mode, with no environment variable in play.
        var resolved = await new ProviderCredentialResolver(store, catalogOverlayPath: AuthTestValues.NoOverlay)
            .ResolveAsync("openai");
        Assert.Equal(CredentialSource.CredentialStore, resolved.Source);
        Assert.Equal(AuthTestValues.ApiKey, resolved.Secret);
    }

    [Fact]
    public async Task Login_ValidatesProviderFieldsWithoutEchoingTheValue()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        var store = new InMemoryCredentialStore();
        var service = CreateService(store);

        var tooShort = await service.LoginAsync("openai", null, ScriptedAuthPrompt.ForApiKey("short"));
        Assert.False(tooShort.Success);
        Assert.Contains("too short", tooShort.Message);
        Assert.Empty(store.Keys);

        var withWhitespace = await service.LoginAsync(
            "openai", null, ScriptedAuthPrompt.ForApiKey("unit test credential with spaces"));
        Assert.False(withWhitespace.Success);
        Assert.Contains("whitespace", withWhitespace.Message);
        Assert.Empty(store.Keys);
    }

    [Fact]
    public async Task Login_CancelledAtThePromptStoresNothing()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        var store = new InMemoryCredentialStore();

        var result = await CreateService(store).LoginAsync("openai", null, ScriptedAuthPrompt.Cancelling());

        Assert.False(result.Success);
        Assert.Contains("Nothing was stored", result.Message);
        Assert.Empty(store.Keys);
    }

    [Fact]
    public async Task Login_WarnsButProceedsWhenAnEnvironmentVariableAlreadyWins()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        env.Set("OPENAI_API_KEY", AuthTestValues.EnvApiKey);

        var store = new InMemoryCredentialStore();
        var prompt = ScriptedAuthPrompt.ForApiKey(AuthTestValues.ApiKey);

        var result = await CreateService(store).LoginAsync("openai", null, prompt);

        Assert.True(result.Success);
        Assert.Contains(prompt.Shown, s => s.Contains("OPENAI_API_KEY") && s.Contains("priority"));

        // The environment value must not have been written to the store.
        var stored = await store.GetAsync(CredentialKeys.ForProvider("openai"));
        Assert.Equal(AuthTestValues.ApiKey, stored?.ApiKey);
        Assert.NotEqual(AuthTestValues.EnvApiKey, stored?.ApiKey);
    }

    [Fact]
    public async Task Login_OnAMachineWithNoCredentialServiceFailsBeforePromptingForASecret()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        var prompt = ScriptedAuthPrompt.ForApiKey(AuthTestValues.ApiKey);

        var result = await CreateService(new ThrowingCredentialStore()).LoginAsync("openai", null, prompt);

        Assert.False(result.Success);
        Assert.Empty(prompt.RequestedFields);
        Assert.Contains("No OS credential service is available", result.Message);
        Assert.Contains(CredentialStoreFactory.OverrideEnvVar + "=file", result.Message);
    }

    [Fact]
    public async Task Login_RejectsAnUnknownProviderAndAnUnsupportedMethod()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        var service = CreateService(new InMemoryCredentialStore());

        var unknown = await service.LoginAsync("not-a-provider", null, ScriptedAuthPrompt.Cancelling());
        Assert.False(unknown.Success);
        Assert.Contains("Unknown provider", unknown.Message);

        var badMethod = await service.LoginAsync("openai", "device-code", ScriptedAuthPrompt.Cancelling());
        Assert.False(badMethod.Success);
        Assert.Contains("Unsupported login method", badMethod.Message);
    }

    [Fact]
    public async Task Login_ToALocalProviderExplainsThatNoCredentialIsNeeded()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();

        var result = await CreateService(new InMemoryCredentialStore())
            .LoginAsync("ollama", null, ScriptedAuthPrompt.Cancelling());

        Assert.False(result.Success);
        Assert.Contains("does not need a credential", result.Message);
    }

    [Fact]
    public async Task Login_ThroughTheFileFallbackWarnsAboutPlaintextStorage()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        using var directory = new TempDirectory();

        var store = new FileFallbackCredentialStore(directory.File("credentials.json"));
        var prompt = ScriptedAuthPrompt.ForApiKey(AuthTestValues.ApiKey);

        var result = await CreateService(store).LoginAsync("openai", null, prompt);

        Assert.True(result.Success);
        Assert.Contains(prompt.Shown, s => s.Contains("WARNING") && s.Contains("not encrypted"));
    }

    [Fact]
    public async Task Logout_RemovesTheStoredCredentialAtomically()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        var store = new InMemoryCredentialStore();
        await store.SetAsync(CredentialKeys.ForProvider("openai"), new StoredCredential
        {
            Kind = CredentialKind.OAuth,
            AccessToken = AuthTestValues.AccessToken,
            RefreshToken = AuthTestValues.RefreshToken
        });

        var result = await CreateService(store).LogoutAsync("openai");

        Assert.True(result.Success);
        Assert.Contains("API key, access token, and refresh token", result.Message);
        Assert.Empty(store.Keys);
        Assert.DoesNotContain(AuthTestValues.AccessToken, result.Message);
        Assert.DoesNotContain(AuthTestValues.RefreshToken, result.Message);
    }

    [Fact]
    public async Task Logout_ReportsWhenNothingWasStored()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();

        var result = await CreateService(new InMemoryCredentialStore()).LogoutAsync("openai");

        Assert.True(result.Success);
        Assert.Contains("nothing to remove", result.Message);
    }

    [Fact]
    public async Task Logout_TellsTheUserWhenAnEnvironmentVariableStillSuppliesACredential()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        env.Set("OPENAI_API_KEY", AuthTestValues.EnvApiKey);

        var store = new InMemoryCredentialStore();
        await store.SetAsync(CredentialKeys.ForProvider("openai"), StoredCredential.ForApiKey(AuthTestValues.ApiKey));

        var result = await CreateService(store).LogoutAsync("openai");

        Assert.True(result.Success);
        Assert.Contains("OPENAI_API_KEY is still set", result.Message);
        Assert.DoesNotContain(AuthTestValues.EnvApiKey, result.Message);
    }

    [Fact]
    public async Task Logout_AfterALoginMakesResolutionSeeTheRemoval()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        var store = new InMemoryCredentialStore();
        var resolver = new ProviderCredentialResolver(store, catalogOverlayPath: AuthTestValues.NoOverlay);
        var service = CreateService(store, resolver: resolver);

        await service.LoginAsync("openai", null, ScriptedAuthPrompt.ForApiKey(AuthTestValues.ApiKey));
        Assert.True((await resolver.ResolveAsync("openai")).HasCredential);

        await service.LogoutAsync("openai");
        Assert.False((await resolver.ResolveAsync("openai")).HasCredential);
    }

    [Fact]
    public async Task List_IsFullyRedactedAndNamesTheStore()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        env.Set("GROQ_API_KEY", AuthTestValues.EnvApiKey);

        var store = new InMemoryCredentialStore();
        await store.SetAsync(CredentialKeys.ForProvider("openai"), StoredCredential.ForApiKey(AuthTestValues.ApiKey));

        var text = await CreateService(store).ListAsync();

        Assert.DoesNotContain(AuthTestValues.ApiKey, text);
        Assert.DoesNotContain(AuthTestValues.EnvApiKey, text);
        Assert.Contains("openai", text);
        Assert.Contains("configured (credential store)", text);
        Assert.Contains("configured (env GROQ_API_KEY)", text);
        Assert.Contains("not configured", text);
        Assert.Contains("never persisted", text);
    }

    [Fact]
    public async Task Status_ShowsSourceAndAccountWithoutRevealingAnySecret()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        var store = new InMemoryCredentialStore();
        await store.SetAsync(CredentialKeys.ForProvider("openai"), new StoredCredential
        {
            Kind = CredentialKind.OAuth,
            AccessToken = AuthTestValues.AccessToken,
            RefreshToken = AuthTestValues.RefreshToken,
            AccountLabel = "person@example.invalid",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
        });

        var text = await CreateService(store).StatusAsync("openai");

        Assert.DoesNotContain(AuthTestValues.AccessToken, text);
        Assert.DoesNotContain(AuthTestValues.RefreshToken, text);
        Assert.Contains(Redaction.Mask, text);
        Assert.Contains("person@example.invalid", text);
        Assert.Contains("credential store", text);
        Assert.Contains("expires", text);
    }

    [Fact]
    public async Task Status_ForEveryProviderIsRedactedToo()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        env.Set("ANTHROPIC_API_KEY", AuthTestValues.EnvApiKey);

        var text = await CreateService(new InMemoryCredentialStore()).StatusAsync(null);

        Assert.DoesNotContain(AuthTestValues.EnvApiKey, text);
        Assert.Contains("environment (ANTHROPIC_API_KEY)", text);
        Assert.Contains("not configured", text);
    }

    [Fact]
    public async Task Status_RejectsAnUnknownProviderWithTheKnownList()
    {
        var text = await CreateService(new InMemoryCredentialStore()).StatusAsync("not-a-provider");

        Assert.Contains("Unknown provider", text);
        Assert.Contains("openai", text);
    }

    [Fact]
    public async Task List_ExplainsWhatToDoWhenThereIsNoCredentialService()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();

        var store = new UnavailableCredentialStore(CredentialStoreFactory.NoServiceGuidance());
        var text = await CreateService(store).ListAsync();

        Assert.Contains("[unavailable]", text);
        Assert.Contains("No OS credential service is available", text);
    }

    [Fact]
    public async Task OAuthLogin_ThroughACatalogOverlayStoresTokensThatLogoutThenRemovesTogether()
    {
        // The overlay is what lets a provider gain an OAuth login without a code change, so this
        // also covers "auth hooks without hard-coding every provider into the TUI".
        using var env = EnvironmentScope.WithNoProviderKeys();
        using var directory = new TempDirectory();
        var overlayPath = directory.File("provider-auth.json");
        System.IO.File.WriteAllText(overlayPath, OverlayJson);

        var store = new InMemoryCredentialStore();
        var tokenClient = new FakeOAuthTokenClient();
        tokenClient.EnqueueDevicePoll(new DevicePollResult(DevicePollStatus.Complete, tokenClient.ExchangeResult, null));

        var service = new AuthService(
            store,
            new ProviderCredentialResolver(store, catalogOverlayPath: overlayPath),
            descriptor => new ProviderAuthHandler(descriptor, tokenClient, delay: (_, _) => Task.CompletedTask),
            clock: null,
            catalogOverlayPath: overlayPath);

        var login = await service.LoginAsync(
            "openai", "device-code", new ScriptedAuthPrompt(new Dictionary<string, string?>()));

        Assert.True(login.Success);
        var stored = await store.GetAsync(CredentialKeys.ForProvider("openai"));
        Assert.Equal(AuthTestValues.AccessToken, stored?.AccessToken);
        Assert.Equal(AuthTestValues.RefreshToken, stored?.RefreshToken);

        var logout = await service.LogoutAsync("openai");
        Assert.True(logout.Success);
        Assert.Null(await store.GetAsync(CredentialKeys.ForProvider("openai")));
    }

    /// <summary>An overlay that enables both OAuth methods for openai. Contains no secrets.</summary>
    internal const string OverlayJson = """
        {
          "providers": {
            "openai": {
              "oauth": {
                "clientId": "test-client-id",
                "authorizationEndpoint": "https://example.invalid/authorize",
                "tokenEndpoint": "https://example.invalid/token",
                "deviceAuthorizationEndpoint": "https://example.invalid/device",
                "scopes": ["read"],
                "usePkce": true,
                "callbackPort": 0,
                "callbackPath": "/andy-cli/callback"
              }
            }
          }
        }
        """;
}
