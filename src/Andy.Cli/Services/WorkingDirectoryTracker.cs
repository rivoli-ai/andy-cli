using System;
using System.IO;

namespace Andy.Cli.Services;

/// <summary>
/// Tracks the interactive session's effective working directory - the single source of truth
/// shared by the header (which shows the current path each frame) and the tool execution layer.
///
/// Why this exists (rivoli-ai/andy-cli#235): the engine's SimpleAgent captures
/// Environment.CurrentDirectory ONCE at construction into a readonly field and stamps that frozen
/// snapshot into every ToolExecutionContext.WorkingDirectory, and a `cd` inside an
/// execute_command call dies with the child shell process. So without this tracker nothing in
/// the app can ever change the working directory mid-conversation, and the path shown at the top
/// of the UI can never move.
///
/// UiUpdatingToolExecutor stamps <see cref="Current"/> into every tool execution context
/// (overriding the frozen snapshot) and calls <see cref="ApplyExecutedCommand"/> after each
/// successful execute_command, so a standalone `cd` persists for the rest of the session and the
/// header stays in sync with where tools actually operate. The process-wide
/// Environment.CurrentDirectory is deliberately left untouched: mutating process-global state
/// from the tool layer would leak into unrelated subsystems (crash logs, config loading, git
/// info) and is not what the tools consult anyway - they use the execution context.
/// </summary>
public sealed class WorkingDirectoryTracker
{
    /// <summary>Process-wide tracker used by the interactive UI (header + tool executor).</summary>
    public static WorkingDirectoryTracker Instance { get; } = new();

    private readonly object _sync = new();
    private string _current;

    /// <summary>Raised with the new directory after <see cref="Current"/> changes.</summary>
    public event Action<string>? Changed;

    public WorkingDirectoryTracker(string? initialDirectory = null)
    {
        _current = !string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory)
            ? Path.GetFullPath(initialDirectory)
            : Directory.GetCurrentDirectory();
    }

    /// <summary>The session's current working directory (always a full path).</summary>
    public string Current
    {
        get { lock (_sync) { return _current; } }
    }

    /// <summary>
    /// Sets the tracked directory when <paramref name="path"/> names an existing directory.
    /// Returns true when the tracker now points at that directory.
    /// </summary>
    public bool TrySet(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        if (!Directory.Exists(full)) return false;

        bool changed;
        lock (_sync)
        {
            changed = !string.Equals(_current, full, StringComparison.Ordinal);
            _current = full;
        }

        if (changed)
        {
            Changed?.Invoke(full);
        }
        return true;
    }

    /// <summary>
    /// Inspects a completed execute_command invocation and applies a directory change when the
    /// command was a standalone `cd`. The target is resolved against
    /// <paramref name="baseDirectory"/> (the call's explicit working_directory parameter) when
    /// given, otherwise against <see cref="Current"/>. Returns the new directory when a change
    /// was applied, null otherwise.
    /// </summary>
    public string? ApplyExecutedCommand(string? command, string? baseDirectory = null)
    {
        var target = ResolveCdTarget(command, baseDirectory ?? Current);
        if (target != null && TrySet(target))
        {
            return Current;
        }
        return null;
    }

    /// <summary>
    /// Parses a shell command and, when it is a standalone `cd [path]` (no chaining, pipes,
    /// redirection, or substitution), resolves the target against
    /// <paramref name="currentDirectory"/>. Returns null for anything that is not a simple cd -
    /// a `cd` that only prefixes a compound command (`cd x &amp;&amp; make`) affects that command
    /// alone, matching how each execute_command call runs in its own shell.
    /// </summary>
    public static string? ResolveCdTarget(string? command, string currentDirectory)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        var trimmed = command.Trim();
        string arg;
        if (trimmed == "cd")
        {
            arg = string.Empty;
        }
        else if (trimmed.StartsWith("cd ", StringComparison.Ordinal) ||
                 trimmed.StartsWith("cd\t", StringComparison.Ordinal))
        {
            arg = trimmed.Substring(3).Trim();
        }
        else
        {
            return null;
        }

        // Conservative: any shell metacharacter means this is not a simple directory change.
        foreach (var meta in new[] { "&&", "||", ";", "|", ">", "<", "`", "$(", "\n", "&" })
        {
            if (arg.Contains(meta, StringComparison.Ordinal)) return null;
        }

        // `cd -` needs previous-directory history we do not keep.
        if (arg == "-") return null;

        // Strip one level of surrounding quotes (`cd "My Dir"`).
        if (arg.Length >= 2 &&
            ((arg[0] == '"' && arg[^1] == '"') || (arg[0] == '\'' && arg[^1] == '\'')))
        {
            arg = arg.Substring(1, arg.Length - 2);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (arg.Length == 0 || arg == "~") return home;
        if (arg.StartsWith("~/", StringComparison.Ordinal))
        {
            arg = Path.Combine(home, arg.Substring(2));
        }

        try
        {
            return Path.GetFullPath(Path.IsPathRooted(arg) ? arg : Path.Combine(currentDirectory, arg));
        }
        catch
        {
            return null;
        }
    }
}
