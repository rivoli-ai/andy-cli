using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Andy.Tools.Core;

namespace Andy.Cli.Services.Formatting;

/// <summary>A formatter process about to be launched, described for the permission gate.</summary>
/// <param name="FormatterName">The definition's name, for the prompt and the audit record.</param>
/// <param name="CommandLine">The full command line, used as the permission specifier.</param>
/// <param name="WorkingDirectory">Where the process would run.</param>
/// <param name="TargetPath">The file the formatter would rewrite.</param>
public sealed record FormatterCommandRequest(
    string FormatterName,
    string CommandLine,
    string WorkingDirectory,
    string TargetPath);

/// <summary>Allow or deny, with the reason to report when denied.</summary>
public sealed record FormatterPermissionVerdict(bool Allowed, string? Reason)
{
    public static FormatterPermissionVerdict Allow { get; } = new(true, null);

    public static FormatterPermissionVerdict Deny(string reason) => new(false, reason);
}

/// <summary>
/// Consent check performed BEFORE a formatter process is started.
///
/// Formatters run arbitrary local binaries, so they go through exactly the same consent path as any
/// other command Andy runs rather than a private bypass.
/// </summary>
public interface IFormatterPermissionGate
{
    Task<FormatterPermissionVerdict> CheckAsync(FormatterCommandRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Routes formatter execution through Andy's normal command-permission path by asking the
/// registered <see cref="IToolPermissionGate"/> to authorize an <c>execute_command</c> call for the
/// formatter's command line.
///
/// This is what makes the acceptance criterion "Plan mode and normal permissions can deny formatter
/// execution before the process starts" hold without formatter-specific policy code:
/// <list type="bullet">
/// <item>Normal permissions: a <c>deny</c> rule matching <c>execute_command(&lt;command&gt;:*)</c>
/// denies the formatter, and an <c>ask</c> prompts through the usual modal - whose decision is
/// recorded in the session approvals file, so formatter runs are audited like any other command.</item>
/// <item>INTEGRATION SEAM (issue #278 - Plan mode): Plan mode denies mutating/executing tool calls at
/// this same gate. Because the check happens before <see cref="FormatterProcessRunner"/> is ever
/// reached, a Plan-mode overlay denies formatters automatically, with no change needed here.</item>
/// </list>
/// </summary>
public sealed class ToolGateFormatterPermission : IFormatterPermissionGate
{
    /// <summary>The tool id formatter execution is authorized as: it is a command execution.</summary>
    public const string GatedToolId = "execute_command";

    private readonly IToolPermissionGate _gate;
    private readonly ToolMetadata? _metadata;

    public ToolGateFormatterPermission(IToolPermissionGate gate, IToolRegistry? toolRegistry = null)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _metadata = toolRegistry?.GetTool(GatedToolId)?.Metadata;
    }

    public async Task<FormatterPermissionVerdict> CheckAsync(
        FormatterCommandRequest request, CancellationToken cancellationToken)
    {
        var context = new ToolExecutionContext
        {
            WorkingDirectory = request.WorkingDirectory,
            CancellationToken = cancellationToken,
        };

        // Mirror UiUpdatingToolExecutor.GrantGatedCapabilities: the capability flags exist so the
        // low-level checks do not pre-empt the gate; the gate stays the consent authority.
        context.Permissions.FileSystemAccess = true;
        context.Permissions.ProcessExecution = true;

        var gateRequest = new ToolPermissionGateRequest
        {
            ToolId = GatedToolId,
            Parameters = new Dictionary<string, object?>
            {
                ["command"] = request.CommandLine,
                ["working_directory"] = request.WorkingDirectory,
            },
            Context = context,
            Metadata = _metadata,
        };

        ToolPermissionVerdict verdict;
        try
        {
            verdict = await _gate.CheckAsync(gateRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail closed: a gate that errors must not become an implicit allow.
            return FormatterPermissionVerdict.Deny(
                $"permission check failed for formatter '{request.FormatterName}': {ex.Message}");
        }

        return verdict.Allowed
            ? FormatterPermissionVerdict.Allow
            : FormatterPermissionVerdict.Deny(
                string.IsNullOrWhiteSpace(verdict.Reason)
                    ? $"permission denied for formatter '{request.FormatterName}'"
                    : verdict.Reason);
    }
}

/// <summary>
/// Used when no <see cref="IToolPermissionGate"/> is registered at all. This mirrors Andy.Tools'
/// own contract - "no gate registered means no gating" - and only ever applies to hosts that
/// deliberately run without the permission system.
/// </summary>
public sealed class UngatedFormatterPermission : IFormatterPermissionGate
{
    public static UngatedFormatterPermission Instance { get; } = new();

    public Task<FormatterPermissionVerdict> CheckAsync(
        FormatterCommandRequest request, CancellationToken cancellationToken)
        => Task.FromResult(FormatterPermissionVerdict.Allow);
}
