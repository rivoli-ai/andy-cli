using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Headless;
using Andy.Cli.HeadlessConfig;
using Andy.Model.Llm;
using Andy.Model.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Cli.Tests.Headless;

// AQ7 stability + regression coverage (rivoli-ai/andy-cli#52).
//
// The epic treats headless flakiness as a blocking bug, so these are
// deterministic, network-free integration tests that drive the real headless
// agent loop (HeadlessAgentRunner.ExecuteAsync) against a scripted in-memory
// ILlmProvider passed through the llmProviderOverride hook. Each test captures
// the NDJSON event stream into a StringWriter, parses every line as JSON, and
// asserts on event kinds, ordering, and key fields so a regression that
// reorders, drops, or malforms an event fails loudly.
//
// Scope per the issue: exercise each event kind produced by
// HeadlessEventEmitter / HeadlessAgentRunner (started, output_written,
// tool_call_started, tool_call_finished, error, finished) and the Success (0)
// and AgentFailure (1) exit-code paths, plus a real cli-transport tool
// round-trip. The cancel / timeout paths (exit 3 / 4, SIGTERM) are owned by
// issue #50 and are intentionally NOT covered here. ConfigError (exit 2)
// schema-validation paths live in HeadlessRunnerTests; this file adds the
// ExecuteAsync-level AgentFailure paths not covered there.
public class HeadlessAgentRunnerStabilityTests
{
    [Fact]
    public async Task FinalTextResponse_EmitsStartedOutputFinished_AndExitsSuccess()
    {
        using var ws = new TempDir();
        var config = NewConfig(ws);
        var provider = new FakeLlmProvider(FakeLlmProvider.TextResponse("The answer is 42."));

        var (events, code) = await RunAsync(config, provider);

        Assert.Equal(HeadlessExitCode.Success, code);

        // Ordering contract: started first, finished last, no error.
        Assert.Equal("started", events.First().Kind);
        Assert.Equal("finished", events.Last().Kind);
        Assert.DoesNotContain(events, e => e.Kind == "error");

        var started = events.Single(e => e.Kind == "started");
        Assert.Equal(config.RunId.ToString(), started.Data.GetProperty("run_id").GetString());
        Assert.Equal(config.Agent.Slug, started.Data.GetProperty("agent_slug").GetString());
        Assert.Equal(config.Model.Provider, started.Data.GetProperty("model_provider").GetString());
        Assert.Equal(config.Model.Id, started.Data.GetProperty("model_id").GetString());
        Assert.Equal(0, started.Data.GetProperty("tool_count").GetInt32());

        var output = events.Single(e => e.Kind == "output_written");
        Assert.Equal(config.Output.File, output.Data.GetProperty("path").GetString());
        Assert.True(output.Data.GetProperty("bytes").GetInt64() > 0);

        var finished = events.Single(e => e.Kind == "finished");
        Assert.Equal(0, finished.Data.GetProperty("exit_code").GetInt32());
        Assert.True(finished.Data.GetProperty("duration_ms").GetInt64() >= 0);
        Assert.True(finished.Data.TryGetProperty("iterations", out _));

        // The final text is persisted atomically to the configured output file.
        Assert.Equal("The answer is 42.", File.ReadAllText(config.Output.File));

        // Every event carries the envelope contract.
        foreach (var e in events)
        {
            Assert.Equal(HeadlessEventEmitter.SchemaVersion, e.SchemaVersion);
            Assert.False(string.IsNullOrEmpty(e.Kind));
        }

        Assert.DoesNotContain(events, e => e.Kind == "required_action_verification");
    }

    [Fact]
    public async Task ConfiguredTranscript_CapturesFinalModelResponseAndTerminalEvent()
    {
        using var ws = new TempDir();
        var transcriptDirectory = Path.Combine(ws.Path, "transcripts");
        var config = NewConfig(ws) with
        {
            Transcript = new HeadlessTranscript { Directory = transcriptDirectory }
        };
        var provider = new FakeLlmProvider(FakeLlmProvider.TextResponse("durable response"));

        var (events, code) = await RunAsync(config, provider);

        Assert.Equal(HeadlessExitCode.Success, code);
        Assert.Contains(events, item =>
            item.Kind == "llm_chunk"
            && item.Data.GetProperty("text").GetString() == "durable response");
        var transcript = Assert.Single(Directory.GetFiles(transcriptDirectory, "*.ndjson"));
        var persisted = File.ReadAllLines(transcript)
            .Select(ParseEvent)
            .ToList();
        Assert.Contains(persisted, item =>
            item.Kind == "llm_chunk"
            && item.Data.GetProperty("text").GetString() == "durable response");
        Assert.Equal("finished", persisted.Last().Kind);
        Assert.Equal(0, persisted.Last().Data.GetProperty("exit_code").GetInt32());
        Assert.Empty(Directory.GetFiles(transcriptDirectory, "*.tmp"));
    }

    [Fact]
    public async Task TranscriptInitializationFailure_IsNonFatalAndPrimaryContractCompletes()
    {
        using var ws = new TempDir();
        var blocker = Path.Combine(ws.Path, "blocker");
        File.WriteAllText(blocker, "x");
        var config = NewConfig(ws) with
        {
            Transcript = new HeadlessTranscript
            {
                Directory = Path.Combine(blocker, "transcripts")
            }
        };
        var provider = new FakeLlmProvider(FakeLlmProvider.TextResponse("still succeeds"));

        var (events, code) = await RunAsync(config, provider);

        Assert.Equal(HeadlessExitCode.Success, code);
        Assert.True(File.Exists(config.Output.File));
        var warning = Assert.Single(events, item =>
            item.Kind == "error"
            && !item.Data.GetProperty("fatal").GetBoolean());
        Assert.Contains("Transcript initialization failed", warning.Data.GetProperty("message").GetString());
        Assert.Equal("finished", events.Last().Kind);
        Assert.Equal(0, events.Last().Data.GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public async Task ToolCallThenFinalResponse_EmitsPairedToolCallEvents()
    {
        using var ws = new TempDir();
        // Register a real, no-op cli tool so the scripted tool call resolves to
        // an actual adapter and SimpleAgent fires its ToolCalled event.
        var config = NewConfig(ws) with { Tools = new[] { CrossPlatform.NoOpCliTool("noop") } };

        var provider = new FakeLlmProvider(
            FakeLlmProvider.ToolCallResponse("noop", "call-1"),
            FakeLlmProvider.TextResponse("done"));

        var (events, code) = await RunAsync(config, provider);

        Assert.Equal(HeadlessExitCode.Success, code);

        var startedTool = events.Single(e => e.Kind == "tool_call_started");
        Assert.Equal("noop", startedTool.Data.GetProperty("tool_name").GetString());
        var callId = startedTool.Data.GetProperty("call_id").GetString();
        Assert.False(string.IsNullOrEmpty(callId));

        var finishedTool = events.Single(e => e.Kind == "tool_call_finished");
        Assert.Equal("noop", finishedTool.Data.GetProperty("tool_name").GetString());
        Assert.Equal(callId, finishedTool.Data.GetProperty("call_id").GetString());
        Assert.True(finishedTool.Data.GetProperty("ok").GetBoolean());
        Assert.True(finishedTool.Data.TryGetProperty("duration_ms", out _));

        // tool_call_started precedes tool_call_finished; both sit inside the
        // started..finished envelope.
        var kinds = events.Select(e => e.Kind).ToList();
        Assert.True(kinds.IndexOf("tool_call_started") < kinds.IndexOf("tool_call_finished"));
        Assert.True(kinds.IndexOf("started") < kinds.IndexOf("tool_call_started"));
        Assert.True(kinds.IndexOf("tool_call_finished") < kinds.LastIndexOf("finished"));
    }

    [Fact]
    public async Task SuccessfulRequiredAction_EmitsEvidenceBeforePublishingOutput()
    {
        using var ws = new TempDir();
        var config = NewConfig(ws) with
        {
            Tools = new[] { CrossPlatform.NoOpCliTool("noop") },
            RequiredActions =
            [
                new HeadlessRequiredAction { ToolName = "noop" }
            ]
        };
        var provider = new FakeLlmProvider(
            FakeLlmProvider.ToolCallResponse("noop", "model-call"),
            FakeLlmProvider.TextResponse("verified"));

        var (events, code) = await RunAsync(config, provider);

        Assert.Equal(HeadlessExitCode.Success, code);
        var verification = events.Single(e => e.Kind == "required_action_verification");
        Assert.True(verification.Data.GetProperty("satisfied").GetBoolean());
        var requirement = Assert.Single(verification.Data.GetProperty("requirements").EnumerateArray());
        Assert.Equal(1, requirement.GetProperty("successful_matches").GetInt32());
        Assert.True(requirement.GetProperty("satisfied").GetBoolean());
        var call = Assert.Single(requirement.GetProperty("calls").EnumerateArray());
        Assert.Equal(
            events.Single(e => e.Kind == "tool_call_finished").Data.GetProperty("call_id").GetString(),
            call.GetProperty("call_id").GetString());
        Assert.Equal("success", call.GetProperty("outcome").GetString());

        var kinds = events.Select(e => e.Kind).ToList();
        Assert.True(kinds.IndexOf("required_action_verification") < kinds.IndexOf("output_written"));
    }

    [Fact]
    public async Task MissingRequiredAction_FailsBeforeOutputPublication()
    {
        using var ws = new TempDir();
        var config = NewConfig(ws) with
        {
            RequiredActions =
            [
                new HeadlessRequiredAction { ToolName = "execute_command" }
            ]
        };
        var provider = new FakeLlmProvider(FakeLlmProvider.TextResponse("I ran all checks."));

        var (events, code) = await RunAsync(config, provider);

        Assert.Equal(HeadlessExitCode.AgentFailure, code);
        Assert.False(File.Exists(config.Output.File));
        Assert.DoesNotContain(events, e => e.Kind == "output_written");
        var verification = events.Single(e => e.Kind == "required_action_verification");
        Assert.False(verification.Data.GetProperty("satisfied").GetBoolean());
        var requirement = Assert.Single(verification.Data.GetProperty("requirements").EnumerateArray());
        Assert.Equal(0, requirement.GetProperty("observed_matches").GetInt32());
        Assert.False(requirement.GetProperty("satisfied").GetBoolean());
        Assert.Contains(events, e =>
            e.Kind == "error"
            && e.Data.GetProperty("message").GetString()!.Contains(
                "Required action verification failed",
                StringComparison.Ordinal));
        Assert.Equal(
            (int)HeadlessExitCode.AgentFailure,
            events.Last().Data.GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public async Task DeniedRequiredAction_CannotProduceSuccess()
    {
        using var ws = new TempDir();
        var config = NewConfig(ws) with
        {
            RequiredActions =
            [
                new HeadlessRequiredAction { ToolName = "delete_file" }
            ]
        };
        var provider = new FakeLlmProvider(
            FakeLlmProvider.ToolCallResponse("delete_file", "denied-call"),
            FakeLlmProvider.TextResponse("done"));

        var (events, code) = await RunAsync(config, provider);

        Assert.Equal(HeadlessExitCode.AgentFailure, code);
        Assert.False(File.Exists(config.Output.File));
        Assert.Equal(
            "denied",
            events.Single(e => e.Kind == "tool_call_finished").Data.GetProperty("outcome").GetString());
        var verification = events.Single(e => e.Kind == "required_action_verification");
        var requirement = Assert.Single(verification.Data.GetProperty("requirements").EnumerateArray());
        Assert.Equal(1, requirement.GetProperty("observed_matches").GetInt32());
        Assert.Equal(0, requirement.GetProperty("successful_matches").GetInt32());
        Assert.Equal(
            "denied",
            Assert.Single(requirement.GetProperty("calls").EnumerateArray())
                .GetProperty("outcome")
                .GetString());
    }

    [Fact]
    public async Task ExactRequiredCommand_IsVerifiedFromObservedParameters()
    {
        using var ws = new TempDir();
        const string command = "dotnet --version";
        var config = NewConfig(ws) with
        {
            Permissions = new HeadlessPermissions { AllowedTools = ["execute_command"] },
            RequiredActions =
            [
                new HeadlessRequiredAction
                {
                    ToolName = "execute_command",
                    CommandEquals = command
                }
            ]
        };
        var provider = new FakeLlmProvider(
            FakeLlmProvider.ToolCallResponse(
                "execute_command",
                "command-call",
                """{"command":"dotnet --version"}"""),
            FakeLlmProvider.TextResponse("command completed"));

        var (events, code) = await RunAsync(config, provider);

        Assert.Equal(HeadlessExitCode.Success, code);
        Assert.True(File.Exists(config.Output.File));
        Assert.Equal(
            "success",
            events.Single(e => e.Kind == "tool_call_finished").Data.GetProperty("outcome").GetString());
        var verification = events.Single(e => e.Kind == "required_action_verification");
        var requirement = Assert.Single(verification.Data.GetProperty("requirements").EnumerateArray());
        Assert.True(requirement.GetProperty("satisfied").GetBoolean());
        Assert.True(requirement.TryGetProperty("command_digest", out _));
        Assert.DoesNotContain(command, verification.Data.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliTransportTool_RoundTripsThroughRealSubprocess()
    {
        using var ws = new TempDir();
        // A trivial real binary written into a temp dir that echoes a marker.
        var echo = CrossPlatform.WriteEchoScript(ws.Path, "AQ7-ROUNDTRIP-MARKER");
        var config = NewConfig(ws) with { Tools = new[] { echo } };

        var provider = new FakeLlmProvider(
            FakeLlmProvider.ToolCallResponse("echo_marker", "call-echo"),
            FakeLlmProvider.TextResponse("subprocess invoked"));

        var (events, code) = await RunAsync(config, provider);

        Assert.Equal(HeadlessExitCode.Success, code);
        Assert.Equal("echo_marker", events.Single(e => e.Kind == "tool_call_started").Data.GetProperty("tool_name").GetString());
        Assert.True(events.Single(e => e.Kind == "tool_call_finished").Data.GetProperty("ok").GetBoolean());

        // started reports the real tool_count from config.
        Assert.Equal(1, events.Single(e => e.Kind == "started").Data.GetProperty("tool_count").GetInt32());
    }

    [Fact]
    public async Task ProviderThrows_EmitsFatalErrorAndExitsAgentFailure()
    {
        using var ws = new TempDir();
        var config = NewConfig(ws);
        var provider = new FakeLlmProvider(() => throw new InvalidOperationException("provider exploded"));

        var (events, code) = await RunAsync(config, provider);

        Assert.Equal(HeadlessExitCode.AgentFailure, code);

        // started still first, finished still last.
        Assert.Equal("started", events.First().Kind);
        Assert.Equal("finished", events.Last().Kind);

        var error = events.Single(e => e.Kind == "error");
        Assert.True(error.Data.GetProperty("fatal").GetBoolean());
        Assert.False(string.IsNullOrEmpty(error.Data.GetProperty("message").GetString()));

        var finished = events.Single(e => e.Kind == "finished");
        Assert.Equal((int)HeadlessExitCode.AgentFailure, finished.Data.GetProperty("exit_code").GetInt32());

        // No output file written on the failure path.
        Assert.False(File.Exists(config.Output.File));
        Assert.DoesNotContain(events, e => e.Kind == "output_written");
    }

    [Fact]
    public async Task UnsupportedToolTransport_EmitsCompleteFailureEnvelope()
    {
        using var ws = new TempDir();
        var config = NewConfig(ws) with
        {
            Tools = new[] { new HeadlessTool { Name = "broken", Transport = "smoke-signal" } },
        };
        // Provider should never be consulted; tool wiring fails first.
        var provider = new FakeLlmProvider(FakeLlmProvider.TextResponse("unreachable"));

        var (events, code) = await RunAsync(config, provider);

        Assert.Equal(HeadlessExitCode.AgentFailure, code);
        Assert.Equal(0, provider.CompleteCallCount);

        Assert.Equal("started", events.First().Kind);
        var error = events.Single(e => e.Kind == "error");
        Assert.True(error.Data.GetProperty("fatal").GetBoolean());
        var finished = events.Single(e => e.Kind == "finished");
        Assert.Equal((int)HeadlessExitCode.AgentFailure, finished.Data.GetProperty("exit_code").GetInt32());
        Assert.Equal("finished", events.Last().Kind);
        Assert.Single(events, e => e.Kind == "started");
        Assert.Single(events, e => e.Kind == "finished");
        Assert.DoesNotContain(events, e => e.Kind == "tool_usage_audit");
    }

    [Fact]
    public async Task UnknownProvider_EmitsCompleteFailureEnvelope()
    {
        using var ws = new TempDir();
        var config = NewConfig(ws) with
        {
            Model = new HeadlessModel { Provider = "not-a-provider", Id = "missing-model" },
        };
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());

        var code = await HeadlessAgentRunner.ExecuteAsync(
            config, stdout, stderr, NullLoggerFactory.Instance);

        var events = stdout.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseEvent)
            .ToList();
        Assert.Equal(HeadlessExitCode.AgentFailure, code);
        Assert.Equal(new[] { "started", "error", "finished" }, events.Select(e => e.Kind));
        Assert.True(events[1].Data.GetProperty("fatal").GetBoolean());
        Assert.DoesNotContain(events, e => e.Kind == "tool_usage_audit");
    }

    [Fact]
    public async Task UnexpectedSetupFailure_EmitsOneCompleteInternalErrorEnvelope()
    {
        using var ws = new TempDir();
        var config = NewConfig(ws);
        var provider = new FakeLlmProvider(FakeLlmProvider.TextResponse("unreachable"));
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());

        var code = await HeadlessAgentRunner.ExecuteAsync(
            config, stdout, stderr, new ThrowingLoggerFactory(), provider);
        var events = stdout.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseEvent)
            .ToList();

        Assert.Equal(HeadlessExitCode.InternalError, code);
        Assert.Equal(0, provider.CompleteCallCount);
        Assert.Equal(new[] { "started", "error", "finished" }, events.Select(e => e.Kind));
        Assert.Equal((int)HeadlessExitCode.InternalError,
            events.Single(e => e.Kind == "finished").Data.GetProperty("exit_code").GetInt32());
        Assert.Single(events, e => e.Kind == "started");
        Assert.Single(events, e => e.Kind == "finished");
    }

    [Fact]
    public async Task MaxIterationsExhausted_MapsCurrentEngineReasonToTimeout()
    {
        using var ws = new TempDir();
        var config = NewConfig(ws) with
        {
            Tools = new[] { CrossPlatform.NoOpCliTool("noop") },
            Limits = new HeadlessLimits { MaxIterations = 1, TimeoutSeconds = 30 },
        };
        var provider = new FakeLlmProvider(FakeLlmProvider.ToolCallResponse("noop", "turn-limit-call"));

        var (events, code) = await RunAsync(config, provider);

        Assert.Equal(HeadlessExitCode.Timeout, code);
        Assert.Equal(1, provider.CompleteCallCount);
        var error = events.Single(e => e.Kind == "error");
        Assert.Contains("max_iterations=1", error.Data.GetProperty("message").GetString());
        Assert.True(error.Data.GetProperty("fatal").GetBoolean());
        var finished = events.Single(e => e.Kind == "finished");
        Assert.Equal((int)HeadlessExitCode.Timeout, finished.Data.GetProperty("exit_code").GetInt32());
        Assert.Equal(1, finished.Data.GetProperty("iterations").GetInt32());
    }

    [Theory]
    [InlineData("max_turns_exceeded", true)]
    [InlineData("MAX_TURNS_EXCEEDED", true)]
    [InlineData("max_turns", true)]
    [InlineData("tool_error", false)]
    [InlineData(null, false)]
    public void IsTurnLimitStopReason_NormalizesSupportedEngineValues(string? stopReason, bool expected)
    {
        Assert.Equal(expected, HeadlessAgentRunner.IsTurnLimitStopReason(stopReason));
    }

    [Fact]
    public async Task OutputFileUnwritable_EmitsErrorAndExitsAgentFailure()
    {
        using var ws = new TempDir();
        // Point output.file under an existing *file* so the atomic write can
        // neither create the directory nor open the temp file. Deterministic.
        var blocker = Path.Combine(ws.Path, "blocker");
        File.WriteAllText(blocker, "x");
        var badOutput = Path.Combine(blocker, "out.txt");

        var config = NewConfig(ws) with
        {
            Output = new HeadlessOutput { File = badOutput, Stream = "stdout" },
        };
        var provider = new FakeLlmProvider(FakeLlmProvider.TextResponse("cannot persist this"));

        var (events, code) = await RunAsync(config, provider);

        Assert.Equal(HeadlessExitCode.AgentFailure, code);
        var error = events.Single(e => e.Kind == "error" && e.Data.GetProperty("fatal").GetBoolean());
        Assert.Contains("output file", error.Data.GetProperty("message").GetString());

        // started always first, finished always last, with the failure code.
        Assert.Equal("started", events.First().Kind);
        Assert.Equal("finished", events.Last().Kind);
        Assert.Equal(
            (int)HeadlessExitCode.AgentFailure,
            events.Last().Data.GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public async Task EveryLineIsWellFormedNdjson()
    {
        // Regression guard against torn / non-JSON lines on the wire: one JSON
        // object per line, each with the schema_version/ts/kind/data envelope.
        using var ws = new TempDir();
        var config = NewConfig(ws);
        var provider = new FakeLlmProvider(FakeLlmProvider.TextResponse("ok"));

        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());
        await HeadlessAgentRunner.ExecuteAsync(
            config, stdout, stderr, NullLoggerFactory.Instance, provider);

        var rawLines = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(rawLines);
        foreach (var line in rawLines)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("schema_version", out _));
            Assert.True(root.TryGetProperty("ts", out _));
            Assert.True(root.TryGetProperty("kind", out _));
            Assert.True(root.TryGetProperty("data", out _));
        }
    }

    // ---- harness -----------------------------------------------------------

    private static async Task<(List<Evt> Events, HeadlessExitCode Code)> RunAsync(
        HeadlessRunConfig config, ILlmProvider provider)
    {
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());

        var code = await HeadlessAgentRunner.ExecuteAsync(
            config, stdout, stderr, NullLoggerFactory.Instance, provider);

        var events = stdout.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseEvent)
            .ToList();

        return (events, code);
    }

    private static Evt ParseEvent(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        return new Evt(
            root.GetProperty("schema_version").GetInt32(),
            root.GetProperty("kind").GetString()!,
            root.GetProperty("data").Clone());
    }

    private static HeadlessRunConfig NewConfig(TempDir ws) => new()
    {
        SchemaVersion = 1,
        RunId = Guid.NewGuid(),
        Agent = new HeadlessAgent { Slug = "aq7-agent", Instructions = "You are a test agent." },
        Model = new HeadlessModel { Provider = "scripted", Id = "scripted-model" },
        Tools = Array.Empty<HeadlessTool>(),
        Workspace = new HeadlessWorkspace { Root = ws.Path },
        Output = new HeadlessOutput { File = Path.Combine(ws.Path, "output.txt"), Stream = "stdout" },
        Limits = new HeadlessLimits { MaxIterations = 5, TimeoutSeconds = 30 },
    };

    // Minimal parsed view of one NDJSON event line.
    private sealed record Evt(int SchemaVersion, string Kind, JsonElement Data);

    private sealed class ThrowingLoggerFactory : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) =>
            throw new InvalidOperationException("logger setup failed");

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aq7-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    // ---- fakes -------------------------------------------------------------

    // Deterministic in-memory provider. HeadlessAgentRunner wraps it in a
    // SimpleAgent and drives it with a single kickoff message; each queued turn
    // is returned once, in order, so the resulting event stream is fully
    // reproducible. A turn may throw to drive the failure path. Mirrors the
    // Message/ToolCall idiom SimpleAgent expects (see the AQ3 StubLlmProvider).
    private sealed class FakeLlmProvider : ILlmProvider
    {
        private readonly Queue<Func<LlmResponse>> _turns;
        private LlmResponse? _last;

        public FakeLlmProvider(params LlmResponse[] responses)
            : this(responses.Select<LlmResponse, Func<LlmResponse>>(r => () => r))
        {
        }

        public FakeLlmProvider(params Func<LlmResponse>[] turns)
            : this((IEnumerable<Func<LlmResponse>>)turns)
        {
        }

        private FakeLlmProvider(IEnumerable<Func<LlmResponse>> turns)
        {
            _turns = new Queue<Func<LlmResponse>>(turns);
        }

        public string Name => "scripted";

        public int CompleteCallCount { get; private set; }

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            CompleteCallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            if (_turns.Count > 0)
            {
                _last = _turns.Dequeue()();
            }
            return Task.FromResult(_last ?? TextResponse(string.Empty));
        }

        public async IAsyncEnumerable<LlmStreamResponse> StreamCompleteAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // SimpleAgent's headless loop uses CompleteAsync; implement streaming
            // as a single terminal chunk for completeness.
            var response = await CompleteAsync(request, cancellationToken);
            yield return new LlmStreamResponse
            {
                Delta = response.AssistantMessage,
                IsComplete = true,
                FinishReason = response.FinishReason,
            };
        }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<IEnumerable<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Enumerable.Empty<ModelInfo>());

        // A final assistant message with no tool calls and FinishReason="stop"
        // terminates SimpleAgent's loop after one turn.
        public static LlmResponse TextResponse(string text) => new()
        {
            AssistantMessage = new Message
            {
                Role = Role.Assistant,
                Content = text,
                ToolCalls = new List<ToolCall>(),
            },
            FinishReason = "stop",
            Model = "scripted-model",
        };

        // An assistant message carrying a single tool call drives one tool turn.
        public static LlmResponse ToolCallResponse(
            string toolName,
            string callId,
            string argumentsJson = "{\"args\":[]}") => new()
            {
                AssistantMessage = new Message
                {
                    Role = Role.Assistant,
                    Content = string.Empty,
                    ToolCalls = new List<ToolCall>
                {
                    new()
                    {
                        Id = callId,
                        Name = toolName,
                        ArgumentsJson = argumentsJson,
                    },
                },
                },
                FinishReason = "tool_calls",
                Model = "scripted-model",
            };
    }

    // Helpers that pick real, no-dependency binaries for the cli-transport
    // tests so they run unchanged on POSIX and Windows hosts.
    private static class CrossPlatform
    {
        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        // A cli tool that exits 0 and needs no input. CliSubprocessTool treats a
        // zero exit as success regardless of stdout.
        public static HeadlessTool NoOpCliTool(string name) => IsWindows
            ? new HeadlessTool { Name = name, Transport = "cli", Binary = "cmd", Command = new[] { "cmd", "/c", "echo" } }
            : new HeadlessTool { Name = name, Transport = "cli", Binary = "echo", Command = new[] { "echo" } };

        // Writes a tiny script into <dir> that echoes <marker> and exits 0,
        // returning a HeadlessTool named "echo_marker" that invokes it.
        public static HeadlessTool WriteEchoScript(string dir, string marker)
        {
            if (IsWindows)
            {
                var bat = Path.Combine(dir, "echo_marker.bat");
                File.WriteAllText(bat, $"@echo {marker}\r\n");
                return new HeadlessTool
                {
                    Name = "echo_marker",
                    Transport = "cli",
                    Binary = "cmd",
                    Command = new[] { "cmd", "/c", bat },
                };
            }

            var sh = Path.Combine(dir, "echo_marker.sh");
            File.WriteAllText(sh, $"#!/bin/sh\necho {marker}\n");
            return new HeadlessTool
            {
                Name = "echo_marker",
                Transport = "cli",
                Binary = "sh",
                Command = new[] { "sh", sh },
            };
        }
    }
}
