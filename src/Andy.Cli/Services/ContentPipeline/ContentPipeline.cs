using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services.ContentPipeline;

/// <summary>
/// A streaming content processing pipeline that ensures clean separation of concerns
/// </summary>
public class ContentPipeline : IDisposable
{
    private readonly IContentProcessor _processor;
    private readonly IContentSanitizer _sanitizer;
    private readonly IContentRenderer _renderer;
    private readonly ILogger<ContentPipeline>? _logger;
    
    private readonly ConcurrentQueue<IContentBlock> _processingQueue = new();
    private readonly ConcurrentDictionary<string, IContentBlock> _completedBlocks = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task _processingTask;
    
    private volatile bool _isFinalized = false;

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
        
        // Start the background processing task
        _processingTask = Task.Run(ProcessQueueAsync, _cancellationTokenSource.Token);
    }

    /// <summary>
    /// Add raw content to be processed
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
            var blocks = _processor.Process(content, blockIdPrefix);
            _logger?.LogInformation("[PIPELINE] Processor returned {Count} blocks", blocks.Count());
            foreach (var block in blocks)
            {
                // Set priority if not default
                if (priority != 100 && block is TextBlock textBlock)
                {
                    var newBlock = new TextBlock(textBlock.Id, textBlock.Content, priority)
                    {
                        IsComplete = textBlock.IsComplete
                    };
                    _processingQueue.Enqueue(newBlock);
                }
                else if (priority != 100 && block is SystemMessageBlock systemBlock)
                {
                    var newBlock = new SystemMessageBlock(systemBlock.Id, systemBlock.Message, systemBlock.Type, priority)
                    {
                        IsComplete = systemBlock.IsComplete
                    };
                    _processingQueue.Enqueue(newBlock);
                }
                else
                {
                    _processingQueue.Enqueue(block);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing raw content");
            ErrorPolicy.RethrowIfStrict(ex);
        }
    }

    /// <summary>
    /// Add a pre-formed content block
    /// </summary>
    public void AddBlock(IContentBlock block)
    {
        if (_isFinalized)
        {
            _logger?.LogWarning("Cannot add block to finalized pipeline");
            return;
        }

        _processingQueue.Enqueue(block);
    }

    /// <summary>
    /// Add system message (like context, success, error)
    /// </summary>
    public void AddSystemMessage(string message, SystemMessageType type, int priority = 1000)
    {
        var blockId = $"system_{type}_{DateTime.UtcNow.Ticks}";
        var block = new SystemMessageBlock(blockId, message, type, priority);
        AddBlock(block);
    }

    /// <summary>
    /// Finalize the pipeline - no more content will be accepted, render remaining blocks
    /// </summary>
    public async Task FinalizeAsync()
    {
        _logger?.LogInformation("[PIPELINE-FINALIZE] Starting finalization");
        _isFinalized = true;
        
        // Wait a bit for any remaining processing
        await Task.Delay(100);
        
        // Force render all remaining blocks
        _logger?.LogInformation("[PIPELINE-FINALIZE] Rendering all pending blocks");
        await RenderAllPendingBlocks();
        _logger?.LogInformation("[PIPELINE-FINALIZE] Finalization complete");
    }

    private async Task ProcessQueueAsync()
    {
        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                if (_processingQueue.TryDequeue(out var block))
                {
                    // Sanitize the block
                    _logger?.LogInformation("[PIPELINE-PROCESS] Sanitizing block {Id} of type {Type}", block.Id, block.GetType().Name);
                    var sanitizedBlock = _sanitizer.Sanitize(block);
                    _logger?.LogInformation("[PIPELINE-PROCESS] Block {Id} after sanitization: IsComplete={Complete}", 
                        sanitizedBlock.Id, sanitizedBlock.IsComplete);
                    
                    // Store completed blocks
                    _completedBlocks.TryAdd(sanitizedBlock.Id, sanitizedBlock);
                    
                    // Render immediately if ready, or wait for finalization for proper ordering
                    if (!_isFinalized && sanitizedBlock.IsComplete && sanitizedBlock.Priority < 500)
                    {
                        // Render high-priority blocks immediately (content)
                        _logger?.LogInformation("[PIPELINE-PROCESS] Rendering block {Id} immediately (priority={Priority})", 
                            sanitizedBlock.Id, sanitizedBlock.Priority);
                        _renderer.Render(sanitizedBlock);
                        // Remove from completed blocks since we've already rendered it
                        _completedBlocks.TryRemove(sanitizedBlock.Id, out _);
                        _logger?.LogInformation("[PIPELINE-PROCESS] Removed block {Id} from completed blocks after immediate render", 
                            sanitizedBlock.Id);
                    }
                    else
                    {
                        _logger?.LogInformation("[PIPELINE-PROCESS] Block {Id} stored for later (finalized={Fin}, complete={Com}, priority={Pri})", 
                            sanitizedBlock.Id, _isFinalized, sanitizedBlock.IsComplete, sanitizedBlock.Priority);
                    }
                }
                else
                {
                    // No work available, check if we should render pending blocks
                    if (_isFinalized)
                    {
                        break;
                    }
                    
                    // Short delay before checking again
                    await Task.Delay(10, _cancellationTokenSource.Token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in processing queue");
                ErrorPolicy.RethrowIfStrict(ex);
            }
        }
    }

    private async Task RenderAllPendingBlocks()
    {
        // Get all completed blocks and sort by priority
        var blocksToRender = _completedBlocks.Values
            .Where(b => b.IsComplete)
            .OrderBy(b => b.Priority)
            .ThenBy(b => b.Id)
            .ToList();

        _logger?.LogInformation("[PIPELINE-RENDER] Found {Count} blocks to render", blocksToRender.Count);

        foreach (var block in blocksToRender)
        {
            try
            {
                _logger?.LogInformation("[PIPELINE-RENDER] Rendering block {Id} of type {Type}, priority {Priority}", 
                    block.Id, block.GetType().Name, block.Priority);
                _renderer.Render(block);
                // Remove the block after rendering to prevent duplicate rendering
                _completedBlocks.TryRemove(block.Id, out _);
                _logger?.LogInformation("[PIPELINE-RENDER] Successfully rendered and removed block {Id}", block.Id);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error rendering block {BlockId}", block.Id);
                ErrorPolicy.RethrowIfStrict(ex);
            }
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        try
        {
            _processingTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error waiting for processing task to complete");
            // Rethrow only if strict to avoid dispose-time crashes in non-strict mode
            ErrorPolicy.RethrowIfStrict(ex);
        }
        
        _cancellationTokenSource.Dispose();
    }
}