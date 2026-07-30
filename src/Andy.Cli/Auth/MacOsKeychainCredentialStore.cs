using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Auth;

/// <summary>
/// macOS backend, backed by the login keychain through <c>/usr/bin/security</c>.
///
/// Why the CLI and not the Security.framework API: keychain items carry an access-control
/// list naming the binaries allowed to read them without prompting. Going through
/// <c>security</c> for both writes and reads keeps that ACL stable, whereas a direct
/// P/Invoke from the .NET host would bind the ACL to whichever <c>dotnet</c> binary happened
/// to run, producing an "allow access?" dialog after every SDK update.
///
/// SECURITY: <c>security add-generic-password</c> only reads the password from stdin when
/// <c>-w</c> is the final argument (it prompts twice, hence the duplicated line). Passing
/// <c>-w &lt;value&gt;</c> would place the secret in the process arguments, which issue #284
/// forbids, so the argument order below is load-bearing and must not be "tidied up".
/// </summary>
public sealed class MacOsKeychainCredentialStore : ICredentialStore
{
    private const string SecurityBinary = "/usr/bin/security";

    /// <summary>Exit code returned by <c>security</c> when the item does not exist.</summary>
    private const int ItemNotFoundExitCode = 44;

    public string Name => "macOS Keychain";

    public CredentialSource Source => CredentialSource.CredentialStore;

    public bool IsAvailable =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
        && System.IO.File.Exists(SecurityBinary);

    public async Task<StoredCredential?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();

        var result = await SecretProcessRunner.RunAsync(
            SecurityBinary,
            new[] { "find-generic-password", "-a", key, "-s", CredentialKeys.ServiceName, "-w" },
            stdin: null,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode == ItemNotFoundExitCode)
        {
            return null;
        }

        if (result.ExitCode != 0)
        {
            throw new CredentialStoreException(
                $"Reading '{key}' from the macOS Keychain failed (exit {result.ExitCode}). {Summarize(result.StandardError)}");
        }

        return StoredCredential.Deserialize(result.StandardOutput);
    }

    public async Task SetAsync(string key, StoredCredential credential, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        ArgumentNullException.ThrowIfNull(credential);

        var payload = credential.Serialize();

        // -U updates an existing item in place, so re-login replaces the record atomically.
        // -w must stay last: that is what makes security read the value from stdin.
        var result = await SecretProcessRunner.RunAsync(
            SecurityBinary,
            new[]
            {
                "add-generic-password",
                "-a", key,
                "-s", CredentialKeys.ServiceName,
                "-l", $"{CredentialKeys.ServiceName} ({key})",
                "-D", "andy-cli provider credential",
                "-U",
                "-w"
            },
            SecretProcessRunner.RepeatedLine(payload, 2),
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new CredentialStoreException(
                $"Writing '{key}' to the macOS Keychain failed (exit {result.ExitCode}). "
                + Summarize(Redaction.Scrub(result.StandardError, payload, credential.ApiKey, credential.AccessToken, credential.RefreshToken)));
        }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();

        var result = await SecretProcessRunner.RunAsync(
            SecurityBinary,
            new[] { "delete-generic-password", "-a", key, "-s", CredentialKeys.ServiceName },
            stdin: null,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode == ItemNotFoundExitCode)
        {
            return false;
        }

        if (result.ExitCode != 0)
        {
            throw new CredentialStoreException(
                $"Removing '{key}' from the macOS Keychain failed (exit {result.ExitCode}). {Summarize(result.StandardError)}");
        }

        return true;
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new CredentialStoreUnavailableException(
                "The macOS Keychain helper (/usr/bin/security) is not available on this system.");
        }
    }

    // security writes item metadata (never the password) to stderr; keep one short line so a
    // failure is diagnosable without dumping keychain internals into the transcript.
    private static string Summarize(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return string.Empty;
        }

        var firstLine = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return firstLine.Length > 200 ? firstLine[..200] : firstLine;
    }

    internal static IReadOnlyList<string> ProbeArguments => new[] { "help" };
}
