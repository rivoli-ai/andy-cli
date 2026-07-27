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

/// <summary>The currently effective opt-ins, split by where they came from.</summary>
/// <param name="ProjectTools">Per-tool grants in the project's <c>.andy/modes.json</c>.</param>
/// <param name="ProjectServers">Server-wide grants in the project's <c>.andy/modes.json</c>.</param>
/// <param name="UserTools">Per-tool grants in the user's <c>.andy/modes.json</c>.</param>
/// <param name="UserServers">Server-wide grants in the user's <c>.andy/modes.json</c>.</param>
public sealed record PlanModeGrantListing(
    IReadOnlyList<string> ProjectTools,
    IReadOnlyList<string> ProjectServers,
    IReadOnlyList<string> UserTools,
    IReadOnlyList<string> UserServers)
{
    public bool IsEmpty =>
        ProjectTools.Count == 0 && ProjectServers.Count == 0
        && UserTools.Count == 0 && UserServers.Count == 0;
}

/// <summary>
/// Read/modify/write access to the Plan-mode opt-ins in <c>.andy/modes.json</c>, plus the
/// bookkeeping the interactive MCP offer needs.
///
/// Writes always land in the PROJECT file: an MCP server is configured per project
/// (<c>.andy/mcp-servers.json</c>), so its Plan-mode grant belongs next to it and travels with the
/// repository rather than silently applying to every workspace on the machine. The user-level file
/// is still merged when the effective policy is computed, so a hand-written global opt-in keeps
/// working; it simply is not what the commands and the prompt write to.
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

    /// <summary>The path grants are written to.</summary>
    public string ProjectConfigPath => _projectPath;

    /// <summary>The additional user-scoped file that is merged when reading.</summary>
    public string UserConfigPath => _userPath;

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
            ? "No matching grant found in the project config."
            : $"Revoked {string.Join(", ", removed)}.");

        // A revoke that matched nothing is reported as a failure so a typo in a script surfaces
        // instead of looking like a successful removal.
        return result.Success && result.Changed.Count == 0
            ? PlanModeGrantResult.Failed(result.Message)
            : result;
    }

    /// <summary>The effective grants from both files, for review.</summary>
    public PlanModeGrantListing List()
    {
        var project = ModeConfigFile.Load(_projectPath);
        var user = ModeConfigFile.Load(_userPath);
        return new PlanModeGrantListing(
            project.PlanReadOnlyTools.ToArray(),
            project.PlanReadOnlyMcpServers.ToArray(),
            user.PlanReadOnlyTools.ToArray(),
            user.PlanReadOnlyMcpServers.ToArray());
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

        var asked = ModeConfigFile.Load(_projectPath).McpPlanOptInAsked;
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
    /// Applies <paramref name="edit"/> to the project config, saves it, rebuilds the effective
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
            var config = ModeConfigFile.Load(_projectPath);
            changed = edit(config);

            try
            {
                config.Save(_projectPath);
            }
            catch (Exception ex)
            {
                return PlanModeGrantResult.Failed(
                    $"Could not write {_projectPath}: {ex.Message}");
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
