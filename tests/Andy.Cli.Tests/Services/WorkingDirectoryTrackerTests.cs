using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Tools.Core;
using Xunit;

namespace Andy.Cli.Tests.Services;

/// <summary>
/// Regression tests for rivoli-ai/andy-cli#235: the current directory shown at the top of the UI
/// must follow the working directory the tools actually operate in.
///
/// Root cause: the engine's SimpleAgent freezes Environment.CurrentDirectory at construction and
/// stamps that snapshot into every ToolExecutionContext, while a `cd` inside execute_command dies
/// with its child shell - so nothing could ever move the working directory, and the header path
/// could never change. The fix introduces <see cref="WorkingDirectoryTracker"/> as the shared
/// source of truth: <see cref="UiUpdatingToolExecutor"/> stamps it into every tool context and
/// applies standalone `cd` commands to it, and the header renders
/// WorkingDirectoryTracker.Instance.Current each frame.
/// </summary>
public class WorkingDirectoryTrackerTests
{
    // ---- cd parsing ------------------------------------------------------------------------

    [Fact]
    public void ResolveCdTarget_AbsolutePath_ReturnsThatPath()
    {
        var target = WorkingDirectoryTracker.ResolveCdTarget("cd /usr/local", "/anywhere");
        Assert.Equal(Path.GetFullPath("/usr/local"), target);
    }

    [Fact]
    public void ResolveCdTarget_RelativePath_ResolvesAgainstCurrentDirectory()
    {
        var current = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wdt-base"));
        var target = WorkingDirectoryTracker.ResolveCdTarget("cd sub/dir", current);
        Assert.Equal(Path.GetFullPath(Path.Combine(current, "sub", "dir")), target);
    }

    [Fact]
    public void ResolveCdTarget_ParentTraversal_Normalizes()
    {
        var current = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wdt-base", "child"));
        var target = WorkingDirectoryTracker.ResolveCdTarget("cd ..", current);
        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wdt-base")), target);
    }

    [Theory]
    [InlineData("cd")]
    [InlineData("cd ~")]
    public void ResolveCdTarget_BareCdOrTilde_ReturnsHome(string command)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(home, WorkingDirectoryTracker.ResolveCdTarget(command, "/anywhere"));
    }

    [Fact]
    public void ResolveCdTarget_QuotedPathWithSpaces_IsUnquoted()
    {
        var target = WorkingDirectoryTracker.ResolveCdTarget("cd \"/tmp/My Dir\"", "/anywhere");
        Assert.Equal(Path.GetFullPath("/tmp/My Dir"), target);
    }

    [Theory]
    [InlineData("ls")]
    [InlineData("echo cd /tmp")]
    [InlineData("cd /tmp && ls")]
    [InlineData("cd /tmp; ls")]
    [InlineData("cd /tmp | cat")]
    [InlineData("cd /tmp > out.txt")]
    [InlineData("cd $(pwd)")]
    [InlineData("cd -")]
    [InlineData("cdx /tmp")]
    [InlineData("")]
    [InlineData(null)]
    public void ResolveCdTarget_NonSimpleCdCommands_ReturnNull(string? command)
    {
        Assert.Null(WorkingDirectoryTracker.ResolveCdTarget(command, "/anywhere"));
    }

    // ---- tracker state ---------------------------------------------------------------------

    [Fact]
    public void TrySet_NonexistentDirectory_ReturnsFalseAndKeepsCurrent()
    {
        var tracker = new WorkingDirectoryTracker();
        var before = tracker.Current;

        Assert.False(tracker.TrySet(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}")));
        Assert.Equal(before, tracker.Current);
    }

    [Fact]
    public void TrySet_ExistingDirectory_UpdatesCurrentAndRaisesChanged()
    {
        var dir = Directory.CreateTempSubdirectory("wdt-set-").FullName;
        try
        {
            var tracker = new WorkingDirectoryTracker();
            string? observed = null;
            tracker.Changed += d => observed = d;

            Assert.True(tracker.TrySet(dir));
            Assert.Equal(Path.GetFullPath(dir), tracker.Current);
            Assert.Equal(Path.GetFullPath(dir), observed);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ApplyExecutedCommand_SimpleCdToExistingDirectory_MovesTracker()
    {
        var dir = Directory.CreateTempSubdirectory("wdt-apply-").FullName;
        try
        {
            var tracker = new WorkingDirectoryTracker();
            var applied = tracker.ApplyExecutedCommand($"cd {dir}");
            Assert.Equal(Path.GetFullPath(dir), applied);
            Assert.Equal(Path.GetFullPath(dir), tracker.Current);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ApplyExecutedCommand_CdToMissingDirectory_DoesNotMoveTracker()
    {
        var tracker = new WorkingDirectoryTracker();
        var before = tracker.Current;

        Assert.Null(tracker.ApplyExecutedCommand($"cd /definitely-missing-{Guid.NewGuid():N}"));
        Assert.Equal(before, tracker.Current);
    }

    // ---- executor integration (the real interactive dispatch path) --------------------------

    [Fact]
    public async Task ExecuteCommandCd_UpdatesTracker_AndSubsequentToolContexts()
    {
        var dirA = Directory.CreateTempSubdirectory("wdt-a-").FullName;
        var dirB = Directory.CreateTempSubdirectory("wdt-b-").FullName;
        try
        {
            var tracker = new WorkingDirectoryTracker(dirA);
            var inner = new CapturingToolExecutor(succeed: true);
            var exec = new UiUpdatingToolExecutor(inner, workingDirectoryTracker: tracker);

            // The model changes directory via execute_command.
            var result = await exec.ExecuteAsync(
                "execute_command",
                new Dictionary<string, object?> { ["command"] = $"cd {dirB}" });

            Assert.True(result.IsSuccessful);
            Assert.Equal(Path.GetFullPath(dirB), tracker.Current);

            // A later tool call must operate in the new directory: the executor stamps the
            // tracked directory into the context the inner executor receives.
            await exec.ExecuteAsync("list_directory", new Dictionary<string, object?> { ["path"] = "." });
            Assert.Equal(Path.GetFullPath(dirB), inner.LastContext?.WorkingDirectory);
        }
        finally
        {
            Directory.Delete(dirA, recursive: true);
            Directory.Delete(dirB, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteCommandCd_OverridesFrozenAgentSnapshot_InDispatchedContext()
    {
        // SimpleAgent stamps a startup snapshot into context.WorkingDirectory on every call.
        // Once the session cd's, that snapshot is stale and must be overridden by the tracker.
        var dirLive = Directory.CreateTempSubdirectory("wdt-live-").FullName;
        try
        {
            var tracker = new WorkingDirectoryTracker(dirLive);
            var inner = new CapturingToolExecutor(succeed: true);
            var exec = new UiUpdatingToolExecutor(inner, workingDirectoryTracker: tracker);

            var frozenContext = new ToolExecutionContext { WorkingDirectory = "/stale/startup/snapshot" };
            await exec.ExecuteAsync("read_file", new Dictionary<string, object?> { ["file_path"] = "x" }, frozenContext);

            Assert.Equal(Path.GetFullPath(dirLive), inner.LastContext?.WorkingDirectory);
        }
        finally
        {
            Directory.Delete(dirLive, recursive: true);
        }
    }

    [Fact]
    public async Task FailedExecuteCommand_DoesNotMoveTracker()
    {
        var dirA = Directory.CreateTempSubdirectory("wdt-fail-a-").FullName;
        var dirB = Directory.CreateTempSubdirectory("wdt-fail-b-").FullName;
        try
        {
            var tracker = new WorkingDirectoryTracker(dirA);
            var inner = new CapturingToolExecutor(succeed: false);
            var exec = new UiUpdatingToolExecutor(inner, workingDirectoryTracker: tracker);

            await exec.ExecuteAsync(
                "execute_command",
                new Dictionary<string, object?> { ["command"] = $"cd {dirB}" });

            Assert.Equal(Path.GetFullPath(dirA), tracker.Current);
        }
        finally
        {
            Directory.Delete(dirA, recursive: true);
            Directory.Delete(dirB, recursive: true);
        }
    }

    [Fact]
    public async Task CompoundCommandWithLeadingCd_DoesNotMoveTracker()
    {
        // `cd x && make` affects that one shell invocation only, matching how each
        // execute_command runs in its own process; only a standalone cd persists.
        var dirA = Directory.CreateTempSubdirectory("wdt-comp-a-").FullName;
        var dirB = Directory.CreateTempSubdirectory("wdt-comp-b-").FullName;
        try
        {
            var tracker = new WorkingDirectoryTracker(dirA);
            var inner = new CapturingToolExecutor(succeed: true);
            var exec = new UiUpdatingToolExecutor(inner, workingDirectoryTracker: tracker);

            await exec.ExecuteAsync(
                "execute_command",
                new Dictionary<string, object?> { ["command"] = $"cd {dirB} && ls" });

            Assert.Equal(Path.GetFullPath(dirA), tracker.Current);
        }
        finally
        {
            Directory.Delete(dirA, recursive: true);
            Directory.Delete(dirB, recursive: true);
        }
    }

    /// <summary>Inner executor that records the context it receives and returns a fixed outcome.</summary>
    private sealed class CapturingToolExecutor : IToolExecutor
    {
        private readonly bool _succeed;
        public ToolExecutionContext? LastContext { get; private set; }

        public CapturingToolExecutor(bool succeed) => _succeed = succeed;

#pragma warning disable CS0067 // events required by IToolExecutor but unused by this fake
        public event EventHandler<ToolExecutionStartedEventArgs>? ExecutionStarted;
        public event EventHandler<ToolExecutionCompletedEventArgs>? ExecutionCompleted;
        public event EventHandler<SecurityViolationEventArgs>? SecurityViolation;
#pragma warning restore CS0067

        public Task<ToolExecutionResult> ExecuteAsync(string toolId, Dictionary<string, object?> parameters, ToolExecutionContext? context = null)
        {
            LastContext = context;
            return Task.FromResult(new ToolExecutionResult
            {
                IsSuccessful = _succeed,
                Message = _succeed ? "ok" : "failed",
                Data = _succeed ? "ok" : null
            });
        }

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
            => ExecuteAsync(request.ToolId, request.Parameters, request.Context);

        public Task<IList<string>> ValidateExecutionRequestAsync(ToolExecutionRequest request)
            => Task.FromResult<IList<string>>(new List<string>());

        public Task<ToolResourceUsage?> EstimateResourceUsageAsync(string toolId, Dictionary<string, object?> parameters)
            => Task.FromResult<ToolResourceUsage?>(null);

        public Task<int> CancelExecutionsAsync(string correlationId) => Task.FromResult(0);

        public IReadOnlyList<RunningExecutionInfo> GetRunningExecutions() => new List<RunningExecutionInfo>();

        public ToolExecutionStatistics GetStatistics() => new ToolExecutionStatistics();
    }
}
