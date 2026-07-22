using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Andy.Acp.Core.Agent;
using Andy.Cli.ACP;
using Andy.Engine;
using Andy.Model.Llm;
using Andy.Tools.Core;
using Andy.Tools.Execution;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Language.Flow;
using Xunit;

namespace Andy.Cli.Tests.ACP;

/// <summary>
/// Tests for AndyAgentProvider - the ACP integration for Andy.CLI
/// </summary>
public class AndyAgentProviderTests
{
    private readonly Mock<ILlmProvider> _mockLlmProvider;
    private readonly Mock<IToolRegistry> _mockToolRegistry;
    private readonly Mock<IToolExecutor> _mockToolExecutor;
    private readonly Mock<ILogger<AndyAgentProvider>> _mockLogger;
    private readonly AndyAgentProvider _provider;

    public AndyAgentProviderTests()
    {
        _mockLlmProvider = new Mock<ILlmProvider>();
        _mockToolRegistry = new Mock<IToolRegistry>();
        _mockToolExecutor = new Mock<IToolExecutor>();
        _mockLogger = new Mock<ILogger<AndyAgentProvider>>();

        // Setup tool registry to return empty tools list
        // GetTools has optional parameters, so we need to setup all overloads
        _mockToolRegistry.Setup(x => x.GetTools(
            It.IsAny<ToolCategory?>(),
            It.IsAny<ToolCapability?>(),
            It.IsAny<IEnumerable<string>?>(),
            It.IsAny<bool>()))
            .Returns(new List<ToolRegistration>());

        _provider = new AndyAgentProvider(
            _mockLlmProvider.Object,
            _mockToolRegistry.Object,
            _mockToolExecutor.Object,
            _mockLogger.Object);
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenLlmProviderIsNull()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new AndyAgentProvider(
            null!,
            _mockToolRegistry.Object,
            _mockToolExecutor.Object,
            _mockLogger.Object));
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenToolRegistryIsNull()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new AndyAgentProvider(
            _mockLlmProvider.Object,
            null!,
            _mockToolExecutor.Object,
            _mockLogger.Object));
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenToolExecutorIsNull()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new AndyAgentProvider(
            _mockLlmProvider.Object,
            _mockToolRegistry.Object,
            null!,
            _mockLogger.Object));
    }

    [Fact]
    public void GetCapabilities_ReturnsCorrectCapabilities()
    {
        // Act
        var capabilities = _provider.GetCapabilities();

        // Assert
        Assert.NotNull(capabilities);
        Assert.True(capabilities.LoadSession);
        Assert.False(capabilities.AudioPrompts);
        Assert.False(capabilities.ImagePrompts);
        Assert.True(capabilities.EmbeddedContext);
    }

    [Fact]
    public async Task CreateSessionAsync_CreatesNewSession_WithGeneratedId()
    {
        // Arrange
        var parameters = new NewSessionParams();

        // Act
        var metadata = await _provider.CreateSessionAsync(parameters, CancellationToken.None);

        // Assert
        Assert.NotNull(metadata);
        Assert.NotNull(metadata.SessionId);
        Assert.StartsWith("session-", metadata.SessionId);
        Assert.Equal("assistant", metadata.Mode);
        Assert.Equal("andy-cli", metadata.Model);
        Assert.NotNull(metadata.Metadata);
        Assert.Equal("andy-cli", metadata.Metadata["provider"]);
        Assert.Equal(0, metadata.Metadata["tools_count"]); // Based on mocked tools (empty list)
    }

    [Fact]
    public async Task CreateSessionAsync_AcceptsNullParameters()
    {
        // Act
        var metadata = await _provider.CreateSessionAsync(null, CancellationToken.None);

        // Assert
        Assert.NotNull(metadata);
        Assert.NotNull(metadata.SessionId);
    }

    [Fact]
    public async Task CreateSessionAsync_ReportsConfiguredProviderAndModel()
    {
        using var provider = new AndyAgentProvider(
            _mockLlmProvider.Object,
            _mockToolRegistry.Object,
            _mockToolExecutor.Object,
            _mockLogger.Object,
            defaultModel: "moonshotai/kimi-k3",
            defaultProvider: "openrouter");

        var metadata = await provider.CreateSessionAsync(null, CancellationToken.None);

        Assert.Equal("moonshotai/kimi-k3", metadata.Model);
        Assert.Equal("openrouter", metadata.Metadata!["provider"]);
    }

    [Fact]
    public async Task CreateSessionAsync_AdvertisesGroupedModelConfigOptions()
    {
        var selections = ModelSelections();
        using var provider = new AndyAgentProvider(
            _mockLlmProvider.Object,
            _mockToolRegistry.Object,
            _mockToolExecutor.Object,
            _mockLogger.Object,
            defaultModel: "moonshotai/kimi-k3",
            agentFactory: new FakeAgentFactory(_ => FakeAgent.ReturningSuccess("ok")),
            defaultProvider: "openrouter",
            modelSelections: selections);

        var metadata = await provider.CreateSessionAsync(null, CancellationToken.None);

        var option = Assert.Single(metadata.ConfigOptions!);
        Assert.Equal("model", option.Id);
        Assert.Equal("model", option.Category);
        Assert.Equal("openrouter::moonshotai/kimi-k3", option.CurrentValueId);
        Assert.Collection(
            option.Groups!,
            group =>
            {
                Assert.Equal("openrouter", group.Group);
                Assert.Equal("OpenRouter", group.Name);
                Assert.Equal("moonshotai/kimi-k3", Assert.Single(group.Options).Name);
            },
            group =>
            {
                Assert.Equal("openai", group.Group);
                Assert.Equal("OpenAI", group.Name);
                Assert.Equal("gpt-4o", Assert.Single(group.Options).Name);
            });
    }

    [Fact]
    public async Task SetConfigOptionAsync_RebuildsOnlySelectedSessionAndUpdatesMetadata()
    {
        var factory = new FakeAgentFactory(_ => FakeAgent.ReturningSuccess("ok"));
        using var provider = NewConfigurableProvider(factory);
        var first = await provider.CreateSessionAsync(null, CancellationToken.None);
        var second = await provider.CreateSessionAsync(null, CancellationToken.None);
        var firstOriginalAgent = factory.Created[0].Agent;
        var secondOriginalAgent = factory.Created[1].Agent;

        var options = await provider.SetConfigOptionAsync(
            first.SessionId,
            "model",
            new SessionConfigValue { ValueId = "openai::gpt-4o" },
            CancellationToken.None);

        Assert.True(firstOriginalAgent.IsDisposed);
        Assert.False(secondOriginalAgent.IsDisposed);
        Assert.Equal(("openai", "gpt-4o"), (factory.Created[2].Provider, factory.Created[2].Model));
        Assert.Equal("openai::gpt-4o", Assert.Single(options).CurrentValueId);

        var loadedFirst = await provider.LoadSessionAsync(
            new LoadSessionParams { SessionId = first.SessionId, Cwd = "/tmp" },
            new Mock<IResponseStreamer>().Object,
            CancellationToken.None);
        var loadedSecond = await provider.LoadSessionAsync(
            new LoadSessionParams { SessionId = second.SessionId, Cwd = "/tmp" },
            new Mock<IResponseStreamer>().Object,
            CancellationToken.None);

        Assert.Equal("gpt-4o", loadedFirst!.Model);
        Assert.Equal("openai", loadedFirst.Metadata!["provider"]);
        Assert.Equal("moonshotai/kimi-k3", loadedSecond!.Model);
        Assert.Equal("openrouter", loadedSecond.Metadata!["provider"]);
    }

    [Fact]
    public async Task SetConfigOptionAsync_RejectsUnknownSelectionWithoutReplacingAgent()
    {
        var factory = new FakeAgentFactory(_ => FakeAgent.ReturningSuccess("ok"));
        using var provider = NewConfigurableProvider(factory);
        var session = await provider.CreateSessionAsync(null, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.SetConfigOptionAsync(
            session.SessionId,
            "model",
            new SessionConfigValue { ValueId = "unknown::model" },
            CancellationToken.None));

        Assert.Single(factory.Created);
        Assert.False(factory.Created[0].Agent.IsDisposed);
    }

    [Fact]
    public async Task SetConfigOptionAsync_RejectsChangeWhilePromptIsRunning()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = 0;
        var factory = new FakeAgentFactory(_ => factoryAgent());
        FakeAgent factoryAgent() => created++ == 0
            ? FakeAgent.Blocking(_ => started.TrySetResult())
            : FakeAgent.ReturningSuccess("replacement");

        using var provider = NewConfigurableProvider(factory);
        var session = await provider.CreateSessionAsync(null, CancellationToken.None);
        var promptTask = provider.ProcessPromptAsync(
            session.SessionId,
            new PromptMessage { Text = "wait" },
            new Mock<IResponseStreamer>().Object,
            CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SetConfigOptionAsync(
            session.SessionId,
            "model",
            new SessionConfigValue { ValueId = "openai::gpt-4o" },
            CancellationToken.None));

        Assert.Equal(2, factory.Created.Count);
        Assert.True(factory.Created[1].Agent.IsDisposed);
        await provider.CancelSessionAsync(session.SessionId, CancellationToken.None);
        var response = await promptTask;
        Assert.Equal(StopReason.Cancelled, response.StopReason);
    }

    /// <summary>Adapter for the conformant LoadSessionAsync(LoadSessionParams, IResponseStreamer, ct) signature.</summary>
    private Task<SessionMetadata?> LoadAsync(string sessionId) =>
        _provider.LoadSessionAsync(
            new LoadSessionParams { SessionId = sessionId, Cwd = "/tmp" },
            new Mock<IResponseStreamer>().Object,
            CancellationToken.None);

    [Fact]
    public async Task LoadSessionAsync_ReturnsSessionMetadata_ForExistingSession()
    {
        // Arrange
        var createResult = await _provider.CreateSessionAsync(null, CancellationToken.None);
        var sessionId = createResult.SessionId;

        // Act
        var metadata = await LoadAsync(sessionId);

        // Assert
        Assert.NotNull(metadata);
        Assert.Equal(sessionId, metadata.SessionId);
        Assert.Equal("assistant", metadata.Mode);
        Assert.Equal("andy-cli", metadata.Model);
    }

    [Fact]
    public async Task LoadSessionAsync_ReturnsNull_ForNonExistentSession()
    {
        // Act
        var metadata = await LoadAsync("non-existent-session");

        // Assert
        Assert.Null(metadata);
    }

    [Fact]
    public async Task ProcessPromptAsync_ReturnsError_WhenSessionNotFound()
    {
        // Arrange
        var prompt = new PromptMessage { Text = "Hello" };
        var mockStreamer = new Mock<IResponseStreamer>();

        // Act
        var response = await _provider.ProcessPromptAsync(
            "non-existent-session",
            prompt,
            mockStreamer.Object,
            CancellationToken.None);

        // Assert
        Assert.NotNull(response);
        Assert.Equal(StopReason.Error, response.StopReason);
        Assert.Contains("Session not found", response.Error);
    }

    [Fact]
    public async Task SetSessionModeAsync_ReturnsFalse_ForNonImplementedFeature()
    {
        // Arrange
        var createResult = await _provider.CreateSessionAsync(null, CancellationToken.None);
        var sessionId = createResult.SessionId;

        // Act
        var result = await _provider.SetSessionModeAsync(sessionId, "code", CancellationToken.None);

        // Assert - Mode switching not implemented yet
        Assert.False(result);
    }

    [Fact]
    public async Task CancelSessionAsync_CompletesSuccessfully()
    {
        // Arrange
        var createResult = await _provider.CreateSessionAsync(null, CancellationToken.None);
        var sessionId = createResult.SessionId;

        // Act & Assert - Should not throw
        await _provider.CancelSessionAsync(sessionId, CancellationToken.None);
    }

    [Fact]
    public async Task MultipleSessionsCanBeCreated()
    {
        // Act
        var session1 = await _provider.CreateSessionAsync(null, CancellationToken.None);
        var session2 = await _provider.CreateSessionAsync(null, CancellationToken.None);
        var session3 = await _provider.CreateSessionAsync(null, CancellationToken.None);

        // Assert
        Assert.NotEqual(session1.SessionId, session2.SessionId);
        Assert.NotEqual(session2.SessionId, session3.SessionId);
        Assert.NotEqual(session1.SessionId, session3.SessionId);

        // Verify all can be loaded
        var loaded1 = await LoadAsync(session1.SessionId);
        var loaded2 = await LoadAsync(session2.SessionId);
        var loaded3 = await LoadAsync(session3.SessionId);

        Assert.NotNull(loaded1);
        Assert.NotNull(loaded2);
        Assert.NotNull(loaded3);
    }

    [Fact]
    public async Task SessionId_IsUniqueGuid()
    {
        // Act
        var session = await _provider.CreateSessionAsync(null, CancellationToken.None);

        // Assert
        var guidPart = session.SessionId.Replace("session-", "");
        Assert.True(Guid.TryParse(guidPart, out _), "Session ID should contain a valid GUID");
    }

    [Fact]
    public async Task CreateSessionAsync_TracksCreatedTime()
    {
        // Arrange
        var beforeCreate = DateTime.UtcNow.AddSeconds(-1);

        // Act
        var session = await _provider.CreateSessionAsync(null, CancellationToken.None);

        // Assert
        var afterCreate = DateTime.UtcNow.AddSeconds(1);
        Assert.True(session.CreatedAt >= beforeCreate);
        Assert.True(session.CreatedAt <= afterCreate);
    }

    // ----- Logger creation (fixes the null-cast bug) -----

    [Fact]
    public void CreateAgentLogger_ReturnsTypedLogger_WhenFactoryProvided()
    {
        using var factory = LoggerFactory.Create(b => { });

        var logger = AndyAgentProvider.CreateAgentLogger(factory);

        Assert.NotNull(logger);
        Assert.IsAssignableFrom<ILogger<SimpleAgent>>(logger);
    }

    [Fact]
    public void CreateAgentLogger_ReturnsNull_WhenFactoryMissing()
    {
        Assert.Null(AndyAgentProvider.CreateAgentLogger(null));
    }

    // ----- Streaming (no artificial word-splitting + delay) -----

    [Fact]
    public async Task ProcessPromptAsync_StreamsResponse_AsSingleOrderedBlock()
    {
        // Arrange: a fake engine agent returns a completed multi-word response.
        const string responseText = "Hello world from andy";
        var factory = new FakeAgentFactory(_ => FakeAgent.ReturningSuccess(responseText));
        var provider = NewProvider(factory);

        var chunks = new List<string>();
        var streamer = new Mock<IResponseStreamer>();
        streamer.Setup(s => s.SendMessageChunkAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string t, CancellationToken _) => { chunks.Add(t); return Task.CompletedTask; });

        var session = await provider.CreateSessionAsync(null, CancellationToken.None);

        // Act
        var response = await provider.ProcessPromptAsync(
            session.SessionId, new PromptMessage { Text = "hi" }, streamer.Object, CancellationToken.None);

        // Assert: one ordered block, not one chunk per word.
        Assert.Equal(StopReason.Completed, response.StopReason);
        Assert.Single(chunks);
        Assert.Equal(responseText, chunks[0]);
    }

    [Fact]
    public async Task ProcessPromptAsync_AnnouncesModelOnce_AndProgressForEveryPrompt()
    {
        var factory = new FakeAgentFactory(_ => FakeAgent.ReturningSuccess("ok"));
        using var provider = new AndyAgentProvider(
            _mockLlmProvider.Object,
            _mockToolRegistry.Object,
            _mockToolExecutor.Object,
            _mockLogger.Object,
            defaultModel: "moonshotai/kimi-k3",
            agentFactory: factory,
            defaultProvider: "openrouter");

        var thoughts = new List<string>();
        var streamer = new Mock<IResponseStreamer>();
        streamer.Setup(s => s.SendThinkingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string text, CancellationToken _) =>
            {
                thoughts.Add(text);
                return Task.CompletedTask;
            });
        streamer.Setup(s => s.SendMessageChunkAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var session = await provider.CreateSessionAsync(null, CancellationToken.None);
        await provider.ProcessPromptAsync(
            session.SessionId, new PromptMessage { Text = "one" }, streamer.Object, CancellationToken.None);
        await provider.ProcessPromptAsync(
            session.SessionId, new PromptMessage { Text = "two" }, streamer.Object, CancellationToken.None);

        Assert.Single(thoughts, text => text == "Model: openrouter/moonshotai/kimi-k3");
        Assert.Equal(2, thoughts.Count(text => text == "Analyzing request..."));
    }

    [Fact]
    public async Task ProcessPromptAsync_FoldsEmbeddedContext_IntoPrompt()
    {
        // Arrange: capture the message the engine receives.
        string? seen = null;
        var factory = new FakeAgentFactory(_ => FakeAgent.Capturing(msg => seen = msg, "ok"));
        var provider = NewProvider(factory);
        var streamer = new Mock<IResponseStreamer>();

        var session = await provider.CreateSessionAsync(null, CancellationToken.None);
        var prompt = new PromptMessage
        {
            Text = "answer this",
            Blocks = new List<ContentBlock>
            {
                new() { Type = "text", Text = "answer this" },
                new()
                {
                    Type = "resource",
                    Resource = new EmbeddedResource { Uri = "file:///ctx.txt", Text = "embedded content here" }
                }
            }
        };

        // Act
        await provider.ProcessPromptAsync(session.SessionId, prompt, streamer.Object, CancellationToken.None);

        // Assert: the embedded context and the user text both reach the engine.
        Assert.NotNull(seen);
        Assert.Contains("embedded content here", seen);
        Assert.Contains("answer this", seen);
    }

    // ----- Cancellation reaches the engine operation -----

    [Fact]
    public async Task CancelSessionAsync_CancelsActiveEngineOperation()
    {
        // Arrange: the engine call blocks on its token and records the token it
        // received, proving cancellation reaches the running engine operation.
        CancellationToken tokenSeenByEngine = default;
        var engineStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var factory = new FakeAgentFactory(_ => FakeAgent.Blocking(ct =>
        {
            tokenSeenByEngine = ct;
            engineStarted.TrySetResult(true);
        }));
        var provider = NewProvider(factory);

        var session = await provider.CreateSessionAsync(null, CancellationToken.None);
        var streamer = new Mock<IResponseStreamer>();

        // Act
        var promptTask = provider.ProcessPromptAsync(
            session.SessionId, new PromptMessage { Text = "long running" }, streamer.Object, CancellationToken.None);

        await engineStarted.Task; // engine call is now in flight
        await provider.CancelSessionAsync(session.SessionId, CancellationToken.None);
        var response = await promptTask;

        // Assert: the token threaded into the engine was cancelled, work stopped,
        // and completion is reported as Cancelled per the protocol.
        Assert.True(tokenSeenByEngine.IsCancellationRequested);
        Assert.Equal(StopReason.Cancelled, response.StopReason);
    }

    [Fact]
    public async Task ProcessPromptAsync_HonorsTransportCancellation()
    {
        var engineStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeAgentFactory(_ => FakeAgent.Blocking(_ => engineStarted.TrySetResult(true)));
        var provider = NewProvider(factory);

        var session = await provider.CreateSessionAsync(null, CancellationToken.None);
        var streamer = new Mock<IResponseStreamer>();
        using var transportCts = new CancellationTokenSource();

        var promptTask = provider.ProcessPromptAsync(
            session.SessionId, new PromptMessage { Text = "hi" }, streamer.Object, transportCts.Token);

        await engineStarted.Task;
        transportCts.Cancel();
        var response = await promptTask;

        Assert.Equal(StopReason.Cancelled, response.StopReason);
    }

    // ----- Concurrent prompts on one session are serialized (fix 4) -----

    [Fact]
    public async Task ProcessPromptAsync_RejectsSecondConcurrentPrompt_WithoutBreakingTheFirst()
    {
        // Arrange: the first prompt blocks in the engine, keeping the session
        // busy. A second prompt arriving on the same session must be rejected
        // cleanly and must NOT dispose the first prompt's cancellation source.
        var engineStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken firstToken = default;
        var factory = new FakeAgentFactory(_ => FakeAgent.Blocking(ct =>
        {
            firstToken = ct;
            engineStarted.TrySetResult(true);
        }));
        var provider = NewProvider(factory);
        var streamer = new Mock<IResponseStreamer>();

        var session = await provider.CreateSessionAsync(null, CancellationToken.None);

        var firstTask = provider.ProcessPromptAsync(
            session.SessionId, new PromptMessage { Text = "long" }, streamer.Object, CancellationToken.None);
        await engineStarted.Task; // first prompt is in flight

        // Act: a second concurrent prompt on the same session.
        var second = await provider.ProcessPromptAsync(
            session.SessionId, new PromptMessage { Text = "again" }, streamer.Object, CancellationToken.None);

        // Assert: the second prompt is a clean protocol error, not an exception.
        Assert.Equal(StopReason.Error, second.StopReason);
        Assert.Contains("already in progress", second.Error);

        // The first prompt's token is intact (not disposed by the rejection) and
        // still cancellable through the session.
        Assert.False(firstToken.IsCancellationRequested);
        await provider.CancelSessionAsync(session.SessionId, CancellationToken.None);
        var first = await firstTask;
        Assert.True(firstToken.IsCancellationRequested);
        Assert.Equal(StopReason.Cancelled, first.StopReason);
    }

    // ----- Concurrent dispose race yields a clean error, not an exception (fix 3) -----

    [Fact]
    public async Task ProcessPromptAsync_ReturnsCleanError_WhenSessionDisposedConcurrently()
    {
        var provider = NewProvider(new FakeAgentFactory(_ => FakeAgent.ReturningSuccess("ok")));
        var streamer = new Mock<IResponseStreamer>();
        var session = await provider.CreateSessionAsync(null, CancellationToken.None);

        // Simulate the race where the entry is disposed after it is resolved but
        // before/at the moment BeginPrompt runs. Reach into the registry and
        // dispose the still-retained entry (dispose does not remove it), so the
        // provider resolves it via TryGet but BeginPrompt throws
        // ObjectDisposedException.
        var registryField = typeof(AndyAgentProvider)
            .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(registryField);
        var registry = (AndySessionRegistry)registryField!.GetValue(provider)!;
        Assert.True(registry.TryGet(session.SessionId, out var entry));
        entry.Dispose();

        // Act: must not throw an unhandled ObjectDisposedException.
        var response = await provider.ProcessPromptAsync(
            session.SessionId, new PromptMessage { Text = "hi" }, streamer.Object, CancellationToken.None);

        // Assert: a clean protocol error.
        Assert.Equal(StopReason.Error, response.StopReason);
        Assert.Contains("no longer available", response.Error);
    }

    // ----- Error handling -----

    [Fact]
    public async Task ProcessPromptAsync_ReportsError_WhenEngineThrows()
    {
        var factory = new FakeAgentFactory(_ => FakeAgent.Throwing(new InvalidOperationException("boom")));
        var provider = NewProvider(factory);
        var streamer = new Mock<IResponseStreamer>();

        var session = await provider.CreateSessionAsync(null, CancellationToken.None);
        var response = await provider.ProcessPromptAsync(
            session.SessionId, new PromptMessage { Text = "hi" }, streamer.Object, CancellationToken.None);

        Assert.Equal(StopReason.Error, response.StopReason);
        Assert.Contains("boom", response.Error);
    }

    // ----- Shutdown / dispose cleans up sessions -----

    [Fact]
    public async Task Dispose_CleansUpSessions()
    {
        var agent = FakeAgent.ReturningSuccess("ok");
        var provider = NewProvider(new FakeAgentFactory(_ => agent));
        var session = await provider.CreateSessionAsync(null, CancellationToken.None);

        provider.Dispose();

        // After disposal the session is gone and the agent has been disposed.
        var loaded = await provider.LoadSessionAsync(new LoadSessionParams { SessionId = session.SessionId, Cwd = "/tmp" }, new Mock<IResponseStreamer>().Object, CancellationToken.None);
        Assert.Null(loaded);
        Assert.True(agent.IsDisposed);
    }

    // ----- Bounded retention evicts old sessions -----

    [Fact]
    public async Task CreateSessionAsync_EvictsAndDisposesOldSessions_WhenCapReached()
    {
        var created = new List<FakeAgent>();
        var factory = new FakeAgentFactory(_ =>
        {
            var a = FakeAgent.ReturningSuccess("ok");
            created.Add(a);
            return a;
        });
        var provider = NewProvider(factory, maxSessions: 2);

        var first = await provider.CreateSessionAsync(null, CancellationToken.None);
        await provider.CreateSessionAsync(null, CancellationToken.None);
        await provider.CreateSessionAsync(null, CancellationToken.None);

        // The oldest (least-recently-used) session must have been evicted and its
        // agent disposed.
        var loadedFirst = await provider.LoadSessionAsync(new LoadSessionParams { SessionId = first.SessionId, Cwd = "/tmp" }, new Mock<IResponseStreamer>().Object, CancellationToken.None);
        Assert.Null(loadedFirst);
        Assert.True(created[0].IsDisposed);

        provider.Dispose();
    }

    // ----- Concurrent sessions are isolated -----

    [Fact]
    public async Task ConcurrentSessions_HaveIsolatedState()
    {
        var provider = NewProvider(new FakeAgentFactory(_ => FakeAgent.ReturningSuccess("ok")));
        var streamer = new Mock<IResponseStreamer>();

        var s1 = await provider.CreateSessionAsync(null, CancellationToken.None);
        var s2 = await provider.CreateSessionAsync(null, CancellationToken.None);

        await provider.ProcessPromptAsync(s1.SessionId, new PromptMessage { Text = "a" }, streamer.Object, CancellationToken.None);

        var loaded1 = await provider.LoadSessionAsync(new LoadSessionParams { SessionId = s1.SessionId, Cwd = "/tmp" }, new Mock<IResponseStreamer>().Object, CancellationToken.None);
        var loaded2 = await provider.LoadSessionAsync(new LoadSessionParams { SessionId = s2.SessionId, Cwd = "/tmp" }, new Mock<IResponseStreamer>().Object, CancellationToken.None);

        // s1 processed a message; s2 did not. State is per-session.
        Assert.NotNull(loaded1);
        Assert.NotNull(loaded2);
        Assert.Equal(1, loaded1!.MessageCount);
        Assert.Equal(0, loaded2!.MessageCount);
    }

    private AndyAgentProvider NewProvider(ISessionAgentFactory factory, int maxSessions = AndySessionRegistry.DefaultMaxSessions)
        => new(
            _mockLlmProvider.Object,
            _mockToolRegistry.Object,
            _mockToolExecutor.Object,
            _mockLogger.Object,
            loggerFactory: null,
            maxSessions: maxSessions,
            defaultModel: null,
            agentFactory: factory);

    private AndyAgentProvider NewConfigurableProvider(ISessionAgentFactory factory) => new(
        _mockLlmProvider.Object,
        _mockToolRegistry.Object,
        _mockToolExecutor.Object,
        _mockLogger.Object,
        defaultModel: "moonshotai/kimi-k3",
        agentFactory: factory,
        defaultProvider: "openrouter",
        modelSelections: ModelSelections());

    private static IReadOnlyList<AcpModelSelection> ModelSelections() =>
        new[]
        {
            new AcpModelSelection(
                "openrouter::moonshotai/kimi-k3",
                "openrouter",
                "OpenRouter",
                "moonshotai/kimi-k3"),
            new AcpModelSelection("openai::gpt-4o", "openai", "OpenAI", "gpt-4o")
        };

    private sealed class FakeAgentFactory : ISessionAgentFactory
    {
        private readonly Func<string, FakeAgent> _create;
        public List<(string Provider, string Model, FakeAgent Agent)> Created { get; } = new();
        public FakeAgentFactory(Func<string, FakeAgent> create) => _create = create;
        public ISessionAgent Create(string systemPrompt, string provider, string model)
        {
            var agent = _create(systemPrompt);
            Created.Add((provider, model, agent));
            return agent;
        }
    }

    private sealed class FakeAgent : ISessionAgent
    {
        private readonly Func<string, CancellationToken, Task<SimpleAgentResult>> _behavior;
        public bool IsDisposed { get; private set; }

        private FakeAgent(Func<string, CancellationToken, Task<SimpleAgentResult>> behavior) => _behavior = behavior;

        public static FakeAgent ReturningSuccess(string response) =>
            new((_, _) => Task.FromResult(new SimpleAgentResult(true, response, 1, TimeSpan.Zero, "stop")));

        public static FakeAgent Capturing(Action<string> onMessage, string response) =>
            new((msg, _) => { onMessage(msg); return Task.FromResult(new SimpleAgentResult(true, response, 1, TimeSpan.Zero, "stop")); });

        public static FakeAgent Throwing(Exception ex) =>
            new((_, _) => throw ex);

        public static FakeAgent Blocking(Action<CancellationToken> onStart) =>
            new(async (_, ct) =>
            {
                onStart(ct);
                await Task.Delay(Timeout.Infinite, ct);
                return new SimpleAgentResult(true, "unreachable", 1, TimeSpan.Zero, "stop");
            });

        public Task<SimpleAgentResult> ProcessMessageAsync(
            string userMessage,
            IResponseStreamer streamer,
            CancellationToken cancellationToken)
            => _behavior(userMessage, cancellationToken);

        public void Dispose() => IsDisposed = true;
    }
}
