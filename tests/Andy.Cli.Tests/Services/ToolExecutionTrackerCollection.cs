using Xunit;

namespace Andy.Cli.Tests.Services;

/// <summary>
/// Serializes every test that touches <see cref="Andy.Cli.Services.ToolExecutionTracker"/>.
///
/// The tracker is a process-wide singleton holding the feed view and the pending-call queues, so
/// two test classes exercising it at once will claim each other's rows and null each other's feed
/// out from under them. xUnit runs test classes in parallel by default; sharing one collection
/// with parallelization disabled is what keeps them honest.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ToolExecutionTrackerCollection
{
    /// <summary>Collection name to put on every class that uses the tracker singleton.</summary>
    public const string Name = "tool-execution-tracker";
}
