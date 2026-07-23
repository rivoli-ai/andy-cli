using System;
using System.IO;
using System.Threading.Tasks;
using Andy.Cli.Commands;
using Andy.Cli.Services.Sessions;
using Andy.Engine;
using Xunit;

namespace Andy.Cli.Tests.Commands;

/// <summary>
/// The /sessions and "andy-cli sessions" listing (issue #231).
/// </summary>
public class SessionsCommandTests : IDisposable
{
    private readonly string _directory;
    private readonly SessionStore _store;

    public SessionsCommandTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "andy-sessions-cmd-tests-" + Guid.NewGuid().ToString("N"));
        _store = new SessionStore(_directory, new SessionRedactor(Array.Empty<string>()));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static TranscriptSnapshot Snapshot(string firstUserMessage) => new()
    {
        Turns = new[]
        {
            new TranscriptTurn
            {
                User = new TranscriptMessage
                {
                    Role = "user",
                    Content = firstUserMessage,
                    Timestamp = DateTimeOffset.UtcNow,
                    Id = Guid.NewGuid().ToString("N")
                },
                Interleaved = Array.Empty<TranscriptMessage>(),
                FinalAssistant = new TranscriptMessage
                {
                    Role = "assistant",
                    Content = "answer",
                    Timestamp = DateTimeOffset.UtcNow,
                    Id = Guid.NewGuid().ToString("N")
                }
            }
        }
    };

    [Fact]
    public async Task Execute_NoSessions_SaysSo()
    {
        var command = new SessionsCommand(_store);
        var result = await command.ExecuteAsync(Array.Empty<string>());

        Assert.True(result.Success);
        Assert.Equal(SessionsCommand.NoSessionsMessage, result.Message);
    }

    [Fact]
    public async Task Execute_ListsIdSnippetAndResumeHint()
    {
        _store.Save("20260723-090000-abcd", Snapshot("Fix the flaky test in FeedView"), "openai", "gpt-4o");

        var command = new SessionsCommand(_store);
        var result = await command.ExecuteAsync(Array.Empty<string>());

        Assert.True(result.Success);
        Assert.Contains("20260723-090000-abcd", result.Message);
        Assert.Contains("Fix the flaky test in FeedView", result.Message);
        Assert.Contains("openai/gpt-4o", result.Message);
        Assert.Contains("1 turn", result.Message);
        Assert.Contains("--resume", result.Message);
    }

    [Fact]
    public async Task Execute_UnknownSubcommand_Fails()
    {
        var command = new SessionsCommand(_store);
        var result = await command.ExecuteAsync(new[] { "delete" });
        Assert.False(result.Success);
    }
}
