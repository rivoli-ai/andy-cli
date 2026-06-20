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
        private readonly Action<string>? _onIntermediateText;
        private readonly ILogger? _logger;

        public UsageTrackingLlmProvider(ILlmProvider inner, Action<LlmUsage> onUsage, ILogger? logger = null)
            : this(inner, onUsage, onIntermediateText: null, logger)
        {
        }

        /// <param name="onIntermediateText">
        /// Optional callback invoked with the assistant's narration text for any round-trip that
        /// ALSO issues tool calls - i.e. text the model emitted before/between tool executions while
        /// the turn is still in progress. The engine (SimpleAgent) is non-streaming and only returns
        /// the FINAL answer from ProcessMessageAsync, but it calls this provider once per round-trip,
        /// so this decorator is the only place the CLI can observe that intermediate text live.
        /// Responses with NO tool calls are the final answer and are intentionally skipped here;
        /// they reach the feed through the normal end-of-turn render path, so this avoids showing the
        /// final answer twice. Never fires for empty/whitespace content.
        /// </param>
        public UsageTrackingLlmProvider(
            ILlmProvider inner,
            Action<LlmUsage> onUsage,
            Action<string>? onIntermediateText,
            ILogger? logger = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _onUsage = onUsage ?? throw new ArgumentNullException(nameof(onUsage));
            _onIntermediateText = onIntermediateText;
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
            ReportIntermediateText(response);
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

        // Surface narration text for round-trips that ALSO issue tool calls (intermediate steps).
        // A response with no tool calls is the final answer and is rendered by the end-of-turn path,
        // so skipping it here prevents showing the final answer twice. Like usage reporting, this is
        // a display side effect and must never disrupt the LLM call.
        private void ReportIntermediateText(LlmResponse response)
        {
            if (_onIntermediateText == null)
                return;
            if (!response.HasToolCalls)
                return;
            var text = response.Content;
            if (string.IsNullOrWhiteSpace(text))
                return;

            try
            {
                _onIntermediateText(text);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Intermediate-text callback threw; ignoring");
            }
        }
    }
}
