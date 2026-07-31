using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Andy.Cli.HeadlessConfig;
using Andy.Engine;
using Andy.Llm;
using Andy.Llm.Configuration;
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
    private const string MaxTurnsExceededStopReason = "max_turns_exceeded";
    private const string LegacyMaxTurnsStopReason = "max_turns";

    public static async Task<HeadlessExitCode> ExecuteAsync(
        HeadlessRunConfig config,
        TextWriter eventStream,
        TextWriter stderr,
        ILoggerFactory loggerFactory,
        ILlmProvider? llmProviderOverride = null,
        CancellationToken ct = default,
        Func<string, string?>? currentBranchResolver = null,
        // rivoli-ai/andy-cli#279: the one-shot front end (`andy-cli run "<prompt>"`)
        // carries the operator's prompt as the USER turn instead of baking it into
        // the system prompt. Absent/blank keeps the config-driven contract's
        // "Begin." kickoff exactly as before.
        string? kickoffMessage = null,
        Andy.Cli.Configuration.AndyConfiguration? layeredConfiguration = null,
        Andy.Cli.Modes.AgentMode mode = Andy.Cli.Modes.AgentMode.Build)
    {
        var transcriptCreation = HeadlessTranscriptSession.TryCreate(config);
        using var emitter = new HeadlessEventEmitter(
            eventStream,
            transcript: transcriptCreation.Session);
        var stopwatch = Stopwatch.StartNew();
        var finished = false;

        void Finish(HeadlessExitCode code, int iterations, string? stopReason)
        {
            if (finished)
            {
                return;
            }

            EmitFinished(emitter, stopwatch, iterations, code, stopReason);
            finished = true;
        }

        // The config has already passed parse and schema validation before this
        // boundary. Start the lifecycle envelope before any fallible runtime setup
        // so tool-host, workspace, and provider failures retain run correlation.
        emitter.EmitStarted(
            runId: config.RunId,
            agentSlug: config.Agent.Slug,
            modelProvider: config.Model.Provider,
            modelId: config.Model.Id,
            toolCount: config.Tools.Count);

        if (!string.IsNullOrWhiteSpace(transcriptCreation.Error))
        {
            stderr.WriteLine($"andy-cli run --headless: {transcriptCreation.Error}");
            emitter.EmitError(transcriptCreation.Error, fatal: false);
        }

        try
        {
            var logger = loggerFactory.CreateLogger("Andy.Cli.Headless.HeadlessAgentRunner");
            return await ExecuteAcceptedRunAsync(
                config,
                emitter,
                stderr,
                loggerFactory,
                logger,
                llmProviderOverride,
                ct,
                currentBranchResolver,
                Finish,
                kickoffMessage,
                layeredConfiguration,
                mode);
        }
        catch (OperationCanceledException)
        {
            if (!finished)
            {
                emitter.EmitError("Headless runtime setup cancelled.", fatal: true);
                Finish(HeadlessExitCode.Cancelled, 0, "setup_cancelled");
            }
            return HeadlessExitCode.Cancelled;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"andy-cli run --headless: internal error: {ex.GetType().Name}: {ex.Message}");
            if (!finished)
            {
                emitter.EmitError($"Headless runtime failed: {ex.GetType().Name}: {ex.Message}", fatal: true);
                Finish(HeadlessExitCode.InternalError, 0, "internal_error");
            }
            return HeadlessExitCode.InternalError;
        }
    }

    private static async Task<HeadlessExitCode> ExecuteAcceptedRunAsync(
        HeadlessRunConfig config,
        HeadlessEventEmitter emitter,
        TextWriter stderr,
        ILoggerFactory loggerFactory,
        ILogger logger,
        ILlmProvider? llmProviderOverride,
        CancellationToken ct,
        Func<string, string?>? currentBranchResolver,
        Action<HeadlessExitCode, int, string?> finish,
        string? kickoffMessage,
        Andy.Cli.Configuration.AndyConfiguration? layeredConfiguration,
        Andy.Cli.Modes.AgentMode mode)
    {
        var iterations = 0;
        var exitCode = HeadlessExitCode.Success;

        // rivoli-ai/andy-cli#180: apply env_vars into the process environment before
        // any provider/tool wiring, so ConfigureLlmFromEnvironment and the tools see
        // them. Reserved names (ANDY_PROXY_URL/ANDY_TOKEN/ANDY_MCP_URL) were already
        // rejected at config-load time, so this can never shadow a runtime secret.
        ApplyEnvVars(config.EnvVars);

        await using var services = BuildServiceProvider(config, loggerFactory, layeredConfiguration, mode);

        // AX.3 (rivoli-ai/conductor#2090): register the assistant's built-in
        // tools into the SAME IToolRegistry the agent uses, so a headless
        // coding agent actually has file/command/text tools — not just the
        // config-declared cli/mcp tools. Mirrors the interactive path
        // (Program.cs / ServiceConfiguration.cs): ToolCatalog.RegisterAllTools
        // populates ToolRegistrationInfo entries in DI, which we then drain into
        // the registry. HeadlessToolHost adds the config tools into the same
        // registry afterwards, so built-ins and cli/mcp tools coexist.
        //
        // Permission caveat (AX.4 territory, NOT solved here): headless wires
        // AddAndyCliPermissions(services, null) — fail-closed with no broker.
        // Mutating built-ins (write_file/delete_file/move_file/copy_file/
        // file_editor/replace_text/create_directory) resolve to "Ask" and are
        // therefore DENIED at execution time with no interactive prompt;
        // execute_command likewise asks via its capability. Read-only built-ins
        // (read_file/list_directory/search_text/git_diff/etc.) stay
        // auto-allowed. AX.4 injects the per-run allow-list that relaxes this.
        RegisterBuiltInTools(services, loggerFactory);

        await using HeadlessToolHost? toolHost =
            await TryBuildToolHostAsync(config, services, loggerFactory, emitter, stderr, ct);

        if (toolHost is null)
        {
            // TryBuildToolHostAsync already emitted an error event and
            // logged. Surface the matching exit code without further chatter.
            finish(HeadlessExitCode.AgentFailure, iterations, "tool_host_setup_failed");
            return HeadlessExitCode.AgentFailure;
        }

        // rivoli-ai/andy-cli#180: workspace.branch is a guard-rail, not a no-op. The
        // container runtime checks the branch out before launch; we VERIFY the
        // workspace is actually on it. A mismatch (or a root that is not a git work
        // tree) means the run would operate on the wrong code, so fail fast rather
        // than silently proceeding.
        var branchError = VerifyWorkspaceBranch(config, currentBranchResolver);
        if (branchError is not null)
        {
            logger.LogError("Workspace branch verification failed: {Error}", branchError);
            emitter.EmitError(branchError, fatal: true);
            finish(HeadlessExitCode.AgentFailure, iterations, "workspace_branch_invalid");
            return HeadlessExitCode.AgentFailure;
        }

        ILlmProvider? llmProvider = llmProviderOverride;
        if (llmProvider is null)
        {
            try
            {
                llmProvider = ResolveLlmProvider(services, config);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to resolve LLM provider {Provider}", config.Model.Provider);
                emitter.EmitError($"Failed to resolve LLM provider '{config.Model.Provider}': {ex.Message}", fatal: true);
                finish(HeadlessExitCode.AgentFailure, iterations, "provider_resolution_failed");
                return HeadlessExitCode.AgentFailure;
            }
        }

        // Allow-listed tools for this run, computed up-front so it can both scope the
        // capability grant below and feed the end-of-run tool-usage audit.
        var allowedTools = config.Permissions?.AllowedTools ?? (IReadOnlyList<string>)Array.Empty<string>();

        var toolExecutor = services.GetRequiredService<IToolExecutor>();

        // rivoli-ai/andy-cli#157: the base ToolExecutor builds every ToolExecutionContext with the
        // restrictive default profile, so a tool that declares ProcessExecution (execute_command)
        // is rejected at runtime ("Tool requires process execution but it is not granted") even when
        // allowed_tools lists it. The interactive path grants the gated capabilities via
        // UiUpdatingToolExecutor; the headless path had no equivalent. Wrap the executor so it grants
        // those capabilities — but ONLY when execute_command is permitted for this run (fail-closed:
        // a config that doesn't allow execute_command must still block it).
        var allowedToolsIncludeExecuteCommand = allowedTools.Contains("execute_command");
        toolExecutor = new HeadlessCapabilityToolExecutor(toolExecutor, allowedToolsIncludeExecuteCommand);

        // #179: observe the ACTUAL tool execution. SimpleAgent calls this executor
        // exactly once per tool call and awaits the real result (or exception), so
        // wrapping it is the CLI's only exact start/finish signal — the engine's
        // ToolCalled event fires pre-execution and there is no engine ToolCompleted
        // event. The observer emits tool_call_started/finished with a measured
        // duration and the real outcome, and records the actual permission verdict
        // (evaluated with the real parameters) into the auditor. Placed OUTERMOST so
        // the measured span covers the permission gate and the observed result
        // reflects a synthesized deny. Resolved here (before the agent is built) so
        // the same auditor feeds the end-of-run tool-usage audit.
        var auditor = new ToolUsageAuditor();
        var requiredActionVerifier = new RequiredActionVerifier(config.RequiredActions ?? []);
        var toolAuthorizer = services
            .GetService(typeof(Andy.Permissions.Authorization.IToolPermissionAuthorizer))
            as Andy.Permissions.Authorization.IToolPermissionAuthorizer;
        toolExecutor = new ObservingToolExecutor(
            toolExecutor,
            emitter,
            auditor,
            toolAuthorizer,
            toolHost.Registry,
            workingDirectory: !string.IsNullOrWhiteSpace(config.Workspace.Root) ? config.Workspace.Root : null,
            requiredActionVerifier: requiredActionVerifier);

        var budget = HeadlessAgentBudgetFactory.Create(config.Limits);
        var maxTurns = budget.WindowTurns;

        // Operate in the configured workspace root, not the process cwd. SimpleAgent
        // threads this into every ToolExecutionContext.WorkingDirectory, so relative
        // paths the model uses (list_directory ".", read_file "calc.py") resolve
        // against the repository under test rather than wherever the headless
        // process happened to be launched. Without this the agent explores the
        // wrong tree and silently fails to apply edits.
        var workingDirectory = !string.IsNullOrWhiteSpace(config.Workspace.Root)
            ? config.Workspace.Root
            : null;
        // Issue #278: tell the model which mode it is in. This is context, not the boundary - the
        // boundary is the overlay wrapped around the executor and the authorizer above.
        var modeSection = Andy.Cli.Modes.AgentModePrompt.SystemPromptSection(
            Andy.Cli.Modes.AgentModeCatalog.Get(mode));
        var systemPrompt = string.IsNullOrWhiteSpace(config.Agent.Instructions)
            ? modeSection
            : config.Agent.Instructions + "\n\n" + modeSection;

        using var agent = new SimpleAgent(
            llmProvider,
            toolHost.Registry,
            toolExecutor,
            systemPrompt: systemPrompt,
            maxTurns: maxTurns,
            workingDirectory: workingDirectory,
            logger: loggerFactory.CreateLogger<SimpleAgent>(),
            maxOutputTokens: budget.MaxOutputTokens,
            continuationPolicy: budget.ContinuationPolicy);

        agent.ContinuationProgress += (_, progress) =>
        {
            emitter.EmitAgentProgress(
                JsonNamingPolicy.SnakeCaseLower.ConvertName(progress.Kind.ToString()),
                progress.WindowNumber,
                progress.TotalTurns,
                progress.StopReason,
                progress.FinishReason,
                progress.MaxOutputTokens,
                progress.ConsecutiveOutputLimitResponses,
                progress.TotalOutputLimitResponses);
        };

        RequiredActionVerificationResult? requiredActionVerification = null;
        var modelHistoryEmitted = false;

        void EmitModelHistory()
        {
            if (modelHistoryEmitted)
            {
                return;
            }

            modelHistoryEmitted = true;
            var turn = 0;
            foreach (var message in agent.GetHistory()
                .Where(message =>
                    message.Role == Andy.Model.Model.Role.Assistant
                    && !string.IsNullOrWhiteSpace(message.Content)))
            {
                emitter.EmitLlmChunk(message.Content!, turn++);
            }
        }

        void EmitRequiredActionVerification()
        {
            if (!requiredActionVerifier.HasRequirements || requiredActionVerification is not null)
            {
                return;
            }

            requiredActionVerification = requiredActionVerifier.Verify();
            emitter.EmitRequiredActionVerification(requiredActionVerification);
        }

        // Finalize a run that has reached the agent loop: emit the AX.4 tool-usage
        // audit, required-action evidence when configured, then the terminal event.
        void Finalize(HeadlessExitCode code, int iters, string? stopReason = null)
        {
            EmitModelHistory();
            EmitToolUsageAudit(emitter, auditor, services, toolHost.Registry, allowedTools);
            EmitRequiredActionVerification();
            finish(code, iters, stopReason);
        }

        // Two cancellation sources: the caller's outer ct (SIGTERM, etc.)
        // and the wall-clock timeout. Whichever fires first short-circuits
        // SimpleAgent and we map to the corresponding exit code below.
        var timeoutSeconds = config.Limits.TimeoutSeconds > 0 ? config.Limits.TimeoutSeconds : 300;
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        SimpleAgentResult? result = null;
        try
        {
            var kickoff = string.IsNullOrWhiteSpace(kickoffMessage) ? KickoffMessage : kickoffMessage;
            result = await agent.ProcessMessageAsync(kickoff, linkedCts.Token);
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
            Finalize(exitCode, iterations, timeoutCts.IsCancellationRequested
                ? "timeout_seconds_exceeded"
                : "cancelled");
            return exitCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent loop threw");
            emitter.EmitError($"Agent loop failed: {ex.GetType().Name}: {ex.Message}", fatal: true);
            Finalize(HeadlessExitCode.AgentFailure, iterations, "agent_exception");
            return HeadlessExitCode.AgentFailure;
        }

        // SimpleAgent may swallow the OperationCanceledException internally and
        // return a failed result instead of throwing, so the catch above does
        // not always see the cancel. Check the tokens directly: an outer ct
        // cancel (SIGTERM) maps to Cancelled (3); the wall-clock timeout maps
        // to Timeout (4). Either way we stop here without writing output, so no
        // partial output is produced.
        if (linkedCts.IsCancellationRequested)
        {
            iterations = result?.TurnCount ?? agent.GetHistory().Count / 2;
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
            Finalize(exitCode, iterations, timeoutCts.IsCancellationRequested
                ? "timeout_seconds_exceeded"
                : "cancelled");
            return exitCode;
        }

        // Engine versions have used two spellings for turn-budget exhaustion.
        // Normalize them at this host boundary so max_iterations always maps to
        // the headless Timeout contract (exit 4).
        if (result is null || !result.Success)
        {
            var stopReason = result?.StopReason ?? "unknown";
            if (IsBudgetStopReason(stopReason))
            {
                emitter.EmitError(
                    $"Agent loop exhausted max_iterations={config.Limits.MaxIterations}: "
                        + $"{stopReason}.",
                    fatal: true);
                exitCode = HeadlessExitCode.Timeout;
            }
            else
            {
                emitter.EmitError($"Agent loop did not converge: {stopReason}", fatal: true);
                exitCode = HeadlessExitCode.AgentFailure;
            }
            Finalize(exitCode, iterations, stopReason);
            return exitCode;
        }

        var output = result.Response ?? string.Empty;
        EmitModelHistory();

        // #219: a plausible model response is not evidence that a required action
        // happened. Verify actual terminal tool outcomes before format validation
        // or output publication, and fail closed when any assertion is unmet.
        EmitRequiredActionVerification();
        if (requiredActionVerification is { Satisfied: false })
        {
            var unmet = requiredActionVerification.Requirements.Count(requirement => !requirement.Satisfied);
            emitter.EmitError(
                $"Required action verification failed: {unmet} requirement(s) were unmet.",
                fatal: true);
            Finalize(HeadlessExitCode.AgentFailure, iterations);
            return HeadlessExitCode.AgentFailure;
        }

        // rivoli-ai/andy-cli#180: enforce agent.output_format. A format label
        // beginning with "json" requires the final output to be valid JSON;
        // otherwise the run fails (exit 1) and NO output file is written, so a
        // downstream consumer never receives malformed structured output.
        var formatError = ValidateOutputFormat(config.Agent.OutputFormat, output);
        if (formatError is not null)
        {
            logger.LogError("Output-format enforcement failed: {Error}", formatError);
            emitter.EmitError(formatError, fatal: true);
            Finalize(HeadlessExitCode.AgentFailure, iterations, "invalid_output_format");
            return HeadlessExitCode.AgentFailure;
        }

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
            Finalize(HeadlessExitCode.AgentFailure, iterations, "output_write_failed");
            return HeadlessExitCode.AgentFailure;
        }

        Finalize(HeadlessExitCode.Success, iterations);
        return HeadlessExitCode.Success;
    }

    internal static bool IsTurnLimitStopReason(string? stopReason) =>
        string.Equals(stopReason, MaxTurnsExceededStopReason, StringComparison.OrdinalIgnoreCase)
        || string.Equals(stopReason, LegacyMaxTurnsStopReason, StringComparison.OrdinalIgnoreCase);

    private static void EmitFinished(
        HeadlessEventEmitter emitter,
        Stopwatch stopwatch,
        int iterations,
        HeadlessExitCode code,
        string? stopReason = null)
    {
        emitter.EmitFinished(
            (int)code,
            stopwatch.ElapsedMilliseconds,
            iterations,
            stopReason);
    }

    internal static bool IsBudgetStopReason(string stopReason) =>
        stopReason.Equals("max_turns", StringComparison.OrdinalIgnoreCase) ||
        stopReason.Equals("max_turns_exceeded", StringComparison.OrdinalIgnoreCase) ||
        stopReason.Equals("continuation_total_turns_exceeded", StringComparison.OrdinalIgnoreCase) ||
        stopReason.Equals("continuation_windows_exceeded", StringComparison.OrdinalIgnoreCase) ||
        stopReason.Equals("continuation_time_exceeded", StringComparison.OrdinalIgnoreCase) ||
        stopReason.Equals("deadline_exhausted", StringComparison.OrdinalIgnoreCase) ||
        stopReason.Equals("output_limit_exhausted", StringComparison.OrdinalIgnoreCase);

    // AX.4: emit the end-of-run tool-usage audit just before `finished`, resolving
    // each invoked tool's permitted status against the live permission engine.
    private static void EmitToolUsageAudit(
        HeadlessEventEmitter emitter,
        ToolUsageAuditor auditor,
        IServiceProvider services,
        IToolRegistry registry,
        IReadOnlyList<string> allowedTools)
    {
        var authorizer = services
            .GetService(typeof(Andy.Permissions.Authorization.IToolPermissionAuthorizer))
            as Andy.Permissions.Authorization.IToolPermissionAuthorizer;
        var entries = auditor.BuildEntries(authorizer, registry);
        emitter.EmitToolUsageAudit(allowedTools, entries);
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

        // rivoli-ai/andy-cli#180: if the write throws or is cancelled mid-flight,
        // the sibling temp file must not be left behind to clutter the workspace or
        // be mistaken for output. Delete it on any failure; the final File.Move
        // consumes it on success so the cleanup is a no-op then.
        try
        {
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
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    // Best-effort cleanup of the atomic-write temp file after a failed or cancelled
    // write. Swallows its own errors: a cleanup failure must not mask the original.
    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
            // Best-effort only.
        }
    }

    // Internal for testing: lets a test exercise the real DI wiring (built-in
    // tool catalog included) instead of reimplementing it in a parallel path.
    internal static ServiceProvider BuildServiceProvider(
        HeadlessRunConfig config,
        ILoggerFactory loggerFactory,
        Andy.Cli.Configuration.AndyConfiguration? layeredConfiguration = null,
        Andy.Cli.Modes.AgentMode mode = Andy.Cli.Modes.AgentMode.Build)
    {
        var services = new ServiceCollection();
        services.AddSingleton(loggerFactory);
        services.AddLogging();

        // Issue #278: seed the run's primary mode BEFORE AddAndyCliPermissions, whose TryAdd keeps
        // this instance and hands it to the mode overlay. A headless run's mode is fixed for its
        // lifetime - there is no interactive path that could switch it.
        services.AddSingleton(new Andy.Cli.Modes.AgentModeState(mode));

        // Andy.Llm: factory + per-provider configuration from environment
        // variables (CEREBRAS_API_KEY, ANTHROPIC_API_KEY, etc.). Default
        // provider is the one named in the run's config so the factory
        // knows which to construct on demand.
        services.ConfigureLlmFromEnvironment();
        services.AddLlmServices(options =>
        {
            options.DefaultProvider = config.Model.Provider;
        });

        // Layered andy.jsonc llm.* (rivoli-ai/andy-cli#280) between the environment
        // and the run config. This is what lets a workspace pin an endpoint - an
        // internal gateway, say - for every session that runs in it. It is applied
        // BEFORE the two Configure calls below, so the run config's own model and
        // api_key_ref still win: an explicit per-run instruction outranks a
        // workspace default.
        if (layeredConfiguration is not null)
        {
            services.Configure<LlmOptions>(options =>
                Andy.Cli.Configuration.LlmOptionsBinder.Apply(options, layeredConfiguration.Llm));
        }

        // The headless config names the model explicitly (config.Model.Id), but
        // ConfigureLlmFromEnvironment only populates each provider's Model from
        // env vars (e.g. OPENROUTER_MODEL). Some providers — OpenRouter among
        // them — require a model at construction time, so thread config.Model.Id
        // into the provider entry the factory will resolve for this run.
        services.Configure<LlmOptions>(options =>
            ApplyConfiguredModel(options, config.Model.Provider, config.Model.Id));

        // rivoli-ai/andy-cli#284: layer in credentials stored by `andy-cli auth login` so a
        // headless run resolves them exactly like interactive and ACP mode. Environment
        // variables (including model.api_key_ref below) still win and are never persisted.
        services.Configure<LlmOptions>(Andy.Cli.Auth.AuthBootstrap.Configure);

        // rivoli-ai/andy-cli#180: honor model.api_key_ref. Only the 'env:NAME' form
        // is supported (validated at load time); load that env var's value into the
        // provider's ApiKey so a config that names a non-default key var actually
        // takes effect. The value is never logged or emitted.
        services.Configure<LlmOptions>(options =>
            ApplyApiKeyRef(options, config.Model.Provider, config.Model.ApiKeyRef));

        services.AddSingleton<ILlmProviderFactory, LlmProviderFactory>();

        // Andy.Tools: registry + executor + the validators / managers
        // ToolExecutor depends on. The built-in tool surface is registered as
        // ToolRegistrationInfo entries here (AX.3) and drained into the registry
        // in ExecuteAsync after the provider is built; HeadlessToolHost then
        // layers the config cli/mcp tools onto the same registry.
        services.AddSingleton<Andy.Tools.Validation.IToolValidator, Andy.Tools.Validation.ToolValidator>();
        services.AddSingleton<IToolRegistry, Andy.Tools.Registry.ToolRegistry>();
        services.AddSingleton<Andy.Tools.Discovery.IToolDiscovery, Andy.Tools.Discovery.ToolDiscoveryService>();
        services.AddSingleton<Andy.Tools.Execution.ISecurityManager, Andy.Tools.Execution.SecurityManager>();
        services.AddSingleton<Andy.Tools.Execution.IResourceMonitor, Andy.Tools.Execution.ResourceMonitor>();
        services.AddSingleton<Andy.Tools.Core.OutputLimiting.IToolOutputLimiter, Andy.Tools.Core.OutputLimiting.ToolOutputLimiter>();
        services.AddSingleton<IToolExecutor, ToolExecutor>();
        services.AddSingleton<IPermissionProfileService, Andy.Tools.Core.PermissionProfileService>();
        services.AddSingleton<Andy.Tools.Framework.IToolLifecycleManager, Andy.Tools.Framework.ToolLifecycleManager>();
        // Gate tool execution through the permission engine. Headless is non-interactive: anything that
        // would prompt is denied (fail-closed) unless ANDY_PERMISSION_MODE=bypass or rules/injection allow it.
        //
        // The layered andy.jsonc configuration is DELIBERATELY not consulted here.
        // Its permissions section carries only a prompting mode, and a file checked
        // into a workspace must never be able to loosen a containerised run: the
        // per-run allow-list in the run config is the sole relaxation mechanism.
        // HeadlessConfigLayer pins permissions.mode to "ask" so the merged view
        // agrees with what is enforced.
        Andy.Cli.Services.CliPermissionServiceExtensions.AddAndyCliPermissions(services, null);
        services.AddSingleton(new Andy.Tools.Framework.ToolFrameworkOptions
        {
            // We register built-ins explicitly via ToolCatalog (the trim-safe,
            // interactive-parity path) and drain them into the registry in
            // ExecuteAsync, so the framework's own built-in registration stays
            // off here — same convention as Program.cs/ServiceConfiguration.cs.
            RegisterBuiltInTools = false,
            EnableObservability = false,
            AutoDiscoverTools = false,
        });

        // AX.3: register the built-in tool catalog (file/command/text/git/web/
        // utility tools) as ToolRegistrationInfo singletons. ExecuteAsync drains
        // these into the IToolRegistry the SimpleAgent receives.
        Andy.Cli.Services.ToolCatalog.RegisterAllTools(services);

        var provider = services.BuildServiceProvider();

        // AX.4 (rivoli-ai/conductor#2091): inject the per-run permission allow-list.
        // For each tool in config.permissions.allowed_tools, install a {tool}(*) Allow
        // rule at the Injected layer (precedence 5), which overrides the Builtin "Ask"
        // defaults (precedence 0). Tools NOT listed stay fail-closed/denied. Absent /
        // empty list = no-op (unchanged fail-closed behavior).
        Andy.Cli.Services.CliPermissionServiceExtensions.ApplyInjectedAllowList(
            provider, config.Permissions?.AllowedTools);

        return provider;
    }

    // Drains the ToolCatalog's ToolRegistrationInfo entries (registered in
    // BuildServiceProvider) into the run's IToolRegistry — the same instance
    // HeadlessToolHost adds config cli/mcp tools to, and the same instance the
    // SimpleAgent resolves tools from. Mirrors the interactive bootstrap in
    // Program.cs / ServiceConfiguration.cs.
    // Internal for testing: see BuildServiceProvider.
    internal static void RegisterBuiltInTools(IServiceProvider services, ILoggerFactory loggerFactory)
    {
        var registry = services.GetRequiredService<IToolRegistry>();
        var logger = loggerFactory.CreateLogger("Andy.Cli.Headless.HeadlessAgentRunner");
        var registrations = services.GetServices<Andy.Tools.Framework.ToolRegistrationInfo>();
        var count = 0;
        foreach (var registration in registrations)
        {
            registry.RegisterTool(registration.ToolType, registration.Configuration);
            count++;
        }

        logger.LogInformation("HeadlessAgentRunner: registered {ToolCount} built-in tool(s) into the run registry", count);
    }

    // Sets the resolved model on the provider config the factory will pick for
    // <paramref name="provider"/>, mirroring LlmProviderFactory's lookup: exact
    // key first, then a match by provider type, creating the entry if neither
    // exists. Internal for testing.
    internal static void ApplyConfiguredModel(LlmOptions options, string provider, string modelId)
    {
        ResolveOrCreateProviderConfig(options, provider).Model = modelId;
    }

    // rivoli-ai/andy-cli#180: resolve model.api_key_ref ('env:NAME') into the
    // provider's ApiKey. No-op when api_key_ref is absent, malformed (already
    // rejected at load time), or the named env var is unset (provider resolution
    // then falls back to its default env var, or fails later as AgentFailure).
    // The secret value is only moved between the environment and the provider
    // config here; it is never written to a log or the event stream. Internal for
    // testing.
    internal static void ApplyApiKeyRef(LlmOptions options, string provider, string? apiKeyRef)
    {
        if (string.IsNullOrEmpty(apiKeyRef)
            || !HeadlessConfig.HeadlessConfigValidator.TryParseEnvRef(apiKeyRef, out var envVarName, out _))
        {
            return;
        }

        var value = Environment.GetEnvironmentVariable(envVarName);
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        ResolveOrCreateProviderConfig(options, provider).ApiKey = value;
    }

    private static ProviderConfig ResolveOrCreateProviderConfig(LlmOptions options, string provider)
    {
        if (!options.Providers.TryGetValue(provider, out var providerConfig))
        {
            var match = options.Providers.FirstOrDefault(p => string.Equals(
                p.Value.Provider ?? p.Key.Split('/')[0],
                provider,
                StringComparison.OrdinalIgnoreCase));
            providerConfig = match.Value;
            if (providerConfig is null)
            {
                providerConfig = new ProviderConfig { Provider = provider };
                options.Providers[provider] = providerConfig;
            }
        }

        return providerConfig;
    }

    // rivoli-ai/andy-cli#180: set env_vars into the process environment. Reserved
    // names are rejected before we get here, so this cannot shadow a runtime
    // secret. Internal for testing.
    internal static void ApplyEnvVars(IReadOnlyDictionary<string, string>? envVars)
    {
        if (envVars is null)
        {
            return;
        }
        foreach (var (key, value) in envVars)
        {
            if (string.IsNullOrEmpty(key)
                || HeadlessConfig.HeadlessConfigValidator.ReservedEnvVars.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    // rivoli-ai/andy-cli#180: verify the workspace is on the configured git
    // branch. Returns null when there is nothing to check (branch absent) or the
    // branch matches; otherwise a clear error message. The branch resolver is
    // injectable for testing; the default shells out to git. Internal for testing.
    internal static string? VerifyWorkspaceBranch(
        HeadlessRunConfig config,
        Func<string, string?>? currentBranchResolver = null)
    {
        var expected = config.Workspace.Branch;
        if (string.IsNullOrWhiteSpace(expected))
        {
            return null;
        }

        var root = config.Workspace.Root;
        if (string.IsNullOrWhiteSpace(root))
        {
            return "workspace.branch is set but workspace.root is empty, so the branch cannot be verified.";
        }

        var resolver = currentBranchResolver ?? ResolveGitBranch;
        var actual = resolver(root);
        if (string.IsNullOrWhiteSpace(actual))
        {
            return $"workspace.branch is '{expected}' but workspace.root '{root}' is not a "
                + "git work tree whose current branch could be determined; refusing to run on "
                + "an unverified tree.";
        }

        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            return $"workspace.branch mismatch: config expects '{expected}' but workspace.root "
                + $"'{root}' is on '{actual}'. The container runtime must check out the expected "
                + "branch before launch.";
        }

        return null;
    }

    // Default branch resolver: `git -C <root> rev-parse --abbrev-ref HEAD`.
    // Returns the branch name, or null if git is unavailable, root is not a repo,
    // HEAD is detached, or git does not exit within the bounded wait. Internal for
    // testing (it is the injectable resolver's default implementation).
    internal static string? ResolveGitBranch(string root)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(root);
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("--abbrev-ref");
            psi.ArgumentList.Add("HEAD");

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            // Drain stdout AND stderr concurrently BEFORE waiting: reading one
            // stream to completion first can deadlock if git fills the other
            // stream's pipe buffer. Then bound the wait; a hung git must not block
            // the run indefinitely. If it does not exit in time, kill the whole
            // process tree and treat the branch as unverifiable (return null),
            // which the caller turns into a fail-fast per the guard-rail design.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return null;
            }

            // Ensure the async reads have completed now that the process has exited.
            Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 1000);

            var output = (stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty).Trim();
            if (process.ExitCode != 0 || output.Length == 0 || output == "HEAD")
            {
                return null;
            }
            return output;
        }
        catch
        {
            return null;
        }
    }

    // rivoli-ai/andy-cli#180: enforce agent.output_format on the final output.
    // A label beginning with "json" (case-insensitive) requires syntactically
    // valid JSON. Any other label is free-form and unconstrained. Returns null
    // when the output satisfies the format, otherwise a clear error. Internal for
    // testing.
    internal static string? ValidateOutputFormat(string? outputFormat, string output)
    {
        if (!RequiresJsonOutput(outputFormat))
        {
            return null;
        }

        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(output);
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return $"agent.output_format '{outputFormat}' requires the final output to be valid "
                + "JSON, but it did not parse. No output file was written.";
        }
    }

    // True when the format label declares JSON structured output: any label that
    // begins with "json" (case-insensitive). This matches the contract promised by
    // schemas/headless-config.v1.json and docs/headless-runtime.md ("a label
    // beginning with json ... requires ... valid JSON"), so "json", "json-plan-v1",
    // "jsonl", "json5", etc. all enforce. Internal for testing.
    internal static bool RequiresJsonOutput(string? outputFormat)
    {
        if (string.IsNullOrWhiteSpace(outputFormat))
        {
            return false;
        }
        return outputFormat.Trim().StartsWith("json", StringComparison.OrdinalIgnoreCase);
    }
}
