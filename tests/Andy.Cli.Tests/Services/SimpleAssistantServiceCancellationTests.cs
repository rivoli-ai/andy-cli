// Verifies that SimpleAssistantService honors cooperative cancellation: when the
// per-turn CancellationToken is cancelled (the same signal the interactive ESC key
// produces in Program.cs), ProcessMessageAsync surfaces an OperationCanceledException
// rather than swallowing it into an error string. The interactive run loop relies on
// this exception propagating so it can show "Cancelled." and stay responsive instead
// of quitting the app.

using System.Runtime.CompilerServices;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Model.Llm;
using Andy.Model.Model;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class SimpleAssistantServiceCancellationTests
{
    private static SimpleAssistantService CreateService(ILlmProvider provider)
    {
        var toolRegistry = new Mock<IToolRegistry>();
        var noTools = new List<ToolRegistration>();
        toolRegistry.Setup(x => x.Tools).Returns(noTools);
        toolRegistry
            .Setup(x => x.GetTools(
                It.IsAny<ToolCategory?>(),
                It.IsAny<ToolCapability?>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<bool>()))
            .Returns(noTools);
        var toolExecutor = new Mock<IToolExecutor>();
        var feed = new FeedView();
        var logger = new Mock<ILogger<SimpleAssistantService>>();

        return new SimpleAssistantService(
            provider,
            toolRegistry.Object,
            toolExecutor.Object,
            feed,
            "test-model",
            "test-provider",
            tokenCounter: null,
            logger: logger.Object);
    }

    [Fact]
    public async Task ProcessMessageAsync_WhenTokenAlreadyCancelled_ThrowsOperationCanceled()
    {
        var service = CreateService(new BlockUntilCancelledLlmProvider());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ProcessMessageAsync("Hello", enableStreaming: false, cts.Token));
    }

    [Fact]
    public async Task ProcessMessageAsync_WhenTokenCancelledMidRun_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        // Trip the cancel as soon as the provider call begins, mirroring an ESC
        // press while the agent loop is in-flight.
        var service = CreateService(new BlockUntilCancelledLlmProvider(onEnter: cts.Cancel));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ProcessMessageAsync("Hello", enableStreaming: false, cts.Token));
    }

    // Mirrors a real provider's reaction to a cancelled token: it blocks on the
    // token and surfaces OperationCanceledException when the token fires.
    private sealed class BlockUntilCancelledLlmProvider : ILlmProvider
    {
        private readonly Action? _onEnter;

        public BlockUntilCancelledLlmProvider(Action? onEnter = null)
        {
            _onEnter = onEnter;
        }

        public string Name => "stub";

        public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            _onEnter?.Invoke();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable: provider should be cancelled.");
        }

        public async IAsyncEnumerable<LlmStreamResponse> StreamCompleteAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<IEnumerable<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Enumerable.Empty<ModelInfo>());
    }
}
