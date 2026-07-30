using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Andy.Cli.Editor;
using Xunit;

namespace Andy.Cli.Tests.Editor;

/// <summary>
/// The temporary file that carries the prompt to the editor (issue #287): restrictive
/// permissions, exact content, and removal on every path.
/// </summary>
public class EditorTempFileTests : IDisposable
{
    private readonly string _root;

    public EditorTempFileTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "andy-temp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Create_WritesTheContentVerbatim()
    {
        const string text = "line one\n\nthree\ncafé 你好 \U0001F600";
        using var temp = EditorTempFile.Create(text, _root);

        Assert.True(File.Exists(temp.Path));
        Assert.Equal(text, temp.ReadAllText());
    }

    [Fact]
    public void Create_WritesUtf8WithoutABom()
    {
        using var temp = EditorTempFile.Create("é", _root);

        var bytes = File.ReadAllBytes(temp.Path);
        Assert.Equal(new byte[] { 0xC3, 0xA9 }, bytes);
    }

    [Fact]
    public void Create_AcceptsEmptyContent()
    {
        using var temp = EditorTempFile.Create("", _root);

        Assert.True(File.Exists(temp.Path));
        Assert.Equal(0, temp.Length);
        Assert.Equal("", temp.ReadAllText());
    }

    [Fact]
    public void File_UsesAMarkdownSuffix_ForEditorHighlighting()
    {
        using var temp = EditorTempFile.Create("x", _root);
        Assert.EndsWith(".md", temp.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void EachCreate_UsesItsOwnPrivateDirectory()
    {
        using var a = EditorTempFile.Create("a", _root);
        using var b = EditorTempFile.Create("b", _root);

        Assert.NotEqual(a.Path, b.Path);
        Assert.NotEqual(Path.GetDirectoryName(a.Path), Path.GetDirectoryName(b.Path));
    }

    [Fact]
    public void FileAndDirectory_AreOwnerOnly()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return; // ACL-based; no Unix mode

        using var temp = EditorTempFile.Create("secret prompt", _root);

        var fileMode = File.GetUnixFileMode(temp.Path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, fileMode);

        var dirMode = File.GetUnixFileMode(Path.GetDirectoryName(temp.Path)!);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            dirMode);
    }

    [Fact]
    public void Dispose_RemovesTheFileAndItsDirectory()
    {
        var temp = EditorTempFile.Create("x", _root);
        string path = temp.Path;
        string dir = Path.GetDirectoryName(path)!;

        temp.Dispose();

        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var temp = EditorTempFile.Create("x", _root);
        temp.Dispose();
        temp.Dispose();
        Assert.False(File.Exists(temp.Path));
    }

    [Fact]
    public void Dispose_SurvivesAnAlreadyDeletedFile()
    {
        var temp = EditorTempFile.Create("x", _root);
        File.Delete(temp.Path);

        temp.Dispose(); // must not throw

        Assert.Equal(-1, temp.Length);
    }

    [Fact]
    public void Length_ReflectsWhatTheEditorWrote()
    {
        using var temp = EditorTempFile.Create("", _root);
        File.WriteAllText(temp.Path, "12345", new UTF8Encoding(false));

        Assert.Equal(5, temp.Length);
    }
}
