using Andy.Engine;
using Andy.Model.Llm;
using Andy.Model.Model;
using Andy.Tools.Core;
using Moq;
using Xunit;

namespace Andy.Cli.Tests.Integration;

/// <summary>
/// Tests that verify correct message ordering in multi-turn conversations with tool calls.
/// These tests exercise SimpleAgent (from Andy.Engine) to ensure that when a conversation
/// involves tool calls, subsequent LLM requests include the intermediate assistant messages
/// (with ToolCalls) before their corresponding tool result messages.
///
/// This prevents the OpenAI API error:
/// "messages with role 'tool' must be a response to a preceding message with 'tool_calls'"
/// </summary>
public class MultiTurnToolCallContextTests
{
    private readonly Mock<ILlmProvider> _mockLlm;
    private readonly Mock<IToolRegistry> _mockRegistry;
    private readonly Mock<IToolExecutor> _mockExecutor;

    public MultiTurnToolCallContextTests()
    {
        _mockLlm = new Mock<ILlmProvider>();
        _mockRegistry = new Mock<IToolRegistry>();
        _mockExecutor = new Mock<IToolExecutor>();

        _mockRegistry.Setup(r => r.Tools)
            .Returns(Array.Empty<ToolRegistration>());
    }

    private SimpleAgent CreateAgent()
    {
        return new SimpleAgent(
            _mockLlm.Object,
            _mockRegistry.Object,
            _mockExecutor.Object,
            systemPrompt: "You are a helpful assistant.",
            maxTurns: 10,
            workingDirectory: "/tmp");
    }

    [Fact]
    public async Task SecondTurn_AfterToolCall_SendsCorrectContext()
    {
        // Regression test: after a turn with tool calls, the next turn must include
        // the intermediate assistant message (with ToolCalls) before tool result messages.
        // Without this, OpenAI rejects the request.

        var capturedRequests = new List<LlmRequest>();
        var callCount = 0;

        _mockLlm.Setup(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LlmRequest, CancellationToken>((req, _) => capturedRequests.Add(req))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // First call: assistant requests a tool
                    return new LlmResponse
                    {
                        AssistantMessage = new Message
                        {
                            Role = Role.Assistant,
                            Content = "Let me look that up.",
                            ToolCalls = new List<ToolCall>
                            {
                                new ToolCall { Id = "call_1", Name = "read_file", ArgumentsJson = "{\"path\":\"/tmp/test.txt\"}" }
                            }
                        }
                    };
                }
                if (callCount == 2)
                {
                    // Second call: final answer after tool result
                    return new LlmResponse
                    {
                        AssistantMessage = new Message { Role = Role.Assistant, Content = "The file contains test data." },
                        FinishReason = "stop"
                    };
                }
                // Third call: response to second user message
                return new LlmResponse
                {
                    AssistantMessage = new Message { Role = Role.Assistant, Content = "Sure, here's more info." },
                    FinishReason = "stop"
                };
            });

        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<ToolExecutionContext>()))
            .ReturnsAsync(new ToolExecutionResult
            {
                IsSuccessful = true,
                Data = "file contents here",
                Message = "Read successfully"
            });

        var agent = CreateAgent();

        // Turn 1: involves a tool call
        await agent.ProcessMessageAsync("Read the test file");

        // Turn 2: simple follow-up
        await agent.ProcessMessageAsync("Tell me more about it");

        // Verify: the 3rd LLM request (from turn 2) has correct context from turn 1
        Assert.Equal(3, capturedRequests.Count);
        var contextRequest = capturedRequests[2];
        var messages = contextRequest.Messages;

        // Expected context: User1 → Assistant(tool_calls) → Tool(result) → Assistant(final) → User2
        Assert.Equal(5, messages.Count);

        // First: original user message
        Assert.Equal(Role.User, messages[0].Role);
        Assert.Equal("Read the test file", messages[0].Content);

        // Second: assistant message WITH tool_calls (this was the bug — it was missing before)
        Assert.Equal(Role.Assistant, messages[1].Role);
        Assert.NotNull(messages[1].ToolCalls);
        Assert.NotEmpty(messages[1].ToolCalls);
        Assert.Equal("read_file", messages[1].ToolCalls[0].Name);

        // Third: tool result
        Assert.Equal(Role.Tool, messages[2].Role);

        // Fourth: final assistant response
        Assert.Equal(Role.Assistant, messages[3].Role);
        Assert.Equal("The file contains test data.", messages[3].Content);

        // Fifth: new user message
        Assert.Equal(Role.User, messages[4].Role);
        Assert.Equal("Tell me more about it", messages[4].Content);
    }

    [Fact]
    public async Task MultiStepToolCalls_ContextPreservesAllIntermediateAssistantMessages()
    {
        // Verify that when a single turn has multiple tool call rounds,
        // ALL intermediate assistant messages are preserved in context

        var capturedRequests = new List<LlmRequest>();
        var callCount = 0;

        _mockLlm.Setup(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LlmRequest, CancellationToken>((req, _) => capturedRequests.Add(req))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return new LlmResponse
                    {
                        AssistantMessage = new Message
                        {
                            Role = Role.Assistant,
                            Content = "First I'll read the file.",
                            ToolCalls = new List<ToolCall>
                            {
                                new ToolCall { Id = "c1", Name = "read_file", ArgumentsJson = "{}" }
                            }
                        }
                    };
                }
                if (callCount == 2)
                {
                    return new LlmResponse
                    {
                        AssistantMessage = new Message
                        {
                            Role = Role.Assistant,
                            Content = "Now I'll write the output.",
                            ToolCalls = new List<ToolCall>
                            {
                                new ToolCall { Id = "c2", Name = "write_file", ArgumentsJson = "{}" }
                            }
                        }
                    };
                }
                if (callCount == 3)
                {
                    return new LlmResponse
                    {
                        AssistantMessage = new Message { Role = Role.Assistant, Content = "All done!" },
                        FinishReason = "stop"
                    };
                }
                return new LlmResponse
                {
                    AssistantMessage = new Message { Role = Role.Assistant, Content = "Follow-up answer." },
                    FinishReason = "stop"
                };
            });

        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<ToolExecutionContext>()))
            .ReturnsAsync(new ToolExecutionResult { IsSuccessful = true, Data = "ok", Message = "ok" });

        var agent = CreateAgent();

        await agent.ProcessMessageAsync("Process the data");
        await agent.ProcessMessageAsync("What happened?");

        // The 4th request should have full interleaved context
        Assert.Equal(4, capturedRequests.Count);
        var msgs = capturedRequests[3].Messages;

        // Turn1: User → Asst(c1) → Tool → Asst(c2) → Tool → Asst(final)
        // Turn2: User
        Assert.Equal(7, msgs.Count);

        Assert.Equal(Role.User, msgs[0].Role);

        Assert.Equal(Role.Assistant, msgs[1].Role);
        Assert.NotEmpty(msgs[1].ToolCalls);
        Assert.Equal("read_file", msgs[1].ToolCalls[0].Name);

        Assert.Equal(Role.Tool, msgs[2].Role);

        Assert.Equal(Role.Assistant, msgs[3].Role);
        Assert.NotEmpty(msgs[3].ToolCalls);
        Assert.Equal("write_file", msgs[3].ToolCalls[0].Name);

        Assert.Equal(Role.Tool, msgs[4].Role);

        Assert.Equal(Role.Assistant, msgs[5].Role);
        Assert.Equal("All done!", msgs[5].Content);

        Assert.Equal(Role.User, msgs[6].Role);
        Assert.Equal("What happened?", msgs[6].Content);
    }

    [Fact]
    public async Task GetHistory_AfterToolCalls_HasCorrectMessageOrder()
    {
        var callCount = 0;

        _mockLlm.Setup(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return new LlmResponse
                    {
                        AssistantMessage = new Message
                        {
                            Role = Role.Assistant,
                            Content = "Using tool.",
                            ToolCalls = new List<ToolCall>
                            {
                                new ToolCall { Id = "c1", Name = "test_tool", ArgumentsJson = "{}" }
                            }
                        }
                    };
                }
                return new LlmResponse
                {
                    AssistantMessage = new Message { Role = Role.Assistant, Content = "Done." },
                    FinishReason = "stop"
                };
            });

        _mockExecutor.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<ToolExecutionContext>()))
            .ReturnsAsync(new ToolExecutionResult { IsSuccessful = true, Data = "result", Message = "ok" });

        var agent = CreateAgent();
        await agent.ProcessMessageAsync("Do something");

        var history = agent.GetHistory();

        // User → Assistant(tool_calls) → Tool(result) → Assistant(final)
        Assert.Equal(4, history.Count);
        Assert.Equal(Role.User, history[0].Role);
        Assert.Equal(Role.Assistant, history[1].Role);
        Assert.NotNull(history[1].ToolCalls);
        Assert.NotEmpty(history[1].ToolCalls);
        Assert.Equal(Role.Tool, history[2].Role);
        Assert.Equal(Role.Assistant, history[3].Role);
        Assert.Equal("Done.", history[3].Content);
    }
}
