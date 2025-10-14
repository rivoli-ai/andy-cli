using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Andy.Model.Llm;
using Andy.Tools.Core;

namespace Andy.Model.Llm
{
    // Compatibility shim for missing MessageRole enum
    public enum MessageRole
    {
        System,
        User,
        Assistant
    }
}

namespace Andy.Llm
{
    // Compatibility shim for tests using the obsolete LlmClient class
    public class LlmClient
    {
        public LlmClient(string apiKey) { }

        public virtual Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            // LlmResponse has readonly properties, so we return a default instance
            return Task.FromResult(new LlmResponse());
        }

        public virtual IAsyncEnumerable<LlmStreamResponse> StreamCompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}

namespace Andy.Cli.Widgets
{
    // Compatibility shim for tests using the obsolete ContentFeed class
    public class ContentFeed
    {
        public void AddMarkdownRich(string text) { }
        public void AddSystemMessage(string text) { }
    }
}

namespace Andy.Cli.Services
{
    // Compatibility shim for tests using the obsolete AiConversationService class
    public class AiConversationService
    {
        public AiConversationService(
            Andy.Llm.LlmClient llmClient,
            IToolRegistry toolRegistry,
            IToolExecutor toolExecutor,
            Andy.Cli.Widgets.ContentFeed contentFeed,
            Microsoft.Extensions.Logging.ILogger<AiConversationService>? logger = null)
        {
        }

        public Task<string> ProcessMessageAsync(string message, CancellationToken cancellationToken = default)
        {
            return Task.FromResult("Test response");
        }
    }
}