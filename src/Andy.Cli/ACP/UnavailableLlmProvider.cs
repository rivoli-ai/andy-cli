using System.Runtime.CompilerServices;
using Andy.Cli.Services;
using Andy.Model.Llm;

namespace Andy.Cli.ACP;

/// <summary>
/// Defers the missing-credential error until a prompt actually needs the model.
/// ACP initialization and session discovery do not call the model and must remain
/// usable by clients that launch the agent before configuring credentials.
/// </summary>
internal sealed class UnavailableLlmProvider : ILlmProvider
{
    private readonly string _message;

    public UnavailableLlmProvider(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        var descriptor = ProviderRegistry.Find(providerName);
        Name = descriptor?.Id ?? providerName;
        _message = descriptor == null
            ? $"LLM provider '{providerName}' is not configured."
            : $"LLM provider '{descriptor.Id}' is not configured. Set {descriptor.PrimaryApiKeyEnvVar} and restart the ACP server.";
    }

    public string Name { get; }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<IEnumerable<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Enumerable.Empty<ModelInfo>());

    public Task<LlmResponse> CompleteAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromException<LlmResponse>(new InvalidOperationException(_message));

    public async IAsyncEnumerable<LlmStreamResponse> StreamCompleteAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        yield break;
    }
}
