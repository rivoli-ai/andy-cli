using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Andy.Cli.Tests.Editor;

/// <summary>
/// A deterministic stand-in for a real editor: a tiny script with no interactivity and no
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
/// <para>Both the directory and the script file name contain a space, so every test that
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

    /// <summary>Absolute path of the script. Contains spaces in the directory and the file name.</summary>
    public string ScriptPath { get; }

    /// <summary>The script path quoted the way a user would quote it in VISUAL/EDITOR.</summary>
    public string QuotedCommand => "\"" + ScriptPath + "\"";

    public static FakeEditor Create()
    {
        string root = Path.Combine(Path.GetTempPath(), "andy fake editor " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        string scriptPath;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            scriptPath = Path.Combine(root, "fake editor.cmd");
            File.WriteAllText(scriptPath, WindowsScript, new UTF8Encoding(false));
        }
        else
        {
            scriptPath = Path.Combine(root, "fake editor.sh");
            File.WriteAllText(scriptPath, UnixScript, new UTF8Encoding(false));
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

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

    // POSIX sh only: no bashisms, no eval. Quoting is preserved so arguments containing
    // spaces round trip through the dump.
    private const string UnixScript =
        "#!/bin/sh\n" +
        "dump=0\n" +
        "content=\"\"\n" +
        "code=0\n" +
        "file=\"\"\n" +
        "prev=\"\"\n" +
        "for a in \"$@\"; do\n" +
        "  case \"$prev\" in\n" +
        "    --content) content=\"$a\" ;;\n" +
        "    --exit) code=\"$a\" ;;\n" +
        "  esac\n" +
        "  case \"$a\" in\n" +
        "    --dump-args) dump=1 ;;\n" +
        "  esac\n" +
        "  prev=\"$a\"\n" +
        "  file=\"$a\"\n" +
        "done\n" +
        "if [ \"$dump\" = \"1\" ]; then\n" +
        "  : > \"$file\"\n" +
        "  for a in \"$@\"; do printf '%s\\n' \"$a\" >> \"$file\"; done\n" +
        "elif [ -n \"$content\" ]; then\n" +
        "  cat \"$content\" > \"$file\"\n" +
        "fi\n" +
        "exit \"$code\"\n";

    private const string WindowsScript =
        "@echo off\r\n" +
        "setlocal\r\n" +
        "set \"DUMP=0\"\r\n" +
        "set \"CONTENT=\"\r\n" +
        "set \"CODE=0\"\r\n" +
        "set \"LAST=\"\r\n" +
        "set \"ACC=%~dp0argdump.tmp\"\r\n" +
        "break>\"%ACC%\"\r\n" +
        ":parse\r\n" +
        "if \"%~1\"==\"\" goto done\r\n" +
        "set \"LAST=%~1\"\r\n" +
        ">>\"%ACC%\" echo(%~1\r\n" +
        "if \"%~1\"==\"--dump-args\" set \"DUMP=1\"\r\n" +
        "if \"%~1\"==\"--content\" set \"CONTENT=%~2\"\r\n" +
        "if \"%~1\"==\"--exit\" set \"CODE=%~2\"\r\n" +
        "shift\r\n" +
        "goto parse\r\n" +
        ":done\r\n" +
        "if \"%DUMP%\"==\"1\" goto dump\r\n" +
        "if not \"%CONTENT%\"==\"\" copy /y \"%CONTENT%\" \"%LAST%\" >nul\r\n" +
        "exit /b %CODE%\r\n" +
        ":dump\r\n" +
        "copy /y \"%ACC%\" \"%LAST%\" >nul\r\n" +
        "exit /b %CODE%\r\n";
}
