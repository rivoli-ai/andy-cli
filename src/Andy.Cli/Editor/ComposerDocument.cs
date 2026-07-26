using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Andy.Cli.Editor;

/// <summary>
/// One piece of composer content. The composer is modelled as an ordered list of
/// parts rather than a flat string so that an external-editor round trip can hand
/// the user plain text and still put the non-text pieces back where they were.
/// </summary>
public abstract record ComposerPart;

/// <summary>Editable free text. This is the only part the external editor sees verbatim.</summary>
public sealed record ComposerTextPart(string Text) : ComposerPart;

/// <summary>
/// A structured, non-text part: an <c>@file</c> reference today, images later.
///
/// SEAM FOR #277 (structured @file prompt parts): the composer on main is still a
/// plain string, so <see cref="ComposerDocument.FromText"/> currently produces a
/// single <see cref="ComposerTextPart"/> and no attachments. When #277 lands, the
/// composer will hand this type its real parts (resolved path, mime type, byte
/// payload, ...) and the editor round trip below will preserve them: the editor
/// only ever sees <see cref="Placeholder"/>, and every surviving placeholder in the
/// edited text is mapped back to the ORIGINAL part instance, never re-parsed into
/// text. Nothing else in this file needs to change for #277.
/// </summary>
/// <param name="Placeholder">
/// The exact text shown to the user in the composer and written into the temp file
/// (for an @file part, "@src/Program.cs"). Must be non-empty and stable.
/// </param>
/// <param name="Kind">Discriminator such as "file" or "image"; opaque to this type.</param>
/// <param name="Reference">The resolved target (path, URI, attachment id) the part points at.</param>
/// <param name="Payload">Optional opaque payload carried through the round trip untouched.</param>
public sealed record ComposerAttachmentPart(
    string Placeholder,
    string Kind,
    string Reference,
    string? Payload = null) : ComposerPart;

/// <summary>
/// The composer's content as an ordered part list, plus the lossless round trip used
/// by the external editor (issue #287).
///
/// <para><b>Round trip.</b> <see cref="ToEditableText"/> renders the document to the
/// plain text written into the temporary file: text parts verbatim, attachments as
/// their placeholder. <see cref="ApplyEditedText"/> takes the text the user saved and
/// rebuilds a document by scanning for the known placeholders; each match re-emits the
/// original attachment record (identity, kind, reference and payload intact) instead of
/// flattening it into text. Placeholders the user deleted are dropped, placeholders the
/// user moved end up at their new position, and a placeholder duplicated by the user
/// re-uses the same attachment record.</para>
///
/// <para>The type is immutable; every operation returns a new document.</para>
/// </summary>
public sealed class ComposerDocument
{
    private readonly IReadOnlyList<ComposerPart> _parts;

    public ComposerDocument(IEnumerable<ComposerPart> parts)
    {
        if (parts is null) throw new ArgumentNullException(nameof(parts));
        _parts = parts.Where(p => p is not ComposerTextPart t || t.Text.Length > 0).ToArray();
    }

    /// <summary>An empty document (empty prompt).</summary>
    public static ComposerDocument Empty { get; } = new(Array.Empty<ComposerPart>());

    /// <summary>The ordered parts. Never contains an empty text part.</summary>
    public IReadOnlyList<ComposerPart> Parts => _parts;

    /// <summary>The structured (non-text) parts, in order.</summary>
    public IReadOnlyList<ComposerAttachmentPart> Attachments =>
        _parts.OfType<ComposerAttachmentPart>().ToArray();

    /// <summary>True when the document carries no content at all.</summary>
    public bool IsEmpty => _parts.Count == 0;

    /// <summary>
    /// Build a document from the flat composer string used on main today. Newlines are
    /// normalized to LF so the temp file, the composer and the comparisons all agree.
    /// </summary>
    public static ComposerDocument FromText(string? text)
    {
        string normalized = NormalizeNewlines(text ?? string.Empty);
        return normalized.Length == 0
            ? Empty
            : new ComposerDocument(new ComposerPart[] { new ComposerTextPart(normalized) });
    }

    /// <summary>
    /// The text handed to the external editor: text parts verbatim, attachments rendered
    /// as their placeholder.
    /// </summary>
    public string ToEditableText()
    {
        var sb = new StringBuilder();
        foreach (var part in _parts)
        {
            switch (part)
            {
                case ComposerTextPart t: sb.Append(t.Text); break;
                case ComposerAttachmentPart a: sb.Append(a.Placeholder); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// The string written back into the composer. Identical to <see cref="ToEditableText"/>
    /// while the composer is a plain string; kept separate so #277 can diverge the two
    /// (editor view vs. composer view) without touching the editor pipeline.
    /// </summary>
    public string ToPromptText() => ToEditableText();

    /// <summary>
    /// Rebuild the document from text the user saved in the external editor, restoring the
    /// structured parts wherever their placeholder still appears.
    /// </summary>
    public ComposerDocument ApplyEditedText(string? editedText)
    {
        string edited = NormalizeNewlines(editedText ?? string.Empty);

        var attachments = Attachments;
        if (attachments.Count == 0)
            return FromText(edited);

        // Pending queue per placeholder preserves original ordering when the same
        // placeholder occurs several times; the last seen record is reused if the user
        // duplicated a placeholder beyond its original count.
        var pending = new Dictionary<string, Queue<ComposerAttachmentPart>>(StringComparer.Ordinal);
        var template = new Dictionary<string, ComposerAttachmentPart>(StringComparer.Ordinal);
        foreach (var a in attachments)
        {
            if (string.IsNullOrEmpty(a.Placeholder)) continue;
            if (!pending.TryGetValue(a.Placeholder, out var q))
            {
                q = new Queue<ComposerAttachmentPart>();
                pending[a.Placeholder] = q;
            }
            q.Enqueue(a);
            template[a.Placeholder] = a;
        }

        // Longest first so "@src/a.cs" wins over a hypothetical "@src" placeholder.
        var placeholders = pending.Keys.OrderByDescending(p => p.Length).ThenBy(p => p, StringComparer.Ordinal).ToArray();

        var result = new List<ComposerPart>();
        var buffer = new StringBuilder();
        int i = 0;
        while (i < edited.Length)
        {
            string? hit = null;
            foreach (var p in placeholders)
            {
                if (i + p.Length <= edited.Length && string.CompareOrdinal(edited, i, p, 0, p.Length) == 0)
                {
                    hit = p;
                    break;
                }
            }

            if (hit is null)
            {
                buffer.Append(edited[i]);
                i++;
                continue;
            }

            if (buffer.Length > 0)
            {
                result.Add(new ComposerTextPart(buffer.ToString()));
                buffer.Clear();
            }

            var queue = pending[hit];
            result.Add(queue.Count > 0 ? queue.Dequeue() : template[hit]);
            i += hit.Length;
        }

        if (buffer.Length > 0) result.Add(new ComposerTextPart(buffer.ToString()));
        return new ComposerDocument(result);
    }

    /// <summary>Normalize CRLF and lone CR to LF (the composer's internal convention).</summary>
    internal static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');
}
