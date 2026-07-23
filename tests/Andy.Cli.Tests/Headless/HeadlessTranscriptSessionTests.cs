using System.Text.Json;
using Andy.Cli.Headless;
using Andy.Cli.HeadlessConfig;
using Xunit;

namespace Andy.Cli.Tests.Headless;

public sealed class HeadlessTranscriptSessionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void EveryTerminalOutcome_IsAtomicallyPublished(int exitCode)
    {
        using var directory = new TempDirectory();
        var creation = HeadlessTranscriptSession.TryCreate(Config(directory.Path));
        Assert.Null(creation.Error);
        Assert.NotNull(creation.Session);
        using var emitter = new HeadlessEventEmitter(TextWriter.Null, transcript: creation.Session);

        emitter.EmitStarted(Guid.NewGuid(), "test-agent", "local", "test-model", 0);
        emitter.EmitError("terminal diagnostic", fatal: exitCode != 0);
        emitter.EmitFinished(exitCode, durationMs: 12, iterations: 2);

        Assert.False(File.Exists(creation.Session!.TempPath));
        Assert.True(File.Exists(creation.Session.FinalPath));
        var events = ReadEvents(creation.Session.FinalPath);
        Assert.Equal("started", events.First().GetProperty("kind").GetString());
        var finished = events.Last();
        Assert.Equal("finished", finished.GetProperty("kind").GetString());
        Assert.Equal(exitCode, finished.GetProperty("data").GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public void PermissionDenial_IsRetainedWithTerminalOutcome()
    {
        using var directory = new TempDirectory();
        var creation = HeadlessTranscriptSession.TryCreate(Config(directory.Path));
        using var emitter = new HeadlessEventEmitter(TextWriter.Null, transcript: creation.Session);

        emitter.EmitStarted(Guid.NewGuid(), "test-agent", "local", "test-model", 1);
        emitter.EmitToolCallStarted("call-1", "delete_file", "sha256:args");
        emitter.EmitToolCallFinished(
            "call-1",
            "delete_file",
            ok: false,
            durationMs: 4,
            error: "Permission denied",
            outcome: "denied");
        emitter.EmitFinished(1, 8, 1);

        var events = ReadEvents(creation.Session!.FinalPath);
        var denial = events.Single(item =>
            item.GetProperty("kind").GetString() == "tool_call_finished");
        Assert.Equal("denied", denial.GetProperty("data").GetProperty("outcome").GetString());
        Assert.Equal("finished", events.Last().GetProperty("kind").GetString());
    }

    [Fact]
    public void SecretsAndBearerTokens_AreRedactedBeforePersistence()
    {
        using var directory = new TempDirectory();
        const string secret = "configured-secret-value";
        var config = Config(directory.Path) with
        {
            EnvVars = new Dictionary<string, string> { ["MY_TRANSCRIPT_SECRET"] = secret },
            Model = new HeadlessModel
            {
                Provider = "local",
                Id = "test-model",
                ApiKeyRef = "env:MY_TRANSCRIPT_SECRET"
            },
            Transcript = Config(directory.Path).Transcript! with
            {
                RedactEnvVars = ["MY_TRANSCRIPT_SECRET"]
            }
        };
        var creation = HeadlessTranscriptSession.TryCreate(config);
        using var emitter = new HeadlessEventEmitter(TextWriter.Null, transcript: creation.Session);

        emitter.EmitLlmChunk(
            $"secret={secret}; Authorization: Bearer abc.def.ghi; "
                + "api_key=sk-example12345; {\"password\":\"plain-password\"}");
        emitter.EmitFinished(0, 1, 1);

        var persisted = File.ReadAllText(creation.Session!.FinalPath);
        Assert.DoesNotContain(secret, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("abc.def.ghi", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-example12345", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-password", persisted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordAndRunLimits_KeepValidTerminalTranscript()
    {
        using var directory = new TempDirectory();
        var config = Config(directory.Path) with
        {
            Transcript = new HeadlessTranscript
            {
                Directory = directory.Path,
                MaxRecordBytes = 1024,
                MaxRunBytes = 4096,
                MaxFiles = 10,
                MaxTotalBytes = 4096
            }
        };
        var creation = HeadlessTranscriptSession.TryCreate(config);
        using var emitter = new HeadlessEventEmitter(TextWriter.Null, transcript: creation.Session);

        emitter.EmitStarted(config.RunId, "test-agent", "local", "test-model", 0);
        for (var i = 0; i < 20; i++)
        {
            emitter.EmitLlmChunk(new string('x', 5000), i);
        }
        emitter.EmitFinished(4, 20, 20);

        var info = new FileInfo(creation.Session!.FinalPath);
        Assert.True(info.Length <= config.Transcript!.MaxRunBytes);
        var events = ReadEvents(info.FullName);
        Assert.Contains(events, item =>
            item.GetProperty("data").TryGetProperty("transcript_truncated", out var truncated)
            && truncated.GetBoolean());
        Assert.Equal("finished", events.Last().GetProperty("kind").GetString());
        Assert.Equal(4, events.Last().GetProperty("data").GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public async Task ConcurrentRunsWithSameRunId_DoNotCollideOrInterleave()
    {
        using var directory = new TempDirectory();
        var config = Config(directory.Path);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
        {
            var creation = HeadlessTranscriptSession.TryCreate(config);
            Assert.Null(creation.Error);
            using var emitter = new HeadlessEventEmitter(TextWriter.Null, transcript: creation.Session);
            emitter.EmitStarted(config.RunId, "test-agent", "local", "test-model", 0);
            emitter.EmitLlmChunk($"run-{index}");
            emitter.EmitFinished(0, index, 1);
        })));

        var files = Directory.GetFiles(directory.Path, "*.ndjson");
        Assert.Equal(8, files.Length);
        Assert.Equal(8, files.Select(Path.GetFileName).Distinct(StringComparer.Ordinal).Count());
        foreach (var file in files)
        {
            var events = ReadEvents(file);
            Assert.Equal(3, events.Count);
            Assert.Equal("started", events[0].GetProperty("kind").GetString());
            Assert.Equal("finished", events[2].GetProperty("kind").GetString());
        }
    }

    [Fact]
    public void RetentionAndCrashCleanup_RemoveOldestArtifactsDeterministically()
    {
        using var directory = new TempDirectory();
        var now = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 3; i++)
        {
            var path = Path.Combine(directory.Path, $"old-{i}.ndjson");
            File.WriteAllText(path, "{}\n");
            File.SetLastWriteTimeUtc(path, now.AddDays(-(i + 1)).UtcDateTime);
        }

        var staleTemp = Path.Combine(directory.Path, ".orphan.tmp");
        File.WriteAllText(staleTemp, "partial");
        File.SetLastWriteTimeUtc(staleTemp, now.AddHours(-2).UtcDateTime);

        var config = Config(directory.Path) with
        {
            Transcript = new HeadlessTranscript
            {
                Directory = directory.Path,
                MaxRecordBytes = 1024,
                MaxRunBytes = 4096,
                MaxAgeDays = 2,
                MaxFiles = 2,
                MaxTotalBytes = 4096
            }
        };
        var creation = HeadlessTranscriptSession.TryCreate(config, new FixedTimeProvider(now));
        Assert.False(File.Exists(staleTemp));
        using var emitter = new HeadlessEventEmitter(
            TextWriter.Null,
            clock: new FixedTimeProvider(now),
            transcript: creation.Session);
        emitter.EmitStarted(config.RunId, "test-agent", "local", "test-model", 0);
        emitter.EmitFinished(0, 1, 1);

        var retained = Directory.GetFiles(directory.Path, "*.ndjson");
        Assert.True(retained.Length <= 2);
        Assert.Contains(creation.Session!.FinalPath, retained);
        Assert.DoesNotContain(retained, path => Path.GetFileName(path) == "old-2.ndjson");
    }

    [Fact]
    public void TotalStorageLimit_RemovesOldestCompletedTranscript()
    {
        using var directory = new TempDirectory();
        var now = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        var oldPath = Path.Combine(directory.Path, "old.ndjson");
        File.WriteAllBytes(oldPath, new byte[3900]);
        File.SetLastWriteTimeUtc(oldPath, now.AddMinutes(-5).UtcDateTime);
        var config = Config(directory.Path) with
        {
            Transcript = new HeadlessTranscript
            {
                Directory = directory.Path,
                MaxRecordBytes = 1024,
                MaxRunBytes = 4096,
                MaxAgeDays = 30,
                MaxFiles = 10,
                MaxTotalBytes = 4096
            }
        };
        var creation = HeadlessTranscriptSession.TryCreate(config, new FixedTimeProvider(now));
        using var emitter = new HeadlessEventEmitter(
            TextWriter.Null,
            clock: new FixedTimeProvider(now),
            transcript: creation.Session);
        emitter.EmitStarted(config.RunId, "test-agent", "local", "test-model", 0);
        emitter.EmitFinished(0, 1, 1);

        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(creation.Session!.FinalPath));
        Assert.True(Directory.GetFiles(directory.Path, "*.ndjson").Sum(path => new FileInfo(path).Length)
            <= config.Transcript!.MaxTotalBytes);
    }

    [Fact]
    public void InitializationFailure_IsReturnedWithoutThrowing()
    {
        using var directory = new TempDirectory();
        var blocker = Path.Combine(directory.Path, "file");
        File.WriteAllText(blocker, "not a directory");
        var config = Config(directory.Path) with
        {
            Transcript = new HeadlessTranscript
            {
                Directory = Path.Combine(blocker, "transcripts")
            }
        };

        var result = HeadlessTranscriptSession.TryCreate(config);

        Assert.Null(result.Session);
        Assert.Contains("Transcript initialization failed", result.Error);
    }

    private static HeadlessRunConfig Config(string directory) => new()
    {
        SchemaVersion = 1,
        RunId = Guid.NewGuid(),
        Agent = new HeadlessAgent { Slug = "test-agent", Instructions = "Test." },
        Model = new HeadlessModel { Provider = "local", Id = "test-model" },
        Tools = [],
        Workspace = new HeadlessWorkspace { Root = directory },
        Output = new HeadlessOutput
        {
            File = Path.Combine(directory, "output.txt"),
            Stream = "stdout"
        },
        Transcript = new HeadlessTranscript { Directory = directory },
        Limits = new HeadlessLimits { MaxIterations = 5, TimeoutSeconds = 30 }
    };

    private static List<JsonElement> ReadEvents(string path)
    {
        return File.ReadLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            })
            .ToList();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"andy-transcript-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }
}
