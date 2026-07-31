using System;
using System.Collections.Generic;
using System.Linq;

namespace Andy.Cli.Modes;

/// <summary>The outcome of a grant or revoke operation.</summary>
/// <param name="Success">False when nothing was written (see <paramref name="Message"/>).</param>
/// <param name="Message">A user-facing summary of what changed, or why nothing did.</param>
/// <param name="Changed">Entries actually added or removed.</param>
public readonly record struct PlanModeGrantResult(
    bool Success,
    string Message,
    IReadOnlyList<string> Changed)
{
    public static PlanModeGrantResult Failed(string message) =>
        new(false, message, Array.Empty<string>());
}

/// <summary>The currently effective opt-ins. All of them are user-scoped.</summary>
/// <param name="Tools">Per-tool grants in the user's <c>.andy/modes.json</c>.</param>
/// <param name="Servers">Server-wide grants in the user's <c>.andy/modes.json</c>.</param>
/// <param name="IgnoredProjectEntries">
/// Grant entries found in the PROJECT file, which are ignored. Listed so a committed file that is
/// having no effect is visible rather than mysterious.
/// </param>
public sealed record PlanModeGrantListing(
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Servers,
    IReadOnlyList<string> IgnoredProjectEntries)
{
    /// <summary>True when no grant is in force (ignored project entries do not count).</summary>
    public bool IsEmpty => Tools.Count == 0 && Servers.Count == 0;
}

/// <summary>
/// Read/modify/write access to the Plan-mode opt-ins, plus the bookkeeping the interactive MCP
/// offer needs.
///
/// Grants are PER DEVELOPER. Every read and every write goes to the USER file
/// (<c>~/.andy/modes.json</c>). The project's <c>.andy/modes.json</c> is committed and shared, so
/// honoring grants from it would hand Plan-mode access to every teammate who clones the repository
/// without any of them ever seeing the opt-in prompt. Grant keys found there are ignored - which
/// leaves the tools DENIED, the safe direction - and reported through <see cref="Diagnostics"/>
/// rather than dropped in silence.
///
/// The store never widens Plan mode by itself. It refuses to record a grant for a tool
/// <see cref="PlanModeToolPolicy"/> classifies as mutating, and even if such an entry were written
/// by hand, <see cref="PlanModeToolPolicy.Evaluate"/> checks the capability-based denials before it
/// consults any opt-in.
/// </summary>
public sealed class PlanModeGrantStore
{
    private readonly object _gate = new();
    private readonly string _projectPath;
    private readonly string _userPath;
    private readonly string _projectDirectory;
    private readonly string _userDirectory;
    private PlanModeToolPolicy _policy;

    public PlanModeGrantStore(string projectDirectory, string? userDirectory = null)
    {
        _projectDirectory = projectDirectory ?? string.Empty;
        _userDirectory = userDirectory ?? ModeConfigFile.DefaultUserDirectory();
        _projectPath = ModeConfigFile.PathFor(_projectDirectory);
        _userPath = ModeConfigFile.PathFor(_userDirectory);
        _policy = ModeConfigFile.LoadPolicy(_projectDirectory, _userDirectory);
    }

    /// <summary>The per-developer file grants are read from and written to.</summary>
    public string GrantConfigPath => _userPath;

    /// <summary>
    /// The project file. It supplies NO grants; kept so diagnostics can name it and so any future
    /// non-grant project-scoped mode setting has somewhere to live.
    /// </summary>
    public string ProjectConfigPath => _projectPath;

    /// <summary>
    /// Warnings about project-scope grant keys that are being ignored. Empty in the normal case.
    /// Surfaced by the interactive session at start-up and by <c>/mode grants</c>.
    /// </summary>
    public IReadOnlyList<string> Diagnostics =>
        ModeConfigFile.ProjectScopeDiagnostics(_projectDirectory, _userDirectory);

    /// <summary>
    /// The effective policy, rebuilt after every change. <see cref="ModeToolGate"/> reads this on
    /// each call, so a grant takes effect immediately - no restart, and no need to rebuild the
    /// service provider or the enforcement decorators.
    /// </summary>
    public PlanModeToolPolicy CurrentPolicy
    {
        get
        {
            lock (_gate)
            {
                return _policy;
            }
        }
    }

    /// <summary>Raised after the effective policy changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Re-reads both files from disk (used after an external edit).</summary>
    public void Reload()
    {
        lock (_gate)
        {
            _policy = ModeConfigFile.LoadPolicy(_projectDirectory, _userDirectory);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Grants specific tool ids. Ids that Plan mode denies on capability grounds are rejected and
    /// nothing is written - a partial grant that silently dropped the interesting entry would be
    /// worse than an error.
    /// </summary>
    public PlanModeGrantResult GrantTools(IEnumerable<string> toolIds)
    {
        var requested = Normalize(toolIds);
        if (requested.Count == 0)
        {
            return PlanModeGrantResult.Failed("No tool ids given.");
        }

        var rejected = requested.Where(PlanModeToolPolicy.IsNeverGrantable).ToList();
        if (rejected.Count > 0)
        {
            var reasons = rejected.Select(id => $"{id} ({PlanModeToolPolicy.MutationReason(id)})");
            return PlanModeGrantResult.Failed(
                "Plan mode cannot be opened up for a mutating tool: "
                + string.Join(", ", reasons)
                + ". Use '/mode build' when you need to make changes.");
        }

        return Mutate(config =>
        {
            var added = new List<string>();
            foreach (var id in requested)
            {
                if (!config.PlanReadOnlyTools.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    config.PlanReadOnlyTools.Add(id);
                    added.Add(id);
                }
            }

            return added;
        },
        added => added.Count == 0
            ? "Already granted; nothing changed."
            : $"Plan mode may now use {added.Count} tool{(added.Count == 1 ? "" : "s")}: {string.Join(", ", added)}");
    }

    /// <summary>
    /// Grants every tool from an MCP server, including tools that server exposes for the first time
    /// later. This is the only grant shape that covers future tools.
    /// </summary>
    public PlanModeGrantResult GrantServer(string serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            return PlanModeGrantResult.Failed("No server name given.");
        }

        var name = serverName.Trim();
        return Mutate(config =>
        {
            if (config.PlanReadOnlyMcpServers.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return new List<string>();
            }

            config.PlanReadOnlyMcpServers.Add(name);
            return new List<string> { name };
        },
        added => added.Count == 0
            ? $"MCP server '{name}' was already granted; nothing changed."
            : $"Plan mode may now use every tool from MCP server '{name}' "
              + $"(tool ids starting with '{McpToolNaming.ServerToolPrefix(name)}'), including ones it exposes later.");
    }

    /// <summary>
    /// Removes grants. Each entry is matched against both the per-tool and the server-wide lists, so
    /// a user can revoke by whichever name they see in <see cref="List"/>.
    /// </summary>
    public PlanModeGrantResult Revoke(IEnumerable<string> entries)
    {
        var requested = Normalize(entries);
        if (requested.Count == 0)
        {
            return PlanModeGrantResult.Failed("No tool ids or server names given.");
        }

        var result = Mutate(config =>
        {
            var removed = new List<string>();
            foreach (var entry in requested)
            {
                if (config.PlanReadOnlyTools.RemoveAll(t => string.Equals(t, entry, StringComparison.OrdinalIgnoreCase)) > 0)
                {
                    removed.Add(entry);
                }

                if (config.PlanReadOnlyMcpServers.RemoveAll(s => string.Equals(s, entry, StringComparison.OrdinalIgnoreCase)) > 0)
                {
                    removed.Add($"server:{entry}");
                }
            }

            return removed;
        },
        removed => removed.Count == 0
            ? "No matching grant found in your user config."
            : $"Revoked {string.Join(", ", removed)}.");

        // A revoke that matched nothing is reported as a failure so a typo in a script surfaces
        // instead of looking like a successful removal.
        return result.Success && result.Changed.Count == 0
            ? PlanModeGrantResult.Failed(result.Message)
            : result;
    }

    /// <summary>
    /// The grants in force (all user-scoped), plus any project-scope entries that are being ignored
    /// so a committed file that is having no effect shows up in the review output.
    /// </summary>
    public PlanModeGrantListing List()
    {
        var user = ModeConfigFile.Load(_userPath);
        var project = ModeConfigFile.Load(_projectPath);
        var ignored = project.PlanReadOnlyMcpServers
            .Select(s => $"server:{s}")
            .Concat(project.PlanReadOnlyTools)
            .ToArray();

        return new PlanModeGrantListing(
            user.PlanReadOnlyTools.ToArray(),
            user.PlanReadOnlyMcpServers.ToArray(),
            ignored);
    }

    /// <summary>True when a server-wide grant is in force for <paramref name="serverName"/>.</summary>
    public bool IsServerGranted(string serverName) =>
        CurrentPolicy.AdditionalReadOnlyServers.Contains(serverName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The subset of <paramref name="toolIds"/> that Plan mode would still deny. Empty when the
    /// server is granted server-wide or every tool is individually granted.
    /// </summary>
    public IReadOnlyList<string> UngrantedTools(IReadOnlyList<string> toolIds)
    {
        var policy = CurrentPolicy;
        return toolIds.Where(id => !policy.Evaluate(id, null).Allowed).ToArray();
    }

    /// <summary>
    /// Whether the interactive offer should be shown for a connected server: it has at least one
    /// tool Plan mode would deny, and at least one of those has not already been offered.
    ///
    /// Re-offering only for genuinely NEW tool ids is what keeps a declined server from nagging on
    /// every start-up while still surfacing tools the server added since.
    /// </summary>
    public bool NeedsOffer(string serverName, IReadOnlyList<string> toolIds)
    {
        if (string.IsNullOrWhiteSpace(serverName) || toolIds.Count == 0)
        {
            return false;
        }

        var ungranted = UngrantedTools(toolIds);
        if (ungranted.Count == 0)
        {
            return false;
        }

        // Read from the USER file: which servers a developer has already been offered is personal, so
        // a committed record must never suppress the prompt for a teammate who has not seen it.
        var asked = ModeConfigFile.Load(_userPath).McpPlanOptInAsked;
        if (!asked.TryGetValue(serverName.Trim(), out var already) || already is null)
        {
            return true;
        }

        return ungranted.Any(id => !already.Contains(id, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Records that the offer was shown for these tools, whatever the user answered. Grants
    /// nothing on its own.
    /// </summary>
    public void RecordOffered(string serverName, IEnumerable<string> toolIds)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            return;
        }

        var name = serverName.Trim();
        Mutate(config =>
        {
            if (!config.McpPlanOptInAsked.TryGetValue(name, out var already) || already is null)
            {
                already = new List<string>();
                config.McpPlanOptInAsked[name] = already;
            }

            foreach (var id in Normalize(toolIds))
            {
                if (!already.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    already.Add(id);
                }
            }

            return new List<string>();
        },
        _ => string.Empty);
    }

    /// <summary>
    /// Applies <paramref name="edit"/> to the per-developer user config, saves it, rebuilds the effective
    /// policy and notifies listeners. A write failure is reported rather than thrown: a read-only
    /// checkout must not take down the session.
    /// </summary>
    private PlanModeGrantResult Mutate(
        Func<ModeConfigFile, List<string>> edit,
        Func<IReadOnlyList<string>, string> describe)
    {
        List<string> changed;
        lock (_gate)
        {
            var config = ModeConfigFile.Load(_userPath);
            changed = edit(config);

            try
            {
                config.Save(_userPath);
            }
            catch (Exception ex)
            {
                return PlanModeGrantResult.Failed(
                    $"Could not write {_userPath}: {ex.Message}");
            }

            _policy = ModeConfigFile.LoadPolicy(_projectDirectory, _userDirectory);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return new PlanModeGrantResult(true, describe(changed), changed);
    }

    private static List<string> Normalize(IEnumerable<string>? values)
    {
        var result = new List<string>();
        if (values is null)
        {
            return result;
        }

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = value.Trim();
            if (!result.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }
}
