using System;
using System.Collections.Generic;

namespace Andy.Cli.Modes;

/// <summary>
/// The single decision point that turns the active mode into a per-call verdict.
///
/// This is the ENFORCEMENT half of the mode feature; the planning DATA the engine produces
/// (<c>AgentPlanView</c> / <c>EnginePlanBridge</c>) is a display concern and has no bearing on
/// what a tool call is allowed to do. Two adapters put this gate ahead of the permission engine:
/// <see cref="ModeGatedToolExecutor"/> (short-circuits execution) and
/// <see cref="ModeGatedPermissionAuthorizer"/> (forces the evaluation to Deny). Because both run
/// OUTSIDE the rule engine, a user, project, local, session, or injected Allow rule can never
/// re-enable something the mode forbids.
/// </summary>
public sealed class ModeToolGate
{
    private readonly AgentModeState _state;
    private readonly Func<PlanModeToolPolicy> _planPolicy;

    public ModeToolGate(AgentModeState state, PlanModeToolPolicy? planPolicy = null)
    {
        _state = state;
        var policy = planPolicy ?? PlanModeToolPolicy.Default;
        _planPolicy = () => policy;
    }

    /// <summary>
    /// Reads the policy from a live grant store instead of capturing it. A Plan-mode opt-in granted
    /// mid-session (via the MCP offer or <c>/mode allow</c>) therefore takes effect on the next tool
    /// call, with no restart and without rebuilding the enforcement decorators around it.
    /// </summary>
    public ModeToolGate(AgentModeState state, PlanModeGrantStore grantStore)
    {
        ArgumentNullException.ThrowIfNull(grantStore);
        _state = state;
        _planPolicy = () => grantStore.CurrentPolicy;
    }

    /// <summary>The mode state this gate consults.</summary>
    public AgentModeState State => _state;

    /// <summary>The Plan-mode policy currently in force.</summary>
    public PlanModeToolPolicy PlanPolicy => _planPolicy();

    /// <summary>
    /// Classifies a tool call under the ACTIVE mode. Build allows everything (the permission
    /// engine remains the authority); Plan applies <see cref="PlanModeToolPolicy"/>.
    /// </summary>
    public ModeToolVerdict Evaluate(string toolId, IReadOnlyDictionary<string, object?>? parameters)
    {
        var definition = _state.CurrentDefinition;
        if (definition.AllowsMutation)
        {
            return ModeToolVerdict.Allow();
        }

        return _planPolicy().Evaluate(toolId, parameters);
    }

    /// <summary>Convenience for callers that only have a mutable dictionary.</summary>
    public ModeToolVerdict Evaluate(string toolId, Dictionary<string, object?>? parameters) =>
        Evaluate(toolId, (IReadOnlyDictionary<string, object?>?)parameters);
}
