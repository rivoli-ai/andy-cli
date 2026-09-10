using System;
using System.IO;
using System.Text;

namespace Andy.Cli.Tests.Editor;

/// <summary>
/// A deterministic stand-in for a real editor: a tiny .NET program with no interactivity and no
/// timing dependence. It always treats the LAST argument as the file to edit, exactly as
/// Andy appends it.
///
/// <list type="bullet">
///   <item><description><c>--dump-args</c>: overwrite the edited file with the argument vector
///     the script actually received, one argument per line. Because the service reads that file
///     back as the edited prompt, a test can assert the exact argv without any side channel -
///     which is how "arguments are passed without invoking a shell" is proven (an argument such
///     as <c>$HOME</c> or <c>*</c> comes back verbatim).</description></item>
///   <item><description><c>--content &lt;path&gt;</c>: copy that file over the edited file.</description></item>
///   <item><description><c>--exit &lt;code&gt;</c>: exit with that status (128+N models a signal death).</description></item>
/// </list>
///
/// <para>Both the directory and the assembly file name contain a space, so every test that
/// launches it also covers paths and commands containing spaces.</para>
/// </summary>
internal sealed class FakeEditor : IDisposable
{
    private readonly string _root;

    private FakeEditor(string root, string scriptPath)
    {
        _root = root;
        ScriptPath = scriptPath;
    }

    /// <summary>Absolute path of the fixture assembly. Contains spaces in the directory and the file name.</summary>
    public string ScriptPath { get; }

    /// <summary>The dotnet host and assembly path quoted as an editor command.</summary>
    public string QuotedCommand => "\"" + (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") + "\" \"" + ScriptPath + "\"";

    public static FakeEditor Create()
    {
        string root = Path.Combine(Path.GetTempPath(), "andy fake editor " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        string scriptPath = Path.Combine(root, "fake editor.dll");
        File.Copy(typeof(Andy.Cli.FakeEditor.EditorFixture).Assembly.Location, scriptPath);
        File.WriteAllText(Path.ChangeExtension(scriptPath, ".runtimeconfig.json"),
            """{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}""");

        return new FakeEditor(root, scriptPath);
    }

    /// <summary>Stage <paramref name="content"/> and return the argument pair that installs it.</summary>
    public string ContentFile(string content)
    {
        string path = Path.Combine(_root, "content-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

}
