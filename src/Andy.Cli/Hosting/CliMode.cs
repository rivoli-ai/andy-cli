namespace Andy.Cli.Hosting;

/// <summary>
/// The top-level execution mode the CLI runs in, selected from the process
/// command-line arguments. Extracted from Program.Main so the (previously
/// inline) dispatch decision can be unit tested in isolation.
/// </summary>
public enum CliMode
{
    /// <summary>Print the version string and exit (--version, -v, version).</summary>
    Version,

    /// <summary>Run as an ACP (Agent Client Protocol) server (--acp).</summary>
    Acp,

    /// <summary>Non-interactive headless run (run ...).</summary>
    Headless,

    /// <summary>One-shot command dispatch (model, tools, permissions, help, ...).</summary>
    Command,

    /// <summary>Interactive terminal UI (the default when no mode-selecting argument is present).</summary>
    Interactive
}
