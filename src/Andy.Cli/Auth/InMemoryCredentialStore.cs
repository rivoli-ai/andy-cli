using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Auth;

/// <summary>
/// A process-local credential store. This is the deterministic backend used by unit and
/// integration tests on CI (issue #284 requires a fake store so tests never touch the
/// developer's real keychain), and it is also what <c>ANDY_CREDENTIAL_STORE=memory</c>
/// selects for a throwaway sandbox.
///
/// Nothing is persisted: the contents vanish with the process. That makes it safe on CI but
/// useless for real logins, which is why <see cref="CredentialStoreFactory"/> only selects it
/// on explicit request.
/// </summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<string, string> _entries = new(StringComparer.Ordinal);

    public string Name => "in-memory (ephemeral)";

    public CredentialSource Source => CredentialSource.CredentialStore;

    public bool IsAvailable => true;

    /// <summary>Non-secret key listing, for tests that assert on store contents.</summary>
    public IReadOnlyList<string> Keys => _entries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    public Task<StoredCredential?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_entries.TryGetValue(key, out var payload)
            ? StoredCredential.Deserialize(payload)
            : null);
    }

    public Task SetAsync(string key, StoredCredential credential, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(credential);
        _entries[key] = credential.Serialize();
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_entries.TryRemove(key, out _));
    }
}
