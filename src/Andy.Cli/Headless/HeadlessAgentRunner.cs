using System.Diagnostics;
using System.IO;
using System.Text;
using Andy.Cli.HeadlessConfig;
using Andy.Engine;
using Andy.Llm;
using Andy.Llm.Extensions;
using Andy.Llm.Providers;
using Andy.Model.Llm;
using Andy.Tools.Core;
using Andy.Tools.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Headless;

// AQ3 (rivoli-ai/andy-cli#44) agent execution loop.
//
// HeadlessRunner.RunAsync delegates here once the AQ2 scaffolding has
// parsed args and validated the config. Responsibilities:
//
//   1. Stand up a per-run IServiceProvider with Andy.Tools + Andy.Llm DI.
//   2. Build an IToolRegistry from config.tools[] via HeadlessToolHost
//      (one McpClient per endpoint, one CliSubprocessTool per cli binding).
//   3. Resolve config.model → ILlmProvider via the factory.
//   4. Construct SimpleAgent with config.agent.instructions as system
//      prompt and config.limits.max_iterations as maxTurns; wire its
//      ToolCalled event into the NDJSON emitter.
//   5. Run with a wall-clock CTS for config.limits.timeout_seconds.
//      Either limit firing maps to HeadlessExitCode.Timeout (4).
//   6. On success, atomically write the LLM's final response to
//      config.output.file (tmp + rename) and emit output_written.
//   7. Always emit `started` first and `finished` last (even on failure)
//      so consumers see a clean envelope for every run.
public static class HeadlessAgentRunner
{
    // The kickoff message handed to SimpleAgent.ProcessMessageAsync. The
    // objective is already baked into config.agent.instructions (system
    // prompt = agent base + delegation_contract.objective per the headless
    // contract); the user-side prompt only needs to prime the model to
    // start producing.
    private const string KickoffMessage = "Begin.";

    public static async Task<HeadlessExitCode> ExecuteAsync(
        HeadlessRunConfig config,
        TextWriter eventStream,
        TextWriter stderr,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        var emitter = new HeadlessEventEmitter(eventStream);
        var stopwatch = Stopwatch.StartNew();
        var logger = loggerFactory.CreateLogger("Andy.Cli.Headless.HeadlessAgentRunner");
        var iterations = 0;
        var exitCode = HeadlessExitCode.Success;

        await using var services = BuildServiceProvider(config, loggerFactory);
        await using HeadlessToolHost? toolHost =
            await TryBuildToolHostAsync(config, services, loggerFactory, emitter, stderr, ct);

        if (toolHost is null)
        {
            // TryBuildToolHostAsync already emitted an error event and
            // logged. Surface the matching exit code without further chatter.
            EmitFinished(emitter, stopwatch, iterations, HeadlessExitCode.AgentFailure);
            return HeadlessExitCode.AgentFailure;
        }

        ILlmProvider? llmProvider;
        try
        {
            llmProvider = ResolveLlmProvider(services, config);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve LLM provider {Provider}", config.Model.Provider);
            emitter.EmitError($"Failed to resolve LLM provider '{config.Model.Provider}': {ex.Message}", fatal: true);
            EmitFinished(emitter, stopwatch, iterations, HeadlessExitCode.AgentFailure);
            return HeadlessExitCode.AgentFailure;
        }

        var toolExecutor = services.GetRequiredService<IToolExecutor>();

        var maxTurns = config.Limits.MaxIterations > 0 ? config.Limits.MaxIterations : 10;
        using var agent = new SimpleAgent(
            llmProvider,
            toolHost.Registry,
            toolExecutor,
            systemPrompt: config.Agent.Instructions,
            maxTurns: maxTurns,
            logger: loggerFactory.CreateLogger<SimpleAgent>());

        WireToolEvents(agent, emitter);

        emitter.EmitStarted(
            runId: config.RunId,
            agentSlug: config.Agent.Slug,
            modelProvider: config.Model.Provider,
            modelId: config.Model.Id,
            toolCount: config.Tools.Count);

        // Two cancellation sources: the caller's outer ct (SIGTERM, etc.)
        // and the wall-clock timeout. Whichever fires first short-circuits
        // SimpleAgent and we map to the corresponding exit code below.
        var timeoutSeconds = config.Limits.TimeoutSeconds > 0 ? config.Limits.TimeoutSeconds : 300;
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        SimpleAgentResult? result = null;
        try
        {
            result = await agent.ProcessMessageAsync(KickoffMessage, linkedCts.Token);
            iterations = result?.TurnCount ?? 0;
        }
        catch (OperationCanceledException)
        {
            iterations = agent.GetHistory().Count / 2;
            if (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                emitter.EmitError(
                    $"Agent loop exceeded timeout_seconds={timeoutSeconds}.",
                    fatal: true);
                exitCode = HeadlessExitCode.Timeout;
            }
            else
            {
                emitter.EmitError("Agent loop cancelled.", fatal: true);
                exitCode = HeadlessExitCode.Cancelled;
            }
            EmitFinished(emitter, stopwatch, iterations, exitCode);
            return exitCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent loop threw");
            emitter.EmitError($"Agent loop failed: {ex.GetType().Name}: {ex.Message}", fatal: true);
            EmitFinished(emitter, stopwatch, iterations, HeadlessExitCode.AgentFailure);
            return HeadlessExitCode.AgentFailure;
        }

        // SimpleAgent reports loop-level success; max_turns hits land here as
        // Success=false with StopReason="max_turns". Treat that as Timeout
        // to match the headless contract (max_iterations → exit 4).
        if (result is null || !result.Success)
        {
            var stopReason = result?.StopReason ?? "unknown";
            if (string.Equals(stopReason, "max_turns", StringComparison.OrdinalIgnoreCase))
            {
                emitter.EmitError(
                    $"Agent loop exhausted max_iterations={maxTurns}.",
                    fatal: true);
                exitCode = HeadlessExitCode.Timeout;
            }
            else
            {
                emitter.EmitError($"Agent loop did not converge: {stopReason}", fatal: true);
                exitCode = HeadlessExitCode.AgentFailure;
            }
            EmitFinished(emitter, stopwatch, iterations, exitCode);
            return exitCode;
        }

        var output = result.Response ?? string.Empty;

        try
        {
            var bytesWritten = await WriteOutputAtomicallyAsync(config.Output.File, output, ct);
            emitter.EmitOutputWritten(config.Output.File, bytesWritten);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write output file {Path}", config.Output.File);
            emitter.EmitError(
                $"Failed to write output file '{config.Output.File}': {ex.Message}",
                fatal: true);
            EmitFinished(emitter, stopwatch, iterations, HeadlessExitCode.AgentFailure);
            return HeadlessExitCode.AgentFailure;
        }

        EmitFinished(emitter, stopwatch, iterations, HeadlessExitCode.Success);
        return HeadlessExitCode.Success;
    }

    private static void EmitFinished(
        HeadlessEventEmitter emitter,
        Stopwatch stopwatch,
        int iterations,
        HeadlessExitCode code)
    {
        emitter.EmitFinished((int)code, stopwatch.ElapsedMilliseconds, iterations);
    }

    // SimpleAgent fires ToolCalled when the LLM emits a tool call but doesn't
    // surface a per-call duration; we time it on our side by stamping started
    // and finishing in the same handler chain. The args themselves stay out
    // of the event stream (digest only) — the producer can't be sure they
    // don't carry secrets.
    private static void WireToolEvents(SimpleAgent agent, HeadlessEventEmitter emitter)
    {
        agent.ToolCalled += (_, e) =>
        {
            var callId = Guid.NewGuid().ToString("N")[..12];
            emitter.EmitToolCallStarted(callId, e.ToolName, argsDigest: null);
            // SimpleAgent does not emit a ToolFinished today; until it does,
            // emit a paired finished event with ok=true and duration_ms=0.
            // Consumers compute end-of-call from the next event in the
            // stream. Upgrade this when SimpleAgent grows ToolFinished.
            emitter.EmitToolCallFinished(callId, e.ToolName, ok: true, durationMs: 0);
        };
    }

    private static ILlmProvider ResolveLlmProvider(IServiceProvider services, HeadlessRunConfig config)
    {
        var factory = services.GetRequiredService<ILlmProviderFactory>();
        var provider = factory.CreateProvider(config.Model.Provider);
        if (provider is null)
        {
            throw new InvalidOperationException(
                $"ILlmProviderFactory returned no provider for '{config.Model.Provider}'. "
                    + $"Confirm the provider env var (e.g. ANTHROPIC_API_KEY) is set; "
                    + $"api_key_ref resolution beyond the env: scheme is not implemented yet.");
        }
        return provider;
    }

    private static async Task<HeadlessToolHost?> TryBuildToolHostAsync(
        HeadlessRunConfig config,
        IServiceProvider services,
        ILoggerFactory loggerFactory,
        HeadlessEventEmitter emitter,
        TextWriter stderr,
        CancellationToken ct)
    {
        var registry = services.GetRequiredService<IToolRegistry>();
        try
        {
            return await HeadlessToolHost.BuildAsync(config.Tools, registry, loggerFactory, ct);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"andy-cli run --headless: tool wiring failed: {ex.Message}");
            // Started hasn't been emitted yet; emit error so the consumer
            // sees a structured failure instead of an empty event stream.
            emitter.EmitError($"Tool wiring failed: {ex.Message}", fatal: true);
            return null;
        }
    }

    // Per AQ4's contract (output file is atomic): write to a sibling temp
    // file in the same directory, fsync, then rename over the target. The
    // same-directory choice keeps the rename atomic on POSIX (rename(2)
    // crosses inodes only inside one filesystem).
    private static async Task<long> WriteOutputAtomicallyAsync(
        string targetPath,
        string content,
        CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        var bytes = Encoding.UTF8.GetBytes(content);

        await using (var stream = new FileStream(
            tempPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true))
        {
            await stream.WriteAsync(bytes.AsMemory(), ct);
            await stream.FlushAsync(ct);
        }

        File.Move(tempPath, targetPath, overwrite: true);
        return bytes.LongLength;
    }

    private static ServiceProvider BuildServiceProvider(HeadlessRunConfig config, ILoggerFactory loggerFactory)
    {
        var services = new ServiceCollection();
        services.AddSingleton(loggerFactory);
        services.AddLogging();

        // Andy.Llm: factory + per-provider configuration from environment
        // variables (CEREBRAS_API_KEY, ANTHROPIC_API_KEY, etc.). Default
        // provider is the one named in the run's config so the factory
        // knows which to construct on demand.
        services.ConfigureLlmFromEnvironment();
        services.AddLlmServices(options =>
        {
            options.DefaultProvider = config.Model.Provider;
        });
        services.AddSingleton<ILlmProviderFactory, LlmProviderFactory>();

        // Andy.Tools: minimal wiring — registry + executor + the validators
        // / managers ToolExecutor depends on. We deliberately don't register
        // any built-in tools (ToolCatalog.RegisterAllTools); the headless
        // run's tool surface is exactly what HeadlessToolHost adds.
        services.AddSingleton<Andy.Tools.Validation.IToolValidator, Andy.Tools.Validation.ToolValidator>();
        services.AddSingleton<IToolRegistry, Andy.Tools.Registry.ToolRegistry>();
        services.AddSingleton<Andy.Tools.Discovery.IToolDiscovery, Andy.Tools.Discovery.ToolDiscoveryService>();
        services.AddSingleton<Andy.Tools.Execution.ISecurityManager, Andy.Tools.Execution.SecurityManager>();
        services.AddSingleton<Andy.Tools.Execution.IResourceMonitor, Andy.Tools.Execution.ResourceMonitor>();
        services.AddSingleton<Andy.Tools.Core.OutputLimiting.IToolOutputLimiter, Andy.Tools.Core.OutputLimiting.ToolOutputLimiter>();
        services.AddSingleton<IToolExecutor, ToolExecutor>();
        services.AddSingleton<IPermissionProfileService, Andy.Tools.Core.PermissionProfileService>();
        services.AddSingleton<Andy.Tools.Framework.IToolLifecycleManager, Andy.Tools.Framework.ToolLifecycleManager>();
        services.AddSingleton(new Andy.Tools.Framework.ToolFrameworkOptions
        {
            RegisterBuiltInTools = false,
            EnableObservability = false,
            AutoDiscoverTools = false,
        });

        return services.BuildServiceProvider();
    }
}
