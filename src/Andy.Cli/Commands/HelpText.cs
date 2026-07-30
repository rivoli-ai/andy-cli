namespace Andy.Cli.Commands;

/// <summary>
/// Single source of truth for the /help output. The interactive slash handler and the
/// command palette's Help entry previously carried two hand-maintained copies of this
/// markdown, which drifted (missing /mcp, /theme, /permissions subcommands, and newly
/// added commands). Both now render this text, and HelpTextTests asserts that every
/// command in <see cref="SlashCommandCatalog"/> appears here, so a new command cannot
/// be registered without also being documented. ASCII only (project rule).
/// </summary>
public static class HelpText
{
    /// <summary>The full interactive-mode help, rendered by /help and the palette Help entry.</summary>
    public static string InteractiveHelpMarkdown() =>
        "# Andy CLI Help\n\n" +
        "## Keyboard Shortcuts:\n" +
        "- **Ctrl+P** (Cmd+P on Mac): Open command palette\n" +
        "- **Ctrl+]**: Toggle scroll mode (Feed / Prompt History)\n" +
        "- **Ctrl+D**: Quit application\n" +
        "- **F2**: Toggle HUD (performance overlay)\n" +
        "- **F3**: Toggle mouse capture (OFF by default = plain-drag native selection and copy; ON = mouse-wheel scroll)\n" +
        "- **Click while scrolled up**: releases mouse capture so plain click-drag selects text; capture is restored when back at the bottom\n" +
        "- **Ctrl+O**: Expand/collapse tool output detail (view-only; does not affect a running turn)\n" +
        "- **ESC**: Quit application (closes the command palette first if open)\n" +
        "- **Page Up/Down**: Scroll chat history\n" +
        "- **Up/Down**: Navigate multi-line text or prompt history (when in History mode)\n" +
        "- **Ctrl+A/E**: Jump to start/end of current line\n" +
        "- **Home/End**: Start/end of line (Ctrl: whole text)\n" +
        "- **Ctrl+K**: Delete from cursor to end of line\n" +
        "- **Ctrl+U**: Delete from start of line to cursor\n\n" +
        "## Scroll Modes:\n" +
        "- **Feed Mode** (default): Blue indicator on left. PageUp/PageDown scrolls conversation.\n" +
        "- **Prompt History Mode**: Orange indicator on left. Up/Down navigates previous messages. Shows message counter (e.g., 5/12).\n\n" +
        "## Commands:\n" +
        "### General Commands:\n" +
        "- **/exit**, **/bye**, **/quit**: Exit the application\n" +
        "- **exit**, **bye**, **quit**: Exit the application (without slash)\n" +
        "- **/clear**: Clear conversation history\n" +
        "- **/restart**: Restart the session (fresh conversation context, counters, and prompt history)\n" +
        "- **/sessions**: List saved sessions that can be resumed\n" +
        "- **/resume [session-id]**: Resume a saved session (most recent when no id is given)\n" +
        "- **/undo**: Revert the file changes made by the last turn (Git workspaces only)\n" +
        "- **/redo**: Reapply the turn reverted by the last /undo\n" +
        "- **/help**: Show this help message\n\n" +
        "### Session Archive Commands:\n" +
        "- **/session export [id] [--out path] [--markdown] [--tools] [--metadata]**: Write a portable archive (or Markdown transcript)\n" +
        "- **/session import <path> [--dry-run] [--title t]**: Validate and install an exported archive\n" +
        "- **/session fork [id] [--at turn] [--title t]**: Branch a session (--at forks the history before that turn)\n" +
        "- **/session rename [id] <title>**: Title a session so it stays discoverable\n" +
        "- **/session stats [id] [--all]**: Token and estimated-cost totals\n\n" +
        "### Model Commands:\n" +
        "- **/model list**: Show available models\n" +
        "- **/model switch <provider>**: Change provider\n" +
        "- **/model info**: Show current model details\n" +
        "- **/model test [prompt]**: Test current model\n\n" +
        "### Theme Commands:\n" +
        "- **/theme**: List available themes and the current one\n" +
        "- **/theme <name>**: Switch the UI theme (e.g. dark, dracula, nord)\n" +
        "- **/theme transparent on|off**: Toggle the transparent background\n\n" +
        "### Tool Commands:\n" +
        "- **/tools list [category]**: List available tools\n" +
        "- **/tools info <tool_name>**: Show tool details\n" +
        "- **/tools execute <tool_name> [params]**: Run a tool\n" +
        "- **/mcp list**: List configured MCP servers\n" +
        "- **/mcp status**: Show MCP connection state and registered tools\n" +
        "- **/lsp status**: Show configured language servers and their state\n" +
        "- **/lsp restart [id]**: Restart language servers\n\n" +
        "### Permission Commands:\n" +
        "- **/permissions**: Open the interactive permission rules manager\n" +
        "- **/permissions list**: List effective permission rules by layer\n" +
        "- **/permissions allow|ask|deny <tool[(spec)]> [--scope user|project|local]**: Persist a rule\n" +
        "- **/permissions revoke <tool[(spec)]> [--scope S]**: Remove a persisted rule\n" +
        "- **/permissions reset**: Delete the user/project/local rule files (back to defaults)\n" +
        "- **/permissions path**: Show the rule file locations\n\n" +
        "### Formatter Commands:\n" +
        "- **/formatters status <file>**: Explain which formatters match a file, and why\n" +
        "- **/formatters list**: List every configured or locally detected formatter\n" +
        "- **/formatters path**: Show the formatter configuration file locations\n\n" +
        "### Authentication Commands:\n" +
        "- **/auth list**: Providers, credential status, and supported login methods\n" +
        "- **/auth login <provider>**: Sign in (masked prompt; --method oauth or device-code)\n" +
        "- **/auth status [provider]**: Show where each credential comes from (fully redacted)\n" +
        "- **/auth logout <provider>**: Remove the stored credential (key and OAuth tokens)\n\n" +
        "### Skill Commands:\n" +
        "- **/skills**: List discovered agent skills (disabled ones are marked)\n" +
        "- **/skills info <name>**: Show a skill's details\n" +
        "- **/skills enable|disable <name>**: Toggle whether the agent may load a skill\n" +
        "- **/skills diagnostics**: Show problems found during skill discovery\n" +
        "- **/skills reload**: Re-scan the skill roots\n\n" +
        "## Providers:\n" +
        "- **cerebras**: Fast Llama models\n" +
        "- **openai**: GPT-4 models\n" +
        "- **anthropic**: Claude models\n\n" +
        "## Tool Categories:\n" +
        "- **FileSystem**: File operations\n" +
        "- **TextProcessing**: Text manipulation\n" +
        "- **System**: System information\n" +
        "- **Web**: HTTP and JSON tools";

    /// <summary>The non-TUI one-shot mode help (andy-cli help).</summary>
    public static string CommandLineHelp() =>
        "Andy CLI - AI Assistant Command Line Interface\n" +
        "\n" +
        "Usage: andy-cli [command] [arguments]\n" +
        "\n" +
        "Commands:\n" +
        "  model, m       - Manage AI models\n" +
        "  tools, t       - Manage and list available tools\n" +
        "  permissions    - View and modify tool permission rules\n" +
        "  formatters     - Show which formatters run after Andy writes a file\n" +
        "  skills         - List, inspect, and enable/disable agent skills\n" +
        "  sessions       - List saved sessions (resume with --resume <id> / --continue)\n" +
        "  session        - Export, import, fork, rename, and measure saved sessions\n" +
        "  auth           - Sign in to providers, review credential status, sign out\n" +
        "  help, ?        - Show this help message\n" +
        "\n" +
        "Run without arguments to start the interactive TUI mode.";
}
