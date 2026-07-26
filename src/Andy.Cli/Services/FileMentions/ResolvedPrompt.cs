using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Andy.Model.Model;

namespace Andy.Cli.Services.FileMentions;

/// <summary>
/// A prompt after its <c>@</c> mentions have been resolved: the text the user typed, the
/// attachments that were produced from it, and the structured parts handed to the agent.
/// </summary>
public sealed class ResolvedPrompt
{
    private readonly Lazy<IReadOnlyList<MessagePart>> _parts;
    private readonly Lazy<string> _composedText;

    public ResolvedPrompt(string originalText, IReadOnlyList<FileMentionAttachment> attachments)
    {
        OriginalText = originalText ?? string.Empty;
        Attachments = attachments ?? Array.Empty<FileMentionAttachment>();
        _parts = new Lazy<IReadOnlyList<MessagePart>>(BuildParts);
        _composedText = new Lazy<string>(() => string.Join(
            "\n\n",
            _parts.Value.OfType<TextPart>().Select(p => p.Text).Where(t => !string.IsNullOrEmpty(t))));
    }

    /// <summary>The prompt exactly as the user typed it, mentions included.</summary>
    public string OriginalText { get; }

    /// <summary>Every mention that was found, in document order, whether or not it attached.</summary>
    public IReadOnlyList<FileMentionAttachment> Attachments { get; }

    /// <summary>Attachments whose content was actually read.</summary>
    public IReadOnlyList<FileMentionAttachment> AttachedFiles =>
        Attachments.Where(a => a.IsAttached).ToList();

    /// <summary>Mentions that did not attach and therefore warrant a note to the user.</summary>
    public IReadOnlyList<FileMentionAttachment> Problems =>
        Attachments.Where(a => !a.IsAttached && a.Status != FileMentionStatus.Duplicate).ToList();

    /// <summary>True when at least one mention produced content.</summary>
    public bool HasAttachments => Attachments.Any(a => a.IsAttached);

    /// <summary>
    /// Structured message parts for the agent: the user's text followed by one part per attached
    /// file, each framed with its source path and (optional) line range.
    /// </summary>
    public IReadOnlyList<MessagePart> Parts => _parts.Value;

    /// <summary>
    /// The flattened equivalent of <see cref="Parts"/>, for call paths that only accept a string.
    /// </summary>
    public string ComposedText => _composedText.Value;

    private IReadOnlyList<MessagePart> BuildParts()
    {
        var parts = new List<MessagePart>();
        if (!string.IsNullOrEmpty(OriginalText))
        {
            parts.Add(new TextPart(OriginalText));
        }

        foreach (var attachment in Attachments)
        {
            string? block = RenderAttachment(attachment);
            if (block is not null)
            {
                parts.Add(new TextPart(block));
            }
        }

        if (parts.Count == 0)
        {
            parts.Add(new TextPart(string.Empty));
        }

        return parts;
    }

    /// <summary>
    /// Render one attachment as a self-describing block. Attached files carry their content;
    /// everything else becomes a short empty element so the model is told the file was requested
    /// but not supplied, instead of silently seeing nothing.
    /// </summary>
    internal static string? RenderAttachment(FileMentionAttachment attachment)
    {
        if (attachment.Status == FileMentionStatus.Duplicate)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.Append("<attached-file path=\"").Append(Escape(attachment.DisplayPath)).Append('"');
        if (attachment.Range is LineRange range)
        {
            sb.Append(" lines=\"").Append(range.Start).Append('-').Append(range.End).Append('"');
        }

        if (!attachment.IsAttached)
        {
            sb.Append(" status=\"").Append(StatusToken(attachment.Status)).Append('"');
            if (!string.IsNullOrEmpty(attachment.Note))
            {
                sb.Append(" note=\"").Append(Escape(attachment.Note)).Append('"');
            }
            sb.Append(" />");
            return sb.ToString();
        }

        sb.Append(">\n");
        sb.Append(attachment.Content);
        if (!string.IsNullOrEmpty(attachment.Content) && !attachment.Content!.EndsWith('\n'))
        {
            sb.Append('\n');
        }
        sb.Append("</attached-file>");
        return sb.ToString();
    }

    private static string StatusToken(FileMentionStatus status) => status switch
    {
        FileMentionStatus.Missing => "missing",
        FileMentionStatus.OutsideWorkspace => "outside-workspace",
        FileMentionStatus.Ignored => "ignored",
        FileMentionStatus.Binary => "binary",
        FileMentionStatus.TooLarge => "too-large",
        FileMentionStatus.Directory => "directory",
        FileMentionStatus.RangeOutOfBounds => "range-out-of-bounds",
        FileMentionStatus.Unreadable => "unreadable",
        FileMentionStatus.BudgetExceeded => "budget-exceeded",
        _ => "not-attached"
    };

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
