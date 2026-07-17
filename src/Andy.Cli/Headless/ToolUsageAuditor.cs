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
    // Per-tool tally. `Verdict` is the permission outcome observed on the ACTUAL
    // execution path (#179): the authorizer is evaluated with the real parameters
    // and working directory at the moment the tool runs, instead of being
    // re-evaluated at end-of-run with an empty parameter bag. `HasVerdict` stays
    // false only for the legacy count-only path (RecordInvocation(name)), which
    // still falls back to the end-of-run re-evaluation in BuildEntries.
    private sealed class Tally
    {
        public int Count;
        public bool AllPermitted = true;
        public bool HasVerdict;
    }

    private readonly ConcurrentDictionary<string, Tally> _invocations = new(StringComparer.Ordinal);

    // Records one invocation of a tool by name, with NO recorded permission verdict.
    // Retained for callers that only tally (and for tests); BuildEntries falls back
    // to re-evaluating the live authorizer for these. Prefer the (toolName, permitted)
    // overload from the execution path so the audit reflects the real verdict.
    // Thread-safe: the post-tool callback can race with in-flight LLM streaming.
    public void RecordInvocation(string toolName)
        => Record(toolName, permitted: null);

    // #179: records one invocation together with the ACTUAL permission verdict
    // observed on the execution path (authorizer evaluated with real parameters).
    // A tool is reported permitted only if EVERY observed invocation was permitted
    // (fail-closed aggregation across differing parameters).
    public void RecordInvocation(string toolName, bool permitted)
        => Record(toolName, permitted);

    private void Record(string toolName, bool? permitted)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return;
        }

        _invocations.AddOrUpdate(
            toolName,
            _ => new Tally
            {
                Count = 1,
                HasVerdict = permitted.HasValue,
                AllPermitted = permitted ?? true,
            },
            (_, tally) =>
            {
                tally.Count++;
                if (permitted.HasValue)
                {
                    tally.HasVerdict = true;
                    tally.AllPermitted &= permitted.Value;
                }

                return tally;
            });
    }

    // Builds the audit rows. For invocations recorded WITH a verdict (the execution
    // path), the recorded verdict is authoritative — it was evaluated with the real
    // parameters. For legacy count-only invocations, falls back to re-evaluating the
    // live permission engine. <paramref name="authorizer"/> and
    // <paramref name="registry"/> may be null (e.g. permissions not wired); in that
    // case an unresolved "permitted" defaults to false (fail-closed).
    public IReadOnlyList<ToolUsageAuditEntry> BuildEntries(
        IToolPermissionAuthorizer? authorizer,
        IToolRegistry? registry)
    {
        return _invocations
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new ToolUsageAuditEntry(
                kv.Key,
                kv.Value.Count,
                kv.Value.HasVerdict
                    ? kv.Value.AllPermitted
                    : IsPermitted(kv.Key, authorizer, registry)))
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
