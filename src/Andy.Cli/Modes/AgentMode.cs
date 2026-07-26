using System;
using System.Collections.Generic;
using System.Linq;

namespace Andy.Cli.Modes;

/// <summary>
/// The CLI's primary operating modes (issue #278). This is the ONE shared mode abstraction:
/// the interactive TUI, the headless runner and (later) the ACP session-mode API all resolve
/// a mode through <see cref="AgentModeCatalog"/> and enforce it through
/// <see cref="Andy.Cli.Modes.ModeToolGate"/>. Adding a mode here makes it available to every
/// entry point at once.
/// </summary>
public enum AgentMode
{
    /// <summary>Full interactive capability: the normal permission engine decides every call.</summary>
    Build,

    /// <summary>
    /// Strictly non-mutating research/planning. Reads, searches and index queries are allowed;
    /// file writes, shell commands and every unclassified tool are denied BEFORE the permission
    /// engine runs, so no allow rule can re-enable them.
    /// </summary>
    Plan,
}

/// <summary>
/// The stable, user- and protocol-facing description of one mode.
///
/// <see cref="Id"/> is the wire identifier: it is what <c>/mode &lt;id&gt;</c> accepts, what is
/// persisted in a session file, what <c>--mode</c> accepts in headless runs, and what an ACP
/// <c>session/set_mode</c> implementation should map onto. Never rename an existing id.
/// </summary>
/// <param name="Mode">The enum value this definition describes.</param>
/// <param name="Id">Stable lowercase wire id ("build", "plan").</param>
/// <param name="DisplayName">Human-readable name used in messages.</param>
/// <param name="Badge">Short uppercase badge shown in the status line.</param>
/// <param name="Summary">One-line description shown by <c>/mode</c> and <c>/help</c>.</param>
/// <param name="AllowsMutation">
/// False for modes that are enforced as read-only by the tool-permission overlay.
/// </param>
public sealed record AgentModeDefinition(
    AgentMode Mode,
    string Id,
    string DisplayName,
    string Badge,
    string Summary,
    bool AllowsMutation);

/// <summary>
/// The closed set of primary modes plus fail-closed parsing.
///
/// Parsing NEVER falls back to a default: an unrecognized mode string is rejected so a typo in
/// <c>/mode</c>, a headless <c>--mode</c> flag, or a hand-edited session file can never silently
/// downgrade the CLI into the permissive mode.
/// </summary>
public static class AgentModeCatalog
{
    public const string BuildId = "build";
    public const string PlanId = "plan";

    public static AgentModeDefinition Build { get; } = new(
        AgentMode.Build,
        BuildId,
        "Build",
        "BUILD",
        "Full capability. Reads, edits, and shell commands are available under the normal permission rules.",
        AllowsMutation: true);

    public static AgentModeDefinition Plan { get; } = new(
        AgentMode.Plan,
        PlanId,
        "Plan",
        "PLAN",
        "Read-only research and planning. Every mutating tool is denied before execution; no allow rule can override it.",
        AllowsMutation: false);

    /// <summary>Every known mode, in display order.</summary>
    public static IReadOnlyList<AgentModeDefinition> All { get; } = new[] { Build, Plan };

    /// <summary>The mode a session starts in when nothing else selects one.</summary>
    public static AgentMode DefaultMode => AgentMode.Build;

    /// <summary>Comma-separated list of accepted mode ids, for error messages.</summary>
    public static string KnownIds => string.Join(", ", All.Select(m => m.Id));

    public static AgentModeDefinition Get(AgentMode mode) =>
        mode == AgentMode.Plan ? Plan : Build;

    /// <summary>
    /// Fail-closed parse of a mode id. Returns false (and leaves <paramref name="definition"/>
    /// null) for null, empty, or unknown values - callers must treat that as an error, never as
    /// "use the default".
    /// </summary>
    public static bool TryParse(string? value, out AgentModeDefinition? definition)
    {
        definition = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.Id, normalized, StringComparison.OrdinalIgnoreCase))
            {
                definition = candidate;
                return true;
            }
        }

        return false;
    }
}
