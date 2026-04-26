// End-to-end tests for AQ3's HeadlessAgentRunner (rivoli-ai/andy-cli#44).
// These exercise the full loop in-process — schema-loaded config →
// SimpleAgent → output file → exit code → event stream — with a stub
// ILlmProvider so the test does not depend on a real model. The empty
// tools[] keeps HeadlessToolHost a no-op so we don't need a stub MCP
// server; CliSubprocessTool/McpRemoteTool have their own focused tests.

using System.IO;
using System.Text;
using System.Text.Json;
using Andy.Cli.Headless;
using Andy.Cli.HeadlessConfig;
using Andy.Model.Llm;
using Andy.Model.Model;
using Andy.Model.Tooling;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Cli.Tests.Headless;

public class HeadlessAgentRunnerEndToEndTests
{
    [Fact]
    public async Task ExecuteAsync_StubLlm_NoTools_WritesOutputAndEmitsLifecycleEvents()
    {
        using var workspace = new TempDir();
        var outputPath = Path.Combine(workspace.Path, "output.json");

        var config = new HeadlessRunConfig
        {
            SchemaVersion = 1,
            RunId = Guid.NewGuid(),
            Agent = new HeadlessAgent
            {
                Slug = "stub-agent",
                Instructions = "You are a stub agent. Reply with the word DONE.",
            },
            Model = new HeadlessModel { Provider = "stub", Id = "stub-1" },
            Tools = Array.Empty<HeadlessTool>(),
            Workspace = new HeadlessWorkspace { Root = workspace.Path },
            Output = new HeadlessOutput { File = outputPath, Stream = "stdout" },
            Limits = new HeadlessLimits { MaxIterations = 4, TimeoutSeconds = 30 },
        };

        var (stdout, stderr) = NewIo();
        var llm = new StubLlmProvider("DONE");

        var code = await HeadlessAgentRunner.ExecuteAsync(
            config, stdout, stderr, NullLoggerFactory.Instance, llmProviderOverride: llm);

        Assert.Equal(HeadlessExitCode.Success, code);

        // Output file exists, atomic write produced exactly the response.
        Assert.True(File.Exists(outputPath));
        Assert.Equal("DONE", File.ReadAllText(outputPath));

        // Event stream: started → output_written → finished, all valid JSON,
        // schema_version pinned at 1.
        var events = ParseEvents(stdout.ToString());
        Assert.Equal(1, events[0].GetProperty("schema_version").GetInt32());
        Assert.Equal("started", events.First().GetProperty("kind").GetString());
        Assert.Equal("finished", events.Last().GetProperty("kind").GetString());
        Assert.Contains(events, e => e.GetProperty("kind").GetString() == "output_written");

        var finished = events.Last().GetProperty("data");
        Assert.Equal(0, finished.GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutSeconds_MapsToExitCode4()
    {
        using var workspace = new TempDir();
        var config = new HeadlessRunConfig
        {
            SchemaVersion = 1,
            RunId = Guid.NewGuid(),
            Agent = new HeadlessAgent { Slug = "stub", Instructions = "stub" },
            Model = new HeadlessModel { Provider = "stub", Id = "stub-1" },
            Tools = Array.Empty<HeadlessTool>(),
            Workspace = new HeadlessWorkspace { Root = workspace.Path },
            Output = new HeadlessOutput { File = Path.Combine(workspace.Path, "out.txt"), Stream = "stdout" },
            // 1-second timeout vs a stub LLM that delays 5s — wall-clock CTS fires.
            Limits = new HeadlessLimits { MaxIterations = 4, TimeoutSeconds = 1 },
        };

        var (stdout, stderr) = NewIo();
        var llm = new StubLlmProvider("Late.", responseDelay: TimeSpan.FromSeconds(5));

        var code = await HeadlessAgentRunner.ExecuteAsync(
            config, stdout, stderr, NullLoggerFactory.Instance, llmProviderOverride: llm);

        Assert.Equal(HeadlessExitCode.Timeout, code);
        var events = ParseEvents(stdout.ToString());
        var finished = events.Last();
        Assert.Equal("finished", finished.GetProperty("kind").GetString());
        Assert.Equal(4, finished.GetProperty("data").GetProperty("exit_code").GetInt32());
    }

    private static (StringWriter Stdout, StringWriter Stderr) NewIo()
        => (new StringWriter(new StringBuilder()), new StringWriter(new StringBuilder()));

    private static List<JsonElement> ParseEvents(string ndjson)
    {
        var lines = ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .ToList();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aq3-e2e-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }

    // SimpleAgent calls CompleteAsync; the stub returns a single
    // assistant message (no tool calls) with FinishReason="stop", which
    // causes SimpleAgent to terminate the loop after one turn.
    private sealed class StubLlmProvider : ILlmProvider
    {
        private readonly string _content;
        private readonly TimeSpan _delay;

        public StubLlmProvider(string content, TimeSpan? responseDelay = null)
        {
            _content = content;
            _delay = responseDelay ?? TimeSpan.Zero;
        }

        public string Name => "stub";

        public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, cancellationToken);
            }
            return new LlmResponse
            {
                AssistantMessage = new Message
                {
                    Role = Role.Assistant,
                    Content = _content,
                    ToolCalls = new List<ToolCall>(),
                },
                FinishReason = "stop",
                Model = "stub-1",
            };
        }

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
