using Andy.Llm;
using Andy.Llm.Extensions;
using Andy.Llm.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Tests;

public class AndyLlmIntegrationTests
{
    [Fact]
    public void AndyLlm_Services_Can_Be_Configured()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureLlmFromEnvironment();
        services.AddLlmServices(options =>
        {
            options.DefaultProvider = "cerebras";
        });

        // Act
        var serviceProvider = services.BuildServiceProvider();
        var llmClient = serviceProvider.GetService<LlmClient>();

        // Assert
        Assert.NotNull(llmClient);
    }

    [Fact]
    public void ConversationContext_Can_Be_Created_And_Used()
    {
        // Arrange
        var conversation = new ConversationContext
        {
            SystemInstruction = "You are a helpful AI assistant.",
            MaxContextMessages = 10
        };

        // Act
        conversation.AddUserMessage("Hello");
        conversation.AddAssistantMessage("Hi there!");
        conversation.AddUserMessage("How are you?");

        var request = conversation.CreateRequest();

        // Assert
        Assert.NotNull(request);
        Assert.NotNull(request.Messages);
        Assert.Equal(4, request.Messages.Count); // System + 3 conversation messages
        
        // Check system message
        var systemMessage = request.Messages.FirstOrDefault(m => m.Role == MessageRole.System);
        Assert.NotNull(systemMessage);
        var systemTextPart = systemMessage.Parts.OfType<TextPart>().FirstOrDefault();
        Assert.NotNull(systemTextPart);
        Assert.Equal("You are a helpful AI assistant.", systemTextPart.Text);
        
        // Check conversation messages
        var userMessages = request.Messages.Where(m => m.Role == MessageRole.User).ToList();
        Assert.Equal(2, userMessages.Count);
        var userTextPart1 = userMessages[0].Parts.OfType<TextPart>().FirstOrDefault();
        Assert.NotNull(userTextPart1);
        Assert.Equal("Hello", userTextPart1.Text);
        var userTextPart2 = userMessages[1].Parts.OfType<TextPart>().FirstOrDefault();
        Assert.NotNull(userTextPart2);
        Assert.Equal("How are you?", userTextPart2.Text);
        
        var assistantMessage = request.Messages.FirstOrDefault(m => m.Role == MessageRole.Assistant);
        Assert.NotNull(assistantMessage);
        var assistantTextPart = assistantMessage.Parts.OfType<TextPart>().FirstOrDefault();
        Assert.NotNull(assistantTextPart);
        Assert.Equal("Hi there!", assistantTextPart.Text);
    }

    [Fact]
    public void LlmRequest_Can_Be_Created_With_Proper_Structure()
    {
        // Arrange & Act
        var request = new LlmRequest
        {
            Messages = new List<Message>
            {
                Message.CreateText(MessageRole.System, "You are a helpful assistant."),
                Message.CreateText(MessageRole.User, "Hello, world!")
            },
            Model = "llama3.1-8b",
            MaxTokens = 100,
            Temperature = 0.7
        };

        // Assert
        Assert.NotNull(request.Messages);
        Assert.Equal(2, request.Messages.Count);
        Assert.Equal("llama3.1-8b", request.Model);
        Assert.Equal(100, request.MaxTokens);
        Assert.Equal(0.7, request.Temperature);
        
        var systemMessage = request.Messages.FirstOrDefault(m => m.Role == MessageRole.System);
        Assert.NotNull(systemMessage);
        var systemTextPart2 = systemMessage.Parts.OfType<TextPart>().FirstOrDefault();
        Assert.NotNull(systemTextPart2);
        Assert.Equal("You are a helpful assistant.", systemTextPart2.Text);
        
        var userMessage = request.Messages.FirstOrDefault(m => m.Role == MessageRole.User);
        Assert.NotNull(userMessage);
        var userTextPart3 = userMessage.Parts.OfType<TextPart>().FirstOrDefault();
        Assert.NotNull(userTextPart3);
        Assert.Equal("Hello, world!", userTextPart3.Text);
    }

    [Fact]
    public void ConversationContext_Respects_MaxContextMessages_Limit()
    {
        // Arrange
        var conversation = new ConversationContext
        {
            SystemInstruction = "You are a helpful AI assistant.",
            MaxContextMessages = 3 // Very small limit for testing
        };

        // Act - Add more messages than the limit
        conversation.AddUserMessage("Message 1");
        conversation.AddAssistantMessage("Response 1");
        conversation.AddUserMessage("Message 2");
        conversation.AddAssistantMessage("Response 2");
        conversation.AddUserMessage("Message 3");
        conversation.AddAssistantMessage("Response 3");
        conversation.AddUserMessage("Message 4"); // This should trigger context trimming

        var request = conversation.CreateRequest();

        // Assert - Should only have system message + max context messages
        // System message + 3 conversation pairs = 7 total, but limited to 3 context messages
        // So we should have system + 3 messages = 4 total
        Assert.True(request.Messages.Count <= 4, $"Expected at most 4 messages, got {request.Messages.Count}");
        
        // The most recent messages should be preserved
        var userMessages = request.Messages.Where(m => m.Role == MessageRole.User).ToList();
        var userTexts = userMessages.Select(m => m.Parts.OfType<TextPart>().FirstOrDefault()?.Text).Where(t => t != null).ToList();
        Assert.Contains("Message 4", userTexts);
    }
}