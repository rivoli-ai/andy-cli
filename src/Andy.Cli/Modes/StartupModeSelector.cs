using System;

namespace Andy.Cli.Modes;

/// <summary>The outcome of resolving a start-up mode from the command line.</summary>
/// <param name="Mode">The mode the session must start in.</param>
/// <param name="Error">
/// Null on success. Non-null when the requested mode was unrecognized, in which case
/// <paramref name="Mode"/> is the most RESTRICTIVE known mode rather than the default - falling
/// back to the mutation-capable default on a typo is exactly the failure this guards against.
/// </param>
public readonly record struct StartupModeSelection(AgentMode Mode, string? Error);

/// <summary>
/// Resolves <c>--mode &lt;id&gt;</c> from the interactive CLI's argument list (issue #278).
///
/// Fail-closed contract: an unknown value never yields the default mode. Headless runs reject the
/// run outright (see <c>HeadlessRunner</c>); the interactive TUI cannot usefully abort at this
/// point, so it starts in the restrictive mode and surfaces the error to the user, who can then
/// switch deliberately with <c>/mode build</c>.
/// </summary>
public static class StartupModeSelector
{
    /// <summary>The most restrictive known mode, used when a request cannot be honored.</summary>
    public static AgentMode SafestMode => AgentMode.Plan;

    public static StartupModeSelection Resolve(string[]? args)
    {
        if (args is null)
        {
            return new StartupModeSelection(AgentModeCatalog.DefaultMode, null);
        }

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            string? value = null;

            if (string.Equals(token, "--mode", StringComparison.Ordinal))
            {
                value = i + 1 < args.Length ? args[i + 1] : null;
            }
            else if (token.StartsWith("--mode=", StringComparison.Ordinal))
            {
                value = token["--mode=".Length..];
            }
            else
            {
                continue;
            }

            if (AgentModeCatalog.TryParse(value, out var definition) && definition is not null)
            {
                return new StartupModeSelection(definition.Mode, null);
            }

            var shown = string.IsNullOrWhiteSpace(value) ? "(missing)" : value;
            return new StartupModeSelection(
                SafestMode,
                $"Unknown mode '{shown}'. Known modes: {AgentModeCatalog.KnownIds}. "
                + $"Starting in {AgentModeCatalog.Get(SafestMode).DisplayName} mode instead of assuming the "
                + "permissive default; use '/mode build' if that is what you wanted.");
        }

        return new StartupModeSelection(AgentModeCatalog.DefaultMode, null);
    }
}
