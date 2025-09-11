using System;
using Andy.Cli.Widgets;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services.ContentPipeline;

/// <summary>
/// Renders content blocks to the FeedView widget
/// </summary>
public class FeedContentRenderer : IContentRenderer
{
    private readonly FeedView _feedView;
    private readonly ILogger<FeedContentRenderer>? _logger;

    public FeedContentRenderer(FeedView feedView, ILogger<FeedContentRenderer>? logger = null)
    {
        _feedView = feedView ?? throw new ArgumentNullException(nameof(feedView));
        _logger = logger;
    }

    public void Render(IContentBlock block)
    {
        try
        {
            switch (block)
            {
                case TextBlock textBlock:
                    RenderTextBlock(textBlock);
                    break;
                    
                case CodeBlock codeBlock:
                    RenderCodeBlock(codeBlock);
                    break;
                    
                case SystemMessageBlock systemBlock:
                    RenderSystemMessageBlock(systemBlock);
                    break;
                    
                default:
                    _logger?.LogWarning("Unknown block type: {BlockType}", block.GetType().Name);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error rendering block {BlockId}", block.Id);
            ErrorPolicy.RethrowIfStrict(ex);
        }
    }

    private void RenderTextBlock(TextBlock block)
    {
        if (string.IsNullOrWhiteSpace(block.Content))
            return;

        _feedView.AddMarkdownRich(block.Content);
        _logger?.LogTrace("Rendered text block {BlockId} with {Length} characters", 
            block.Id, block.Content.Length);
    }

    private void RenderCodeBlock(CodeBlock block)
    {
        if (string.IsNullOrWhiteSpace(block.Code))
            return;

        _feedView.AddCode(block.Code, block.Language);
        _logger?.LogTrace("Rendered code block {BlockId} with language {Language} and {Length} characters", 
            block.Id, block.Language ?? "none", block.Code.Length);
    }

    private void RenderSystemMessageBlock(SystemMessageBlock block)
    {
        if (string.IsNullOrWhiteSpace(block.Message))
            return;

        // All system messages use AddMarkdownRich for now
        // Future enhancement: could add different rendering based on type
        _feedView.AddMarkdownRich(block.Message);
        
        _logger?.LogTrace("Rendered system message block {BlockId} of type {Type}", 
            block.Id, block.Type);
    }
}