using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Auth;

/// <summary>
/// Windows backend, backed by the Windows Credential Manager (generic credentials) through
/// advapi32.
///
/// SECURITY: the credential blob is passed as an in-process buffer, so the secret never
/// reaches a command line, a temp file, or a log. The buffer is zeroed before it is freed.
/// Credentials are stored with CRED_PERSIST_LOCAL_MACHINE so they do not roam to other
/// machines with the user's profile.
/// </summary>
public sealed class WindowsCredentialManagerStore : ICredentialStore
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    /// <summary>Windows caps a generic credential blob at 2560 bytes.</summary>
    private const int MaxBlobBytes = 2560;

    public string Name => "Windows Credential Manager";

    public CredentialSource Source => CredentialSource.CredentialStore;

    public bool IsAvailable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public Task<StoredCredential?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        cancellationToken.ThrowIfCancellationRequested();

        var target = TargetName(key);
        if (!CredReadW(target, CRED_TYPE_GENERIC, 0, out var handle))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ERROR_NOT_FOUND)
            {
                return Task.FromResult<StoredCredential?>(null);
            }

            throw new CredentialStoreException(
                $"Reading '{key}' from the Windows Credential Manager failed (error {error}).");
        }

        try
        {
            var native = Marshal.PtrToStructure<NativeCredential>(handle);
            if (native.CredentialBlob == IntPtr.Zero || native.CredentialBlobSize == 0)
            {
                return Task.FromResult<StoredCredential?>(null);
            }

            var bytes = new byte[native.CredentialBlobSize];
            Marshal.Copy(native.CredentialBlob, bytes, 0, bytes.Length);
            try
            {
                return Task.FromResult(StoredCredential.Deserialize(Encoding.UTF8.GetString(bytes)));
            }
            finally
            {
                Array.Clear(bytes);
            }
        }
        finally
        {
            CredFree(handle);
        }
    }

    public Task SetAsync(string key, StoredCredential credential, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(credential);

        var payload = credential.Serialize();
        var bytes = Encoding.UTF8.GetBytes(payload);
        if (bytes.Length > MaxBlobBytes)
        {
            throw new CredentialStoreException(
                $"The credential for '{key}' is too large for the Windows Credential Manager "
                + $"({bytes.Length} bytes; the limit is {MaxBlobBytes}).");
        }

        var blob = Marshal.AllocHGlobal(bytes.Length);
        var targetPtr = Marshal.StringToHGlobalUni(TargetName(key));
        var userNamePtr = Marshal.StringToHGlobalUni(CredentialKeys.ServiceName);
        var commentPtr = Marshal.StringToHGlobalUni("andy-cli provider credential");

        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);

            var native = new NativeCredential
            {
                Flags = 0,
                Type = CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                Comment = commentPtr,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                TargetAlias = IntPtr.Zero,
                UserName = userNamePtr
            };

            if (!CredWriteW(ref native, 0))
            {
                throw new CredentialStoreException(
                    $"Writing '{key}' to the Windows Credential Manager failed (error {Marshal.GetLastWin32Error()}).");
            }

            return Task.CompletedTask;
        }
        finally
        {
            // Zero the secret before releasing the buffer so it does not linger in the heap.
            for (var i = 0; i < bytes.Length; i++)
            {
                Marshal.WriteByte(blob, i, 0);
            }

            Array.Clear(bytes);
            Marshal.FreeHGlobal(blob);
            Marshal.FreeHGlobal(targetPtr);
            Marshal.FreeHGlobal(userNamePtr);
            Marshal.FreeHGlobal(commentPtr);
        }
    }

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        cancellationToken.ThrowIfCancellationRequested();

        if (CredDeleteW(TargetName(key), CRED_TYPE_GENERIC, 0))
        {
            return Task.FromResult(true);
        }

        var error = Marshal.GetLastWin32Error();
        if (error == ERROR_NOT_FOUND)
        {
            return Task.FromResult(false);
        }

        throw new CredentialStoreException(
            $"Removing '{key}' from the Windows Credential Manager failed (error {error}).");
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new CredentialStoreUnavailableException(
                "The Windows Credential Manager is only available on Windows.");
        }
    }

    // Namespaced so andy-cli entries are obvious in the Credential Manager UI and cannot
    // collide with another application's generic credentials.
    internal static string TargetName(string key) => $"{CredentialKeys.ServiceName}:{key}";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, uint type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, uint type, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = false)]
    private static extern void CredFree(IntPtr buffer);
}
