using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services.ContentPipeline;

/// <summary>
/// A streaming content processing pipeline that ensures clean separation of concerns.
///
/// Ordering and delivery guarantees:
/// - A single background consumer performs ALL rendering, so every block is rendered
///   exactly once. Producers only enqueue; they never render.
/// - Blocks below <see cref="DeferThreshold"/> (regular content) are rendered as they
///   arrive, preserving arrival order.
/// - Blocks at or above <see cref="DeferThreshold"/> (system messages) are buffered and
///   flushed on drain in a stable priority order (arrival order within a priority tier).
/// - Finalization is deterministic: it completes the channel and awaits an explicit
///   completion signal, so it returns exactly when the consumer has drained everything.
///   There is no arbitrary timing/delay.
/// </summary>
public class ContentPipeline : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Priority boundary between "render immediately as it streams in" (below the
    /// threshold) and "defer and flush in priority order on drain" (at/above).
    /// </summary>
    public const int DeferThreshold = 500;

    private readonly IContentProcessor _processor;
    private readonly IContentSanitizer _sanitizer;
    private readonly IContentRenderer _renderer;
    private readonly ILogger<ContentPipeline>? _logger;

    private readonly Channel<IContentBlock> _channel;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task _processingTask;

    // Signalled exactly once when the background consumer loop has fully drained/stopped.
    private readonly TaskCompletionSource<bool> _processingCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Deferred (high-priority) blocks, only ever touched by the single consumer thread.
    private readonly List<IContentBlock> _deferred = new();

    private volatile bool _isFinalized;
    private int _disposed;

    public ContentPipeline(
        IContentProcessor processor,
        IContentSanitizer sanitizer,
        IContentRenderer renderer,
        ILogger<ContentPipeline>? logger = null)
    {
        _processor = processor;
        _sanitizer = sanitizer;
        _renderer = renderer;
        _logger = logger;

        // Unbounded, multiple concurrent producers, single consumer.
        _channel = Channel.CreateUnbounded<IContentBlock>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _processingTask = Task.Run(ProcessQueueAsync);
    }

    /// <summary>
    /// Add raw content to be processed.
    /// </summary>
    public void AddRawContent(string content, string blockIdPrefix = "", int priority = 100)
    {
        _logger?.LogInformation("[PIPELINE] AddRawContent: length={Length}, priority={Priority}", content?.Length ?? 0, priority);

        if (_isFinalized)
        {
            _logger?.LogWarning("Cannot add content to finalized pipeline");
            return;
        }

        try
        {
            var blocks = _processor.Process(content!, blockIdPrefix);
            foreach (var block in blocks)
            {
                // Apply an explicit priority override if one was requested.
                var toEnqueue = priority != 100 ? WithPriority(block, priority) : block;
                Enqueue(toEnqueue);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing raw content");
            ErrorPolicy.RethrowIfStrict(ex);
        }
    }

    /// <summary>
    /// Add a pre-formed content block.
    /// </summary>
    public void AddBlock(IContentBlock block)
    {
        if (_isFinalized)
        {
            _logger?.LogWarning("Cannot add block to finalized pipeline");
            return;
        }

        Enqueue(block);
    }

    /// <summary>
    /// Add system message (like context, success, error).
    /// </summary>
    public void AddSystemMessage(string message, SystemMessageType type, int priority = 1000)
    {
        var blockId = $"system_{type}_{DateTime.UtcNow.Ticks}";
        var block = new SystemMessageBlock(blockId, message, type, priority);
        AddBlock(block);
    }

    private void Enqueue(IContentBlock block)
    {
        // TryWrite returns false only once the writer has been completed (finalized).
        // Racing a concurrent finalize is expected; drop-with-log rather than throwing so a
        // late producer can never crash or double-deliver.
        if (!_channel.Writer.TryWrite(block))
        {
            _logger?.LogWarning("Dropped block {Id}; pipeline already finalized", block.Id);
        }
    }

    private static IContentBlock WithPriority(IContentBlock block, int priority)
    {
        return block switch
        {
            TextBlock t => new TextBlock(t.Id, t.Content, priority) { IsComplete = t.IsComplete },
            SystemMessageBlock s => new SystemMessageBlock(s.Id, s.Message, s.Type, priority) { IsComplete = s.IsComplete },
            CodeBlock c => new CodeBlock(c.Id, c.Code, c.Language, priority) { IsComplete = c.IsComplete },
            _ => block
        };
    }

    /// <summary>
    /// Finalize the pipeline: accept no more content, then deterministically wait for the
    /// background consumer to render everything that was enqueued (including deferred,
    /// priority-ordered blocks). Safe to call more than once.
    /// </summary>
    public async Task FinalizeAsync()
    {
        _logger?.LogInformation("[PIPELINE-FINALIZE] Starting finalization");
        _isFinalized = true;

        // Complete the writer so the consumer's ReadAllAsync loop terminates once drained.
        // TryComplete is idempotent (returns false if already completed).
        _channel.Writer.TryComplete();

        // Deterministic drain: return exactly when the consumer has finished.
        await _processingCompletion.Task.ConfigureAwait(false);
        _logger?.LogInformation("[PIPELINE-FINALIZE] Finalization complete");
    }

    private async Task ProcessQueueAsync()
    {
        // Capture the token once for the lifetime of the loop. Disposal never disposes the
        // CancellationTokenSource until this loop has completed (see Dispose/DisposeAsync), so
        // the token is never accessed after its source is disposed.
        var cancellationToken = _cancellationTokenSource.Token;
        try
        {
            var reader = _channel.Reader;
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var block))
                {
                    RenderOrDefer(block);
                }
            }

            // Channel completed and fully drained: flush any deferred (priority) blocks.
            FlushDeferred();
        }
        catch (OperationCanceledException)
        {
            // Cancellation (disposal) - stop promptly; finalization is not expected in this path.
        }
        catch (ObjectDisposedException)
        {
            // Benign shutdown race: the CancellationTokenSource was disposed as the loop wound
            // down. Treat like cancellation - stop quietly, do not error-log.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in processing queue");
            // Note: do not rethrow here. The exception would otherwise fault the background
            // task and be observed asynchronously (potentially at dispose time). Strict-mode
            // propagation for render/sanitize errors happens inside the renderer/sanitizer.
        }
        finally
        {
            _processingCompletion.TrySetResult(true);
        }
    }

    private void RenderOrDefer(IContentBlock block)
    {
        IContentBlock sanitized;
        try
        {
            sanitized = _sanitizer.Sanitize(block);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error sanitizing block {BlockId}", block.Id);
            ErrorPolicy.RethrowIfStrict(ex);
            return;
        }

        // Incomplete/empty blocks are never rendered.
        if (!sanitized.IsComplete)
        {
            return;
        }

        if (sanitized.Priority < DeferThreshold)
        {
            RenderOne(sanitized);
        }
        else
        {
            _deferred.Add(sanitized);
        }
    }

    private void FlushDeferred()
    {
        if (_deferred.Count == 0)
        {
            return;
        }

        // OrderBy is stable, so blocks with equal priority keep arrival order.
        foreach (var block in _deferred.OrderBy(b => b.Priority))
        {
            RenderOne(block);
        }

        _deferred.Clear();
    }

    private void RenderOne(IContentBlock block)
    {
        try
        {
            _renderer.Render(block);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error rendering block {BlockId}", block.Id);
            ErrorPolicy.RethrowIfStrict(ex);
        }
    }

    /// <summary>
    /// Asynchronous disposal: cancels the consumer and awaits its completion without
    /// blocking the calling (UI) thread.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
        _cancellationTokenSource.Cancel();

        try
        {
            await _processingCompletion.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error awaiting processing task during async disposal");
        }

        _cancellationTokenSource.Dispose();
    }

    /// <summary>
    /// Synchronous disposal: signals cancellation and releases resources without blocking on the
    /// background task. Non-throwing and non-error-logging by design.
    ///
    /// The CancellationTokenSource is disposed only AFTER the consumer loop has completed, never
    /// eagerly: a consumer parked in WaitToReadAsync(token) would otherwise observe a disposed
    /// source and throw ObjectDisposedException. We hand disposal to a continuation on the
    /// processing-completion signal so the calling (UI) thread is never blocked. Prefer
    /// <see cref="DisposeAsync"/> when an await is available.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();

        try
        {
            _cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed elsewhere - nothing to do.
        }

        // Dispose the CTS only once the consumer has stopped using its token.
        _processingCompletion.Task.ContinueWith(
            static (_, state) =>
            {
                try
                {
                    ((CancellationTokenSource)state!).Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed (e.g. by a concurrent DisposeAsync) - benign.
                }
            },
            _cancellationTokenSource,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
