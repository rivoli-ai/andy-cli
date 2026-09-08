using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Editor;
using Andy.Cli.Services;
using Andy.Cli.Services.FileMentions;
using Andy.Cli.Widgets;
using Andy.Model.Model;
using Xunit;

namespace Andy.Cli.Tests.Editor;

public class PastedComposerTests
{
    private static string Payload => "\t code\r\n  @missing-file.cs\r\n" + new string('x', PromptLine.LargePasteBytes) + "\r\n\u03bb\t ";
    private static ConsoleKeyInfo Key(ConsoleKey key, bool control = false, bool shift = false)
        => new('\0', key, shift, false, control);

    [Fact]
    public void MultiplePastesKeepExactPayloadAndSurroundingEdits()
    {
        var prompt = new PromptLine();
        prompt.InsertText("before ");
        Assert.True(prompt.InsertPaste(Payload, false, out _));
        prompt.InsertText(" between ");
        Assert.True(prompt.InsertPaste(Payload + "second", false, out _));
        prompt.InsertText(" after");
        var document = prompt.GetDocument();
        Assert.Equal(2, document.Attachments.Count);
        Assert.NotEqual(document.Attachments[0].Reference, document.Attachments[1].Reference);
        Assert.DoesNotContain(new string('x', 100), prompt.Text);
        Assert.Equal("before " + Payload + " between " + Payload + "second after", document.ToSubmittedText());
        Assert.All(document.Attachments, part => Assert.Contains(part.Placeholder, prompt.Text));
    }

    [Theory]
    [InlineData(ConsoleKey.Backspace, false)]
    [InlineData(ConsoleKey.Delete, false)]
    [InlineData(ConsoleKey.U, true)]
    [InlineData(ConsoleKey.K, true)]
    public void OneDeletionRemovesWholePasteAndUndoRestoresIdentity(ConsoleKey key, bool control)
    {
        var prompt = new PromptLine();
        prompt.InsertPaste(Payload, false, out _);
        var paste = Assert.Single(prompt.GetDocument().Attachments);
        if (key is ConsoleKey.Delete or ConsoleKey.K) prompt.OnKey(Key(ConsoleKey.Home));
        prompt.OnKey(Key(key, control));
        Assert.Equal("", prompt.Text);
        Assert.Empty(prompt.GetDocument().Attachments);
        prompt.OnKey(Key(ConsoleKey.Z, true));
        Assert.Same(paste, Assert.Single(prompt.GetDocument().Attachments));
        Assert.Equal(Payload, prompt.GetDocument().ToSubmittedText());
        prompt.OnKey(Key(ConsoleKey.Z, true, true));
        Assert.True(prompt.GetDocument().IsEmpty);
    }

    [Fact]
    public void CursorAndPartialReplacementCannotSplitAnItem()
    {
        var prompt = new PromptLine();
        prompt.InsertText("before ");
        prompt.InsertPaste(Payload, false, out _);
        int end = prompt.CursorPosition;
        prompt.OnKey(Key(ConsoleKey.LeftArrow));
        Assert.Equal("before ".Length, prompt.CursorPosition);
        prompt.OnKey(Key(ConsoleKey.RightArrow));
        Assert.Equal(end, prompt.CursorPosition);
        prompt.ReplaceRange("before ".Length + 2, 1, "replacement");
        Assert.Equal("before replacement", prompt.Text);
        Assert.Empty(prompt.GetDocument().Attachments);
        prompt.UndoEdit();
        Assert.Equal("before " + Payload, prompt.GetDocument().ToSubmittedText());
        prompt.SetWrapWidth(12);
        prompt.OnKey(Key(ConsoleKey.UpArrow));
        Assert.Contains(prompt.CursorPosition, new[] { "before ".Length, end });
    }

    [Fact]
    public void SubmissionHistoryAndExternalEditorKeepOriginalPart()
    {
        var prompt = new PromptLine();
        prompt.InsertPaste(Payload, false, out _);
        var original = Assert.Single(prompt.GetDocument().Attachments);
        string label = prompt.Text;
        Assert.Equal(label, prompt.OnKey(Key(ConsoleKey.Enter)));
        Assert.True(prompt.GetDocument().IsEmpty);
        prompt.OnKey(Key(ConsoleKey.UpArrow, true));
        Assert.Same(original, Assert.Single(prompt.GetDocument().Attachments));
        var adapter = new PromptLineComposer(prompt);
        var edited = adapter.GetDocument().ApplyEditedText("before " + label + " after");
        adapter.SetDocument(edited);
        Assert.Same(original, Assert.Single(adapter.GetDocument().Attachments));
        Assert.Equal("before " + Payload + " after", adapter.GetDocument().ToSubmittedText());
        adapter.SetDocument(edited.ApplyEditedText("removed"));
        Assert.Empty(adapter.GetDocument().Attachments);
        prompt.UndoEdit();
        Assert.Same(original, Assert.Single(prompt.GetDocument().Attachments));
    }

    [Fact]
    public void RejectedPasteLeavesExistingDraftAndUndoHistoryUntouched()
    {
        var prompt = new PromptLine();
        prompt.InsertPaste(Payload, false, out _);
        var original = prompt.GetDocument();
        foreach (var (text, truncated) in new[] { ("incomplete", true), (new string('x', PromptLine.MaxPasteBytes + 1), false), ("binary\0", false) })
        {
            Assert.False(prompt.InsertPaste(text, truncated, out var error));
            Assert.NotNull(error);
            Assert.Same(original, prompt.GetDocument());
        }
        Assert.True(prompt.UndoEdit());
        Assert.True(prompt.GetDocument().IsEmpty);
    }

    [Fact]
    public void ThresholdAndShellModeAreExplicit()
    {
        var prompt = new PromptLine();
        prompt.InsertPaste("small\r\ntext", false, out _);
        Assert.Empty(prompt.GetDocument().Attachments);
        Assert.Equal("small\ntext", prompt.Text);
        prompt.SetText("");
        prompt.InsertPaste(new string('\n', PromptLine.LargePasteLines - 1), false, out _);
        Assert.Single(prompt.GetDocument().Attachments);
        prompt.SetText("");
        Assert.True(prompt.TryEnterShellMode());
        prompt.InsertPaste(new string('x', PromptLine.LargePasteBytes), false, out _);
        Assert.Empty(prompt.GetDocument().Attachments);
        Assert.Equal(PromptLine.LargePasteBytes, prompt.Text.Length);
    }

    [Fact]
    public async Task QueueRecallAndBoundaryDeliveryKeepPayloadWithoutResolvingPastedMentions()
    {
        var prompt = new PromptLine();
        prompt.InsertPaste(Payload, false, out _);
        var document = prompt.GetDocument();
        var queue = new PendingMessageQueue();
        var queued = queue.Enqueue(prompt.Text, 1, document: document);
        Assert.True(queue.TryUpdate(queued.Id, prompt.Text + " edited", out queued));
        Assert.Same(document.Attachments[0], queued.Document!.Attachments[0]);
        Assert.True(queue.TryRemove(queued.Id, out var recalled));
        prompt.SetDocument(recalled.Document!);
        prompt.InsertText(" follow-up");
        queued = queue.Enqueue(prompt.Text, 2, document: prompt.GetDocument());
        var resolver = new FileMentionSession();
        var delivery = new PendingMessageDelivery(queue.Drain, queue.RestoreFront, async (message, ct) =>
        {
            var resolved = await resolver.ResolveAsync(message.Document!, ct);
            Assert.Empty(resolved.Attachments);
            return resolved.Parts;
        }, _ => { });
        var batches = await delivery.TakeAsync(CancellationToken.None);
        Assert.Equal(Payload + " edited follow-up", Assert.IsType<TextPart>(Assert.Single(Assert.Single(batches))).Text);
        Assert.Empty(queue.Snapshot());
        Assert.Same(document.Attachments[0], queued.Document!.Attachments[0]);
    }
    [Fact]
    public void ChunkedTerminalPastePreservesUnicodeAndNeverSubmitsItsNewlines()
    {
        var parser = new Andy.Cli.Input.TerminalInputParser();
        var prompt = new PromptLine();
        var input = System.Text.Encoding.UTF8.GetBytes("\u001b[200~" + Payload + "\u001b[201~");
        var events = input.SelectMany(value => parser.Feed(new[] { value })).ToList();
        var paste = Assert.Single(events);
        Assert.Equal(Andy.Cli.Input.TerminalInputKind.Paste, paste.Kind);
        Assert.True(prompt.InsertPaste(paste.PasteText!, paste.PasteTruncated, out _));
        Assert.Equal(Payload, prompt.GetDocument().ToSubmittedText());
        Assert.Single(prompt.GetDocument().Attachments);
    }

    [Fact]
    public async Task CancelledBoundaryPreparationRestoresTheOriginalPasteDocument()
    {
        var prompt = new PromptLine();
        prompt.InsertPaste(Payload, false, out _);
        var document = prompt.GetDocument();
        var queue = new PendingMessageQueue();
        var queued = queue.Enqueue(prompt.Text, 1, document: document);
        using var cancellation = new CancellationTokenSource();
        var delivery = new PendingMessageDelivery(queue.Drain, queue.RestoreFront, (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult<System.Collections.Generic.IReadOnlyList<MessagePart>>(new MessagePart[] { new TextPart(Payload) });
        }, _ => Assert.Fail("Cancelled preparation must not accept the batch"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery.TakeAsync(cancellation.Token));
        Assert.Same(queued, Assert.Single(queue.Snapshot()));
        Assert.Same(document, queue.Snapshot()[0].Document);
    }
    [Fact]
    public void RenderShowsOnlyTheCompactLabelAndSurroundingText()
    {
        var prompt = new PromptLine();
        prompt.InsertText("Review ");
        prompt.InsertPaste(Payload, false, out _);
        prompt.InsertText(" please");
        var builder = new Andy.Tui.DisplayList.DisplayListBuilder();
        prompt.Render(new Andy.Tui.Layout.Rect(0, 0, 100, 5), builder.Build(), builder);
        var displayed = string.Concat(builder.Build().Ops.OfType<Andy.Tui.DisplayList.TextRun>().Select(run => run.Content));
        Assert.Contains("Review ", displayed);
        Assert.Contains(Assert.Single(prompt.GetDocument().Attachments).Placeholder, displayed);
        Assert.Contains(" please", displayed);
        Assert.DoesNotContain("@missing-file.cs", displayed);
        Assert.DoesNotContain(new string('x', 100), displayed);
        Assert.Equal("Review " + Payload + " please", prompt.GetDocument().ToSubmittedText());
    }
}
