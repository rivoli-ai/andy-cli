namespace Andy.Cli.Services.ContentPipeline;

/// <summary>
/// Base interface for all content blocks that can be processed and rendered
/// </summary>
public interface IContentBlock
{
    /// <summary>Unique identifier for this block</summary>
    string Id { get; }
    
    /// <summary>Priority for rendering order (lower = rendered first)</summary>
    int Priority { get; }
    
    /// <summary>Whether this block is complete and ready for rendering</summary>
    bool IsComplete { get; }
}

/// <summary>
/// A text content block
/// </summary>
public class TextBlock : IContentBlock
{
    public string Id { get; }
    public int Priority { get; }
    public bool IsComplete { get; set; }
    public string Content { get; set; }
    
    public TextBlock(string id, string content, int priority = 100)
    {
        Id = id;
        Content = content;
        Priority = priority;
        IsComplete = true;
    }
}

/// <summary>
/// A code block
/// </summary>
public class CodeBlock : IContentBlock
{
    public string Id { get; }
    public int Priority { get; }
    public bool IsComplete { get; set; }
    public string Code { get; set; }
    public string? Language { get; set; }
    
    public CodeBlock(string id, string code, string? language = null, int priority = 100)
    {
        Id = id;
        Code = code;
        Language = language;
        Priority = priority;
        IsComplete = true;
    }
}

/// <summary>
/// A system message block (like context info)
/// </summary>
public class SystemMessageBlock : IContentBlock
{
    public string Id { get; }
    public int Priority { get; }
    public bool IsComplete { get; set; }
    public string Message { get; set; }
    public SystemMessageType Type { get; set; }
    
    public SystemMessageBlock(string id, string message, SystemMessageType type, int priority = 1000)
    {
        Id = id;
        Message = message;
        Type = type;
        Priority = priority;
        IsComplete = true;
    }
}

public enum SystemMessageType
{
    Context,
    Success,
    Error,
    Warning,
    Info
}