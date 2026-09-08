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
/// Adapts the interactive prompt's structured document to the external editor.
/// Attachment records and exact paste payloads survive the round trip.
/// </summary>
public sealed class PromptLineComposer : IComposerDocumentSource
{
    private readonly PromptLine _prompt;

    public PromptLineComposer(PromptLine prompt)
        => _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));

    public ComposerDocument GetDocument() => _prompt.GetDocument();

    public void SetDocument(ComposerDocument document)
        => _prompt.SetDocument(document ?? ComposerDocument.Empty);
}
