// Reproduction for the interactive "Object reference not set to an instance of an object"
// (NullReferenceException) the user hits on a FOLLOW-UP turn after a tool call.
//
// The crash is swallowed inside Andy.Engine's SimpleAgent loop (catch -> Response = "Error: ...").
// Andy-cli's SimpleAssistantService passes the engine `logger as ILogger<SimpleAgent>`, which is
// always null (wrong generic), so the stack trace is never recorded. Here we drive SimpleAgent
// directly with a real capturing logger so the engine's catch records the full trace.
//
// Skips when OPENROUTER_API_KEY is absent.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Andy.Cli.Headless;
using Andy.Cli.HeadlessConfig;
using Andy.Cli.Services;
using Andy.Engine;
using Andy.Model.Llm;
using Andy.Tools.Core;
using Andy.Tools.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class InteractiveAssistantReproTests
{
    [Fact]
    public async Task FollowUpTurnAfterToolCall_DoesNotNullReference()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return; // no model credentials available — skip
        }

        Environment.SetEnvironmentVariable("ANDY_PERMISSION_MODE", "bypass");

        using var ws = new TempDir();

        var config = new HeadlessRunConfig
        {
            SchemaVersion = 1,
            RunId = Guid.NewGuid(),
            Agent = new HeadlessAgent { Slug = "repro", Instructions = "stub" },
            Model = new HeadlessModel { Provider = "openrouter", Id = "xiaomi/mimo-v2.5" },
            Tools = Array.Empty<HeadlessTool>(),
            Workspace = new HeadlessWorkspace { Root = ws.Path },
            Output = new HeadlessOutput { File = Path.Combine(ws.Path, "out.txt"), Stream = "stdout" },
            Limits = new HeadlessLimits { MaxIterations = 10, TimeoutSeconds = 120 },
        };

        var services = HeadlessAgentRunner.BuildServiceProvider(config, NullLoggerFactory.Instance);
        HeadlessAgentRunner.RegisterBuiltInTools(services, NullLoggerFactory.Instance);
        var registry = services.GetRequiredService<IToolRegistry>();
        var executor = services.GetRequiredService<IToolExecutor>();
        var factory = services.GetRequiredService<Andy.Llm.Providers.ILlmProviderFactory>();
        var provider = factory.CreateProvider("openrouter");
        Assert.NotNull(provider);

        // Capturing logger: the engine's catch logs the exception; record it to the crash log.
        var captured = new List<Exception>();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(new CaptureProvider(captured)));

        var agent = new SimpleAgent(
            provider!, registry, executor,
            systemPrompt: "You are a coding assistant. Use tools to inspect the repository and help the user.",
            maxTurns: 10,
            workingDirectory: ws.Path,
            logger: loggerFactory.CreateLogger<SimpleAgent>());

        // Turn 1: a tool call lands in the conversation history.
        var turn1 = await agent.ProcessMessageAsync(
            "Run this shell command and summarize the output: gh issue list -R rivoli-ai/andy-cli --limit 25");

        // Turn 2: the follow-up that crashes for the user.
        var turn2 = await agent.ProcessMessageAsync("let's work on issue #21");

        foreach (var ex in captured)
        {
            CrashLog.Write("engine.SimpleAgent", ex);
        }

        var crash = captured.Count > 0 ? string.Join("\n----\n", captured.Select(e => e.ToString())) : "(none captured)";
        var crashed =
            (turn1.Response?.Contains("Object reference not set", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (turn2.Response?.Contains("Object reference not set", StringComparison.OrdinalIgnoreCase) ?? false) ||
            captured.Any(e => e is NullReferenceException);

        Assert.False(
            crashed,
            $"Follow-up turn hit a NullReferenceException.\n" +
            $"Turn1: {turn1.Response}\nTurn2: {turn2.Response}\n\nCaptured:\n{crash}");
    }

    private sealed class CaptureProvider : ILoggerProvider
    {
        private readonly List<Exception> _sink;
        public CaptureProvider(List<Exception> sink) => _sink = sink;
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(_sink);
        public void Dispose() { }

        private sealed class CaptureLogger : ILogger
        {
            private readonly List<Exception> _sink;
            public CaptureLogger(List<Exception> sink) => _sink = sink;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (exception != null) _sink.Add(exception);
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"andy-nre-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
