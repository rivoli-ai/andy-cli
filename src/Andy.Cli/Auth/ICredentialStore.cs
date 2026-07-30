using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Auth;

/// <summary>
/// Abstraction over the platform secret store that holds long-lived provider credentials.
///
/// Implementations must never place secret material on a command line (process arguments
/// are world-readable on every supported platform), never log it, and never include it in
/// an exception message. Backends that shell out move the secret over stdin/stdout only.
///
/// The store is a flat key/value map. Keys are non-secret, stable, and derived by
/// <see cref="CredentialKeys"/>; values are the opaque payload produced by
/// <see cref="StoredCredential.Serialize"/>. Exactly one entry per provider keeps logout
/// atomic - a single delete removes the API key, the refresh token, and the cached access
/// token together.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Short, non-secret backend name used in status output (for example "macOS Keychain").</summary>
    string Name { get; }

    /// <summary>The credential source this backend reports for resolved credentials.</summary>
    CredentialSource Source { get; }

    /// <summary>
    /// Whether the backend is usable on this machine right now. Implementations probe
    /// cheaply and cache nothing that would go stale within a process lifetime.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Reads a credential, or null when the key is absent.</summary>
    /// <exception cref="CredentialStoreUnavailableException">The backend cannot be used.</exception>
    Task<StoredCredential?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces a credential.</summary>
    /// <exception cref="CredentialStoreUnavailableException">The backend cannot be used.</exception>
    Task SetAsync(string key, StoredCredential credential, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a credential. Returns true when an entry existed and was removed. The delete
    /// is atomic with respect to the record's contents because the whole record is one entry.
    /// </summary>
    /// <exception cref="CredentialStoreUnavailableException">The backend cannot be used.</exception>
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// Builds the non-secret store keys used for provider credentials. Kept in one place so the
/// CLI, the TUI, and any future migration agree on the exact naming.
/// </summary>
public static class CredentialKeys
{
    /// <summary>The service/collection name used by every backend.</summary>
    public const string ServiceName = "andy-cli";

    /// <summary>The store key for a provider's credential record.</summary>
    public static string ForProvider(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException("Provider id is required.", nameof(providerId));
        }

        return "provider:" + providerId.Trim().ToLowerInvariant();
    }
}

/// <summary>
/// Raised when no credential store can be used. The message is written for a human
/// operator and lists the supported ways forward; it never contains secret material.
/// </summary>
public sealed class CredentialStoreUnavailableException : Exception
{
    public CredentialStoreUnavailableException(string message) : base(message)
    {
    }

    public CredentialStoreUnavailableException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// Raised when a credential store is present but the operation failed. Messages are scrubbed
/// through <see cref="Redaction.Scrub(string?, string?[])"/> before construction.
/// </summary>
public sealed class CredentialStoreException : Exception
{
    public CredentialStoreException(string message) : base(message)
    {
    }

    public CredentialStoreException(string message, Exception inner) : base(message, inner)
    {
    }
}
