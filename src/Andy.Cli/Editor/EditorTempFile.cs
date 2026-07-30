using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Andy.Cli.Editor;

/// <summary>
/// A short-lived, owner-only temporary file holding the editable text of the composer.
///
/// <para>The file lives inside its own randomly named directory so that permissions can be
/// locked down on both the directory (0700) and the file (0600) on Unix; on Windows the
/// per-user temp directory ACL applies. The <c>.md</c> suffix gives editors sensible
/// syntax highlighting and soft wrapping for prose prompts.</para>
///
/// <para>Deletion is idempotent and runs on every completion path, including a
/// <c>ProcessExit</c> hook so an abrupt exit while the editor is open still cleans up.</para>
/// </summary>
public sealed class EditorTempFile : IDisposable
{
    private const string DirectoryPrefix = "andy-editor-";
    private const string FileName = "andy-prompt.md";

    private readonly string _directory;
    private int _disposed;

    private EditorTempFile(string directory, string path)
    {
        _directory = directory;
        Path = path;
        try { AppDomain.CurrentDomain.ProcessExit += OnProcessExit; } catch { /* ignore */ }
    }

    /// <summary>Absolute path of the file handed to the editor.</summary>
    public string Path { get; }

    /// <summary>Current size in bytes, or -1 when the file no longer exists.</summary>
    public long Length
    {
        get
        {
            try
            {
                var info = new FileInfo(Path);
                return info.Exists ? info.Length : -1;
            }
            catch { return -1; }
        }
    }

    /// <summary>
    /// Create the private directory and file and write <paramref name="contents"/> as UTF-8
    /// without a BOM (a BOM would show up as stray characters in the edited prompt).
    /// </summary>
    /// <param name="contents">Initial text; may be empty for an empty prompt.</param>
    /// <param name="root">Temp root override (tests); defaults to the system temp directory.</param>
    public static EditorTempFile Create(string contents, string? root = null)
    {
        string baseDir = root ?? System.IO.Path.GetTempPath();
        string directory = System.IO.Path.Combine(baseDir, DirectoryPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        RestrictDirectory(directory);

        string path = System.IO.Path.Combine(directory, FileName);
        // Create empty first so the restrictive mode is in place before any content lands.
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        RestrictFile(path);

        File.WriteAllText(path, contents ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new EditorTempFile(directory, path);
    }

    /// <summary>Read the file back as UTF-8 text.</summary>
    public string ReadAllText() => File.ReadAllText(Path, Encoding.UTF8);

    /// <summary>Delete the file and its private directory. Safe to call repeatedly.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { AppDomain.CurrentDomain.ProcessExit -= OnProcessExit; } catch { /* ignore */ }
        Cleanup();
    }

    private void OnProcessExit(object? sender, EventArgs e) => Cleanup();

    private void Cleanup()
    {
        try { if (File.Exists(Path)) File.Delete(Path); } catch { /* best effort */ }
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }

    private static void RestrictDirectory(string directory)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch { /* filesystem may not support modes (e.g. some mounts) */ }
    }

    private static void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { /* filesystem may not support modes */ }
    }
}
