using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Editor;

/// <summary>
/// Owns "what does the editor start from, and what does the composer end up holding" for
/// both entry points of issue #287, so the key binding and the slash command cannot drift.
///
/// <para>The two entry points differ only in WHEN the composer is read:</para>
/// <list type="bullet">
///   <item><description><see cref="FromComposerAsync"/> (Ctrl+X): the composer still holds the
///     user's text, so it is read live and only overwritten on a successful edit.</description></item>
///   <item><description><see cref="FromSubmittedCommandAsync"/> (/editor): pressing Enter has
///     already handed the text to the dispatcher and cleared the composer, so the host passes
///     the snapshot it took BEFORE that keystroke. The leading <c>/editor</c> token is stripped
///     and the rest becomes the editor's starting content. Because the composer is empty by
///     then, this path always writes back: the edited document on success, and the pre-submit
///     content on every failure path so a failed /editor never eats what the user typed.</description></item>
/// </list>
/// </summary>
public sealed class ExternalEditorInvoker
{
    private readonly ExternalEditorService _service;
    private readonly IComposerDocumentSource _composer;

    public ExternalEditorInvoker(ExternalEditorService service, IComposerDocumentSource composer)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
    }

    /// <summary>
    /// Key-binding path. Starts from the live composer; the composer is replaced only after a
    /// successful edit and is left untouched on every failure path.
    /// </summary>
    public async Task<ExternalEditorResult> FromComposerAsync(CancellationToken cancellationToken = default)
    {
        var result = await _service.EditAsync(_composer.GetDocument(), cancellationToken).ConfigureAwait(false);
        if (result.Applied) _composer.SetDocument(result.Document);
        return result;
    }

    /// <summary>
    /// Slash-command path. <paramref name="preSubmitDocument"/> is the composer as it was
    /// immediately before the submitting keystroke cleared it; the leading slash command is
    /// removed and the remainder seeds the editor.
    /// </summary>
    public async Task<ExternalEditorResult> FromSubmittedCommandAsync(
        ComposerDocument? preSubmitDocument,
        CancellationToken cancellationToken = default)
    {
        var seed = StripLeadingSlashCommand(preSubmitDocument ?? ComposerDocument.Empty);
        var result = await _service.EditAsync(seed, cancellationToken).ConfigureAwait(false);

        // Submission already emptied the composer, so this path must write back on EVERY
        // outcome. Writing the seed (not the pre-submit document) is what keeps the command
        // token from being re-inserted, and writing exactly one document is what keeps the
        // edited text from being appended to the text it replaced.
        _composer.SetDocument(result.Applied ? result.Document : seed);
        return result;
    }

    /// <summary>
    /// Remove a leading <c>/command</c> token, plus the spaces/tabs and at most one newline
    /// that follow it, and return what the command should operate on.
    ///
    /// <para>The token is only ever recognized inside the document's FIRST text part, so a
    /// structured part can never be consumed by the scan: <c>/editor @src/a.cs review</c>
    /// yields a document that still holds the original <c>@src/a.cs</c> attachment record.
    /// A document that does not begin with a slash, or that begins with a structured part, is
    /// returned unchanged.</para>
    /// </summary>
    public static ComposerDocument StripLeadingSlashCommand(ComposerDocument document)
    {
        if (document is null || document.Parts.Count == 0) return ComposerDocument.Empty;
        if (document.Parts[0] is not ComposerTextPart first) return document;

        string text = first.Text;
        if (text.Length == 0 || text[0] != '/') return document;

        int i = 1;
        while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        if (i < text.Length && text[i] == '\n') i++;

        return document.RemoveLeadingCharacters(i);
    }
}
