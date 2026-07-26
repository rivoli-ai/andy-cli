using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Auth;

/// <summary>
/// Linux backend, backed by the freedesktop Secret Service (GNOME Keyring, KWallet's
/// Secret Service bridge, KeePassXC, ...) through libsecret's <c>secret-tool</c>.
///
/// SECURITY: <c>secret-tool store</c> reads the secret from stdin, so nothing sensitive ever
/// reaches the process arguments. The attribute pair (<c>service</c>, <c>account</c>) is
/// non-secret and is what identifies the item.
///
/// Availability requires both the binary and a session bus - a bare SSH session or a
/// container typically has neither, which is exactly the "headless system with no credential
/// service" case handled by <see cref="CredentialStoreFactory"/>.
/// </summary>
public sealed class SecretServiceCredentialStore : ICredentialStore
{
    private const string SecretToolBinary = "secret-tool";
    private const string ServiceAttribute = "service";
    private const string AccountAttribute = "account";

    public string Name => "Linux Secret Service (libsecret)";

    public CredentialSource Source => CredentialSource.CredentialStore;

    // Probing costs a process spawn, and availability is checked once per store operation
    // across every provider, so the answer is computed once per process.
    private bool? _available;

    public bool IsAvailable
    {
        get
        {
            if (_available.HasValue)
            {
                return _available.Value;
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                _available = false;
                return false;
            }

            // A Secret Service provider is reached over the D-Bus session bus. Without one,
            // secret-tool hangs or fails, so treat its absence as "no credential service".
            var bus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
            if (string.IsNullOrEmpty(bus))
            {
                _available = false;
                return false;
            }

            _available = SecretProcessRunner.CanRun(SecretToolBinary, new[] { "--help" });
            return _available.Value;
        }
    }

    public async Task<StoredCredential?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();

        var result = await SecretProcessRunner.RunAsync(
            SecretToolBinary,
            new[] { "lookup", ServiceAttribute, CredentialKeys.ServiceName, AccountAttribute, key },
            stdin: null,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            // secret-tool exits non-zero with no diagnostics when the item simply is not there.
            if (string.IsNullOrWhiteSpace(result.StandardError))
            {
                return null;
            }

            throw new CredentialStoreException(
                $"Reading '{key}' from the Secret Service failed (exit {result.ExitCode}). {Summarize(result.StandardError)}");
        }

        return StoredCredential.Deserialize(result.StandardOutput);
    }

    public async Task SetAsync(string key, StoredCredential credential, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        ArgumentNullException.ThrowIfNull(credential);

        var payload = credential.Serialize();

        var result = await SecretProcessRunner.RunAsync(
            SecretToolBinary,
            new[]
            {
                "store",
                "--label", $"{CredentialKeys.ServiceName} ({key})",
                ServiceAttribute, CredentialKeys.ServiceName,
                AccountAttribute, key
            },
            payload,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new CredentialStoreException(
                $"Writing '{key}' to the Secret Service failed (exit {result.ExitCode}). "
                + Summarize(Redaction.Scrub(result.StandardError, payload, credential.ApiKey, credential.AccessToken, credential.RefreshToken)));
        }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();

        // Read first so the caller learns whether anything was actually removed; secret-tool
        // clear reports success either way.
        var existing = await GetAsync(key, cancellationToken).ConfigureAwait(false);

        var result = await SecretProcessRunner.RunAsync(
            SecretToolBinary,
            new[] { "clear", ServiceAttribute, CredentialKeys.ServiceName, AccountAttribute, key },
            stdin: null,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0 && !string.IsNullOrWhiteSpace(result.StandardError))
        {
            throw new CredentialStoreException(
                $"Removing '{key}' from the Secret Service failed (exit {result.ExitCode}). {Summarize(result.StandardError)}");
        }

        return existing != null;
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new CredentialStoreUnavailableException(
                "No freedesktop Secret Service is reachable (libsecret's secret-tool and a D-Bus session bus are both required).");
        }
    }

    private static string Summarize(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return string.Empty;
        }

        var firstLine = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return firstLine.Length > 200 ? firstLine[..200] : firstLine;
    }

    internal static IReadOnlyList<string> ProbeArguments => new[] { "--help" };
}
