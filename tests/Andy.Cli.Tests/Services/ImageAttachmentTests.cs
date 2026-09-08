using Andy.Engine;
using System.Text;
using Andy.Cli.Domain;
using Andy.Cli.Input;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Model.Llm;
using Andy.Model.Model;
using Andy.Tools.Core;
using Moq;

namespace Andy.Cli.Tests.Services;

public class ImageAttachmentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly byte[] _png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
    public ImageAttachmentTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);
    private ImageAttachment Create()
    {
        var path = Path.Combine(_directory, "sample image.png");
        File.WriteAllBytes(path, _png);
        Assert.True(ImageAttachment.TryCreate(path, out var image, out var error), error);
        return image!;
    }

    [Fact]
    public void SnapshotSurvivesFileChangesAndQueueEditing()
    {
        var image = Create();
        File.WriteAllText(image.FilePath, "changed");
        Assert.Equal(_png, image.Data.ToArray());
        var indicator = new AttachmentIndicator();
        indicator.Show(image);
        var queue = new PendingMessageQueue();
        var pending = queue.Enqueue("describe", 1, indicator.Take());
        Assert.False(indicator.Visible);
        Assert.True(queue.TryUpdate(pending.Id, "compare", out _));
        Assert.True(queue.TryDequeue(out var next));
        Assert.Same(image, next.Image);
        Assert.Equal("compare", next.Text);
    }

    [Theory]
    [InlineData("wrong.jpg", false)]
    [InlineData("large.png", true)]
    public void RejectsMismatchedOrOversizeFiles(string name, bool large)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, _png);
        if (large) { using var file = File.OpenWrite(path); file.SetLength(ImageAttachment.MaxBytes + 1L); }
        Assert.False(ImageAttachment.TryCreate(path, out var image, out var error));
        Assert.Null(image);
        Assert.NotNull(error);
    }

    [Fact]
    public void ParsesQuotedEscapedAndUriPathsWithoutTreatingCommandsAsDrops()
    {
        var image = Create();
        foreach (var text in new[] { "'" + image.FilePath + "'", image.FilePath.Replace(" ", "\\ "), new Uri(image.FilePath).AbsoluteUri })
        {
            Assert.True(ImageDropPath.TryParse(text, out var parsed));
            Assert.Equal(image.FilePath, parsed);
        }
        Assert.False(ImageDropPath.TryParse("describe '" + image.FilePath + "'", out _));
        Assert.False(ImageDropPath.TryParse("file://remote/tmp/a.png", out _));
        Assert.False(ImageDropPath.TryParse("/tmp/a.png\n/tmp/b.png", out _));
    }

    [Fact]
    public void BracketedPasteAcrossEveryByteBoundaryIsOneTextEvent()
    {
        var parser = new TerminalInputParser();
        var events = new List<TerminalInputEvent>();
        const string text = "first\nsecond é\rthird";
        foreach (var b in Encoding.UTF8.GetBytes("\u001b[200~" + text + "\u001b[201~")) events.AddRange(parser.Feed(new[] { b }));
        var paste = Assert.Single(events);
        Assert.Equal(TerminalInputKind.Paste, paste.Kind);
        Assert.Equal(text, paste.PasteText);
        Assert.False(paste.PasteTruncated);
    }

    [Fact]
    public void OversizePasteTruncatesThenResumesKeyParsing()
    {
        var parser = new TerminalInputParser();
        var events = parser.Feed(Encoding.UTF8.GetBytes("\u001b[200~" + new string('x', TerminalInputParser.MaxPasteBytes + 100) + "\u001b[201~a"));
        Assert.Equal(2, events.Count);
        Assert.True(events[0].PasteTruncated);
        Assert.Equal(TerminalInputParser.MaxPasteBytes, events[0].PasteText!.Length);
        Assert.Equal('a', events[1].Key.KeyChar);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RealEngineReceivesImageOnlyWhenProviderSupportsVision(bool supportsVision)
    {
        var image = Create();
        var provider = new Mock<IVisionCapableLlmProvider>();
        provider.SetupGet(p => p.Name).Returns("provider-with-no-name-heuristic");
        provider.Setup(p => p.SupportsImageInputAsync(It.IsAny<CancellationToken>())).ReturnsAsync(supportsVision);
        LlmRequest? request = null;
        provider.Setup(p => p.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LlmRequest, CancellationToken>((r, _) => request = r)
            .ReturnsAsync(new LlmResponse { AssistantMessage = new Message { Role = Role.Assistant, Content = "response" } });
        var registry = new Mock<IToolRegistry>();
        registry.SetupGet(r => r.Tools).Returns(Array.Empty<ToolRegistration>());
        registry.Setup(r => r.GetTools()).Returns(Array.Empty<ToolRegistration>());
        using var service = new SimpleAssistantService(provider.Object, registry.Object, new Mock<IToolExecutor>().Object, new FeedView(), "model", "stub");
        Assert.Equal("response", await service.ProcessMessageAsync("Describe", imageAttachment: image));
        Assert.NotNull(request);
        var user = request.Messages.Last(m => m.Role == Role.User);
        if (supportsVision)
        {
            var part = Assert.Single((MultimodalMessage.GetAttachedParts(user) ?? user.Parts).OfType<ImagePart>());
            Assert.Equal(_png, part.ImageData);
            Assert.Equal("image/png", part.MimeType);
        }
        else
        {
            Assert.Empty((MultimodalMessage.GetAttachedParts(user) ?? user.Parts).OfType<ImagePart>());
            Assert.Contains((MultimodalMessage.GetAttachedParts(user) ?? user.Parts).OfType<TextPart>(), p => p.Text.Contains(image.FilePath));
        }
    }
}
