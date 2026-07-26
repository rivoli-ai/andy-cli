using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Auth;

/// <summary>
/// The documented last-resort fallback for machines with no OS credential service.
///
/// It writes <c>~/.andy/credentials.json</c> with owner-only permissions (0600, in a 0700
/// directory). The payload is base64, which is <b>encoding, not encryption</b>: anyone who can
/// read the file can read the credential. That is why this backend is never selected
/// automatically - <see cref="CredentialStoreFactory"/> only returns it when the operator sets
/// <c>ANDY_CREDENTIAL_STORE=file</c>, and every write path surfaces
/// <see cref="PlaintextWarning"/>. Issue #284 explicitly forbids silently writing plaintext
/// secrets, so this opt-in must stay explicit.
/// </summary>
public sealed class FileFallbackCredentialStore : ICredentialStore
{
    /// <summary>Shown whenever this backend is used, so the trade-off is never invisible.</summary>
    public const string PlaintextWarning =
        "WARNING: credentials are stored in a plain file (no OS credential service is in use). "
        + "The file is readable by your user account and by root, and it is not encrypted. "
        + "Prefer environment variables injected by your secret manager for unattended machines.";

    private readonly string _filePath;
    private readonly object _gate = new();

    public FileFallbackCredentialStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".andy",
            "credentials.json"))
    {
    }

    /// <summary>Test seam: lets a test point the fallback at a temporary directory.</summary>
    public FileFallbackCredentialStore(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    public string Name => "file fallback (not encrypted)";

    public CredentialSource Source => CredentialSource.FileFallback;

    public bool IsAvailable => true;

    /// <summary>The path this store reads and writes. Non-secret; safe to print.</summary>
    public string FilePath => _filePath;

    public Task<StoredCredential?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var entries = Load();
            return Task.FromResult(entries.TryGetValue(key, out var payload)
                ? StoredCredential.Deserialize(payload)
                : null);
        }
    }

    public Task SetAsync(string key, StoredCredential credential, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(credential);

        lock (_gate)
        {
            var entries = Load();
            entries[key] = credential.Serialize();
            Save(entries);
        }

        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var entries = Load();
            if (!entries.Remove(key))
            {
                return Task.FromResult(false);
            }

            Save(entries);
            return Task.FromResult(true);
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // A corrupt store must not brick the CLI; recovery is documented in
            // docs/provider-auth.md (delete the file and log in again).
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (IOException ex)
        {
            throw new CredentialStoreException($"Could not read the credential file at '{_filePath}'.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new CredentialStoreException($"Could not read the credential file at '{_filePath}'.", ex);
        }
    }

    private void Save(Dictionary<string, string> entries)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            RestrictToOwner(directory, isDirectory: true);
        }

        // Write to a sibling temp file with the restrictive mode already applied, then move it
        // into place, so the credential file is never momentarily world-readable and a crash
        // mid-write cannot truncate the existing store.
        var tempPath = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });

        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            RestrictToOwner(tempPath, isDirectory: false);
            using var writer = new StreamWriter(stream);
            writer.Write(json);
        }

        File.Move(tempPath, _filePath, overwrite: true);
        RestrictToOwner(_filePath, isDirectory: false);
    }

    private static void RestrictToOwner(string path, bool isDirectory)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // NTFS inherits the user profile ACL; there is no chmod equivalent to apply here.
            return;
        }

        try
        {
            var mode = isDirectory
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best effort: a filesystem without POSIX modes (for example an exFAT volume)
            // cannot be tightened, and failing the login for that would be worse than the
            // warning the caller already prints.
        }
    }
}
