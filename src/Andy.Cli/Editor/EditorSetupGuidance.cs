using System.Collections.Generic;
using System.Text;

namespace Andy.Cli.Editor;

/// <summary>
/// User-facing setup guidance shown when no external editor is configured, or when the
/// configured value cannot be parsed. Single source of truth so the CLI message and
/// docs/external-editor.md stay in step (pinned by ExternalEditorDocsTests).
/// </summary>
public static class EditorSetupGuidance
{
    /// <summary>The documentation page describing the whole feature.</summary>
    public const string DocsPath = "docs/external-editor.md";

    /// <summary>
    /// Recommended values for common editors. The value is exactly what belongs on the
    /// right-hand side of <c>export VISUAL=...</c>.
    /// </summary>
    public static IReadOnlyList<(string Editor, string Value, string Note)> Examples { get; } = new[]
    {
        ("Vim", "vim", "blocks until you :wq, no extra flags needed"),
        ("Neovim", "nvim", "blocks until you :wq, no extra flags needed"),
        ("VS Code", "code --wait", "--wait is required or the editor returns immediately"),
        ("Cursor", "cursor --wait", "--wait is required or the editor returns immediately"),
        ("Zed", "zed --wait", "--wait is required or the editor returns immediately"),
        ("Nano", "nano", "blocks until Ctrl+O then Ctrl+X"),
        ("Micro", "micro", "blocks until Ctrl+S then Ctrl+Q"),
        ("Helix", "hx", "blocks until :wq"),
        ("Emacs (terminal)", "emacs -nw", "-nw keeps Emacs inside this terminal"),
        ("Emacsclient", "emacsclient -nw -a ''", "-a '' starts a daemon if none is running"),
    };

    /// <summary>The message shown when neither VISUAL nor EDITOR is set.</summary>
    public static string NotConfiguredMessage()
    {
        var sb = new StringBuilder();
        sb.Append("No external editor is configured. Andy reads VISUAL first, then EDITOR.\n\n");
        sb.Append("Set one for this shell:\n\n");
        sb.Append("    export VISUAL='vim'\n\n");
        sb.Append("...or make it permanent by adding that line to ~/.zshrc, ~/.bashrc or ~/.profile.\n");
        sb.Append("On Windows PowerShell use: $env:VISUAL = 'code --wait'\n\n");
        sb.Append("Common values:\n\n");
        foreach (var (editor, value, note) in Examples)
            sb.Append("    ").Append(editor.PadRight(18)).Append(value.PadRight(22)).Append("# ").Append(note).Append('\n');
        sb.Append('\n');
        sb.Append("GUI editors must block until the file is closed (that is what --wait does);\n");
        sb.Append("without it Andy sees an instantly-finished editor and nothing changes.\n\n");
        sb.Append("See ").Append(DocsPath).Append(" for the full guide.");
        return sb.ToString();
    }

    /// <summary>Extra help shown when a configured value fails to parse.</summary>
    public static string QuotingHelp()
    {
        var sb = new StringBuilder();
        sb.Append("Andy launches the editor directly, without a shell, so the value is split on\n");
        sb.Append("spaces with single/double quotes honored. Quote a program path that contains\n");
        sb.Append("spaces:\n\n");
        sb.Append("    export VISUAL='\"/Applications/My Editor/bin/edit\" --wait'\n\n");
        sb.Append("Shell features (pipes, ;, &&, $VAR, ~, globs) are NOT expanded; they are passed\n");
        sb.Append("through as literal argument text.\n\n");
        sb.Append("See ").Append(DocsPath).Append(" for the full guide.");
        return sb.ToString();
    }
}
