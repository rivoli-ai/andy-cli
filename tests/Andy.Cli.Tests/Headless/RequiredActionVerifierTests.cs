using Andy.Cli.Headless;
using Andy.Cli.HeadlessConfig;
using Xunit;

namespace Andy.Cli.Tests.Headless;

public class RequiredActionVerifierTests
{
    [Theory]
    [InlineData(ToolCallOutcome.Failed)]
    [InlineData(ToolCallOutcome.Denied)]
    [InlineData(ToolCallOutcome.TimedOut)]
    [InlineData(ToolCallOutcome.Cancelled)]
    public void NonSuccessOutcome_NeverSatisfiesRequirement(string outcome)
    {
        var verifier = NewVerifier(new HeadlessRequiredAction { ToolName = "execute_command" });
        verifier.RecordTerminalOutcome(
            "call-1",
            "execute_command",
            new Dictionary<string, object?> { ["command"] = "dotnet test" },
            outcome);

        var result = verifier.Verify();

        Assert.False(result.Satisfied);
        var requirement = Assert.Single(result.Requirements);
        Assert.Equal(1, requirement.ObservedMatches);
        Assert.Equal(0, requirement.SuccessfulMatches);
        Assert.Equal(outcome, Assert.Single(requirement.Calls).Outcome);
    }

    [Fact]
    public void AtLeast_RequiresConfiguredNumberOfSuccessfulCalls()
    {
        var verifier = NewVerifier(new HeadlessRequiredAction
        {
            ToolName = "read_file",
            AtLeast = 2
        });
        verifier.RecordTerminalOutcome("call-1", "read_file", null, ToolCallOutcome.Success);

        Assert.False(verifier.Verify().Satisfied);

        verifier.RecordTerminalOutcome("call-2", "read_file", null, ToolCallOutcome.Success);
        var result = verifier.Verify();

        Assert.True(result.Satisfied);
        var requirement = Assert.Single(result.Requirements);
        Assert.Equal(2, requirement.SuccessfulMatches);
        Assert.Equal(new[] { "call-1", "call-2" }, requirement.Calls.Select(call => call.CallId));
    }

    [Fact]
    public void CommandEquals_UsesExactNormalizedCommandAndStableCallIdentity()
    {
        var verifier = NewVerifier(new HeadlessRequiredAction
        {
            ToolName = "execute_command",
            CommandEquals = "dotnet test"
        });
        verifier.RecordTerminalOutcome(
            "wrong-call",
            "execute_command",
            new Dictionary<string, object?> { ["command"] = "dotnet build" },
            ToolCallOutcome.Success);
        verifier.RecordTerminalOutcome(
            "matched-call",
            "execute_command",
            new Dictionary<string, object?> { ["command"] = "dotnet test" },
            ToolCallOutcome.Success);

        var result = verifier.Verify();

        Assert.True(result.Satisfied);
        var requirement = Assert.Single(result.Requirements);
        var evidence = Assert.Single(requirement.Calls);
        Assert.Equal("matched-call", evidence.CallId);
        Assert.Equal(ToolCallOutcome.Success, evidence.Outcome);
    }

    [Fact]
    public void Evidence_IsBoundedWithoutChangingCounts()
    {
        var verifier = NewVerifier(new HeadlessRequiredAction { ToolName = "read_file" });
        for (var index = 0; index < RequiredActionVerifier.MaxEvidenceCallsPerRequirement + 5; index++)
        {
            verifier.RecordTerminalOutcome(
                $"call-{index}",
                "read_file",
                null,
                ToolCallOutcome.Success);
        }

        var requirement = Assert.Single(verifier.Verify().Requirements);

        Assert.Equal(RequiredActionVerifier.MaxEvidenceCallsPerRequirement + 5, requirement.SuccessfulMatches);
        Assert.Equal(RequiredActionVerifier.MaxEvidenceCallsPerRequirement, requirement.Calls.Count);
    }

    private static RequiredActionVerifier NewVerifier(params HeadlessRequiredAction[] requirements)
        => new(requirements);
}
