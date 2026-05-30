// AQ5 (rivoli-ai/andy-cli#50): cancel-protocol tests for the headless agent
// loop. SIGTERM (and SIGINT) from andy-containers are wired in Program.cs to
// cancel a CancellationTokenSource whose token flows into
// HeadlessAgentRunner.ExecuteAsync. A true in-process SIGTERM is impractical
// to deliver deterministically in xUnit (it would tear down the test host),
// so these tests exercise the exact cancellation-token path SIGTERM triggers:
// the token is cancelled mid-run and we assert the graceful contract — exit
// code 3 (Cancelled), a flushed `finished` event carrying exit_code 3, and
// no output file written (no partial output).

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

public class HeadlessAgentRunnerCancelTests
{
    [Fact]
    public async Task ExecuteAsync_TokenCancelledMidRun_MapsToExitCode3_FlushesFinished_NoOutputFile()
    {
        using var workspace = new TempDir();
        var outputPath = Path.Combine(workspace.Path, "output.json");

        var config = new HeadlessRunConfig
        {
            SchemaVersion = 1,
            RunId = Guid.NewGuid(),
            Agent = new HeadlessAgent { Slug = "stub", Instructions = "stub" },
            Model = new HeadlessModel { Provider = "stub", Id = "stub-1" },
            Tools = Array.Empty<HeadlessTool>(),
            Workspace = new HeadlessWorkspace { Root = workspace.Path },
            Output = new HeadlessOutput { File = outputPath, Stream = "stdout" },
            // Generous wall-clock timeout so the outer cancel (not the timeout)
            // is unambiguously what stops the run.
            Limits = new HeadlessLimits { MaxIterations = 4, TimeoutSeconds = 120 },
        };

        var (stdout, stderr) = NewIo();
        using var cts = new CancellationTokenSource();

        // The fake completion blocks until the run's token is cancelled, then
        // surfaces the cancellation exactly as a real provider would — this is
        // the same signal a SIGTERM-cancelled CTS produces.
        var llm = new BlockUntilCancelledLlmProvider(onEnter: cts.Cancel);

        var code = await HeadlessAgentRunner.ExecuteAsync(
            config, stdout, stderr, NullLoggerFactory.Instance,
            llmProviderOverride: llm, ct: cts.Token);

        Assert.Equal(HeadlessExitCode.Cancelled, code);

        // No partial output: the output file must not exist after a cancel.
        Assert.False(File.Exists(outputPath));

        // The `finished` event was flushed and carries exit_code 3.
        var events = ParseEvents(stdout.ToString());
        var finished = events.Last();
        Assert.Equal("finished", finished.GetProperty("kind").GetString());
        Assert.Equal(3, finished.GetProperty("data").GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public async Task ExecuteAsync_TokenAlreadyCancelled_StillEmitsFinishedWithExitCode3()
    {
        using var workspace = new TempDir();
        var outputPath = Path.Combine(workspace.Path, "output.json");

        var config = new HeadlessRunConfig
        {
            SchemaVersion = 1,
            RunId = Guid.NewGuid(),
            Agent = new HeadlessAgent { Slug = "stub", Instructions = "stub" },
            Model = new HeadlessModel { Provider = "stub", Id = "stub-1" },
            Tools = Array.Empty<HeadlessTool>(),
            Workspace = new HeadlessWorkspace { Root = workspace.Path },
            Output = new HeadlessOutput { File = outputPath, Stream = "stdout" },
            Limits = new HeadlessLimits { MaxIterations = 4, TimeoutSeconds = 120 },
        };

        var (stdout, stderr) = NewIo();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var llm = new BlockUntilCancelledLlmProvider();

        var code = await HeadlessAgentRunner.ExecuteAsync(
            config, stdout, stderr, NullLoggerFactory.Instance,
            llmProviderOverride: llm, ct: cts.Token);

        Assert.Equal(HeadlessExitCode.Cancelled, code);
        Assert.False(File.Exists(outputPath));

        var events = ParseEvents(stdout.ToString());
        var finished = events.Last();
        Assert.Equal("finished", finished.GetProperty("kind").GetString());
        Assert.Equal(3, finished.GetProperty("data").GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutBeatsCancel_MapsToExitCode4()
    {
        using var workspace = new TempDir();
        var outputPath = Path.Combine(workspace.Path, "output.json");

        var config = new HeadlessRunConfig
        {
            SchemaVersion = 1,
            RunId = Guid.NewGuid(),
            Agent = new HeadlessAgent { Slug = "stub", Instructions = "stub" },
            Model = new HeadlessModel { Provider = "stub", Id = "stub-1" },
            Tools = Array.Empty<HeadlessTool>(),
            Workspace = new HeadlessWorkspace { Root = workspace.Path },
            Output = new HeadlessOutput { File = outputPath, Stream = "stdout" },
            // 1-second wall-clock timeout vs a never-completing provider, with
            // an outer token that is never cancelled — the timeout path wins.
            Limits = new HeadlessLimits { MaxIterations = 4, TimeoutSeconds = 1 },
        };

        var (stdout, stderr) = NewIo();
        var llm = new BlockUntilCancelledLlmProvider();

        var code = await HeadlessAgentRunner.ExecuteAsync(
            config, stdout, stderr, NullLoggerFactory.Instance,
            llmProviderOverride: llm, ct: CancellationToken.None);

        Assert.Equal(HeadlessExitCode.Timeout, code);
        Assert.False(File.Exists(outputPath));

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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aq5-cancel-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }

    // CompleteAsync awaits the supplied token until it is cancelled, then
    // throws OperationCanceledException — exactly how an in-flight model call
    // reacts to a SIGTERM-cancelled token. An optional onEnter hook lets a
    // test trip the cancel deterministically once the call is underway.
    private sealed class BlockUntilCancelledLlmProvider : ILlmProvider
    {
        private readonly Action? _onEnter;

        public BlockUntilCancelledLlmProvider(Action? onEnter = null)
        {
            _onEnter = onEnter;
        }

        public string Name => "stub";

        public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            _onEnter?.Invoke();
            // Block until cancellation; Task.Delay(Infinite) throws
            // TaskCanceledException (an OperationCanceledException) when the
            // token fires.
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable: provider should be cancelled.");
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
