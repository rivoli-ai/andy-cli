using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Configuration;
using Andy.Cli.Services;
using Andy.Cli.Services.Sessions;
using Andy.Cli.Services.Shell;
using Andy.Permissions.DependencyInjection;
using Andy.Tools.Core;
using Andy.Tools.Core.OutputLimiting;
using Andy.Tools.Discovery;
using Andy.Tools.Execution;
using Andy.Tools.Framework;
using Andy.Tools.Registry;
using Andy.Tools.Validation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Cli.Tests.Integration;

/// <summary>
/// Interactive shell escape (issue #286) executed end to end through the REAL permission-gated
/// executor - the same wiring <see cref="ExecuteCommandToolIntegrationTests"/> exercises for the
/// model's shell tool. That is the point of these tests: a command the user typed must be subject
/// to exactly the same gate, so a rule that stops the model stops the user too (and a future
/// Plan-mode deny overlay, which installs rules on the same layered store, needs no code here).
///
/// The permission store is isolated from user/project files so results are deterministic.
/// </summary>
[Collection("bash-tool-env")]
public sealed class UserShellCommandRunnerTests
{
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>Builds an andy-cli-representative provider with an isolated permission store.</summary>
    private static ServiceProvider BuildProvider(Andy.Permissions.Prompt.IPermissionPrompt? prompt = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (prompt is not null)
        {
            services.AddSingleton(prompt); // before AddAndyPermissions, which TryAdds a default
        }

        services.AddSingleton<IToolValidator, ToolValidator>();
        services.AddSingleton<IToolRegistry, Andy.Tools.Registry.ToolRegistry>();
        services.AddSingleton<IToolDiscovery, ToolDiscoveryService>();
        services.AddSingleton<ISecurityManager, SecurityManager>();
        services.AddSingleton<IResourceMonitor, ResourceMonitor>();
        services.AddSingleton<IToolOutputLimiter, ToolOutputLimiter>();
        services.AddSingleton<IToolExecutor, ToolExecutor>();
        services.AddSingleton<IPermissionProfileService, PermissionProfileService>();
        services.AddSingleton(new ToolFrameworkOptions
        {
            RegisterBuiltInTools = false,
            EnableObservability = false,
            AutoDiscoverTools = false,
        });

        ToolCatalog.RegisterAllTools(services);

        services.AddAndyPermissions(o =>
        {
            o.UserFilePath = null;
            o.ProjectFilePath = null;
            o.LocalFilePath = null;
            o.ManagedFilePath = null;
        });

        var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<IToolRegistry>();
        foreach (var reg in sp.GetServices<ToolRegistrationInfo>())
        {
            registry.RegisterTool(reg.ToolType, reg.Configuration);
        }
        return sp;
    }

    /// <summary>
    /// Installs a container-style injected allow rule for the duration of the scope, so a test can
    /// exercise EXECUTION behaviour (streams, cancellation, truncation) without also re-testing the
    /// gate. Must wrap the <see cref="BuildProvider"/> call: the injected layer is read when the
    /// permission store is constructed.
    /// </summary>
    private sealed class AllowAllCommands : IDisposable
    {
        private readonly string? _previous;

        public AllowAllCommands()
        {
            _previous = Environment.GetEnvironmentVariable(PermissionInjectionBootstrap.JsonEnvVar);
            Environment.SetEnvironmentVariable(PermissionInjectionBootstrap.JsonEnvVar,
                """{ "allow": ["execute_command(*)"] }""");
        }

        public void Dispose() =>
            Environment.SetEnvironmentVariable(PermissionInjectionBootstrap.JsonEnvVar, _previous);
    }

    private static UserShellCommandRunner Runner(
        ServiceProvider sp,
        ShellEscapeOptions? options = null,
        WorkingDirectoryTracker? tracker = null)
        => new(sp.GetRequiredService<IToolExecutor>(),
               options ?? ShellEscapeOptions.Default,
               tracker ?? new WorkingDirectoryTracker(System.IO.Path.GetTempPath()));

    // --- happy path ----------------------------------------------------------------------

    [Fact]
    public async Task KnownSafeCommand_RunsAndReportsStdoutAndExitCode()
    {
        using var sp = BuildProvider();

        var result = await Runner(sp).RunAsync("echo hello_shell_escape");

        Assert.Equal(UserShellOutcome.Succeeded, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello_shell_escape", result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.False(result.TimedOut);
        Assert.False(result.WasTruncated);
        Assert.Equal("exit 0", result.StatusLabel);
    }

    [Fact]
    public async Task NonZeroExit_IsReportedAsFailureWithItsExitCode()
    {
        using var sp = BuildProvider();

        // "false" is known-safe and exits non-zero.
        var result = await Runner(sp).RunAsync("false");

        Assert.Equal(UserShellOutcome.Failed, result.Outcome);
        Assert.NotEqual(0, result.ExitCode ?? 0);
        Assert.StartsWith("exit ", result.StatusLabel);
    }

    [Fact]
    public async Task Stderr_IsCapturedSeparatelyFromStdout()
    {
        if (IsWindows) return; // POSIX shell syntax
        using var allow = new AllowAllCommands();
        using var sp = BuildProvider();

        // One line that writes to both streams. They must arrive separately, or the feed cannot
        // colour stderr differently and a build's error summary drowns in its own progress output.
        var result = await Runner(sp).RunAsync("echo out_stream; ls /nope_andy_286");

        Assert.Contains("out_stream", result.StandardOutput);
        Assert.Contains("nope_andy_286", result.StandardError);
        Assert.DoesNotContain("No such file", result.StandardOutput);
        Assert.Equal(UserShellOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task QuotesPipesAndUnicode_FollowTheShellContract()
    {
        if (IsWindows) return; // bash -c semantics
        using var allow = new AllowAllCommands();
        using var sp = BuildProvider();

        // Quoting, a pipe and non-ASCII text in one line: the command string is handed to the
        // shell verbatim, so the shell's own rules apply and nothing here re-quotes or re-escapes.
        var result = await Runner(sp).RunAsync("echo 'café 你好' | tr -d ' '");

        Assert.Equal(UserShellOutcome.Succeeded, result.Outcome);
        Assert.Contains("café", result.StandardOutput);
        Assert.Contains("你好", result.StandardOutput);
    }

    [Fact]
    public async Task Redirects_AreHonouredByTheShellItself()
    {
        if (IsWindows) return;
        // A redirect makes the gate ask (the packaged rule matcher does not decompose ">" into an
        // allowable segment), which is itself the contract under test: the user consents, then the
        // shell - not this code - performs the redirect.
        // Once-scoped, so this consent cannot leak into another test through the process-static
        // session-consent store (#170).
        using var sp = BuildProvider(new AllowOncePrompt());
        var target = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "andy-shell-escape-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var result = await Runner(sp).RunAsync($"echo redirected_by_shell_escape > {target}");

            Assert.Equal(UserShellOutcome.Succeeded, result.Outcome);
            Assert.True(System.IO.File.Exists(target), "the shell should have created the file");
            Assert.Contains("redirected_by_shell_escape", System.IO.File.ReadAllText(target));
            // The redirect took the output, so nothing came back on stdout.
            Assert.Equal(string.Empty, result.StandardOutput.Trim());
        }
        finally
        {
            try { System.IO.File.Delete(target); } catch (System.IO.IOException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task MultilineCommand_RunsAsOneShellScript()
    {
        if (IsWindows) return;
        using var sp = BuildProvider();

        var result = await Runner(sp).RunAsync("echo first\necho second");

        Assert.Equal(UserShellOutcome.Succeeded, result.Outcome);
        Assert.Contains("first", result.StandardOutput);
        Assert.Contains("second", result.StandardOutput);
    }

    // --- permissions ---------------------------------------------------------------------

    [Fact]
    public async Task NeutralCommand_IsDeniedByTheGateWhenNoRuleAllowsIt()
    {
        // No rule + no interactive prompt (fail-closed) is the same evaluation the model gets.
        using var sp = BuildProvider();

        var result = await Runner(sp).RunAsync("dotnet --list-sdks");

        Assert.Equal(UserShellOutcome.Denied, result.Outcome);
        Assert.Equal("denied", result.StatusLabel);
        Assert.Contains("permission", result.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.True(result.HasNoOutput);
    }

    [Fact]
    public async Task DangerousCommand_IsBlockedByTheBuiltinDenyRule()
    {
        // Dangerous-command handling is the gate's, not shell escape's: the same builtin deny that
        // stops the model stops a user-typed command, before any process starts.
        using var sp = BuildProvider();

        var result = await Runner(sp).RunAsync("rm -rf /");

        Assert.Equal(UserShellOutcome.Denied, result.Outcome);
        Assert.Contains("permission", result.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InjectedAllowRule_LetsTheSameCommandRun()
    {
        using var allow = new AllowAllCommands();
        using var sp = BuildProvider();
        var result = await Runner(sp).RunAsync("dotnet --list-sdks");

        Assert.Equal(UserShellOutcome.Succeeded, result.Outcome);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ApprovalPrompt_IsRaisedOnceAndTheSessionScopeIsRemembered()
    {
        // Persisted approval scopes behave exactly as they do for model-invoked commands: the same
        // IPermissionPrompt, the same PersistScope.Session grant, no second prompt.
        var prompt = new AllowSessionPrompt();
        using var sp = BuildProvider(prompt);
        var runner = Runner(sp);

        // A neutral command distinct from the other tests' - session consent lives in a
        // process-static store that outlives the per-test provider (see #170).
        var first = await runner.RunAsync("dotnet --list-runtimes");
        Assert.Equal(UserShellOutcome.Succeeded, first.Outcome);
        Assert.Equal(1, prompt.CallCount);

        var second = await runner.RunAsync("dotnet --list-runtimes");
        Assert.Equal(UserShellOutcome.Succeeded, second.Outcome);
        Assert.Equal(1, prompt.CallCount);
    }

    [Fact]
    public async Task DeniedApprovalPrompt_BlocksTheCommand()
    {
        var prompt = new DenyPrompt();
        using var sp = BuildProvider(prompt);

        var result = await Runner(sp).RunAsync("dotnet --list-references-that-do-not-exist");

        Assert.Equal(UserShellOutcome.Denied, result.Outcome);
        Assert.Equal(1, prompt.CallCount);
    }

    private sealed class AllowSessionPrompt : Andy.Permissions.Prompt.IPermissionPrompt
    {
        public int CallCount;
        public Task<Andy.Permissions.Model.PermissionDecision> RequestAsync(
            Andy.Permissions.Model.PermissionRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CallCount);
            return Task.FromResult(new Andy.Permissions.Model.PermissionDecision(
                true, Andy.Permissions.Model.PersistScope.Session));
        }
    }

    private sealed class AllowOncePrompt : Andy.Permissions.Prompt.IPermissionPrompt
    {
        public Task<Andy.Permissions.Model.PermissionDecision> RequestAsync(
            Andy.Permissions.Model.PermissionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new Andy.Permissions.Model.PermissionDecision(
                true, Andy.Permissions.Model.PersistScope.Once));
    }

    private sealed class DenyPrompt : Andy.Permissions.Prompt.IPermissionPrompt
    {
        public int CallCount;
        public Task<Andy.Permissions.Model.PermissionDecision> RequestAsync(
            Andy.Permissions.Model.PermissionRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CallCount);
            return Task.FromResult(Andy.Permissions.Model.PermissionDecision.DenyOnce);
        }
    }

    // --- cancellation and timeout --------------------------------------------------------

    [Fact]
    public async Task Cancellation_StopsTheCommandPromptlyAndReportsCancelled()
    {
        if (IsWindows) return; // "sleep" is not a cmd.exe builtin
        using var allow = new AllowAllCommands();
        using var sp = BuildProvider();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(400));

        var stopwatch = Stopwatch.StartNew();
        var result = await Runner(sp).RunAsync("sleep 30", cts.Token);
        stopwatch.Stop();

        Assert.Equal(UserShellOutcome.Cancelled, result.Outcome);
        // The whole point of Ctrl+C: control comes back long before the command would finish.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20),
            $"cancellation took {stopwatch.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public async Task AlreadyCancelledToken_NeverStartsTheCommand()
    {
        using var sp = BuildProvider();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await Runner(sp).RunAsync("echo should_not_run", cts.Token);

        Assert.NotEqual(UserShellOutcome.Succeeded, result.Outcome);
        Assert.DoesNotContain("should_not_run", result.StandardOutput);
    }

    [Fact]
    public async Task Timeout_IsSurfacedAsCancelledAndLabelled()
    {
        if (IsWindows) return;
        using var allow = new AllowAllCommands();
        using var sp = BuildProvider();
        var options = ShellEscapeOptions.Default with { TimeoutSeconds = 1 };

        var result = await Runner(sp, options).RunAsync("sleep 20");

        Assert.Equal(UserShellOutcome.Cancelled, result.Outcome);
        Assert.True(result.TimedOut, "the tool should report timed_out");
        Assert.Equal("timed out", result.StatusLabel);
    }

    // --- output limits, redaction, working directory --------------------------------------

    [Fact]
    public async Task Output_IsCappedAtTheConfiguredBudgetAndReportsWhatWasDropped()
    {
        if (IsWindows) return;
        using var allow = new AllowAllCommands();
        using var sp = BuildProvider();
        var options = ShellEscapeOptions.Default with { MaxOutputCharacters = 64 };

        var result = await Runner(sp, options).RunAsync("seq 1 500");

        Assert.Equal(64, result.StandardOutput.Length);
        Assert.True(result.WasTruncated);
        Assert.True(result.StandardOutputTruncated > 0);
    }

    [Fact]
    public void Bound_KeepsShortOutputIntactAndTrimsLongOutput()
    {
        using var sp = BuildProvider();
        var runner = Runner(sp, ShellEscapeOptions.Default with { MaxOutputCharacters = 10 });

        Assert.Equal(("short", 0), runner.Bound("short"));
        Assert.Equal(("0123456789", 3), runner.Bound("0123456789abc"));
        Assert.Equal((string.Empty, 0), runner.Bound(null));
    }

    [Fact]
    public async Task Redact_ScrubsSecretsFromTheCommandAndItsOutput()
    {
        if (IsWindows) return;
        using var allow = new AllowAllCommands();
        using var sp = BuildProvider();
        // Built at runtime so a repository secret scanner does not flag the fixture.
        var fakeKey = string.Concat("sk", "-", "abcdefghijklmnop");
        var result = await Runner(sp).RunAsync($"echo api_key={fakeKey}");

        // The FEED sees the real thing - it is the user's own terminal.
        Assert.Contains(fakeKey, result.StandardOutput);

        // Everything that leaves the terminal is scrubbed first.
        var redacted = result.Redact(new SessionRedactor(Array.Empty<string>()));
        Assert.DoesNotContain(fakeKey, redacted.StandardOutput);
        Assert.DoesNotContain(fakeKey, redacted.Command);
        Assert.Contains(SessionRedactor.Replacement, redacted.StandardOutput);
    }

    [Fact]
    public async Task WorkingDirectory_ComesFromTheTrackerAndAStandaloneCdPersists()
    {
        using var allow = new AllowAllCommands();
        using var sp = BuildProvider();
        var start = System.IO.Path.GetTempPath().TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var tracker = new WorkingDirectoryTracker(start);
        var runner = Runner(sp, tracker: tracker);

        // "pwd" is known-safe; no "cd" preamble is added to the command, so the string the gate
        // previewed is the string that ran.
        var pwd = await runner.RunAsync(IsWindows ? "cd" : "pwd");
        Assert.Equal(UserShellOutcome.Succeeded, pwd.Outcome);
        Assert.Equal(tracker.Current, pwd.WorkingDirectory);

        var parent = System.IO.Directory.GetParent(start)!.FullName;
        var moved = await runner.RunAsync("cd " + parent);
        Assert.Equal(UserShellOutcome.Succeeded, moved.Outcome);
        Assert.Equal(parent, tracker.Current);
    }

    // --- disable switch ------------------------------------------------------------------

    [Fact]
    public async Task DisabledOptions_NeverReachTheExecutor()
    {
        var executor = new ThrowingExecutor();
        var runner = new UserShellCommandRunner(executor, ShellEscapeOptions.Disabled,
            new WorkingDirectoryTracker(System.IO.Path.GetTempPath()));

        var result = await runner.RunAsync("echo nope");

        Assert.Equal(UserShellOutcome.Disabled, result.Outcome);
        Assert.Equal(0, executor.Calls);
        Assert.Equal("disabled", result.StatusLabel);
    }

    [Fact]
    public async Task BlankCommand_IsRejectedWithoutTouchingTheExecutor()
    {
        var executor = new ThrowingExecutor();
        var runner = new UserShellCommandRunner(executor, ShellEscapeOptions.Default,
            new WorkingDirectoryTracker(System.IO.Path.GetTempPath()));

        var result = await runner.RunAsync("   ");

        Assert.Equal(UserShellOutcome.Disabled, result.Outcome);
        Assert.Equal(0, executor.Calls);
    }

    private sealed class ThrowingExecutor : IToolExecutor
    {
        public int Calls;

        public event EventHandler<ToolExecutionStartedEventArgs>? ExecutionStarted { add { } remove { } }
        public event EventHandler<ToolExecutionCompletedEventArgs>? ExecutionCompleted { add { } remove { } }
        public event EventHandler<SecurityViolationEventArgs>? SecurityViolation { add { } remove { } }

        public Task<ToolExecutionResult> ExecuteAsync(string toolId, Dictionary<string, object?> parameters, ToolExecutionContext? context = null)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("A disabled or blank shell escape must never dispatch a tool.");
        }

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request)
            => ExecuteAsync(request.ToolId, request.Parameters, request.Context);

        public Task<IList<string>> ValidateExecutionRequestAsync(ToolExecutionRequest request)
            => Task.FromResult<IList<string>>(new List<string>());

        public Task<ToolResourceUsage?> EstimateResourceUsageAsync(string toolId, Dictionary<string, object?> parameters)
            => Task.FromResult<ToolResourceUsage?>(null);

        public Task<int> CancelExecutionsAsync(string? toolId = null) => Task.FromResult(0);

        public IReadOnlyList<RunningExecutionInfo> GetRunningExecutions() => Array.Empty<RunningExecutionInfo>();

        public ToolExecutionStatistics GetStatistics() => new();
    }
}
