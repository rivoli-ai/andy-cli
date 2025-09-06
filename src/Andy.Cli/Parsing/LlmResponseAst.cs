using System;
using System.Collections.Generic;
using System.Linq;

namespace Andy.Cli.Parsing;

/// <summary>
/// Abstract Syntax Tree (AST) representation for LLM responses
/// Provides a formal, structured view of model outputs
/// </summary>
public abstract class AstNode
{
    public string NodeType { get; protected set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
    public List<AstNode> Children { get; set; } = new();
    public AstNode? Parent { get; set; }
    public int StartPosition { get; set; }
    public int EndPosition { get; set; }
    
    protected AstNode(string nodeType)
    {
        NodeType = nodeType;
    }
    
    public abstract T Accept<T>(IAstVisitor<T> visitor);
}

/// <summary>
/// Root node representing the entire LLM response
/// </summary>
public class ResponseNode : AstNode
{
    public string ModelProvider { get; set; } = "";
    public string ModelName { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public ResponseMetadata ResponseMetadata { get; set; } = new();
    
    public ResponseNode() : base("Response") { }
    
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitResponse(this);
}

/// <summary>
/// Text content segment with optional formatting
/// </summary>
public class TextNode : AstNode
{
    public string Content { get; set; } = "";
    public TextFormat Format { get; set; } = TextFormat.Plain;
    public string? Language { get; set; } // For code blocks
    
    public TextNode() : base("Text") { }
    
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitText(this);
}

/// <summary>
/// Tool call request from the model
/// </summary>
public class ToolCallNode : AstNode
{
    public string CallId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public Dictionary<string, object?> Arguments { get; set; } = new();
    public bool IsComplete { get; set; } = true;
    
    public ToolCallNode() : base("ToolCall") { }
    
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitToolCall(this);
}

/// <summary>
/// Code block with language and optional execution context
/// </summary>
public class CodeNode : AstNode
{
    public string Language { get; set; } = "";
    public string Code { get; set; } = "";
    public bool IsExecutable { get; set; }
    public string? FileName { get; set; }
    public int? LineNumber { get; set; }
    
    public CodeNode() : base("Code") { }
    
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitCode(this);
}

/// <summary>
/// File reference or path mentioned in the response
/// </summary>
public class FileReferenceNode : AstNode
{
    public string Path { get; set; } = "";
    public FileReferenceType ReferenceType { get; set; }
    public bool IsAbsolute { get; set; }
    public string? LineReference { get; set; } // e.g., ":42" or ":10-20"
    
    public FileReferenceNode() : base("FileReference") { }
    
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitFileReference(this);
}

/// <summary>
/// Question or prompt from the model requiring user input
/// </summary>
public class QuestionNode : AstNode
{
    public string Question { get; set; } = "";
    public QuestionType Type { get; set; }
    public List<string>? SuggestedOptions { get; set; }
    
    public QuestionNode() : base("Question") { }
    
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitQuestion(this);
}

/// <summary>
/// Internal reasoning or thought process (should be hidden from user)
/// </summary>
public class ThoughtNode : AstNode
{
    public string Content { get; set; } = "";
    public bool ShouldHide { get; set; } = true;
    
    public ThoughtNode() : base("Thought") { }
    
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitThought(this);
}

/// <summary>
/// Markdown structure with proper hierarchy
/// </summary>
public class MarkdownNode : AstNode
{
    public MarkdownElement Element { get; set; }
    public int Level { get; set; } // For headers
    public string? ListMarker { get; set; } // For lists
    
    public MarkdownNode() : base("Markdown") { }
    
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitMarkdown(this);
}

/// <summary>
/// Error or warning in the response
/// </summary>
public class ErrorNode : AstNode
{
    public string Message { get; set; } = "";
    public ErrorSeverity Severity { get; set; }
    public string? ErrorCode { get; set; }
    
    public ErrorNode() : base("Error") { }
    
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitError(this);
}

/// <summary>
/// Command or instruction to be executed
/// </summary>
public class CommandNode : AstNode
{
    public string Command { get; set; } = "";
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string>? Environment { get; set; }
    
    public CommandNode() : base("Command") { }
    
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitCommand(this);
}

// Enums for node properties
public enum TextFormat
{
    Plain,
    Markdown,
    Html,
    Latex,
    Json,
    Xml
}

public enum FileReferenceType
{
    Read,
    Write,
    Create,
    Delete,
    Modify,
    Navigate,
    Mention
}

public enum QuestionType
{
    YesNo,
    MultipleChoice,
    OpenEnded,
    Confirmation,
    Clarification
}

public enum MarkdownElement
{
    Heading,
    Paragraph,
    List,
    ListItem,
    BlockQuote,
    Table,
    Link,
    Image,
    HorizontalRule,
    Emphasis,
    Strong
}

public enum ErrorSeverity
{
    Info,
    Warning,
    Error,
    Critical
}

/// <summary>
/// Visitor pattern for traversing and processing the AST
/// </summary>
public interface IAstVisitor<T>
{
    T VisitResponse(ResponseNode node);
    T VisitText(TextNode node);
    T VisitToolCall(ToolCallNode node);
    T VisitCode(CodeNode node);
    T VisitFileReference(FileReferenceNode node);
    T VisitQuestion(QuestionNode node);
    T VisitThought(ThoughtNode node);
    T VisitMarkdown(MarkdownNode node);
    T VisitError(ErrorNode node);
    T VisitCommand(CommandNode node);
}

/// <summary>
/// Response metadata for tracking parsing and model state
/// </summary>
public class ResponseMetadata
{
    public bool IsComplete { get; set; }
    public string? FinishReason { get; set; }
    public int TokenCount { get; set; }
    public TimeSpan ParseTime { get; set; }
    public List<string> Warnings { get; set; } = new();
    public Dictionary<string, object> ModelSpecific { get; set; } = new();
}