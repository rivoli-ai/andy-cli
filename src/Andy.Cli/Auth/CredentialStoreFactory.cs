using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Auth;

/// <summary>
/// Selects the credential store backend for this machine.
///
/// Selection order:
/// 1. the <c>ANDY_CREDENTIAL_STORE</c> override, when set;
/// 2. the OS credential service for the current platform, when it is actually usable;
/// 3. <see cref="UnavailableCredentialStore"/>, which fails every operation with actionable
///    guidance rather than silently degrading to a plaintext file.
///
/// Step 3 is the security-critical one: issue #284 requires headless machines with no
/// credential service to fail loudly. The plaintext fallback exists but must be requested
/// explicitly with <c>ANDY_CREDENTIAL_STORE=file</c>.
///
/// CONFIG SEAM (#280): the override is read from the environment here. When the layered
/// configuration work lands, this is the single place that should instead consult the
/// resolved config layer (with the environment still winning), and nothing else in the auth
/// stack needs to change.
/// </summary>
public static class CredentialStoreFactory
{
    /// <summary>Environment variable that pins the backend. See the class remarks for values.</summary>
    public const string OverrideEnvVar = "ANDY_CREDENTIAL_STORE";

    /// <summary>Creates the backend for the current environment.</summary>
    public static ICredentialStore Create()
        => Create(Environment.GetEnvironmentVariable(OverrideEnvVar));

    /// <summary>Creates the backend for an explicit override value (null or empty selects "auto").</summary>
    public static ICredentialStore Create(string? overrideValue)
    {
        var requested = (overrideValue ?? string.Empty).Trim().ToLowerInvariant();

        switch (requested)
        {
            case "":
            case "auto":
                return CreatePlatformDefault();

            case "keychain":
            case "macos":
                return new MacOsKeychainCredentialStore();

            case "wincred":
            case "windows":
                return new WindowsCredentialManagerStore();

            case "secretservice":
            case "libsecret":
            case "linux":
                return new SecretServiceCredentialStore();

            case "file":
                return new FileFallbackCredentialStore();

            case "memory":
                return new InMemoryCredentialStore();

            case "none":
                return new UnavailableCredentialStore(
                    $"Credential storage is disabled ({OverrideEnvVar}=none). {EnvironmentOnlyGuidance}");

            default:
                return new UnavailableCredentialStore(
                    $"Unknown {OverrideEnvVar} value '{overrideValue}'. "
                    + "Supported values: auto, keychain, wincred, secretservice, file, memory, none.");
        }
    }

    private static ICredentialStore CreatePlatformDefault()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var keychain = new MacOsKeychainCredentialStore();
            if (keychain.IsAvailable)
            {
                return keychain;
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var credentialManager = new WindowsCredentialManagerStore();
            if (credentialManager.IsAvailable)
            {
                return credentialManager;
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var secretService = new SecretServiceCredentialStore();
            if (secretService.IsAvailable)
            {
                return secretService;
            }
        }

        return new UnavailableCredentialStore(NoServiceGuidance());
    }

    /// <summary>Guidance shown when credentials can only come from the environment.</summary>
    public const string EnvironmentOnlyGuidance =
        "Provider credentials can still be supplied through environment variables "
        + "(for example ANTHROPIC_API_KEY), which andy-cli always prefers and never persists.";

    /// <summary>
    /// The actionable, secret-free message shown when this machine has no credential service.
    /// Kept public so both the CLI and the TUI print exactly the same guidance.
    /// </summary>
    public static string NoServiceGuidance()
    {
        var platformHint = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? "Install a freedesktop Secret Service provider (for example gnome-keyring or KeePassXC) "
              + "and make sure a D-Bus session bus is running (DBUS_SESSION_BUS_ADDRESS)."
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "The macOS Keychain helper (/usr/bin/security) was not found; a login keychain is required."
                : RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? "The Windows Credential Manager could not be reached."
                    : "This platform has no supported credential service.";

        return "No OS credential service is available on this machine, so andy-cli will not store "
               + "your credential. Nothing was written."
               + Environment.NewLine
               + "  1. " + platformHint
               + Environment.NewLine
               + "  2. " + EnvironmentOnlyGuidance
               + Environment.NewLine
               + $"  3. If you accept storing the credential in a plain, unencrypted file readable by "
               + $"your account, set {OverrideEnvVar}=file and run the command again."
               + Environment.NewLine
               + "See docs/provider-auth.md for the full precedence and recovery guide.";
    }
}

/// <summary>
/// The "no credential service" backend. Reads report "nothing stored" so credential
/// resolution keeps working from environment variables, while writes and deletes fail with
/// actionable guidance instead of silently persisting a plaintext secret.
/// </summary>
public sealed class UnavailableCredentialStore : ICredentialStore
{
    private readonly string _guidance;

    public UnavailableCredentialStore(string guidance)
    {
        _guidance = guidance;
    }

    public string Name => "none (no credential service)";

    public CredentialSource Source => CredentialSource.None;

    public bool IsAvailable => false;

    /// <summary>The actionable message this store fails with. Non-secret.</summary>
    public string Guidance => _guidance;

    /// <summary>
    /// Reads succeed with "not found". A machine that only uses environment variables must not
    /// have every startup turned into an error just because it has no keychain.
    /// </summary>
    public Task<StoredCredential?> GetAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult<StoredCredential?>(null);

    public Task SetAsync(string key, StoredCredential credential, CancellationToken cancellationToken = default)
        => throw new CredentialStoreUnavailableException(_guidance);

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
        => throw new CredentialStoreUnavailableException(_guidance);
}
