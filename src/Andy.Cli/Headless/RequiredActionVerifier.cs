using Andy.Cli.HeadlessConfig;

namespace Andy.Cli.Headless;

/// <summary>
/// Verifies declared headless actions from terminal outcomes observed at the
/// real tool-execution boundary. Model text is never considered evidence.
/// </summary>
public sealed class RequiredActionVerifier
{
    internal const int MaxEvidenceCallsPerRequirement = 16;

    private sealed record ObservedCall(
        long Sequence,
        string CallId,
        string ToolName,
        string Outcome,
        object? Command);

    private readonly IReadOnlyList<HeadlessRequiredAction> _requirements;
    private readonly List<ObservedCall> _calls = [];
    private readonly object _gate = new();
    private long _sequence;

    public RequiredActionVerifier(IReadOnlyList<HeadlessRequiredAction>? requirements)
        => _requirements = requirements ?? [];

    public bool HasRequirements => _requirements.Count > 0;

    public void RecordTerminalOutcome(
        string callId,
        string toolName,
        IReadOnlyDictionary<string, object?>? parameters,
        string outcome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        if (!HasRequirements || !_requirements.Any(requirement =>
            string.Equals(requirement.ToolName, toolName, StringComparison.Ordinal)))
        {
            return;
        }

        object? command = null;
        parameters?.TryGetValue("command", out command);
        lock (_gate)
        {
            _calls.Add(new ObservedCall(
                ++_sequence,
                callId,
                toolName,
                outcome,
                command));
        }
    }

    public RequiredActionVerificationResult Verify()
    {
        ObservedCall[] calls;
        lock (_gate)
        {
            calls = _calls.OrderBy(call => call.Sequence).ToArray();
        }

        var entries = _requirements
            .Select((requirement, index) => VerifyRequirement(index, requirement, calls))
            .ToArray();
        return new RequiredActionVerificationResult(
            entries.All(entry => entry.Satisfied),
            entries);
    }

    private static RequiredActionVerificationEntry VerifyRequirement(
        int index,
        HeadlessRequiredAction requirement,
        IReadOnlyList<ObservedCall> calls)
    {
        var matching = calls
            .Where(call =>
                string.Equals(call.ToolName, requirement.ToolName, StringComparison.Ordinal)
                && (requirement.CommandEquals is null
                    || RequiredCommandMatcher.IsExactMatch(requirement.CommandEquals, call.Command)))
            .ToArray();
        var successes = matching.Count(call =>
            string.Equals(call.Outcome, ToolCallOutcome.Success, StringComparison.Ordinal));
        var minimum = requirement.AtLeast > 0 ? requirement.AtLeast : 1;
        var evidence = matching
            .Take(MaxEvidenceCallsPerRequirement)
            .Select(call => new RequiredActionCallEvidence(call.CallId, call.Outcome))
            .ToArray();

        return new RequiredActionVerificationEntry(
            index,
            requirement.ToolName,
            requirement.CommandEquals,
            minimum,
            matching.Length,
            successes,
            successes >= minimum,
            evidence);
    }
}

public sealed record RequiredActionVerificationResult(
    bool Satisfied,
    IReadOnlyList<RequiredActionVerificationEntry> Requirements);

public sealed record RequiredActionVerificationEntry(
    int Index,
    string ToolName,
    string? CommandEquals,
    int AtLeast,
    int ObservedMatches,
    int SuccessfulMatches,
    bool Satisfied,
    IReadOnlyList<RequiredActionCallEvidence> Calls);

public sealed record RequiredActionCallEvidence(string CallId, string Outcome);
