using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Andy.Cli.Commands.Custom;

/// <summary>
/// The live set of Markdown-defined slash commands for one workspace, plus the expansion
/// entry point.
/// </summary>
/// <remarks>
/// Discovery is lazy and cached; <see cref="Invalidate"/> drops the cache so
/// <c>/commands reload</c> picks up new or edited files WITHOUT restarting the TUI (the
/// interactive surfaces re-read <see cref="Commands"/> after a reload).
///
/// The class intentionally has no TUI, DI, or engine dependency: the interactive dispatcher,
/// a future headless runner, and the ACP server construct one of these and call
/// <see cref="TryExpand"/> to get the same parse and expansion behavior.
///
/// SECURITY (issue #281): the catalog only ever produces TEXT. It cannot grant a permission,
/// enable a tool, change an agent, or bypass a Plan-mode overlay, because the expanded prompt
/// is handed to the ordinary user-message path where every existing gate still applies. The
/// <c>provider</c>/<c>model</c>/<c>mode</c> frontmatter fields are advisory metadata that the
/// caller may display or act on through its normal switching path; nothing here applies them.
/// </remarks>
public sealed class CustomCommandCatalog
{
    private readonly object _lock = new();
    private readonly string _workspaceDirectory;
    private readonly string? _homeDirectory;
    private readonly IReadOnlyCollection<string> _reservedNames;
    private CustomCommandDiscoveryResult? _cached;

    public CustomCommandCatalog(
        string workspaceDirectory,
        string? homeDirectory = null,
        CustomCommandLimits? limits = null,
        IReadOnlyCollection<string>? reservedNames = null,
        ICustomCommandFileResolver? fileResolver = null)
    {
        _workspaceDirectory = string.IsNullOrWhiteSpace(workspaceDirectory)
            ? Directory.GetCurrentDirectory()
            : workspaceDirectory;
        _homeDirectory = homeDirectory;
        Limits = limits ?? CustomCommandLimits.Default;
        _reservedNames = reservedNames ?? SlashCommandCatalog.ReservedCommandNames;
        FileResolver = fileResolver ?? new WorkspaceFileMentionResolver();
    }

    /// <summary>Create the catalog for the current working directory.</summary>
    public static CustomCommandCatalog CreateDefault(string? workspaceDirectory = null)
        => new(workspaceDirectory ?? Directory.GetCurrentDirectory());

    public CustomCommandLimits Limits { get; }

    /// <summary>
    /// The <c>@file</c> mention resolver. INTEGRATION SEAM for #277: assign that issue's
    /// shared structured resolver here and the local one becomes dead code.
    /// </summary>
    public ICustomCommandFileResolver FileResolver { get; set; }

    public string WorkspaceDirectory => _workspaceDirectory;

    /// <summary>Commands in stable, name-sorted order.</summary>
    public IReadOnlyList<CustomCommandDefinition> Commands => Snapshot().Commands;

    /// <summary>Problems found during the last discovery pass.</summary>
    public IReadOnlyList<CustomCommandDiagnostic> Diagnostics => Snapshot().Diagnostics;

    /// <summary>The scanned roots, user first.</summary>
    public IReadOnlyList<string> Roots => Snapshot().Roots;

    /// <summary>Drop the cache so the next access re-scans the roots (used by /commands reload).</summary>
    public void Invalidate()
    {
        lock (_lock)
            _cached = null;
    }

    /// <summary>Re-scan immediately and return the fresh command list.</summary>
    public IReadOnlyList<CustomCommandDefinition> Reload()
    {
        Invalidate();
        return Commands;
    }

    /// <summary>
    /// Look a command up by name. Accepts both the canonical colon form (<c>git:commit</c>)
    /// and the path form a user is likely to type (<c>git/commit</c>).
    /// </summary>
    public CustomCommandDefinition? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var normalized = name.Trim().TrimStart('/').Replace('/', ':').Replace('\\', ':').ToLowerInvariant();
        return Commands.FirstOrDefault(c => string.Equals(c.Name, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Expand a command into a prompt. Returns false with an <paramref name="error"/> when the
    /// name is unknown; a known command always expands (missing arguments are empty, never an
    /// error), with any non-fatal problem reported on the result's diagnostics.
    /// </summary>
    public bool TryExpand(
        string name,
        string? rawArguments,
        out ExpandedCommandPrompt? prompt,
        out string? error)
    {
        prompt = null;
        error = null;

        var definition = Find(name);
        if (definition is null)
        {
            error = $"Unknown command: /{name?.TrimStart('/')}";
            return false;
        }

        prompt = Expand(definition, rawArguments);
        return true;
    }

    /// <summary>Expand a known command definition against a raw argument string.</summary>
    public ExpandedCommandPrompt Expand(CustomCommandDefinition definition, string? rawArguments)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));

        var diagnostics = new List<CustomCommandDiagnostic>();
        var text = CustomCommandExpander.ExpandTemplate(definition.Template, rawArguments);

        var provided = CustomCommandArguments.Parse((rawArguments ?? "").Trim()).Count;
        var needed = definition.MaxPositional;
        if (needed > provided)
        {
            diagnostics.Add(new CustomCommandDiagnostic(
                CustomCommandDiagnosticSeverity.Warning, definition.FilePath,
                $"/{definition.Name} references ${needed} but only {provided} argument(s) were given; " +
                "the missing placeholders expanded to nothing."));
        }

        var files = FileResolver.Resolve(text, _workspaceDirectory, Limits, diagnostics);
        return new ExpandedCommandPrompt(definition, text, files, diagnostics);
    }

    private CustomCommandDiscoveryResult Snapshot()
    {
        lock (_lock)
        {
            return _cached ??= CustomCommandDiscovery.Discover(
                _workspaceDirectory, _homeDirectory, Limits, _reservedNames);
        }
    }
}
