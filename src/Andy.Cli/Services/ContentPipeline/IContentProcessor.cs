using System.Collections.Generic;

namespace Andy.Cli.Services.ContentPipeline;

/// <summary>
/// Interface for processing raw content into clean content blocks
/// </summary>
public interface IContentProcessor
{
    /// <summary>
    /// Process raw text into content blocks
    /// </summary>
    IEnumerable<IContentBlock> Process(string rawContent, string blockIdPrefix = "");
}

/// <summary>
/// Interface for sanitizing and transforming content blocks
/// </summary>
public interface IContentSanitizer
{
    /// <summary>
    /// Sanitize and transform a content block
    /// </summary>
    IContentBlock Sanitize(IContentBlock block);
}

/// <summary>
/// Interface for rendering content blocks to the feed
/// </summary>
public interface IContentRenderer
{
    /// <summary>
    /// Render a content block to the feed
    /// </summary>
    void Render(IContentBlock block);
}