// Unit tests for HeadlessEventEmitter (AQ3, rivoli-ai/andy-cli#44).
// Verify NDJSON envelope shape, snake_case wire names, schema_version pin,
// and digest determinism. Concurrency: serialise via the emitter's lock.

using System.IO;
using System.Text;
using System.Text.Json;
using Andy.Cli.Headless;
using Xunit;

namespace Andy.Cli.Tests.Headless;

public class HeadlessEventEmitterTests
{
    [Fact]
    public void EmitStarted_ProducesWellFormedEnvelope()
    {
        var (sw, emitter) = NewEmitter();
        var runId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        emitter.EmitStarted(runId, "triage-agent", "anthropic", "claude-sonnet-4-6", toolCount: 3);

        var doc = ParseSingleLine(sw.ToString());
        Assert.Equal(1, doc.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal("started", doc.RootElement.GetProperty("kind").GetString());
        Assert.True(doc.RootElement.TryGetProperty("ts", out _));
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(runId.ToString(), data.GetProperty("run_id").GetString());
        Assert.Equal("triage-agent", data.GetProperty("agent_slug").GetString());
        Assert.Equal("anthropic", data.GetProperty("model_provider").GetString());
        Assert.Equal("claude-sonnet-4-6", data.GetProperty("model_id").GetString());
        Assert.Equal(3, data.GetProperty("tool_count").GetInt32());
    }

    [Fact]
    public void EmitFinished_CarriesExitCodeAndIterations()
    {
        var (sw, emitter) = NewEmitter();
        emitter.EmitFinished(exitCode: 4, durationMs: 1234, iterations: 7);

        var doc = ParseSingleLine(sw.ToString());
        Assert.Equal("finished", doc.RootElement.GetProperty("kind").GetString());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(4, data.GetProperty("exit_code").GetInt32());
        Assert.Equal(1234, data.GetProperty("duration_ms").GetInt64());
        Assert.Equal(7, data.GetProperty("iterations").GetInt32());
    }

    [Fact]
    public void EmitError_PreservesFatalFlag()
    {
        var (sw, emitter) = NewEmitter();
        emitter.EmitError("connection refused", fatal: true);

        var doc = ParseSingleLine(sw.ToString());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("connection refused", data.GetProperty("message").GetString());
        Assert.True(data.GetProperty("fatal").GetBoolean());
    }

    [Fact]
    public void Emit_MultipleEvents_OneJsonPerLine()
    {
        var (sw, emitter) = NewEmitter();
        emitter.EmitStarted(Guid.NewGuid(), "a", "p", "m", 0);
        emitter.EmitLlmChunk("hi");
        emitter.EmitFinished(0, 10, 1);

        var lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        foreach (var line in lines)
        {
            // Must round-trip — no truncation, no concatenation.
            using var doc = JsonDocument.Parse(line);
            Assert.True(doc.RootElement.TryGetProperty("kind", out _));
        }
    }

    [Fact]
    public void ComputeDigest_IsDeterministic_AndHandlesNull()
    {
        var d1 = HeadlessEventEmitter.ComputeDigest(new { a = 1, b = "x" });
        var d2 = HeadlessEventEmitter.ComputeDigest(new { a = 1, b = "x" });
        Assert.Equal(d1, d2);
        Assert.StartsWith("sha256:", d1);

        var nullDigest = HeadlessEventEmitter.ComputeDigest(null);
        Assert.Equal("sha256:empty", nullDigest);
    }

    [Fact]
    public void Emit_FromMultipleThreads_LinesNeverInterleave()
    {
        var (sw, emitter) = NewEmitter();
        var workers = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            for (var j = 0; j < 50; j++)
            {
                emitter.EmitLlmChunk($"thread-{i}-msg-{j}");
            }
        })).ToArray();
        Task.WaitAll(workers);

        // Each line must be a complete JSON document; no partial writes
        // would otherwise show up as a JsonReaderException.
        var lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(8 * 50, lines.Length);
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
        }
    }

    private static (StringWriter, HeadlessEventEmitter) NewEmitter()
    {
        var sw = new StringWriter(new StringBuilder());
        return (sw, new HeadlessEventEmitter(sw));
    }

    private static JsonDocument ParseSingleLine(string output)
    {
        var line = output.Trim();
        return JsonDocument.Parse(line);
    }
}
