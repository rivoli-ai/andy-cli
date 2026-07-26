using System;
using System.Threading.Tasks;
using Andy.Cli.Auth;
using Xunit;

namespace Andy.Cli.Tests.Auth;

/// <summary>
/// Opt-in coverage for the real OS credential services.
///
/// These tests write to (and then delete from) the machine's actual keychain, credential
/// manager, or secret service, so they are SKIPPED unless
/// <c>ANDY_AUTH_REAL_STORE_TESTS=1</c> is set. CI and ordinary local runs never touch the
/// developer's keychain; the deterministic coverage lives in the other files in this folder,
/// which run against <see cref="InMemoryCredentialStore"/>.
///
/// To run them locally:
///   ANDY_AUTH_REAL_STORE_TESTS=1 dotnet test --filter FullyQualifiedName~RealCredentialStoreTests
/// On macOS the keychain may prompt for permission the first time.
/// </summary>
[Collection("EnvironmentVariableTests")]
public class RealCredentialStoreTests
{
    private const string OptInEnvVar = "ANDY_AUTH_REAL_STORE_TESTS";

    /// <summary>A key namespace that cannot collide with a real provider entry.</summary>
    private static string TestKey => "provider:andy-cli-selftest-" + Guid.NewGuid().ToString("N")[..8];

    private static bool OptedIn =>
        Environment.GetEnvironmentVariable(OptInEnvVar) is "1" or "true";

    [Fact]
    public async Task PlatformStore_RoundTripsAndDeletesACredential()
    {
        // Opt-in only: without the environment variable this is a no-op, so CI and ordinary
        // local runs never write to the developer's real credential service. xUnit has no
        // first-class skip without an extra package, and this repository pins its package graph
        // with packages.lock.json, so an early return is the low-risk equivalent.
        if (!OptedIn)
        {
            return;
        }

        var store = CredentialStoreFactory.Create();
        if (!store.IsAvailable)
        {
            return;
        }

        var key = TestKey;
        try
        {
            Assert.Null(await store.GetAsync(key));

            await store.SetAsync(key, StoredCredential.ForApiKey(AuthTestValues.ApiKey, "selftest"));

            var loaded = await store.GetAsync(key);
            Assert.Equal(AuthTestValues.ApiKey, loaded?.ApiKey);
            Assert.Equal("selftest", loaded?.AccountLabel);

            // Re-writing must replace the record rather than fail on a duplicate.
            await store.SetAsync(key, StoredCredential.ForApiKey(AuthTestValues.OtherApiKey, "selftest-2"));
            Assert.Equal(AuthTestValues.OtherApiKey, (await store.GetAsync(key))?.ApiKey);

            Assert.True(await store.DeleteAsync(key));
            Assert.Null(await store.GetAsync(key));
            Assert.False(await store.DeleteAsync(key));
        }
        finally
        {
            try
            {
                await store.DeleteAsync(key);
            }
            catch (Exception)
            {
                // Cleanup is best effort; the assertions above already removed the entry.
            }
        }
    }

    [Fact]
    public async Task PlatformStore_RoundTripsAnOAuthRecordWithBothTokens()
    {
        // Opt-in only: without the environment variable this is a no-op, so CI and ordinary
        // local runs never write to the developer's real credential service. xUnit has no
        // first-class skip without an extra package, and this repository pins its package graph
        // with packages.lock.json, so an early return is the low-risk equivalent.
        if (!OptedIn)
        {
            return;
        }

        var store = CredentialStoreFactory.Create();
        if (!store.IsAvailable)
        {
            return;
        }

        var key = TestKey;
        try
        {
            await store.SetAsync(key, new StoredCredential
            {
                Kind = CredentialKind.OAuth,
                AccessToken = AuthTestValues.AccessToken,
                RefreshToken = AuthTestValues.RefreshToken,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
                AccountLabel = "selftest"
            });

            var loaded = await store.GetAsync(key);
            Assert.Equal(CredentialKind.OAuth, loaded?.Kind);
            Assert.Equal(AuthTestValues.AccessToken, loaded?.AccessToken);
            Assert.Equal(AuthTestValues.RefreshToken, loaded?.RefreshToken);

            // Logout must take both tokens away in one operation.
            Assert.True(await store.DeleteAsync(key));
            Assert.Null(await store.GetAsync(key));
        }
        finally
        {
            try
            {
                await store.DeleteAsync(key);
            }
            catch (Exception)
            {
                // Best effort.
            }
        }
    }
}
