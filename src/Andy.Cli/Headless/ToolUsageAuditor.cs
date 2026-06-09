using System.Collections.Concurrent;
using System.Linq;
using Andy.Permissions.Authorization;
using Andy.Permissions.Model;
using Andy.Tools.Core;

namespace Andy.Cli.Headless;

// AX.4 (rivoli-ai/conductor#2091): records which tools the headless agent actually
// invoked during a run and, for each, whether the permission engine permitted it
// under the injected allow-list. The agent fires ToolCalled when the LLM emits a
// tool call (before execution); we tally those. The "permitted" verdict is computed
// from the SAME permission engine that gates execution (IToolPermissionAuthorizer),
// so the audit reflects the real fail-closed decision rather than guessing:
//   - Allow  => permitted (in the injected allow-list, or an auto-allowed read-only
//               built-in).
//   - Ask    => NOT permitted: headless is non-interactive (no broker), so Ask is
//               denied at execution time.
//   - Deny   => NOT permitted.
//
// The audit is emitted once at end-of-run (EmitToolUsageAudit) so an external
// verifier (AX.10) can confirm only permitted tools ran.
public sealed class ToolUsageAuditor
{
    private readonly ConcurrentDictionary<string, int> _invocations = new(StringComparer.Ordinal);

    // Records one invocation of a tool by name. Thread-safe: the post-tool callback
    // can race with in-flight LLM streaming.
    public void RecordInvocation(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return;
        }

        _invocations.AddOrUpdate(toolName, 1, static (_, count) => count + 1);
    }

    // Builds the audit rows, resolving each invoked tool's permitted status against
    // the live permission engine. <paramref name="authorizer"/> and
    // <paramref name="registry"/> may be null (e.g. permissions not wired); in that
    // case "permitted" cannot be evaluated and defaults to false (fail-closed).
    public IReadOnlyList<ToolUsageAuditEntry> BuildEntries(
        IToolPermissionAuthorizer? authorizer,
        IToolRegistry? registry)
    {
        return _invocations
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new ToolUsageAuditEntry(
                kv.Key,
                kv.Value,
                IsPermitted(kv.Key, authorizer, registry)))
            .ToList();
    }

    // Asks the permission engine for this tool's effective outcome. Only Allow counts
    // as permitted under the headless fail-closed contract (Ask has no broker → deny).
    private static bool IsPermitted(
        string toolName,
        IToolPermissionAuthorizer? authorizer,
        IToolRegistry? registry)
    {
        if (authorizer is null)
        {
            return false;
        }

        var metadata = registry?.GetTool(toolName)?.Metadata;
        var context = new ToolAuthorizationContext(
            toolName,
            new Dictionary<string, object?>(),
            WorkingDirectory: null,
            Metadata: metadata);

        try
        {
            var evaluation = authorizer.Evaluate(context);
            return evaluation.Outcome == PermissionOutcome.Allow;
        }
        catch
        {
            // A resolver that needs concrete parameters it doesn't have should not
            // crash the audit; treat an unresolvable evaluation as not-permitted.
            return false;
        }
    }
}
