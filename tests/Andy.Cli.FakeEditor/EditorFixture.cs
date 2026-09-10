using System.Text;

namespace Andy.Cli.FakeEditor;

/// <summary>Deterministic editor process used by the CLI integration tests on every OS.</summary>
public static class EditorFixture
{
    public static int Main(string[] args)
    {
        if (args.Length == 0) return 2;
        bool dump = false;
        string? content = null;
        int exitCode = 0;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--dump-args") dump = true;
            else if (args[i] == "--content") content = args[++i];
            else if (args[i] == "--exit") exitCode = int.Parse(args[++i]);
        }
        if (dump) File.WriteAllText(args[^1], string.Join("\n", args) + "\n", new UTF8Encoding(false));
        else if (content is not null) File.Copy(content, args[^1], overwrite: true);
        return exitCode;
    }
}
