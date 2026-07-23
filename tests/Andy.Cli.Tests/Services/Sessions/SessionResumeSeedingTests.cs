using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Cli.Services.Sessions;
using Andy.Cli.Widgets;
using Andy.Model.Llm;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Cli.Tests.Services.Sessions;

/// <summary>
/// End-to-end resume seeding at the testable seam (issue #231): a conversation is
/// exported from one SimpleAssistantService (real packaged SimpleAgent underneath),
/// persisted through the SessionStore (redaction and JSON round-trip included),
/// restored into a NEW service, and the next LLM request is verified to carry the
/// full prior message history.
/// </summary>
public class SessionResumeSeedingTests : IDisposable
{
    private readonly Mock<ILlmProvider> _mockLlmProvider = new();
    private readonly Mock<IToolRegistry> _mockToolRegistry = new();
    private readonly Mock<IToolExecutor> _mockToolExecutor = new();
    private readonly FeedView _feedView = new();
    private readonly string _directory;

    public SessionResumeSeedingTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "andy-session-seed-tests-" + Guid.NewGuid().ToString("N"));
        _mockToolRegistry.Setup(x => x.GetTools(
                It.IsAny<ToolCategory?>(), It.IsAny<ToolCapability?>(),
                It.IsAny<IEnumerable<string>?>(), It.IsAny<bool>()))
            .Returns(new List<ToolRegistration>());
        // The agent enumerates registry.Tools; an unconfigured mock returns null and throws.
        _mockToolRegistry.Setup(x => x.Tools).Returns(new List<ToolRegistration>());
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

    private SimpleAssistantService CreateService() => new(
        _mockLlmProvider.Object,
        _mockToolRegistry.Object,
        _mockToolExecutor.Object,
        _feedView,
        "test-model",
        "test-provider",
        tokenCounter: null,
        loggerFactory: NullLoggerFactory.Instance);

    private void SetupResponses(Queue<string> responses, List<LlmRequest> capturedRequests)
    {
        _mockLlmProvider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LlmRequest, CancellationToken>((req, _) => capturedRequests.Add(req))
            .ReturnsAsync(() => new LlmResponse
            {
                AssistantMessage = new Andy.Model.Model.Message
                {
                    Role = Andy.Model.Model.MessageRole.Assistant,
                    Content = responses.Dequeue(),
                    ToolCalls = new List<Andy.Model.Model.ToolCall>()
                },
                Usage = new LlmUsage { PromptTokens = 10, CompletionTokens = 5 }
            });
    }

    [Fact]
    public async Task ResumedService_SendsPriorConversationToProvider()
    {
        var capturedRequests = new List<LlmRequest>();
        SetupResponses(new Queue<string>(new[] { "Paris.", "About 2.1 million." }), capturedRequests);

        // Session 1: one turn, then export and persist through the store.
        var service1 = CreateService();
        await service1.ProcessMessageAsync("What is the capital of France?");
        var store = new SessionStore(_directory, new SessionRedactor(Array.Empty<string>()));
        var sessionId = SessionStore.NewSessionId();
        Assert.True(store.Save(sessionId, service1.ExportTranscript(), "test-provider", "test-model"));
        service1.Dispose();

        // Session 2 (fresh process in real life): load and restore, then ask a follow-up.
        var record = store.Load(sessionId);
        Assert.NotNull(record);
        var service2 = CreateService();
        service2.RestoreTranscript(record!.Snapshot);

        var response = await service2.ProcessMessageAsync("And how many people live there?");
        Assert.Equal("About 2.1 million.", response);

        // The second session's LLM request must contain the FULL prior conversation.
        var resumedRequest = capturedRequests.Last();
        var contents = resumedRequest.Messages.Select(m => m.Content ?? "").ToList();
        Assert.Contains("What is the capital of France?", contents);
        Assert.Contains("Paris.", contents);
        Assert.Contains("And how many people live there?", contents);

        // And the prior turn precedes the new user message.
        Assert.True(
            contents.FindIndex(c => c.Contains("capital of France")) <
            contents.FindIndex(c => c.Contains("how many people live there")));
        service2.Dispose();
    }

    [Fact]
    public async Task RestoredHistory_IsVisibleInContextStats()
    {
        var capturedRequests = new List<LlmRequest>();
        SetupResponses(new Queue<string>(new[] { "Hello!" }), capturedRequests);

        var service1 = CreateService();
        await service1.ProcessMessageAsync("Hi");
        var snapshot = service1.ExportTranscript();
        service1.Dispose();

        var service2 = CreateService();
        Assert.Equal(0, service2.GetContextStats().TurnCount);
        service2.RestoreTranscript(snapshot);
        Assert.True(service2.GetContextStats().TurnCount > 0);
        service2.Dispose();
    }

    [Fact]
    public async Task RestoreTranscript_IntoNonEmptyConversation_Throws()
    {
        var capturedRequests = new List<LlmRequest>();
        SetupResponses(new Queue<string>(new[] { "one", "two" }), capturedRequests);

        var service = CreateService();
        await service.ProcessMessageAsync("first message");
        var snapshot = service.ExportTranscript();

        // The engine restores only into an EMPTY conversation; resuming therefore
        // always builds a fresh service (see Program's resume paths).
        Assert.Throws<InvalidOperationException>(() => service.RestoreTranscript(snapshot));
        service.Dispose();
    }
}
