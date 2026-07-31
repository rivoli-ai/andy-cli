using System;
using Andy.Cli.Widgets;

namespace Andy.Cli.Editor;

/// <summary>
/// The composer as seen by the external-editor round trip: something that can produce a
/// <see cref="ComposerDocument"/> and adopt an edited one.
/// </summary>
public interface IComposerDocumentSource
{
    /// <summary>Snapshot the composer's current content.</summary>
    ComposerDocument GetDocument();

    /// <summary>Replace the composer's content. Called only after a successful edit.</summary>
    void SetDocument(ComposerDocument document);
}

/// <summary>
/// Adapts <see cref="PromptLine"/> (the interactive composer) to <see cref="IComposerDocumentSource"/>.
///
/// <para>SEAM FOR #277: <see cref="PromptLine"/> on main stores a single string, so a document
/// built here has exactly one text part and no attachments - the round trip is lossless but
/// trivially so. When #277 gives the composer structured <c>@file</c> (and later image) parts,
/// this adapter is the ONLY place that has to change: build the document from those parts and
/// push them back in <see cref="SetDocument"/>. <see cref="ComposerDocument.ApplyEditedText"/>
/// already preserves attachment identity and position, and
/// <see cref="ExternalEditorService"/> never sees anything but a document.</para>
/// </summary>
public sealed class PromptLineComposer : IComposerDocumentSource
{
    private readonly PromptLine _prompt;

    public PromptLineComposer(PromptLine prompt)
        => _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));

    public ComposerDocument GetDocument() => ComposerDocument.FromText(_prompt.Text);

    public void SetDocument(ComposerDocument document)
        => _prompt.SetText((document ?? ComposerDocument.Empty).ToPromptText());
}
