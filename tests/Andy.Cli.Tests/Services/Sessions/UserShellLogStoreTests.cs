using System;
using System.IO;
using System.Linq;
using Andy.Cli.Services.Sessions;
using Andy.Cli.Services.Shell;
using Xunit;

namespace Andy.Cli.Tests.Services.Sessions;

/// <summary>
/// The per-session log of commands the USER ran in shell mode (issue #286). It exists so those
/// commands can never be mistaken for the model's tool calls in replay, export or an audit: they
/// live in their own file, every record carries an explicit source, and nothing reaches disk
/// unredacted.
/// </summary>
public class UserShellLogStoreTests : IDisposable
{
    private readonly string _directory;

    public UserShellLogStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "andy-usershell-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private UserShellLogStore CreateStore()
        => new(_directory, new SessionRedactor(Array.Empty<string>()));

    private static UserShellCommandResult Result(
        string command = "git status",
        string stdout = "clean\n",
        string stderr = "",
        int? exitCode = 0,
        UserShellOutcome outcome = UserShellOutcome.Succeeded,
        DateTimeOffset? startedAt = null)
        => new(
            Command: command,
            Outcome: outcome,
            ExitCode: exitCode,
            StandardOutput: stdout,
            StandardError: stderr,
            Duration: TimeSpan.FromMilliseconds(250),
            WorkingDirectory: "/work",
            TimedOut: false,
            StandardOutputTruncated: 0,
            StandardErrorTruncated: 0,
            ErrorMessage: null,
            StartedAtUtc: startedAt ?? DateTimeOffset.UnixEpoch.AddSeconds(10));

    [Fact]
    public void Record_ThenLoad_RoundTripsTheCommand()
    {
        var store = CreateStore();

        store.Record("s1", Result(command: "git log -1", stdout: "abc123\n"));
        var loaded = store.Load("s1");

        var record = Assert.Single(loaded);
        Assert.Equal("git log -1", record.Command);
        Assert.Equal(0, record.ExitCode);
        Assert.Equal("exit 0", record.Status);
        Assert.Equal("/work", record.WorkingDirectory);
        Assert.Equal(250, record.DurationMs);
        Assert.Contains("abc123", record.OutputPreview);
    }

    [Fact]
    public void Record_AppendsInOrder()
    {
        var store = CreateStore();

        store.Record("s1", Result(command: "one"));
        store.Record("s1", Result(command: "two"));

        Assert.Equal(new[] { "one", "two" }, store.Load("s1").Select(r => r.Command));
    }

    [Fact]
    public void PersistedFile_LabelsEveryRecordAsTheUsersOwnCommand()
    {
        var store = CreateStore();
        store.Record("s1", Result());

        // Whitespace-insensitive: the redaction pass re-serializes the document.
        var raw = File.ReadAllText(Path.Combine(_directory, "s1.shell.json"))
            .Replace(" ", "").Replace("\n", "").Replace("\r", "");

        // Self-describing on disk: an exported log never needs context to be read correctly.
        Assert.Contains("\"kind\":\"" + UserShellRecord.Kind + "\"", raw);
        Assert.Contains("\"source\":\"" + UserShellRecord.Source + "\"", raw);
    }

    [Fact]
    public void Record_RedactsSecretsBeforeTouchingDisk()
    {
        var fakeKey = string.Concat("sk", "-", "abcdefghijklmnop");
        var store = CreateStore();

        store.Record("s1", Result(command: $"echo {fakeKey}", stdout: fakeKey + "\n"));

        var raw = File.ReadAllText(Path.Combine(_directory, "s1.shell.json"));
        Assert.DoesNotContain(fakeKey, raw);
        Assert.Contains(SessionRedactor.Replacement, raw);
    }

    [Fact]
    public void Record_CapsTheStoredOutputPreview()
    {
        var store = CreateStore();

        store.Record("s1", Result(stdout: new string('y', UserShellLogStore.MaxOutputPreviewCharacters + 500)));

        var record = Assert.Single(store.Load("s1"));
        Assert.Equal(UserShellLogStore.MaxOutputPreviewCharacters, record.OutputPreview.Length);
    }

    [Fact]
    public void Record_KeepsBothStreamsInThePreview()
    {
        var store = CreateStore();

        store.Record("s1", Result(stdout: "on_stdout", stderr: "on_stderr", exitCode: 1, outcome: UserShellOutcome.Failed));

        var record = Assert.Single(store.Load("s1"));
        Assert.Contains("on_stdout", record.OutputPreview);
        Assert.Contains("on_stderr", record.OutputPreview);
        Assert.Equal("exit 1", record.Status);
    }

    [Fact]
    public void Record_KeepsDeniedAndCancelledOutcomesDistinguishable()
    {
        var store = CreateStore();

        store.Record("s1", Result(command: "rm -rf /", exitCode: null, outcome: UserShellOutcome.Denied));
        store.Record("s1", Result(command: "sleep 100", exitCode: null, outcome: UserShellOutcome.Cancelled));

        var records = store.Load("s1");
        Assert.Equal("denied", records[0].Status);
        Assert.Equal("cancelled", records[1].Status);
        Assert.Null(records[0].ExitCode);
    }

    [Fact]
    public void Record_TrimsTheLogOnceItGrowsPastItsCap()
    {
        var store = CreateStore();
        for (var i = 0; i < UserShellLogStore.MaxRecords + 3; i++)
        {
            store.Record("s1", Result(command: "cmd" + i));
        }

        var records = store.Load("s1");
        Assert.Equal(UserShellLogStore.MaxRecords, records.Count);
        Assert.Equal("cmd3", records[0].Command);
    }

    [Fact]
    public void Load_ForAnUnknownSession_IsEmpty()
    {
        Assert.Empty(CreateStore().Load("never-seen"));
    }

    [Fact]
    public void Load_ToleratesACorruptFile()
    {
        // A broken log must never block resuming a session.
        File.WriteAllText(Path.Combine(_directory, "s1.shell.json"), "{ not json");

        Assert.Empty(CreateStore().Load("s1"));
    }

    [Fact]
    public void Load_IgnoresAnUnknownSchemaVersion()
    {
        File.WriteAllText(Path.Combine(_directory, "s1.shell.json"),
            """{ "schemaVersion": 99, "sessionId": "s1", "commands": [ { "command": "x" } ] }""");

        Assert.Empty(CreateStore().Load("s1"));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("")]
    public void InvalidSessionIds_AreRefusedRatherThanWritten(string sessionId)
    {
        var store = CreateStore();

        store.Record(sessionId, Result());

        Assert.Empty(store.Load(sessionId));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void Delete_RemovesTheLog()
    {
        var store = CreateStore();
        store.Record("s1", Result());

        store.Delete("s1");

        Assert.Empty(store.Load("s1"));
    }

    [Fact]
    public void ToTranscriptLine_AttributesTheCommandExplicitly()
    {
        var record = new UserShellRecord(
            DateTimeOffset.UnixEpoch, "git status", 0, "exit 0", 10, "/work", "clean");

        var line = record.ToTranscriptLine();

        Assert.Contains("[user shell]", line);
        Assert.Contains("! git status", line);
        Assert.Contains("exit 0", line);
    }

    [Fact]
    public void ToTranscriptLine_FlattensMultilineCommands()
    {
        var record = new UserShellRecord(
            DateTimeOffset.UnixEpoch, "for f in *; do\n  echo $f\ndone", 0, "exit 0", 10, "/work", "");

        var line = record.ToTranscriptLine();

        Assert.DoesNotContain("\n", line);
    }
}
