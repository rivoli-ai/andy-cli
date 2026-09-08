using System.Runtime.CompilerServices;
using System.Text.Json;
using Andy.Cli.Headless;
using Andy.Cli.Headless.Contract;
using Andy.Cli.HeadlessConfig;
using Andy.Model.Llm;
using Andy.Model.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace Andy.Cli.Tests.Headless;

public class HeadlessStreamingTests
{
    [Fact]
    public async Task EmitsFirstChunkBeforeProviderFinishes_WithoutHistoryDuplicates()
    {
        using var workspace = new Workspace();
        var provider = new GatedProvider();
        var output = new StringWriter();
        var run = HeadlessAgentRunner.ExecuteAsync(workspace.Config, output, new StringWriter(),
            NullLoggerFactory.Instance, llmProviderOverride: provider);
        await provider.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(run.IsCompleted);
            Assert.Contains(Events(output), e => e.GetProperty("kind").GetString() == "llm_chunk"
                && e.GetProperty("data").GetProperty("text").GetString() == "hello ");
        }
        finally { provider.Release.TrySetResult(); }
        Assert.Equal(HeadlessExitCode.Success, await run);
        var chunks = Events(output).Where(e => e.GetProperty("kind").GetString() == "llm_chunk").ToArray();
        Assert.Equal(new[] { "hello ", "world", "" }, chunks.Select(e => e.GetProperty("data").GetProperty("text").GetString()));
        Assert.Equal("final", chunks.Last().GetProperty("data").GetProperty("state").GetString());
        Assert.Equal("hello world", File.ReadAllText(workspace.Config.Output.File));
        foreach (var evt in Events(output))
            Assert.True(HeadlessConfigContract.ValidateEvent(evt).IsValid);
    }

    [Fact]
    public async Task CancellationStopsFurtherChunksAndDoesNotPublishPartialOutput()
    {
        using var workspace = new Workspace();
        using var cts = new CancellationTokenSource();
        var provider = new GatedProvider();
        var output = new StringWriter();
        var run = HeadlessAgentRunner.ExecuteAsync(workspace.Config, output, new StringWriter(),
            NullLoggerFactory.Instance, llmProviderOverride: provider, ct: cts.Token);
        await provider.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        Assert.Equal(HeadlessExitCode.Cancelled, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        var snapshot = output.ToString();
        provider.Release.TrySetResult();
        Assert.Equal(snapshot, output.ToString());
        Assert.DoesNotContain("world", output.ToString());
        Assert.Equal("finished", Events(output).Last().GetProperty("kind").GetString());
        Assert.False(File.Exists(workspace.Config.Output.File));
    }

    [Fact]
    public void TranscriptRedactsSecretsAcrossDeltaBoundaries()
    {
        using var workspace = new Workspace(transcript: true);
        var creation = HeadlessTranscriptSession.TryCreate(workspace.Config);
        using var transcript = creation.Session!;
        using var emitter = new HeadlessEventEmitter(new StringWriter(), transcript: transcript);
        emitter.EmitLlmChunk("key: sensitive-", 0, "delta");
        emitter.EmitLlmChunk("value-123 end", 0, "delta");
        emitter.EmitLlmChunk("", 0, "final");
        emitter.EmitFinished(0, 1, 1);
        var retained = File.ReadAllText(transcript.FinalPath);
        Assert.DoesNotContain("sensitive-", retained);
        Assert.DoesNotContain("value-123", retained);
        Assert.Contains("[REDACTED]", retained);
        Assert.Contains("transcript_coalesced", retained);
    }

    [Fact]
    public void LargeProviderChunksAreBoundedWithoutLosingUnicode()
    {
        var output = new StringWriter();
        using var emitter = new HeadlessEventEmitter(output);
        var text = new string('a', 4095) + char.ConvertFromUtf32(0x1F600) + new string('b', 5000);
        emitter.EmitLlmChunk(text, 0, "delta");
        var chunks = Events(output).Select(e => e.GetProperty("data").GetProperty("text").GetString()!).ToArray();
        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, 4096));
        Assert.Equal(text, string.Concat(chunks));
    }

    private static JsonElement[] Events(StringWriter writer) =>
        writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();

    private sealed class Workspace : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "streaming-" + Guid.NewGuid().ToString("N"));
        public HeadlessRunConfig Config { get; }
        public Workspace(bool transcript = false)
        {
            Directory.CreateDirectory(Path);
            Config = new HeadlessRunConfig
            {
                SchemaVersion = 1,
                RunId = Guid.NewGuid(),
                EnvVars = new Dictionary<string, string> { ["ANDY_TOKEN"] = "sensitive-value-123" },
                Transcript = transcript ? new HeadlessTranscript { Directory = Path } : null,
                Agent = new HeadlessAgent { Slug = "streaming", Instructions = "Respond." },
                Model = new HeadlessModel { Provider = "stub", Id = "stub" },
                Tools = Array.Empty<HeadlessTool>(),
                Workspace = new HeadlessWorkspace { Root = Path },
                Output = new HeadlessOutput { File = System.IO.Path.Combine(Path, "out.txt"), Stream = "stdout" },
                Limits = new HeadlessLimits { MaxIterations = 3, TimeoutSeconds = 20 }
            };
        }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    private sealed class GatedProvider : ILlmProvider
    {
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "stub";
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Must use streaming.");
        public async IAsyncEnumerable<LlmStreamResponse> StreamCompleteAsync(LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new LlmStreamResponse { Delta = new Message { Role = Role.Assistant, Content = "hello " } };
            Waiting.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            yield return new LlmStreamResponse { Delta = new Message { Role = Role.Assistant, Content = "world" }, FinishReason = "stop", IsComplete = true };
        }
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IEnumerable<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Enumerable.Empty<ModelInfo>());
    }
}
