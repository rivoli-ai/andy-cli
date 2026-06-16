using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Andy.Model.Llm;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services
{
    /// <summary>
    /// Transparent <see cref="ILlmProvider"/> decorator that reports the provider's REAL token
    /// usage to a callback on every completion. The engine calls the wrapped provider once per
    /// round-trip within an agent turn, so the callback fires live as the turn progresses — this
    /// is how the thinking row shows actual (not estimated) input/output tokens that move while the
    /// model is still working. Behaviour is otherwise identical to the inner provider: every call
    /// is delegated and the original response/chunk is returned unchanged.
    /// </summary>
    public sealed class UsageTrackingLlmProvider : ILlmProvider
    {
        private readonly ILlmProvider _inner;
        private readonly Action<LlmUsage> _onUsage;
        private readonly ILogger? _logger;

        public UsageTrackingLlmProvider(ILlmProvider inner, Action<LlmUsage> onUsage, ILogger? logger = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _onUsage = onUsage ?? throw new ArgumentNullException(nameof(onUsage));
            _logger = logger;
        }

        public string Name => _inner.Name;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
            => _inner.IsAvailableAsync(cancellationToken);

        public Task<IEnumerable<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
            => _inner.ListModelsAsync(cancellationToken);

        public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            var response = await _inner.CompleteAsync(request, cancellationToken);
            if (response.Usage != null)
                Report(response.Usage);
            return response;
        }

        public async IAsyncEnumerable<LlmStreamResponse> StreamCompleteAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var chunk in _inner.StreamCompleteAsync(request, cancellationToken))
            {
                if (chunk.Usage != null)
                    Report(chunk.Usage);
                yield return chunk;
            }
        }

        // The callback must never disrupt the LLM call: usage reporting is a display side effect,
        // so swallow (and log) any exception it throws rather than failing the turn.
        private void Report(LlmUsage usage)
        {
            try
            {
                _onUsage(usage);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Usage callback threw; ignoring");
            }
        }
    }
}
