using System;
using System.Collections.Generic;
using Andy.Permissions.Authorization;
using Andy.Permissions.Model;

namespace Andy.Cli.Modes;

/// <summary>
/// Wraps the permission engine's <see cref="IToolPermissionAuthorizer"/> so the active mode is
/// applied BEFORE any rule layer is consulted.
///
/// When the mode forbids a call this returns a synthetic Deny evaluation and never asks the inner
/// authorizer, so the user / project / local / session / injected layers - including a broad
/// <c>write_file(*)</c> Allow or a per-run injected allow-list - cannot widen it back. When the
/// mode permits the call, evaluation is delegated unchanged and the normal rules decide.
///
/// Every consumer of the authorizer therefore agrees on the verdict: the packaged
/// <c>PermissionedToolExecutor</c> that gates interactive and headless execution, the headless
/// <c>ObservingToolExecutor</c> that enforces and reports outcomes, and the end-of-run tool-usage
/// audit.
/// </summary>
public sealed class ModeGatedPermissionAuthorizer : IToolPermissionAuthorizer
{
    private readonly IToolPermissionAuthorizer _inner;
    private readonly ModeToolGate _gate;

    public ModeGatedPermissionAuthorizer(IToolPermissionAuthorizer inner, ModeToolGate gate)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    public PermissionEvaluation Evaluate(ToolAuthorizationContext context)
    {
        var verdict = _gate.Evaluate(context.ToolId, context.Parameters);
        if (verdict.Allowed)
        {
            return _inner.Evaluate(context);
        }

        return DenyEvaluation(context.ToolId);
    }

    /// <summary>
    /// A Deny evaluation attributed to the mode overlay. The synthetic rule is tagged
    /// <see cref="PermissionLayer.Managed"/> - the layer reserved for policy that the user cannot
    /// override - so anything that renders the matched rule shows the denial as policy, not as a
    /// user-editable preference.
    /// </summary>
    internal static PermissionEvaluation DenyEvaluation(string toolId)
    {
        var specifier = PermissionRule.IsValidToolId(toolId) ? toolId : "*";
        var rule = PermissionRule.Parse($"{specifier}(*)", PermissionOutcome.Deny, PermissionLayer.Managed);
        var resource = new EvaluatedResource(
            new ResourceAccess(ResourceKind.None, toolId ?? string.Empty),
            PermissionOutcome.Deny,
            rule,
            true);

        return new PermissionEvaluation(
            PermissionOutcome.Deny,
            new List<EvaluatedResource> { resource });
    }
}
