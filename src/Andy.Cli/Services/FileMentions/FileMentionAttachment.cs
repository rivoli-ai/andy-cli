using System;

namespace Andy.Cli.Services.FileMentions;

/// <summary>Outcome of resolving a single <c>@</c> mention.</summary>
public enum FileMentionStatus
{
    /// <summary>Content was read and is included in the prompt.</summary>
    Attached,

    /// <summary>No such file exists under the workspace root.</summary>
    Missing,

    /// <summary>The path resolves outside the workspace root and was refused.</summary>
    OutsideWorkspace,

    /// <summary>The path is excluded by .gitignore or the built-in skip list.</summary>
    Ignored,

    /// <summary>The file is not text and was not attached.</summary>
    Binary,

    /// <summary>The file exceeds the per-file size budget.</summary>
    TooLarge,

    /// <summary>The path is a directory; directories are not attached as content.</summary>
    Directory,

    /// <summary>The requested line range starts past the end of the file.</summary>
    RangeOutOfBounds,

    /// <summary>The file exists but could not be read (permissions, I/O error).</summary>
    Unreadable,

    /// <summary>The prompt's overall attachment budget was already used up.</summary>
    BudgetExceeded,

    /// <summary>The same path and range was already attached earlier in the prompt.</summary>
    Duplicate
}

/// <summary>
/// A single resolved <c>@</c> mention: where it came from, what happened, and (when successful)
/// the text that will be sent to the model.
/// </summary>
public sealed class FileMentionAttachment
{
    public FileMentionAttachment(
        string mentionText,
        string requestedPath,
        FileMentionStatus status,
        string? relativePath = null,
        string? absolutePath = null,
        LineRange? range = null,
        string? content = null,
        string? note = null)
    {
        MentionText = mentionText;
        RequestedPath = requestedPath;
        Status = status;
        RelativePath = relativePath;
        AbsolutePath = absolutePath;
        Range = range;
        Content = content;
        Note = note;
    }

    /// <summary>The mention exactly as it appeared in the prompt, including the leading <c>@</c>.</summary>
    public string MentionText { get; }

    /// <summary>The path portion of the mention, before resolution.</summary>
    public string RequestedPath { get; }

    /// <summary>Resolution outcome.</summary>
    public FileMentionStatus Status { get; }

    /// <summary>Workspace-relative path (forward slashes) when the mention resolved to a real entry.</summary>
    public string? RelativePath { get; }

    /// <summary>Absolute path when the mention resolved to a real entry.</summary>
    public string? AbsolutePath { get; }

    /// <summary>One-based inclusive line range, clamped to the file, when the mention requested one.</summary>
    public LineRange? Range { get; }

    /// <summary>File text that will be sent to the model. Null unless <see cref="Status"/> is Attached.</summary>
    public string? Content { get; }

    /// <summary>Human-readable explanation for a non-attached outcome.</summary>
    public string? Note { get; }

    /// <summary>True when content was read and will be sent.</summary>
    public bool IsAttached => Status == FileMentionStatus.Attached;

    /// <summary>Path shown to the user: the workspace-relative path when known, else what they typed.</summary>
    public string DisplayPath => RelativePath ?? RequestedPath;
}
