using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Andy.Cli.Auth;
using Andy.Llm.Configuration;
using Xunit;

namespace Andy.Cli.Tests.Auth;

/// <summary>
/// The seam that hands resolved credentials to Andy.Llm. Interactive, headless, and ACP mode all
/// go through <see cref="LlmCredentialBinder"/>, so these tests are what guarantee the three
/// modes resolve credentials consistently.
/// </summary>
[Collection("EnvironmentVariableTests")]
public class LlmCredentialBinderTests
{
    private static ProviderCredentialResolver Resolver(ICredentialStore store)
        => new(store, catalogOverlayPath: AuthTestValues.NoOverlay);

    [Fact]
    public async Task StoredCredential_FillsAProviderThatHasNoKeyYet()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        var store = new InMemoryCredentialStore();
        await store.SetAsync(CredentialKeys.ForProvider("openai"), StoredCredential.ForApiKey(AuthTestValues.ApiKey));

        var options = new LlmOptions();
        LlmCredentialBinder.ApplyResolved(options, Resolver(store));

        Assert.Equal(AuthTestValues.ApiKey, options.Providers["openai"].ApiKey);
    }

    [Fact]
    public async Task EnvironmentCredential_OverwritesWhateverWasAlreadyInTheOptions()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        env.Set("OPENAI_API_KEY", AuthTestValues.EnvApiKey);

        var store = new InMemoryCredentialStore();
        await store.SetAsync(CredentialKeys.ForProvider("openai"), StoredCredential.ForApiKey(AuthTestValues.ApiKey));

        var options = new LlmOptions();
        options.Providers["openai"] = new ProviderConfig { Provider = "openai", ApiKey = "value-from-appsettings" };

        LlmCredentialBinder.ApplyResolved(options, Resolver(store));

        Assert.Equal(AuthTestValues.EnvApiKey, options.Providers["openai"].ApiKey);
    }

    [Fact]
    public async Task StoredCredential_DoesNotOverwriteAnExplicitlyConfiguredKey()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        var store = new InMemoryCredentialStore();
        await store.SetAsync(CredentialKeys.ForProvider("openai"), StoredCredential.ForApiKey(AuthTestValues.ApiKey));

        var options = new LlmOptions();
        options.Providers["openai"] = new ProviderConfig { Provider = "openai", ApiKey = "value-from-appsettings" };

        LlmCredentialBinder.ApplyResolved(options, Resolver(store));

        Assert.Equal("value-from-appsettings", options.Providers["openai"].ApiKey);
    }

    [Fact]
    public async Task Binder_MatchesAnExistingEntryByProviderTypeRatherThanCreatingADuplicate()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        var store = new InMemoryCredentialStore();
        await store.SetAsync(CredentialKeys.ForProvider("openai"), StoredCredential.ForApiKey(AuthTestValues.ApiKey));

        // Mirrors appsettings.json, which registers named variants like "openai/codex".
        var options = new LlmOptions();
        options.Providers["openai/codex"] = new ProviderConfig { Provider = "openai" };

        LlmCredentialBinder.ApplyResolved(options, Resolver(store));

        Assert.Equal(AuthTestValues.ApiKey, options.Providers["openai/codex"].ApiKey);
        Assert.False(options.Providers.ContainsKey("openai"));
    }

    [Fact]
    public void Binder_LeavesProvidersWithNoCredentialAlone()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();

        var options = new LlmOptions();
        LlmCredentialBinder.ApplyResolved(options, Resolver(new InMemoryCredentialStore()));

        Assert.All(options.Providers.Values, config => Assert.True(string.IsNullOrEmpty(config.ApiKey)));
    }

    [Fact]
    public async Task AllThreeModes_ResolveTheSameCredentialForTheSameProvider()
    {
        // Interactive, headless, and ACP each build their own LlmOptions graph but call the same
        // binder. Simulating the three graphs must produce identical credentials.
        using var env = EnvironmentScope.WithNoProviderKeys();
        env.Set("GROQ_API_KEY", AuthTestValues.EnvApiKey);

        var store = new InMemoryCredentialStore();
        await store.SetAsync(CredentialKeys.ForProvider("openai"), StoredCredential.ForApiKey(AuthTestValues.ApiKey));

        var interactive = new LlmOptions();
        // Headless names the provider and model up front.
        var headless = new LlmOptions();
        headless.Providers["openai"] = new ProviderConfig { Provider = "openai", Model = "gpt-4o" };
        // ACP pre-creates the entry for the detected default provider.
        var acp = new LlmOptions { DefaultProvider = "openai" };
        acp.Providers["openai"] = new ProviderConfig { Provider = "openai" };

        var resolver = Resolver(store);
        foreach (var options in new[] { interactive, headless, acp })
        {
            LlmCredentialBinder.ApplyResolved(options, resolver);
        }

        foreach (var options in new[] { interactive, headless, acp })
        {
            Assert.Equal(AuthTestValues.ApiKey, options.Providers["openai"].ApiKey);
            Assert.Equal(AuthTestValues.EnvApiKey, options.Providers["groq"].ApiKey);
        }

        // The headless graph's other settings must survive the binding.
        Assert.Equal("gpt-4o", headless.Providers["openai"].Model);
    }

    [Fact]
    public async Task ApplyResolvedAsync_MatchesTheBlockingOverload()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        var store = new InMemoryCredentialStore();
        await store.SetAsync(CredentialKeys.ForProvider("cerebras"), StoredCredential.ForApiKey(AuthTestValues.ApiKey));

        var blocking = new LlmOptions();
        var awaited = new LlmOptions();
        var resolver = Resolver(store);

        LlmCredentialBinder.ApplyResolved(blocking, resolver);
        await LlmCredentialBinder.ApplyResolvedAsync(awaited, resolver);

        Assert.Equal(blocking.Providers["cerebras"].ApiKey, awaited.Providers["cerebras"].ApiKey);
    }

    [Fact]
    public void Apply_IgnoresResolutionsWithNoSecret()
    {
        var options = new LlmOptions();
        LlmCredentialBinder.Apply(options, new[]
        {
            new ResolvedCredential { ProviderId = "openai", Source = CredentialSource.None },
            new ResolvedCredential { ProviderId = "ollama", Source = CredentialSource.NotRequired }
        });

        Assert.Empty(options.Providers);
    }

    [Fact]
    public async Task AuthBootstrap_ConfiguresOptionsAndInstallsTheDetectionProbe()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        var store = new InMemoryCredentialStore();
        await store.SetAsync(CredentialKeys.ForProvider("openai"), StoredCredential.ForApiKey(AuthTestValues.ApiKey));

        AuthBootstrap.UseResolver(Resolver(store));
        try
        {
            var options = new LlmOptions();
            AuthBootstrap.Configure(options);

            Assert.Equal(AuthTestValues.ApiKey, options.Providers["openai"].ApiKey);
            Assert.True(Andy.Cli.Services.ProviderRegistry.HasCredentials("openai"));
            Assert.Equal("openai", new Andy.Cli.Services.ProviderDetectionService().DetectDefaultProvider());
        }
        finally
        {
            AuthBootstrap.UseResolver(null);
        }
    }
}
