using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Auth;
using Andy.Cli.Services;

namespace Andy.Cli.Tests.Auth;

/// <summary>
/// Shared helpers for the provider-auth tests.
///
/// Every test here runs against <see cref="InMemoryCredentialStore"/> or a temporary directory.
/// Nothing in this file may touch the developer's real keychain, credential manager, or secret
/// service - the real-store coverage lives in RealCredentialStoreTests behind an opt-in
/// environment variable.
///
/// The literal values below are deliberately obvious placeholders (no vendor key prefixes, no
/// high-entropy strings) so the repository's secret scanner has nothing to flag.
/// </summary>
internal static class AuthTestValues
{
    public const string ApiKey = "unit-test-credential-value-one";
    public const string OtherApiKey = "unit-test-credential-value-two";
    public const string EnvApiKey = "unit-test-environment-value-one";
    public const string AccessToken = "unit-test-access-token-value";
    public const string RefreshToken = "unit-test-refresh-token-value";
    public const string RenewedAccessToken = "unit-test-renewed-access-token";

    /// <summary>An overlay path that never exists, so the catalog is deterministic in tests.</summary>
    public static string NoOverlay => Path.Combine(Path.GetTempPath(), "andy-cli-tests-missing-provider-auth.json");
}

/// <summary>
/// Sets environment variables for the duration of a test and restores the previous values,
/// including the <c>ProviderRegistry.StoredCredentialProbe</c> hook that the auth bootstrap
/// installs process-wide.
/// </summary>
internal sealed class EnvironmentScope : IDisposable
{
    private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);
    private readonly Func<string, bool>? _previousProbe;

    public EnvironmentScope(params string[] namesToClear)
    {
        _previousProbe = ProviderRegistry.StoredCredentialProbe;

        foreach (var name in namesToClear)
        {
            Set(name, null);
        }
    }

    public EnvironmentScope Set(string name, string? value)
    {
        if (!_previous.ContainsKey(name))
        {
            _previous[name] = Environment.GetEnvironmentVariable(name);
        }

        Environment.SetEnvironmentVariable(name, value);
        return this;
    }

    public void Dispose()
    {
        foreach (var pair in _previous)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }

        ProviderRegistry.StoredCredentialProbe = _previousProbe;
    }

    /// <summary>Clears every provider API-key variable so a test starts from a known state.</summary>
    public static EnvironmentScope WithNoProviderKeys()
    {
        var scope = new EnvironmentScope();
        foreach (var provider in ProviderRegistry.All)
        {
            foreach (var name in provider.ApiKeyEnvVars)
            {
                scope.Set(name, null);
            }
        }

        return scope;
    }
}

/// <summary>A temporary directory removed on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "andy-cli-auth-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Test cleanup only.
        }
    }
}

/// <summary>
/// A scripted <see cref="IAuthPrompt"/>: answers each requested field from a queue and records
/// everything shown to the user, so a test can assert that no secret was ever displayed.
/// </summary>
internal sealed class ScriptedAuthPrompt : IAuthPrompt
{
    private readonly Dictionary<string, string?> _answers;

    public ScriptedAuthPrompt(Dictionary<string, string?> answers)
    {
        _answers = answers;
    }

    public static ScriptedAuthPrompt ForApiKey(string? apiKey, string? accountLabel = null)
        => new(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["api_key"] = apiKey,
            ["account_label"] = accountLabel
        });

    /// <summary>A prompt that cancels immediately (the user pressed Escape).</summary>
    public static ScriptedAuthPrompt Cancelling() => ForApiKey(null);

    public List<string> Shown { get; } = new();

    public List<AuthFieldSpec> RequestedFields { get; } = new();

    public Task<string?> PromptAsync(AuthFieldSpec field, CancellationToken cancellationToken)
    {
        RequestedFields.Add(field);
        return Task.FromResult(_answers.TryGetValue(field.Name, out var value) ? value : null);
    }

    public void Info(string message) => Shown.Add(message);

    public void Warn(string message) => Shown.Add(message);

    public void PresentUrl(string caption, string url) => Shown.Add($"{caption}: {url}");
}

/// <summary>
/// A scripted authorization server. Every OAuth test drives this instead of the network, so
/// state validation, cancellation, timeout, refresh, and logout are deterministic on CI.
/// </summary>
internal sealed class FakeOAuthTokenClient : IOAuthTokenClient
{
    private readonly Queue<DevicePollResult> _devicePolls = new();

    public string? LastAuthorizationCode { get; private set; }

    public string? LastCodeVerifier { get; private set; }

    public string? LastRedirectUri { get; private set; }

    public string? LastRefreshToken { get; private set; }

    public int RefreshCallCount { get; private set; }

    public OAuthTokenResponse ExchangeResult { get; set; } = new()
    {
        AccessToken = AuthTestValues.AccessToken,
        RefreshToken = AuthTestValues.RefreshToken,
        ExpiresIn = TimeSpan.FromHours(1),
        AccountLabel = "test-account"
    };

    public OAuthTokenResponse RefreshResult { get; set; } = new()
    {
        AccessToken = AuthTestValues.RenewedAccessToken,
        RefreshToken = AuthTestValues.RefreshToken,
        ExpiresIn = TimeSpan.FromHours(1),
        AccountLabel = "test-account"
    };

    public Exception? RefreshFailure { get; set; }

    public OAuthDeviceAuthorization DeviceAuthorization { get; set; } = new()
    {
        DeviceCode = "unit-test-device-code",
        UserCode = "ABCD-EFGH",
        VerificationUri = "https://example.invalid/device",
        Interval = TimeSpan.FromMilliseconds(1),
        ExpiresIn = TimeSpan.FromMinutes(5)
    };

    public void EnqueueDevicePoll(DevicePollResult result) => _devicePolls.Enqueue(result);

    public Task<OAuthTokenResponse> ExchangeAuthorizationCodeAsync(
        OAuthEndpointConfig config,
        string code,
        string redirectUri,
        string? codeVerifier,
        CancellationToken cancellationToken)
    {
        LastAuthorizationCode = code;
        LastRedirectUri = redirectUri;
        LastCodeVerifier = codeVerifier;
        return Task.FromResult(ExchangeResult);
    }

    public Task<OAuthDeviceAuthorization> StartDeviceAuthorizationAsync(
        OAuthEndpointConfig config,
        CancellationToken cancellationToken)
        => Task.FromResult(DeviceAuthorization);

    public Task<DevicePollResult> PollDeviceTokenAsync(
        OAuthEndpointConfig config,
        string deviceCode,
        CancellationToken cancellationToken)
        => Task.FromResult(_devicePolls.Count > 0
            ? _devicePolls.Dequeue()
            : new DevicePollResult(DevicePollStatus.Pending, null, "authorization_pending"));

    public Task<OAuthTokenResponse> RefreshAsync(
        OAuthEndpointConfig config,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        RefreshCallCount++;
        LastRefreshToken = refreshToken;
        if (RefreshFailure != null)
        {
            throw RefreshFailure;
        }

        return Task.FromResult(RefreshResult);
    }
}

/// <summary>A credential store that always reports the machine has no credential service.</summary>
internal sealed class ThrowingCredentialStore : ICredentialStore
{
    public string Name => "test unavailable store";

    public CredentialSource Source => CredentialSource.None;

    public bool IsAvailable => false;

    public Task<StoredCredential?> GetAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult<StoredCredential?>(null);

    public Task SetAsync(string key, StoredCredential credential, CancellationToken cancellationToken = default)
        => throw new CredentialStoreUnavailableException("no credential service");

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
        => throw new CredentialStoreUnavailableException("no credential service");
}

/// <summary>
/// Builds a descriptor with OAuth enabled, without needing an overlay file on disk. Used by the
/// OAuth flow tests, which must not depend on any provider actually shipping OAuth today.
/// </summary>
internal static class TestDescriptors
{
    public static ProviderAuthDescriptor OAuthProvider(
        string providerId = "openai",
        int callbackPort = 0,
        bool deviceCode = false)
        => new()
        {
            ProviderId = providerId,
            DisplayName = "Test Provider",
            EnvironmentVariables = new[] { "ANDY_TEST_PROVIDER_API_KEY" },
            RequiresCredential = true,
            ApiKeyFields = Array.Empty<AuthFieldSpec>(),
            OAuth = new OAuthEndpointConfig
            {
                ClientId = "test-client-id",
                AuthorizationEndpoint = "https://example.invalid/authorize",
                TokenEndpoint = "https://example.invalid/token",
                DeviceAuthorizationEndpoint = deviceCode ? "https://example.invalid/device" : null,
                Scopes = new List<string> { "read" },
                UsePkce = true,
                CallbackPort = callbackPort,
                CallbackPath = "/andy-cli/callback"
            }
        };
}
