// rivoli-ai/andy-cli#279: end-to-end coverage for `andy-cli run "<prompt>"`.
//
// These drive the REAL OneShotRunner -> HeadlessAgentRunner path with a scripted
// ILlmProvider, so they exercise the production DI wiring, the production
// permission gate (fail-closed, no broker), the production event emitter, and the
// production HeadlessExitCode mapping. A regression in any of those shows up here
// rather than in a parallel re-implementation.

using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Andy.Cli.HeadlessConfig;
using Andy.Cli.OneShot;
using Andy.Model.Llm;
using Andy.Model.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Cli.Tests.OneShot;

public class OneShotRunnerTests
{
    // Guard for "never blocks waiting for an interactive approval": every run in
    // this file must terminate well inside this budget.
    private static readonly TimeSpan NonBlockingBudget = TimeSpan.FromSeconds(60);

    private static readonly string[] KnownEventKinds =
    [
        "started",
        "llm_chunk",
        "tool_call_started",
        "tool_call_finished",
        "tool_usage_audit",
        "required_action_verification",
        "output_written",
        "error",
        "finished"
    ];

    // ---- input combination -------------------------------------------------

    [Fact]
    public async Task PositionalOnly_SendsTheWordsAsTheUserTurnAndPrintsTheAnswer()
    {
        using var ws = new TempDir();
        var llm = new ScriptedLlmProvider(ScriptedLlmProvider.TextResponse("Positional answer."));

        var run = await RunAsync(["run", "--cwd", ws.Path, "explain", "this", "repository"], llm);

        Assert.Equal(HeadlessExitCode.Success, run.Code);
        Assert.Equal("explain this repository", run.UserPrompt);
        Assert.Equal("Positional answer.", run.Stdout.Trim());
    }

    [Fact]
    public async Task StdinOnly_SendsThePipedTextVerbatim()
    {
        using var ws = new TempDir();
        var llm = new ScriptedLlmProvider(ScriptedLlmProvider.TextResponse("Stdin answer."));

        var run = await RunAsync(
            ["run", "--cwd", ws.Path], llm, stdin: "diff --git a/x b/x\n+added\n");

        Assert.Equal(HeadlessExitCode.Success, run.Code);
        Assert.Equal("diff --git a/x b/x\n+added", run.UserPrompt);
        Assert.Equal("Stdin answer.", run.Stdout.Trim());
    }

    [Fact]
    public async Task PositionalAndStdin_AreCombinedWithTheDocumentedSeparator()
    {
        using var ws = new TempDir();
        var llm = new ScriptedLlmProvider(ScriptedLlmProvider.TextResponse("Combined answer."));

        var run = await RunAsync(
            ["run", "--cwd", ws.Path, "review this diff"], llm, stdin: "diff --git a/x b/x\n");

        Assert.Equal(HeadlessExitCode.Success, run.Code);
        Assert.Equal(
            "review this diff\n\n"
            + OneShotPrompt.StdinBeginMarker + "\n"
            + "diff --git a/x b/x\n"
            + OneShotPrompt.StdinEndMarker,
            run.UserPrompt);
    }

    [Fact]
    public async Task NoStdinFlag_IgnoresRedirectedInput()
    {
        using var ws = new TempDir();
        var llm = new ScriptedLlmProvider(ScriptedLlmProvider.TextResponse("ok"));

        var run = await RunAsync(
            ["run", "--cwd", ws.Path, "--no-stdin", "just this"], llm, stdin: "should be ignored");

        Assert.Equal(HeadlessExitCode.Success, run.Code);
        Assert.Equal("just this", run.UserPrompt);
    }

    [Fact]
    public async Task UnicodeStdin_SurvivesTheRoundTrip()
    {
        using var ws = new TempDir();
        const string unicode = "café 你好 مرحبا 🚀 — em dash";
        var llm = new ScriptedLlmProvider(ScriptedLlmProvider.TextResponse(unicode));

        var run = await RunAsync(["run", "--cwd", ws.Path, "echo"], llm, stdin: unicode);

        Assert.Equal(HeadlessExitCode.Success, run.Code);
        Assert.Contains(unicode, run.UserPrompt, StringComparison.Ordinal);
        Assert.Contains(unicode, run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LargeStdin_IsTruncatedAtTheBoundAndReportedOnStderr()
    {
        using var ws = new TempDir();
        var huge = new string('z', OneShotPrompt.MaxStdinChars + 4096);
        var llm = new ScriptedLlmProvider(ScriptedLlmProvider.TextResponse("summarized"));

        var run = await RunAsync(["run", "--cwd", ws.Path, "summarise this"], llm, stdin: huge);

        Assert.Equal(HeadlessExitCode.Success, run.Code);
        Assert.Contains("truncated", run.Stderr, StringComparison.OrdinalIgnoreCase);
        // Instruction + separator scaffolding + exactly MaxStdinChars of payload.
        Assert.Equal(OneShotPrompt.MaxStdinChars, run.UserPrompt!.Count(c => c == 'z'));
    }

    [Fact]
    public async Task LargeStdinBelowTheBound_IsDeliveredWhole()
    {
        using var ws = new TempDir();
        var large = new string('q', 512 * 1024);
        var llm = new ScriptedLlmProvider(ScriptedLlmProvider.TextResponse("ok"));

        var run = await RunAsync(["run", "--cwd", ws.Path, "count"], llm, stdin: large);

        Assert.Equal(HeadlessExitCode.Success, run.Code);
        Assert.Equal(large.Length, run.UserPrompt!.Count(c => c == 'q'));
    }

    // ---- empty input -------------------------------------------------------

    [Fact]
    public async Task NoPromptAtAll_ExitsNonZeroWithActionableUsage()
    {
        var run = await RunAsync(["run"], llm: null, stdin: null);

        Assert.NotEqual(HeadlessExitCode.Success, run.Code);
        Assert.Equal(HeadlessExitCode.ConfigError, run.Code);
        Assert.Contains("no prompt", run.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pipe it on stdin", run.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("andy-cli run [options]", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(string.Empty, run.Stdout);
    }

    [Fact]
    public async Task WhitespaceOnlyStdinAndNoWords_ExitsNonZeroWithUsage()
    {
        var run = await RunAsync(["run"], llm: null, stdin: "   \n\t\n");

        Assert.Equal(HeadlessExitCode.ConfigError, run.Code);
        Assert.Contains("no prompt", run.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    // ---- option validation -------------------------------------------------

    [Fact]
    public async Task UnknownFlag_ExitsConfigErrorWithUsage()
    {
        var run = await RunAsync(["run", "--weird", "hi"], llm: null);

        Assert.Equal(HeadlessExitCode.ConfigError, run.Code);
        Assert.Contains("Unknown argument", run.Stderr, StringComparison.Ordinal);
        Assert.Contains("Usage:", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingCwd_ExitsConfigError()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"one-shot-missing-{Guid.NewGuid():N}");

        var run = await RunAsync(["run", "--cwd", missing, "hi"], llm: null);

        Assert.Equal(HeadlessExitCode.ConfigError, run.Code);
        Assert.Contains("does not exist", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnresolvableProvider_ExitsConfigError()
    {
        using var ws = new TempDir();

        var run = await RunAsync(
            ["run", "--cwd", ws.Path, "hi"],
            llm: null,
            modelResolver: (_, _) => OneShotModelResolution.Failed("No LLM provider is configured."));

        Assert.Equal(HeadlessExitCode.ConfigError, run.Code);
        Assert.Contains("No LLM provider is configured", run.Stderr, StringComparison.Ordinal);
    }

    // ---- output modes ------------------------------------------------------

    [Fact]
    public async Task TextMode_KeepsStdoutFreeOfNdjsonAndPutsNarrationOnStderr()
    {
        using var ws = new TempDir();
        var llm = new ScriptedLlmProvider(
            ScriptedLlmProvider.ToolCallResponse("read_file", "call-1", $"{{\"file_path\":\"{Escape(ws.Path)}/a.txt\"}}"),
            ScriptedLlmProvider.TextResponse("Read it."));
        File.WriteAllText(Path.Combine(ws.Path, "a.txt"), "hello");

        var run = await RunAsync(["run", "--cwd", ws.Path, "read a.txt"], llm);

        Assert.Equal(HeadlessExitCode.Success, run.Code);
        Assert.Equal("Read it.", run.Stdout.Trim());
        Assert.DoesNotContain("schema_version", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("[tool] read_file", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutputFlag_WritesTheAnswerToTheNamedFile()
    {
        using var ws = new TempDir();
        var target = Path.Combine(ws.Path, "answer.txt");
        var llm = new ScriptedLlmProvider(ScriptedLlmProvider.TextResponse("Durable answer."));

        var run = await RunAsync(["run", "--cwd", ws.Path, "--output", target, "hi"], llm);

        Assert.Equal(HeadlessExitCode.Success, run.Code);
        Assert.True(File.Exists(target));
        Assert.Equal("Durable answer.", File.ReadAllText(target));
    }

    [Fact]
    public async Task TextMode_DoesNotLeaveATempOutputFileBehind()
    {
        using var ws = new TempDir();
        var before = Directory.GetFiles(Path.GetTempPath(), "andy-oneshot-*.txt").Length;
        var llm = new ScriptedLlmProvider(ScriptedLlmProvider.TextResponse("clean"));

        var run = await RunAsync(["run", "--cwd", ws.Path, "hi"], llm);

        Assert.Equal(HeadlessExitCode.Success, run.Code);
        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "andy-oneshot-*.txt").Length);
    }

    // ---- NDJSON schema stability ------------------------------------------

    [Fact]
    public async Task NdjsonMode_EmitsTheSameSchemaStableEnvelopeAsTheHeadlessRuntime()
    {
        using var ws = new TempDir();
        var llm = new ScriptedLlmProvider(ScriptedLlmProvider.TextResponse("Machine answer."));

        var run = await RunAsync(["run", "--cwd", ws.Path, "--json", "hi"], llm);

        Assert.Equal(HeadlessExitCode.Success, run.Code);

        var events = ParseEvents(run.Stdout);
        Assert.NotEmpty(events);
        foreach (var e in events)
        {
            Assert.Equal(1, e.GetProperty("schema_version").GetInt32());
            Assert.True(DateTimeOffset.TryParse(e.GetProperty("ts").GetString(), out _));
            var kind = e.GetProperty("kind").GetString();
            Assert.Contains(kind, KnownEventKinds);
            Assert.Equal(JsonValueKind.Object, e.GetProperty("data").ValueKind);
        }

        Assert.Equal("started", events[0].GetProperty("kind").GetString());
        Assert.Equal("finished", events[^1].GetProperty("kind").GetString());
        Assert.Equal(0, events[^1].GetProperty("data").GetProperty("exit_code").GetInt32());
        Assert.Contains(events, e => e.GetProperty("kind").GetString() == "output_written");

        var started = events[0].GetProperty("data");
        Assert.Equal(OneShotRunner.AgentSlug, started.GetProperty("agent_slug").GetString());
        Assert.Equal("anthropic", started.GetProperty("model_provider").GetString());
        Assert.Equal("stub-model", started.GetProperty("model_id").GetString());
    }

    [Fact]
    public async Task NdjsonMode_ExitCodeMatchesTheFinishedEvent()
    {
        using var ws = new TempDir();
        var llm = new ScriptedLlmProvider(() => throw new InvalidOperationException("provider exploded"));

        var run = await RunAsync(["run", "--cwd", ws.Path, "--json", "hi"], llm);

        Assert.Equal(HeadlessExitCode.AgentFailure, run.Code);
        var events = ParseEvents(run.Stdout);
        Assert.Equal((int)run.Code, events[^1].GetProperty("data").GetProperty("exit_code").GetInt32());
        Assert.Contains(events, e =>
            e.GetProperty("kind").GetString() == "error"
            && e.GetProperty("data").GetProperty("fatal").GetBoolean());
    }

    // ---- permissions -------------------------------------------------------

    [Fact]
    public async Task DefaultProfile_DeniesMutatingToolsWithoutEverPrompting()
    {
        using var ws = new TempDir();
        var llm = new ScriptedLlmProvider(
            ScriptedLlmProvider.ToolCallResponse(
                "write_file", "deny-1", $"{{\"file_path\":\"{Escape(ws.Path)}/new.txt\",\"content\":\"x\"}}"),
            ScriptedLlmProvider.TextResponse("I could not write."));

        var run = await RunAsync(["run", "--cwd", ws.Path, "--json", "write a file"], llm);

        // The run terminated on its own (no interactive approval was awaited).
        Assert.Equal(HeadlessExitCode.Success, run.Code);
        Assert.False(File.Exists(Path.Combine(ws.Path, "new.txt")));

        var events = ParseEvents(run.Stdout);
        var finished = events.Single(e => e.GetProperty("kind").GetString() == "tool_call_finished");
        Assert.Equal("denied", finished.GetProperty("data").GetProperty("outcome").GetString());
        Assert.False(finished.GetProperty("data").GetProperty("ok").GetBoolean());

        var audit = events.Single(e => e.GetProperty("kind").GetString() == "tool_usage_audit");
        Assert.Empty(audit.GetProperty("data").GetProperty("allowed_tools").EnumerateArray());
        var row = Assert.Single(audit.GetProperty("data").GetProperty("tools").EnumerateArray());
        Assert.Equal("write_file", row.GetProperty("tool_name").GetString());
        Assert.False(row.GetProperty("permitted").GetBoolean());
    }

    [Fact]
    public async Task DefaultProfile_LeavesReadOnlyToolsAllowed()
    {
        using var ws = new TempDir();
        File.WriteAllText(Path.Combine(ws.Path, "a.txt"), "hello");
        var llm = new ScriptedLlmProvider(
            ScriptedLlmProvider.ToolCallResponse("read_file", "read-1", $"{{\"file_path\":\"{Escape(ws.Path)}/a.txt\"}}"),
            ScriptedLlmProvider.TextResponse("Read."));

        var run = await RunAsync(["run", "--cwd", ws.Path, "--json", "read a.txt"], llm);

        Assert.Equal(HeadlessExitCode.Success, run.Code);
        var events = ParseEvents(run.Stdout);
        var audit = events.Single(e => e.GetProperty("kind").GetString() == "tool_usage_audit");
        var row = Assert.Single(audit.GetProperty("data").GetProperty("tools").EnumerateArray());
        Assert.Equal("read_file", row.GetProperty("tool_name").GetString());
        Assert.True(row.GetProperty("permitted").GetBoolean());
    }

    [Fact]
    public async Task AllowToolFlag_RelaxesExactlyTheNamedTool()
    {
        using var ws = new TempDir();
        var target = Path.Combine(ws.Path, "written.txt");
        var llm = new ScriptedLlmProvider(
            ScriptedLlmProvider.ToolCallResponse(
                "write_file", "allow-1", $"{{\"file_path\":\"{Escape(target)}\",\"content\":\"written\"}}"),
            ScriptedLlmProvider.ToolCallResponse(
                "delete_file", "deny-2", $"{{\"file_path\":\"{Escape(target)}\"}}"),
            ScriptedLlmProvider.TextResponse("Done."));

        var run = await RunAsync(
            ["run", "--cwd", ws.Path, "--json", "--allow-tool", "write_file", "write it"], llm);

        Assert.Equal(HeadlessExitCode.Success, run.Code);

        var events = ParseEvents(run.Stdout);
        var audit = events.Single(e => e.GetProperty("kind").GetString() == "tool_usage_audit");
        Assert.Equal(
            new[] { "write_file" },
            audit.GetProperty("data").GetProperty("allowed_tools").EnumerateArray()
                .Select(x => x.GetString()).ToArray());

        var rows = audit.GetProperty("data").GetProperty("tools").EnumerateArray().ToList();
        Assert.True(rows.Single(r => r.GetProperty("tool_name").GetString() == "write_file")
            .GetProperty("permitted").GetBoolean());
        Assert.False(rows.Single(r => r.GetProperty("tool_name").GetString() == "delete_file")
            .GetProperty("permitted").GetBoolean());
    }

    // ---- redaction ---------------------------------------------------------

    [Fact]
    public async Task ToolArgumentsAreDigested_NotEchoedOnTheEventStream()
    {
        using var ws = new TempDir();
        const string secret = "SUPER-SECRET-TOKEN-4f2b";
        var llm = new ScriptedLlmProvider(
            ScriptedLlmProvider.ToolCallResponse(
                "execute_command", "cmd-1", $"{{\"command\":\"echo {secret}\"}}"),
            ScriptedLlmProvider.TextResponse("finished"));

        var run = await RunAsync(["run", "--cwd", ws.Path, "--json", "run a command"], llm);

        Assert.Equal(HeadlessExitCode.Success, run.Code);
        Assert.DoesNotContain(secret, run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, run.Stderr, StringComparison.Ordinal);

        var events = ParseEvents(run.Stdout);
        var started = events.Single(e => e.GetProperty("kind").GetString() == "tool_call_started");
        Assert.StartsWith("sha256:", started.GetProperty("data").GetProperty("args_digest").GetString());
    }

    // ---- cancellation, timeout, exit codes --------------------------------

    [Fact]
    public async Task AlreadyCancelledToken_ExitsCancelled()
    {
        using var ws = new TempDir();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var llm = new ScriptedLlmProvider(ScriptedLlmProvider.TextResponse("never"));

        var run = await RunAsync(["run", "--cwd", ws.Path, "hi"], llm, ct: cts.Token);

        Assert.Equal(HeadlessExitCode.Cancelled, run.Code);
    }

    [Fact]
    public async Task SignalCancellationMidRun_ExitsCancelledAndWritesNoAnswer()
    {
        using var ws = new TempDir();
        using var cts = new CancellationTokenSource();
        var target = Path.Combine(ws.Path, "answer.txt");
        // Emulates the SIGTERM path Program.RunHeadlessAsync wires: cancel the
        // shared CTS while the provider is still working.
        var llm = new ScriptedLlmProvider(async ct =>
        {
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return ScriptedLlmProvider.TextResponse("too late");
        });

        var run = await RunAsync(
            ["run", "--cwd", ws.Path, "--output", target, "hi"], llm, ct: cts.Token);

        Assert.Equal(HeadlessExitCode.Cancelled, run.Code);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task TimeoutFlag_MapsToTheHeadlessTimeoutExitCode()
    {
        using var ws = new TempDir();
        var llm = new ScriptedLlmProvider(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            return ScriptedLlmProvider.TextResponse("late");
        });

        var run = await RunAsync(["run", "--cwd", ws.Path, "--timeout", "1", "--json", "hi"], llm);

        Assert.Equal(HeadlessExitCode.Timeout, run.Code);
        var events = ParseEvents(run.Stdout);
        Assert.Equal(4, events[^1].GetProperty("data").GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public async Task MaxIterationsExhausted_MapsToTheHeadlessTimeoutExitCode()
    {
        using var ws = new TempDir();
        // Always answers with another tool call, so the turn budget runs out.
        var llm = new ScriptedLlmProvider(
            () => ScriptedLlmProvider.ToolCallResponse("read_file", "loop", "{\"file_path\":\"nope.txt\"}"));

        var run = await RunAsync(["run", "--cwd", ws.Path, "--max-iterations", "2", "hi"], llm);

        Assert.Equal(HeadlessExitCode.Timeout, run.Code);
    }

    [Fact]
    public async Task ProviderFailure_MapsToAgentFailureAndPrintsNoAnswer()
    {
        using var ws = new TempDir();
        var llm = new ScriptedLlmProvider(() => throw new InvalidOperationException("provider exploded"));

        var run = await RunAsync(["run", "--cwd", ws.Path, "hi"], llm);

        Assert.Equal(HeadlessExitCode.AgentFailure, run.Code);
        Assert.Equal(string.Empty, run.Stdout);
        Assert.Contains("[error]", run.Stderr, StringComparison.Ordinal);
    }

    // ---- broken pipe -------------------------------------------------------

    [Fact]
    public async Task BrokenStdoutPipe_DoesNotCrashOrChangeTheExitCode()
    {
        using var ws = new TempDir();
        var llm = new ScriptedLlmProvider(ScriptedLlmProvider.TextResponse("answer nobody reads"));

        var stdout = new ExplodingWriter();
        var stderr = new StringWriter(new StringBuilder());

        var code = await OneShotRunner.RunAsync(
            ["run", "--cwd", ws.Path, "--json", "hi"],
            stdout,
            stderr,
            NullLoggerFactory.Instance,
            stdin: null,
            modelResolver: StubResolver,
            llmProviderOverride: llm).WaitAsync(NonBlockingBudget);

        Assert.Equal(HeadlessExitCode.Success, code);
        Assert.True(stdout.Attempts > 0);
    }

    [Fact]
    public async Task BrokenStderrPipe_DoesNotCrashTextMode()
    {
        using var ws = new TempDir();
        var llm = new ScriptedLlmProvider(ScriptedLlmProvider.TextResponse("answer"));

        var stdout = new StringWriter(new StringBuilder());
        var stderr = new ExplodingWriter();

        var code = await OneShotRunner.RunAsync(
            ["run", "--cwd", ws.Path, "hi"],
            stdout,
            stderr,
            NullLoggerFactory.Instance,
            stdin: null,
            modelResolver: StubResolver,
            llmProviderOverride: llm).WaitAsync(NonBlockingBudget);

        Assert.Equal(HeadlessExitCode.Success, code);
        Assert.Equal("answer", stdout.ToString().Trim());
    }

    // ---- strict headless compatibility ------------------------------------

    [Fact]
    public async Task HeadlessRunner_StillRoutesTheStrictContractToTheConfigPath()
    {
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());

        var code = await HeadlessRunner.RunAsync(
            ["run", "--headless"], stdout, stderr, NullLoggerFactory.Instance);

        // The strict path's own "--config is required" contract, unchanged.
        Assert.Equal(HeadlessExitCode.ConfigError, code);
        Assert.Contains("--config", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeadlessRunner_DispatchesBarePromptToTheOneShotPath()
    {
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());

        // No prompt and stdin explicitly not redirected: the one-shot usage,
        // not the strict "--headless is required" message.
        var code = await HeadlessRunner.RunAsync(
            ["run"], stdout, stderr, NullLoggerFactory.Instance, stdin: null, stdinRedirected: false);

        Assert.Equal(HeadlessExitCode.ConfigError, code);
        Assert.Contains("no prompt", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HeadlessRunner_ReadsInjectedStdinForTheOneShotPath()
    {
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());

        // A prompt arrives purely on stdin; resolution then fails on the provider,
        // which proves the stdin plumbing ran (an empty prompt would have exited
        // earlier with the usage text).
        var code = await HeadlessRunner.RunAsync(
            ["run", "--provider", "definitely-not-a-provider"],
            stdout,
            stderr,
            NullLoggerFactory.Instance,
            stdin: new StringReader("piped prompt"),
            stdinRedirected: true);

        Assert.Equal(HeadlessExitCode.ConfigError, code);
        Assert.Contains("Unknown provider", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("no prompt", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // ---- harness -----------------------------------------------------------

    private static OneShotModelResolution StubResolver(string? provider, string? model)
        => OneShotModelResolution.Resolved(provider ?? "anthropic", model ?? "stub-model");

    private sealed record RunResult(
        HeadlessExitCode Code,
        string Stdout,
        string Stderr,
        string? UserPrompt);

    private static async Task<RunResult> RunAsync(
        string[] args,
        ScriptedLlmProvider? llm,
        string? stdin = null,
        OneShotModelResolver? modelResolver = null,
        CancellationToken ct = default)
    {
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());

        var code = await OneShotRunner.RunAsync(
            args,
            stdout,
            stderr,
            NullLoggerFactory.Instance,
            stdin: stdin is null ? null : new StringReader(stdin),
            modelResolver: modelResolver ?? StubResolver,
            llmProviderOverride: llm,
            ct: ct).WaitAsync(NonBlockingBudget);

        return new RunResult(code, stdout.ToString(), stderr.ToString(), llm?.FirstUserMessage);
    }

    private static List<JsonElement> ParseEvents(string ndjson)
        => ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();

    private static string Escape(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"one-shot-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }

    // A TextWriter that fails like a closed pipe on every write.
    private sealed class ExplodingWriter : TextWriter
    {
        public int Attempts { get; private set; }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            Attempts++;
            throw new IOException("Broken pipe");
        }

        public override void Write(string? value)
        {
            Attempts++;
            throw new IOException("Broken pipe");
        }

        public override void WriteLine(string? value)
        {
            Attempts++;
            throw new IOException("Broken pipe");
        }

        public override void Flush() => throw new IOException("Broken pipe");
    }

    // Deterministic scripted provider. Records the first user turn so the tests
    // can assert on exactly what the input-combination rules produced.
    private sealed class ScriptedLlmProvider : ILlmProvider
    {
        private readonly Queue<Func<CancellationToken, Task<LlmResponse>>> _turns;
        private Func<CancellationToken, Task<LlmResponse>>? _last;

        public ScriptedLlmProvider(params LlmResponse[] responses)
            : this(responses.Select<LlmResponse, Func<CancellationToken, Task<LlmResponse>>>(
                r => _ => Task.FromResult(r)))
        {
        }

        public ScriptedLlmProvider(params Func<LlmResponse>[] turns)
            : this(turns.Select<Func<LlmResponse>, Func<CancellationToken, Task<LlmResponse>>>(
                t => _ => Task.FromResult(t())))
        {
        }

        public ScriptedLlmProvider(Func<CancellationToken, Task<LlmResponse>> turn)
            : this(new[] { turn })
        {
        }

        private ScriptedLlmProvider(IEnumerable<Func<CancellationToken, Task<LlmResponse>>> turns)
        {
            _turns = new Queue<Func<CancellationToken, Task<LlmResponse>>>(turns);
        }

        public string Name => "scripted";

        public string? FirstUserMessage { get; private set; }

        public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            FirstUserMessage ??= request.Messages
                .FirstOrDefault(m => m.Role == Role.User)?.Content;

            cancellationToken.ThrowIfCancellationRequested();

            if (_turns.Count > 0)
            {
                _last = _turns.Dequeue();
            }
            if (_last is null)
            {
                return TextResponse(string.Empty);
            }
            return await _last(cancellationToken);
        }

        public async IAsyncEnumerable<LlmStreamResponse> StreamCompleteAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
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

        public static LlmResponse TextResponse(string text) => new()
        {
            AssistantMessage = new Message
            {
                Role = Role.Assistant,
                Content = text,
                ToolCalls = new List<ToolCall>(),
            },
            FinishReason = "stop",
            Model = "stub-model",
        };

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
                Model = "stub-model",
            };
    }
}
