using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Andy.Cli.Services.Sessions;
using Andy.Engine;

namespace Andy.Cli.Tests.Services.Sessions;

/// <summary>
/// Shared fixtures for the session archive/fork/stats tests (issue #285).
///
/// Any secret-shaped string used here is assembled at RUNTIME from fragments so the
/// repository's secret scanner does not flag the fixture itself as a committed
/// credential - the same convention the existing SessionStore redaction test uses.
/// </summary>
internal static class SessionArchiveTestData
{
    /// <summary>A synthetic, never-valid provider key shape used to prove redaction.</summary>
    public static string SyntheticApiKey() => string.Concat("sk", "-", "fixture", "9876543210");

    /// <summary>A synthetic bearer token shape used to prove redaction.</summary>
    public static string SyntheticBearerToken() => string.Concat("Bearer", " ", "fixture.token-0192837465");

    public static TranscriptMessage Message(string role, string content, IReadOnlyList<TranscriptToolCall>? calls = null,
        IReadOnlyList<TranscriptToolResult>? results = null, IReadOnlyList<TranscriptPart>? parts = null) => new()
        {
            Role = role,
            Content = content,
            Timestamp = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero),
            Id = Guid.NewGuid().ToString("N"),
            ToolCalls = calls,
            ToolResults = results,
            Parts = parts
        };

    /// <summary>A snapshot with <paramref name="turnCount"/> plain user/assistant turns.</summary>
    public static TranscriptSnapshot Snapshot(int turnCount)
    {
        var turns = new List<TranscriptTurn>();
        for (var i = 1; i <= turnCount; i++)
        {
            turns.Add(new TranscriptTurn
            {
                User = Message("user", $"question {i}"),
                Interleaved = Array.Empty<TranscriptMessage>(),
                FinalAssistant = Message("assistant", $"answer {i}")
            });
        }
        return new TranscriptSnapshot { Version = TranscriptSnapshot.CurrentVersion, Turns = turns };
    }

    /// <summary>
    /// A snapshot exercising tool calls, tool results, and structured parts. The tool
    /// payloads are parameterized because the transcript types are init-only, so a test
    /// that needs a different payload has to build the snapshot with it up front.
    /// </summary>
    public static TranscriptSnapshot RichSnapshot(
        string? toolArgumentsJson = null,
        string? toolResultJson = null)
    {
        var call = new TranscriptToolCall
        {
            Id = "call_1",
            Name = "read_file",
            ArgumentsJson = toolArgumentsJson ?? "{\"path\":\"/tmp/notes.txt\"}"
        };
        var result = new TranscriptToolResult
        {
            CallId = "call_1",
            Name = "read_file",
            IsError = false,
            ResultJson = toolResultJson ?? "{\"content\":\"line one\"}"
        };

        return new TranscriptSnapshot
        {
            Version = TranscriptSnapshot.CurrentVersion,
            Turns = new[]
            {
                new TranscriptTurn
                {
                    User = Message("user", "read my notes", parts: new[]
                    {
                        new TranscriptPart { Type = "text", Text = "read my notes" }
                    }),
                    Interleaved = new[]
                    {
                        Message("assistant", "Looking at the file now.", calls: new[] { call }),
                        Message("tool", "", results: new[] { result })
                    },
                    FinalAssistant = Message("assistant", "Your notes say: line one")
                },
                new TranscriptTurn
                {
                    User = Message("user", "thanks"),
                    Interleaved = Array.Empty<TranscriptMessage>(),
                    FinalAssistant = Message("assistant", "You are welcome.")
                }
            }
        };
    }

    /// <summary>A store rooted at a fresh temp directory with environment redaction disabled.</summary>
    public static SessionStore CreateStore(string directory) =>
        new(directory, new SessionRedactor(Array.Empty<string>()));

    public static string NewTempDirectory(string label) =>
        Path.Combine(Path.GetTempPath(), $"andy-{label}-" + Guid.NewGuid().ToString("N"));

    /// <summary>Best-effort cleanup that tolerates a directory that was never created.</summary>
    public static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    public static string[] SessionFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.GetFiles(directory).Select(Path.GetFileName).OfType<string>().OrderBy(n => n).ToArray()
            : Array.Empty<string>();
}

/// <summary>Base class giving each test class an isolated store + workspace directory.</summary>
public abstract class SessionArchiveTestBase : IDisposable
{
    protected SessionArchiveTestBase(string label)
    {
        StoreDirectory = SessionArchiveTestData.NewTempDirectory(label + "-store");
        WorkDirectory = SessionArchiveTestData.NewTempDirectory(label + "-work");
        Directory.CreateDirectory(StoreDirectory);
        Directory.CreateDirectory(WorkDirectory);
        Store = SessionArchiveTestData.CreateStore(StoreDirectory);
    }

    protected string StoreDirectory { get; }
    protected string WorkDirectory { get; }
    protected SessionStore Store { get; }

    protected string WorkPath(string fileName) => Path.Combine(WorkDirectory, fileName);

    public void Dispose()
    {
        foreach (var directory in new[] { StoreDirectory, WorkDirectory })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
        GC.SuppressFinalize(this);
    }
}
