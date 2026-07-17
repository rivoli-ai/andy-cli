// rivoli-ai/andy-cli#179: ToolUsageAuditor must record the ACTUAL permission
// verdict observed on the execution path (evaluated with the real parameters),
// rather than re-evaluating at end-of-run with an empty parameter bag. These
// tests cover the verdict-recording overload and its fail-closed aggregation.

using Andy.Cli.Headless;
using Xunit;

namespace Andy.Cli.Tests.Headless;

public class ToolUsageAuditorVerdictTests
{
    [Fact]
    public void RecordedVerdict_IsAuthoritative_NoAuthorizerNeeded()
    {
        var auditor = new ToolUsageAuditor();
        auditor.RecordInvocation("read_file", permitted: true);
        auditor.RecordInvocation("delete_file", permitted: false);

        // No authorizer/registry supplied: recorded verdicts must still be used.
        var entries = auditor.BuildEntries(authorizer: null, registry: null);

        var read = Assert.Single(entries, e => e.ToolName == "read_file");
        Assert.True(read.Permitted);
        var delete = Assert.Single(entries, e => e.ToolName == "delete_file");
        Assert.False(delete.Permitted);
    }

    [Fact]
    public void MixedVerdicts_AggregateFailClosed()
    {
        var auditor = new ToolUsageAuditor();
        auditor.RecordInvocation("write_file", permitted: true);
        auditor.RecordInvocation("write_file", permitted: false);

        var entry = Assert.Single(auditor.BuildEntries(authorizer: null, registry: null));
        Assert.Equal(2, entry.Invocations);
        // permitted only if EVERY invocation was permitted.
        Assert.False(entry.Permitted);
    }

    [Fact]
    public void LegacyCountOnly_FallsBackToNotPermitted_WhenNoAuthorizer()
    {
        var auditor = new ToolUsageAuditor();
        auditor.RecordInvocation("read_file"); // no verdict recorded

        var entry = Assert.Single(auditor.BuildEntries(authorizer: null, registry: null));
        Assert.Equal(1, entry.Invocations);
        Assert.False(entry.Permitted); // fail-closed when unresolved
    }
}
