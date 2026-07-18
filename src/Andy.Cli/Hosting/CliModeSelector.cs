namespace Andy.Cli.Hosting;

/// <summary>
/// Selects the <see cref="CliMode"/> for a given set of command-line arguments.
///
/// This is a behaviour-preserving extraction of the mode-dispatch decision that
/// previously lived inline at the top of <c>Program.Main</c>. The branch order is
/// significant and mirrors the original exactly:
///   1. version   (--version, -v, version)
///   2. acp       (--acp)
///   3. headless  (run ...)  -- needs the structured exit-code contract
///   4. command   (first arg does not start with '-')
///   5. interactive (everything else, including no arguments)
/// Keeping the order identical means, for example, that the bare words
/// <c>version</c> and <c>run</c> are still recognised before the generic
/// non-dash "command" branch.
/// </summary>
public static class CliModeSelector
{
    public static CliMode Select(string[] args)
    {
        if (args.Length == 0)
        {
            return CliMode.Interactive;
        }

        var first = args[0];

        if (first == "--version" || first == "-v" || first == "version")
        {
            return CliMode.Version;
        }

        if (first == "--acp")
        {
            return CliMode.Acp;
        }

        if (first == "run")
        {
            return CliMode.Headless;
        }

        if (!first.StartsWith("-"))
        {
            return CliMode.Command;
        }

        return CliMode.Interactive;
    }
}
