using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Auth;
using Xunit;

namespace Andy.Cli.Tests.Auth;

/// <summary>
/// OAuth coverage required by issue #284: state validation, cancellation, timeout, refresh, and
/// logout. Every test drives <see cref="FakeOAuthTokenClient"/> instead of the network; only the
/// loopback listener is real, and it binds to 127.0.0.1 on an ephemeral port.
/// </summary>
[Collection("EnvironmentVariableTests")]
public class OAuthFlowTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void LoopbackListener_BindsToLoopbackOnly()
    {
        using var listener = LoopbackOAuthListener.Start(0, "/andy-cli/callback");

        Assert.StartsWith("http://127.0.0.1:", listener.RedirectUri);
        Assert.DoesNotContain("localhost", listener.RedirectUri);
        Assert.DoesNotContain("0.0.0.0", listener.RedirectUri);
    }

    [Fact]
    public async Task LoopbackListener_AcceptsTheCodeWhenTheStateMatches()
    {
        using var listener = LoopbackOAuthListener.Start(0, "/andy-cli/callback");
        var state = OAuthSecurity.CreateRandomToken();

        var waiting = listener.WaitForAuthorizationCodeAsync(state, ShortTimeout, CancellationToken.None);
        await GetAsync($"{listener.RedirectUri}?code=test-authorization-code&state={Uri.EscapeDataString(state)}");

        Assert.Equal("test-authorization-code", await waiting);
    }

    [Fact]
    public async Task LoopbackListener_RejectsACallbackWithAMismatchedState()
    {
        using var listener = LoopbackOAuthListener.Start(0, "/andy-cli/callback");
        var state = OAuthSecurity.CreateRandomToken();

        var waiting = listener.WaitForAuthorizationCodeAsync(state, ShortTimeout, CancellationToken.None);
        await GetAsync($"{listener.RedirectUri}?code=test-authorization-code&state=forged-state");

        var failure = await Assert.ThrowsAsync<OAuthException>(() => waiting);
        Assert.Contains("expected state", failure.Message);
        Assert.Contains("No credential was stored", failure.Message);
    }

    [Fact]
    public async Task LoopbackListener_RejectsACallbackWithNoState()
    {
        using var listener = LoopbackOAuthListener.Start(0, "/andy-cli/callback");
        var state = OAuthSecurity.CreateRandomToken();

        var waiting = listener.WaitForAuthorizationCodeAsync(state, ShortTimeout, CancellationToken.None);
        await GetAsync($"{listener.RedirectUri}?code=test-authorization-code");

        await Assert.ThrowsAsync<OAuthException>(() => waiting);
    }

    [Fact]
    public async Task LoopbackListener_SurfacesAProviderReportedError()
    {
        using var listener = LoopbackOAuthListener.Start(0, "/andy-cli/callback");
        var state = OAuthSecurity.CreateRandomToken();

        var waiting = listener.WaitForAuthorizationCodeAsync(state, ShortTimeout, CancellationToken.None);
        await GetAsync($"{listener.RedirectUri}?error=access_denied&state={Uri.EscapeDataString(state)}");

        var failure = await Assert.ThrowsAsync<OAuthException>(() => waiting);
        Assert.Contains("access_denied", failure.Message);
    }

    [Fact]
    public async Task LoopbackListener_TimesOutWhenTheUserNeverFinishes()
    {
        using var listener = LoopbackOAuthListener.Start(0, "/andy-cli/callback");

        var failure = await Assert.ThrowsAsync<OAuthException>(() =>
            listener.WaitForAuthorizationCodeAsync(
                OAuthSecurity.CreateRandomToken(), TimeSpan.FromMilliseconds(150), CancellationToken.None));

        Assert.Contains("Timed out", failure.Message);
        Assert.Contains("No credential was stored", failure.Message);
    }

    [Fact]
    public async Task LoopbackListener_HonoursCancellation()
    {
        using var listener = LoopbackOAuthListener.Start(0, "/andy-cli/callback");
        using var cts = new CancellationTokenSource();

        var waiting = listener.WaitForAuthorizationCodeAsync(
            OAuthSecurity.CreateRandomToken(), TimeSpan.FromSeconds(30), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task LoopbackLogin_ExchangesTheCodeWithPkceAndReturnsAnOAuthCredential()
    {
        var tokenClient = new FakeOAuthTokenClient();
        var handler = new ProviderAuthHandler(
            TestDescriptors.OAuthProvider(),
            tokenClient,
            loopbackTimeout: ShortTimeout,
            clock: () => new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));

        var prompt = new CallbackDrivingPrompt();
        var credential = await handler.LoginAsync(AuthMethodKind.OAuthLoopback, prompt, CancellationToken.None);

        Assert.NotNull(credential);
        Assert.Equal(CredentialKind.OAuth, credential!.Kind);
        Assert.Equal(AuthTestValues.AccessToken, credential.AccessToken);
        Assert.Equal(AuthTestValues.RefreshToken, credential.RefreshToken);
        Assert.Equal("oauth_loopback", credential.Method);
        Assert.Equal(new DateTimeOffset(2026, 7, 25, 13, 0, 0, TimeSpan.Zero), credential.ExpiresAtUtc);

        // PKCE must be used, and the redirect the code was exchanged against must be the loopback one.
        Assert.False(string.IsNullOrEmpty(tokenClient.LastCodeVerifier));
        Assert.StartsWith("http://127.0.0.1:", tokenClient.LastRedirectUri);

        // The authorization URL must carry a state and an S256 challenge.
        var url = prompt.AuthorizationUrl!;
        Assert.Contains("state=", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("response_type=code", url);
    }

    [Fact]
    public async Task LoopbackLogin_ReturnsNullWhenTheUserCancels()
    {
        var handler = new ProviderAuthHandler(
            TestDescriptors.OAuthProvider(),
            new FakeOAuthTokenClient(),
            loopbackTimeout: TimeSpan.FromSeconds(30));

        using var cts = new CancellationTokenSource();
        var prompt = new CancellingPrompt(cts);

        var credential = await handler.LoginAsync(AuthMethodKind.OAuthLoopback, prompt, cts.Token);

        Assert.Null(credential);
    }

    [Fact]
    public async Task DeviceCodeLogin_PollsThroughPendingAndSlowDownUntilItCompletes()
    {
        var tokenClient = new FakeOAuthTokenClient();
        tokenClient.EnqueueDevicePoll(new DevicePollResult(DevicePollStatus.Pending, null, "authorization_pending"));
        tokenClient.EnqueueDevicePoll(new DevicePollResult(DevicePollStatus.SlowDown, null, "slow_down"));
        tokenClient.EnqueueDevicePoll(new DevicePollResult(DevicePollStatus.Complete, tokenClient.ExchangeResult, null));

        var handler = new ProviderAuthHandler(
            TestDescriptors.OAuthProvider(deviceCode: true),
            tokenClient,
            clock: () => new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero),
            delay: (_, _) => Task.CompletedTask);

        var prompt = new ScriptedAuthPrompt(new Dictionary<string, string?>());
        var credential = await handler.LoginAsync(AuthMethodKind.OAuthDeviceCode, prompt, CancellationToken.None);

        Assert.NotNull(credential);
        Assert.Equal("oauth_device", credential!.Method);
        Assert.Equal(AuthTestValues.AccessToken, credential.AccessToken);

        // The user code must be shown so the user can enter it; the device code must not be.
        Assert.Contains(prompt.Shown, s => s.Contains("ABCD-EFGH"));
        Assert.DoesNotContain(prompt.Shown, s => s.Contains("unit-test-device-code"));
    }

    [Fact]
    public async Task DeviceCodeLogin_FailsWhenTheUserDeclines()
    {
        var tokenClient = new FakeOAuthTokenClient();
        tokenClient.EnqueueDevicePoll(new DevicePollResult(DevicePollStatus.Denied, null, "access_denied"));

        var handler = new ProviderAuthHandler(
            TestDescriptors.OAuthProvider(deviceCode: true),
            tokenClient,
            clock: () => new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero),
            delay: (_, _) => Task.CompletedTask);

        var failure = await Assert.ThrowsAsync<OAuthException>(() =>
            handler.LoginAsync(AuthMethodKind.OAuthDeviceCode, new ScriptedAuthPrompt(new Dictionary<string, string?>()), CancellationToken.None));

        Assert.Contains("access_denied", failure.Message);
        Assert.Contains("No credential was stored", failure.Message);
    }

    [Fact]
    public async Task DeviceCodeLogin_ExpiresWhenTheGrantLifetimeElapses()
    {
        var tokenClient = new FakeOAuthTokenClient
        {
            DeviceAuthorization = new OAuthDeviceAuthorization
            {
                DeviceCode = "unit-test-device-code",
                UserCode = "WXYZ-1234",
                VerificationUri = "https://example.invalid/device",
                Interval = TimeSpan.FromMilliseconds(1),
                ExpiresIn = TimeSpan.FromSeconds(30)
            }
        };

        // A clock that jumps past the grant lifetime on its second reading.
        var readings = 0;
        var start = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset Clock() => readings++ == 0 ? start : start.AddMinutes(5);

        var handler = new ProviderAuthHandler(
            TestDescriptors.OAuthProvider(deviceCode: true),
            tokenClient,
            clock: Clock,
            delay: (_, _) => Task.CompletedTask);

        var failure = await Assert.ThrowsAsync<OAuthException>(() =>
            handler.LoginAsync(AuthMethodKind.OAuthDeviceCode, new ScriptedAuthPrompt(new Dictionary<string, string?>()), CancellationToken.None));

        Assert.Contains("expired", failure.Message);
    }

    [Fact]
    public async Task DeviceCodeLogin_ReturnsNullWhenCancelledWhilePolling()
    {
        var tokenClient = new FakeOAuthTokenClient();
        using var cts = new CancellationTokenSource();

        var handler = new ProviderAuthHandler(
            TestDescriptors.OAuthProvider(deviceCode: true),
            tokenClient,
            clock: () => new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero),
            delay: (_, token) =>
            {
                cts.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        var credential = await handler.LoginAsync(
            AuthMethodKind.OAuthDeviceCode, new ScriptedAuthPrompt(new Dictionary<string, string?>()), cts.Token);

        Assert.Null(credential);
    }

    [Fact]
    public async Task Refresh_KeepsThePreviousRefreshTokenWhenTheServerOmitsIt()
    {
        var now = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
        var tokenClient = new FakeOAuthTokenClient
        {
            RefreshResult = new OAuthTokenResponse
            {
                AccessToken = AuthTestValues.RenewedAccessToken,
                RefreshToken = null,
                ExpiresIn = TimeSpan.FromHours(1)
            }
        };

        var handler = new ProviderAuthHandler(TestDescriptors.OAuthProvider(), tokenClient, clock: () => now);

        var refreshed = await handler.RefreshAsync(new StoredCredential
        {
            Kind = CredentialKind.OAuth,
            AccessToken = AuthTestValues.AccessToken,
            RefreshToken = AuthTestValues.RefreshToken,
            AccountLabel = "kept-account",
            ExpiresAtUtc = now.AddSeconds(-1)
        }, CancellationToken.None);

        Assert.Equal(AuthTestValues.RenewedAccessToken, refreshed.AccessToken);
        Assert.Equal(AuthTestValues.RefreshToken, refreshed.RefreshToken);
        Assert.Equal("kept-account", refreshed.AccountLabel);
    }

    [Fact]
    public async Task Refresh_WithoutARefreshTokenExplainsHowToRecover()
    {
        var handler = new ProviderAuthHandler(TestDescriptors.OAuthProvider(), new FakeOAuthTokenClient());

        var failure = await Assert.ThrowsAsync<OAuthException>(() => handler.RefreshAsync(new StoredCredential
        {
            Kind = CredentialKind.OAuth,
            AccessToken = AuthTestValues.AccessToken
        }, CancellationToken.None));

        Assert.Contains("auth login", failure.Message);
    }

    [Fact]
    public void NeedsRefresh_OnlyAppliesToExpiringOAuthCredentialsWithARefreshToken()
    {
        var now = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
        var handler = new ProviderAuthHandler(TestDescriptors.OAuthProvider(), new FakeOAuthTokenClient(), clock: () => now);

        Assert.False(handler.NeedsRefresh(StoredCredential.ForApiKey(AuthTestValues.ApiKey), now));

        Assert.False(handler.NeedsRefresh(new StoredCredential
        {
            Kind = CredentialKind.OAuth,
            AccessToken = AuthTestValues.AccessToken,
            ExpiresAtUtc = now.AddSeconds(-1)
        }, now));

        Assert.True(handler.NeedsRefresh(new StoredCredential
        {
            Kind = CredentialKind.OAuth,
            AccessToken = AuthTestValues.AccessToken,
            RefreshToken = AuthTestValues.RefreshToken,
            // Inside the one-minute renewal skew.
            ExpiresAtUtc = now.AddSeconds(30)
        }, now));

        Assert.False(handler.NeedsRefresh(new StoredCredential
        {
            Kind = CredentialKind.OAuth,
            AccessToken = AuthTestValues.AccessToken,
            RefreshToken = AuthTestValues.RefreshToken,
            ExpiresAtUtc = now.AddHours(2)
        }, now));
    }

    [Fact]
    public void StateComparison_IsExactAndRejectsEmptyValues()
    {
        var state = OAuthSecurity.CreateRandomToken();

        Assert.True(OAuthSecurity.StateMatches(state, state));
        Assert.False(OAuthSecurity.StateMatches(state, state + "x"));
        Assert.False(OAuthSecurity.StateMatches(state, null));
        Assert.False(OAuthSecurity.StateMatches(null, state));
        Assert.False(OAuthSecurity.StateMatches(string.Empty, string.Empty));
    }

    [Fact]
    public void PkceChallenge_IsTheS256DigestOfTheVerifier()
    {
        var verifier = OAuthSecurity.CreateRandomToken();
        var challenge = OAuthSecurity.CreateCodeChallenge(verifier);

        Assert.NotEqual(verifier, challenge);
        Assert.Equal(challenge, OAuthSecurity.CreateCodeChallenge(verifier));
        Assert.DoesNotContain("=", challenge);
        Assert.DoesNotContain("+", challenge);
        Assert.DoesNotContain("/", challenge);
    }

    [Fact]
    public async Task ApiKeyOnlyProvider_RejectsAnOAuthLogin()
    {
        var descriptor = ProviderAuthCatalog.Find("groq", AuthTestValues.NoOverlay);
        Assert.NotNull(descriptor);
        var handler = new ProviderAuthHandler(descriptor!, new FakeOAuthTokenClient());

        Assert.Equal(new[] { AuthMethodKind.ApiKey }, handler.SupportedMethods.ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.LoginAsync(AuthMethodKind.OAuthLoopback, ScriptedAuthPrompt.Cancelling(), CancellationToken.None));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            result[Uri.UnescapeDataString(pair[..separator])] = Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        return result;
    }

    private static async Task GetAsync(string url)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await client.GetAsync(url);
            _ = response.StatusCode;
        }
        catch (HttpRequestException)
        {
            // The listener may already have completed and torn down the connection.
        }
    }

    /// <summary>
    /// Drives the loopback flow end to end: when the authorization URL is presented, it plays the
    /// browser's part and calls the redirect URI back with a matching state.
    /// </summary>
    private sealed class CallbackDrivingPrompt : IAuthPrompt
    {
        public string? AuthorizationUrl { get; private set; }

        public Task<string?> PromptAsync(AuthFieldSpec field, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void PresentUrl(string caption, string url)
        {
            AuthorizationUrl = url;

            var query = ParseQuery(new Uri(url).Query);
            var redirectUri = query["redirect_uri"];
            var state = query["state"];

            _ = Task.Run(() => GetAsync(
                $"{redirectUri}?code=test-authorization-code&state={Uri.EscapeDataString(state)}"));
        }
    }

    /// <summary>Cancels the caller's token as soon as the flow starts waiting for a callback.</summary>
    private sealed class CancellingPrompt : IAuthPrompt
    {
        private readonly CancellationTokenSource _cts;

        public CancellingPrompt(CancellationTokenSource cts) => _cts = cts;

        public Task<string?> PromptAsync(AuthFieldSpec field, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public void Info(string message) => _cts.Cancel();

        public void Warn(string message)
        {
        }

        public void PresentUrl(string caption, string url)
        {
        }
    }
}
