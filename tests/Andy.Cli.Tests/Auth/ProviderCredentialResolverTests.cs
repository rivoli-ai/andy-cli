using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Auth;
using Andy.Cli.Services;
using Xunit;

namespace Andy.Cli.Tests.Auth;

/// <summary>
/// Credential-resolution precedence: environment first (and never persisted), then the
/// credential store, then nothing.
/// </summary>
[Collection("EnvironmentVariableTests")]
public class ProviderCredentialResolverTests
{
    private static ProviderCredentialResolver CreateResolver(
        ICredentialStore store,
        Func<string, IProviderAuthHandler?>? handlerFactory = null,
        Func<DateTimeOffset>? clock = null)
        => new(store, handlerFactory, clock, AuthTestValues.NoOverlay);

    [Fact]
    public async Task StoredCredential_IsUsedWhenNoEnvironmentVariableIsSet()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        var store = new InMemoryCredentialStore();
        await store.SetAsync(CredentialKeys.ForProvider("openai"), StoredCredential.ForApiKey(AuthTestValues.ApiKey, "team-a"));

        var resolved = await CreateResolver(store).ResolveAsync("openai");

        Assert.Equal(CredentialSource.CredentialStore, resolved.Source);
        Assert.Equal(AuthTestValues.ApiKey, resolved.Secret);
        Assert.Equal("team-a", resolved.AccountLabel);
        Assert.True(resolved.HasCredential);
    }

    [Fact]
    public async Task EnvironmentCredential_OverridesTheStoredOneAndLeavesItUnmodified()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        env.Set("OPENAI_API_KEY", AuthTestValues.EnvApiKey);

        var store = new InMemoryCredentialStore();
        await store.SetAsync(CredentialKeys.ForProvider("openai"), StoredCredential.ForApiKey(AuthTestValues.ApiKey));

        var resolved = await CreateResolver(store).ResolveAsync("openai");

        Assert.Equal(CredentialSource.Environment, resolved.Source);
        Assert.Equal(AuthTestValues.EnvApiKey, resolved.Secret);
        Assert.Equal("OPENAI_API_KEY", resolved.EnvironmentVariable);

        // The stored credential must be untouched: an environment value is never written back.
        var stored = await store.GetAsync(CredentialKeys.ForProvider("openai"));
        Assert.Equal(AuthTestValues.ApiKey, stored?.ApiKey);
        Assert.Single(store.Keys);
    }

    [Fact]
    public async Task EnvironmentCredential_IsNeverPersistedWhenNothingIsStored()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        env.Set("ANTHROPIC_API_KEY", AuthTestValues.EnvApiKey);

        var store = new InMemoryCredentialStore();
        var resolved = await CreateResolver(store).ResolveAsync("anthropic");

        Assert.Equal(CredentialSource.Environment, resolved.Source);
        Assert.Empty(store.Keys);
    }

    [Fact]
    public async Task MissingCredential_ResolvesToNotConfiguredWithoutThrowing()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();

        var resolved = await CreateResolver(new InMemoryCredentialStore()).ResolveAsync("groq");

        Assert.Equal(CredentialSource.None, resolved.Source);
        Assert.False(resolved.HasCredential);
        Assert.Null(resolved.Secret);
    }

    [Fact]
    public async Task LocalProvider_ReportsThatNoCredentialIsRequired()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();

        var resolved = await CreateResolver(new InMemoryCredentialStore()).ResolveAsync("ollama");

        Assert.Equal(CredentialSource.NotRequired, resolved.Source);
        Assert.True(resolved.HasCredential);
    }

    [Fact]
    public async Task UnavailableCredentialService_DegradesToEnvironmentOnlyResolution()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        env.Set("GROQ_API_KEY", AuthTestValues.EnvApiKey);

        var resolver = CreateResolver(new ThrowingCredentialStore());

        Assert.Equal(CredentialSource.Environment, (await resolver.ResolveAsync("groq")).Source);
        Assert.Equal(CredentialSource.None, (await resolver.ResolveAsync("openai")).Source);
    }

    [Fact]
    public async Task ExpiredOAuthCredential_IsRefreshedAndWrittenBackThroughTheSameStore()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        var now = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

        var store = new InMemoryCredentialStore();
        await store.SetAsync(CredentialKeys.ForProvider("openai"), new StoredCredential
        {
            Kind = CredentialKind.OAuth,
            AccessToken = AuthTestValues.AccessToken,
            RefreshToken = AuthTestValues.RefreshToken,
            ExpiresAtUtc = now.AddSeconds(-1),
            AccountLabel = "test-account"
        });

        var tokenClient = new FakeOAuthTokenClient();
        var handler = new ProviderAuthHandler(TestDescriptors.OAuthProvider(), tokenClient, clock: () => now);
        var resolver = CreateResolver(store, _ => handler, () => now);

        var resolved = await resolver.ResolveAsync("openai");

        Assert.Equal(AuthTestValues.RenewedAccessToken, resolved.Secret);
        Assert.Equal(1, tokenClient.RefreshCallCount);
        Assert.Equal(AuthTestValues.RefreshToken, tokenClient.LastRefreshToken);

        var persisted = await store.GetAsync(CredentialKeys.ForProvider("openai"));
        Assert.Equal(AuthTestValues.RenewedAccessToken, persisted?.AccessToken);
        Assert.Equal(AuthTestValues.RefreshToken, persisted?.RefreshToken);
    }

    [Fact]
    public async Task FailedRefresh_KeepsTheStoredTokenAndSurfacesANonSecretNote()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        var now = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

        var store = new InMemoryCredentialStore();
        await store.SetAsync(CredentialKeys.ForProvider("openai"), new StoredCredential
        {
            Kind = CredentialKind.OAuth,
            AccessToken = AuthTestValues.AccessToken,
            RefreshToken = AuthTestValues.RefreshToken,
            ExpiresAtUtc = now.AddSeconds(-1)
        });

        var tokenClient = new FakeOAuthTokenClient { RefreshFailure = new OAuthException("invalid_grant") };
        var handler = new ProviderAuthHandler(TestDescriptors.OAuthProvider(), tokenClient, clock: () => now);

        var resolved = await CreateResolver(store, _ => handler, () => now).ResolveAsync("openai");

        Assert.Equal(AuthTestValues.AccessToken, resolved.Secret);
        Assert.Contains("Token refresh failed", resolved.Note);
        Assert.DoesNotContain(AuthTestValues.RefreshToken, resolved.Note!);
    }

    [Fact]
    public async Task ValidOAuthCredential_IsNotRefreshed()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        var now = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

        var store = new InMemoryCredentialStore();
        await store.SetAsync(CredentialKeys.ForProvider("openai"), new StoredCredential
        {
            Kind = CredentialKind.OAuth,
            AccessToken = AuthTestValues.AccessToken,
            RefreshToken = AuthTestValues.RefreshToken,
            ExpiresAtUtc = now.AddHours(2)
        });

        var tokenClient = new FakeOAuthTokenClient();
        var handler = new ProviderAuthHandler(TestDescriptors.OAuthProvider(), tokenClient, clock: () => now);

        var resolved = await CreateResolver(store, _ => handler, () => now).ResolveAsync("openai");

        Assert.Equal(AuthTestValues.AccessToken, resolved.Secret);
        Assert.Equal(0, tokenClient.RefreshCallCount);
    }

    [Fact]
    public async Task Invalidate_MakesTheNextResolutionSeeANewlyStoredCredential()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        var store = new InMemoryCredentialStore();
        var resolver = CreateResolver(store);

        Assert.False((await resolver.ResolveAsync("openai")).HasCredential);

        await store.SetAsync(CredentialKeys.ForProvider("openai"), StoredCredential.ForApiKey(AuthTestValues.ApiKey));

        // The memoized "not configured" answer survives until it is invalidated.
        Assert.False((await resolver.ResolveAsync("openai")).HasCredential);

        resolver.Invalidate("openai");
        Assert.True((await resolver.ResolveAsync("openai")).HasCredential);
    }

    [Fact]
    public async Task DetectDefaultProvider_PrefersRegistryOrderAcrossBothSources()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        env.Set("GOOGLE_API_KEY", AuthTestValues.EnvApiKey);

        var store = new InMemoryCredentialStore();
        await store.SetAsync(CredentialKeys.ForProvider("openai"), StoredCredential.ForApiKey(AuthTestValues.ApiKey));

        var resolver = CreateResolver(store);
        var detected = await resolver.DetectDefaultProviderAsync();

        // openai (priority 1) beats google (priority 5) regardless of which source supplied it.
        Assert.Equal("openai", detected);

        var configured = await resolver.ListConfiguredProvidersAsync();
        Assert.Contains("openai", configured);
        Assert.Contains("google", configured);
        Assert.DoesNotContain("groq", configured);
    }

    [Fact]
    public async Task ResolvedCredential_ToStringIsRedacted()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        var store = new InMemoryCredentialStore();
        await store.SetAsync(CredentialKeys.ForProvider("openai"), StoredCredential.ForApiKey(AuthTestValues.ApiKey));

        var resolved = await CreateResolver(store).ResolveAsync("openai");

        Assert.DoesNotContain(AuthTestValues.ApiKey, resolved.ToString());
        Assert.Contains(Redaction.Mask, resolved.ToString());
    }

    [Fact]
    public async Task UnknownProvider_IsReportedRatherThanThrowing()
    {
        var resolved = await CreateResolver(new InMemoryCredentialStore()).ResolveAsync("not-a-provider");

        Assert.Equal(CredentialSource.None, resolved.Source);
        Assert.Equal("Unknown provider.", resolved.Note);
    }

    [Fact]
    public async Task StoredCredentialProbe_MakesProviderRegistrySeeAStoredCredential()
    {
        using var env = EnvironmentScope.WithNoProviderKeys();
        var store = new InMemoryCredentialStore();
        await store.SetAsync(CredentialKeys.ForProvider("groq"), StoredCredential.ForApiKey(AuthTestValues.ApiKey));

        Assert.False(ProviderRegistry.HasCredentials("groq"));

        var resolver = CreateResolver(store);
        AuthBootstrap.UseResolver(resolver);
        try
        {
            Assert.True(ProviderRegistry.HasCredentials("groq"));
            Assert.False(ProviderRegistry.HasCredentials("openai"));

            // Detection, /model, and the ACP catalog all read HasCredentials, so they agree.
            Assert.True(new ProviderDetectionService().IsProviderAvailable("groq"));
        }
        finally
        {
            AuthBootstrap.UseResolver(null);
        }

        Assert.False(ProviderRegistry.HasCredentials("groq"));
    }
}
