// rivoli-ai/andy-cli#180: runtime enforcement of previously-inactive v1 fields.
// Exercises HeadlessAgentRunner with a stub ILlmProvider so behavior is
// deterministic and offline: workspace.branch verification, agent.output_format
// enforcement, env_vars / api_key_ref application, and atomic-output cleanup.

using System.IO;
using System.Text;
using Andy.Cli.Headless;
using Andy.Cli.HeadlessConfig;
using Andy.Llm.Configuration;
using Andy.Model.Llm;
using Andy.Model.Model;
using Andy.Model.Tooling;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Cli.Tests.Headless;

public class HeadlessInactiveFieldsRuntimeTests
{
    // ---- workspace.branch verification --------------------------------------

    [Fact]
    public async Task Branch_Matches_Runs()
    {
        using var ws = new TempDir();
        var config = Config(ws, branch: "main");
        var (stdout, stderr) = NewIo();

        var code = await HeadlessAgentRunner.ExecuteAsync(
            config, stdout, stderr, NullLoggerFactory.Instance,
            llmProviderOverride: new StubLlm("DONE"),
            currentBranchResolver: _ => "main");

        Assert.Equal(HeadlessExitCode.Success, code);
    }

    [Fact]
    public async Task Branch_Mismatch_FailsFast()
    {
        using var ws = new TempDir();
        var config = Config(ws, branch: "main");
        var (stdout, stderr) = NewIo();

        var code = await HeadlessAgentRunner.ExecuteAsync(
            config, stdout, stderr, NullLoggerFactory.Instance,
            llmProviderOverride: new StubLlm("DONE"),
            currentBranchResolver: _ => "feature/other");

        Assert.Equal(HeadlessExitCode.AgentFailure, code);
        var mismatchOut = stdout.ToString();
        Assert.Contains("\"kind\":\"error\"", mismatchOut);
        Assert.Contains("mismatch", mismatchOut);
        // The run must not have produced an output file.
        Assert.False(File.Exists(config.Output.File));
    }

    [Fact]
    public async Task Branch_Unresolvable_FailsFast()
    {
        using var ws = new TempDir();
        var config = Config(ws, branch: "main");
        var (stdout, stderr) = NewIo();

        var code = await HeadlessAgentRunner.ExecuteAsync(
            config, stdout, stderr, NullLoggerFactory.Instance,
            llmProviderOverride: new StubLlm("DONE"),
            currentBranchResolver: _ => null);

        Assert.Equal(HeadlessExitCode.AgentFailure, code);
    }

    [Fact]
    public async Task Branch_Absent_NotVerified()
    {
        using var ws = new TempDir();
        var config = Config(ws, branch: null);
        var (stdout, stderr) = NewIo();
        var resolverCalled = false;

        var code = await HeadlessAgentRunner.ExecuteAsync(
            config, stdout, stderr, NullLoggerFactory.Instance,
            llmProviderOverride: new StubLlm("DONE"),
            currentBranchResolver: _ => { resolverCalled = true; return "anything"; });

        Assert.Equal(HeadlessExitCode.Success, code);
        Assert.False(resolverCalled);
    }

    // ---- agent.output_format enforcement ------------------------------------

    [Fact]
    public async Task OutputFormat_Json_InvalidOutput_FailsAndWritesNoFile()
    {
        using var ws = new TempDir();
        var config = Config(ws, outputFormat: "json-triage-output-v1");
        var (stdout, stderr) = NewIo();

        var code = await HeadlessAgentRunner.ExecuteAsync(
            config, stdout, stderr, NullLoggerFactory.Instance,
            llmProviderOverride: new StubLlm("this is not json"));

        Assert.Equal(HeadlessExitCode.AgentFailure, code);
        Assert.False(File.Exists(config.Output.File));
        var formatOut = stdout.ToString();
        Assert.Contains("\"kind\":\"error\"", formatOut);
        Assert.Contains("valid", formatOut);
    }

    [Fact]
    public async Task OutputFormat_Json_ValidOutput_Succeeds()
    {
        using var ws = new TempDir();
        var config = Config(ws, outputFormat: "json");
        var (stdout, stderr) = NewIo();

        var code = await HeadlessAgentRunner.ExecuteAsync(
            config, stdout, stderr, NullLoggerFactory.Instance,
            llmProviderOverride: new StubLlm("{\"ok\": true}"));

        Assert.Equal(HeadlessExitCode.Success, code);
        Assert.Equal("{\"ok\": true}", File.ReadAllText(config.Output.File));
    }

    [Fact]
    public async Task OutputFormat_Plain_DoesNotConstrainOutput()
    {
        using var ws = new TempDir();
        var config = Config(ws, outputFormat: "plain");
        var (stdout, stderr) = NewIo();

        var code = await HeadlessAgentRunner.ExecuteAsync(
            config, stdout, stderr, NullLoggerFactory.Instance,
            llmProviderOverride: new StubLlm("free-form prose, not JSON"));

        Assert.Equal(HeadlessExitCode.Success, code);
    }

    [Theory]
    [InlineData("json", true)]
    [InlineData("json-plan-v1", true)]
    [InlineData("JSON", true)]
    [InlineData("plain", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void RequiresJsonOutput_Classifies(string? format, bool expected)
    {
        Assert.Equal(expected, HeadlessAgentRunner.RequiresJsonOutput(format));
    }

    // ---- atomic-output cleanup on write failure ------------------------------

    [Fact]
    public async Task OutputWrite_Failure_LeavesNoTempFile()
    {
        using var ws = new TempDir();
        // Point output.file at an existing DIRECTORY so the final rename fails
        // after the temp file has been written; the temp must be cleaned up.
        var collidingDir = Path.Combine(ws.Path, "output-is-a-dir");
        Directory.CreateDirectory(collidingDir);

        var config = Config(ws, outputFile: collidingDir);
        var (stdout, stderr) = NewIo();

        var code = await HeadlessAgentRunner.ExecuteAsync(
            config, stdout, stderr, NullLoggerFactory.Instance,
            llmProviderOverride: new StubLlm("DONE"));

        Assert.Equal(HeadlessExitCode.AgentFailure, code);
        var leftoverTemps = Directory.GetFiles(ws.Path, "output-is-a-dir.tmp.*");
        Assert.Empty(leftoverTemps);
    }

    // ---- output.stream == fifo redirects the event stream --------------------

    [Fact]
    public async Task FifoStream_RedirectsEventsToSinkPath_NotStdout()
    {
        using var ws = new TempDir();
        // Stand in for the named FIFO with a regular file the runtime opens for
        // writing; the container runtime supplies an actual mkfifo path in prod.
        var sinkPath = Path.Combine(ws.Path, "events.stream");
        await File.WriteAllTextAsync(sinkPath, string.Empty);

        // One MCP tool at a closed local port: tool wiring fails fast (no network
        // wait), which still emits error + finished to the chosen event stream.
        var configJson = $$"""
        {
          "schema_version": 1,
          "run_id": "{{Guid.NewGuid()}}",
          "agent": { "slug": "fifo-agent", "instructions": "stub" },
          "model": { "provider": "anthropic", "id": "claude-sonnet-4-6" },
          "tools": [
            { "name": "unreachable", "transport": "mcp", "endpoint": "http://127.0.0.1:9/mcp/unreachable" }
          ],
          "workspace": { "root": "{{ws.Path.Replace("\\", "\\\\")}}" },
          "output": { "file": "{{Path.Combine(ws.Path, "out.txt").Replace("\\", "\\\\")}}", "stream": "fifo" },
          "event_sink": { "path": "{{sinkPath.Replace("\\", "\\\\")}}" },
          "limits": { "max_iterations": 2, "timeout_seconds": 10 }
        }
        """;
        var configPath = Path.Combine(ws.Path, "run.json");
        await File.WriteAllTextAsync(configPath, configJson);

        var (stdout, stderr) = NewIo();
        var code = await HeadlessRunner.RunAsync(
            ["run", "--headless", "--config", configPath], stdout, stderr, NullLoggerFactory.Instance);

        Assert.Equal(HeadlessExitCode.AgentFailure, code);

        // Events went to the FIFO sink, not stdout.
        var sinkContent = await File.ReadAllTextAsync(sinkPath);
        Assert.Contains("\"kind\":\"finished\"", sinkContent);
        Assert.DoesNotContain("\"kind\":\"finished\"", stdout.ToString());
    }

    // ---- env_vars / api_key_ref application (unit) ---------------------------

    [Fact]
    public void ApplyEnvVars_SetsNonReserved_SkipsReserved()
    {
        var name = $"H180_TEST_{Guid.NewGuid():N}";
        try
        {
            HeadlessAgentRunner.ApplyEnvVars(new Dictionary<string, string>
            {
                [name] = "applied",
                ["ANDY_TOKEN"] = "must-not-apply",
            });

            Assert.Equal("applied", Environment.GetEnvironmentVariable(name));
            Assert.NotEqual("must-not-apply", Environment.GetEnvironmentVariable("ANDY_TOKEN"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void ApplyApiKeyRef_EnvPresent_SetsProviderApiKey()
    {
        var name = $"H180_KEY_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, "resolved-secret");
        try
        {
            var options = new LlmOptions();
            options.Providers["anthropic"] = new ProviderConfig { Provider = "anthropic" };

            HeadlessAgentRunner.ApplyApiKeyRef(options, "anthropic", $"env:{name}");

            Assert.Equal("resolved-secret", options.Providers["anthropic"].ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void ApplyApiKeyRef_EnvAbsent_DoesNotOverwrite()
    {
        var options = new LlmOptions();
        options.Providers["anthropic"] = new ProviderConfig { Provider = "anthropic", ApiKey = "preexisting" };

        HeadlessAgentRunner.ApplyApiKeyRef(options, "anthropic", $"env:H180_MISSING_{Guid.NewGuid():N}");

        Assert.Equal("preexisting", options.Providers["anthropic"].ApiKey);
    }

    // ---- helpers -------------------------------------------------------------

    private static HeadlessRunConfig Config(
        TempDir ws,
        string? branch = null,
        string? outputFormat = null,
        string? outputFile = null)
        => new()
        {
            SchemaVersion = 1,
            RunId = Guid.NewGuid(),
            Agent = new HeadlessAgent
            {
                Slug = "stub-agent",
                Instructions = "stub",
                OutputFormat = outputFormat,
            },
            Model = new HeadlessModel { Provider = "stub", Id = "stub-1" },
            Tools = Array.Empty<HeadlessTool>(),
            Workspace = new HeadlessWorkspace { Root = ws.Path, Branch = branch },
            Output = new HeadlessOutput
            {
                File = outputFile ?? Path.Combine(ws.Path, "output.txt"),
                Stream = "stdout",
            },
            Limits = new HeadlessLimits { MaxIterations = 4, TimeoutSeconds = 30 },
        };

    private static (StringWriter Stdout, StringWriter Stderr) NewIo()
        => (new StringWriter(new StringBuilder()), new StringWriter(new StringBuilder()));

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"h180-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed class StubLlm : ILlmProvider
    {
        private readonly string _content;
        public StubLlm(string content) => _content = content;
        public string Name => "stub";

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmResponse
            {
                AssistantMessage = new Message
                {
                    Role = Role.Assistant,
                    Content = _content,
                    ToolCalls = new List<ToolCall>(),
                },
                FinishReason = "stop",
                Model = "stub-1",
            });

        public async IAsyncEnumerable<LlmStreamResponse> StreamCompleteAsync(
            LlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<IEnumerable<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Enumerable.Empty<ModelInfo>());
    }
}
