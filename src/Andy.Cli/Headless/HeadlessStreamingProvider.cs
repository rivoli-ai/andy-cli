using System.Runtime.CompilerServices;
using Andy.Model.Llm;

namespace Andy.Cli.Headless;

/// <summary>Lets Engine use its honest completion fallback for providers with an empty streaming path.</summary>
internal sealed class HeadlessStreamingProvider(ILlmProvider inner) : ILlmProvider
{
    public string Name => inner.Name;
    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        => inner.CompleteAsync(request, cancellationToken);
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        => inner.IsAvailableAsync(cancellationToken);
    public Task<IEnumerable<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
        => inner.ListModelsAsync(cancellationToken);

    public async IAsyncEnumerable<LlmStreamResponse> StreamCompleteAsync(
        LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var received = false;
        await foreach (var chunk in inner.StreamCompleteAsync(request, cancellationToken))
        {
            received = true;
            yield return chunk;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!received)
            throw new NotSupportedException("The provider returned no streaming packets.");
    }
}
