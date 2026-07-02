// Copyright (c) Rivoli AI 2026. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Domain;
using Andy.Cli.Input;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Engine;
using Andy.Model.Llm;
using Andy.Model.Model;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Andy.Cli.Tests.Integration;

/// <summary>
/// End-to-end integration tests for the image-attachment flow. They exercise the real production
/// pieces involved when a user drops an image onto the terminal:
/// <list type="bullet">
///   <item><see cref="TerminalInputParser"/> decodes iTerm2 / Kitty file-drop sequences into temp files.</item>
///   <item><see cref="ImageAttachment.TryCreate"/> validates the dropped path and produces an <see cref="ImageAttachment"/>.</item>
///   <item><see cref="AttachmentIndicator"/> accepts the validated attachment.</item>
///   <item><see cref="ImageAttachmentProcessor"/> enriches the user prompt: base64 for vision-capable providers, text reference for text-only providers.</item>
///   <item><see cref="SimpleAssistantService"/> forwards the enriched message to a mock LLM, preserving the attachment semantics through the agent conversation history.</item>
/// </list>
/// </summary>
public sealed class ImageAttachmentIntegrationTests : IDisposable
{
    private readonly string _dropDir;

    public ImageAttachmentIntegrationTests()
    {
        _dropDir = Path.Combine(Path.GetTempPath(), $"andy-attach-int-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dropDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dropDir))
        {
            try { Directory.Delete(_dropDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string RandomPath(string extension)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"andy-attach-int-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"test_{Guid.NewGuid():N}.{extension}");
    }

    private static byte[] SmallPngBytes()
    {
        // Minimal 1x1 PNG, opaque black. Good enough for extension/size validation and base64 tests.
        return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");
    }

    private static ImageAttachment CreateValidPngAttachment()
    {
        var path = RandomPath("png");
        File.WriteAllBytes(path, SmallPngBytes());
        var result = ImageAttachment.TryCreate(path);
        Assert.True(result.IsSuccess, result.IsFailure ? result.ErrorValue.Message : null);
        return result.SuccessValue;
    }

    private static SimpleAssistantService CreateAssistant(
        ILlmProvider provider,
        FeedView feed,
        string providerName,
        string modelName)
    {
        var registry = new ToolRegistry();
        var executor = new Mock<IToolExecutor>();
        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.None));
        return new SimpleAssistantService(
            provider,
            registry,
            executor.Object,
            feed,
            modelName,
            providerName,
            tokenCounter: null,
            loggerFactory: loggerFactory);
    }

    // ---------- TerminalInputParser -> ImageAttachment -> AttachmentIndicator ----------

    [Fact]
    public void FileDropEvent_ThroughTerminalInputParser_DecodesToValidImageAttachment_AndIndicatorAcceptsIt()
    {
        var fileName = "diagram.png";
        byte[] content = SmallPngBytes();
        string b64Name = Convert.ToBase64String(Encoding.UTF8.GetBytes(fileName));
        string b64Data = Convert.ToBase64String(content);
        string payload = $"\x1b]1337;File=name={b64Name};size={content.Length}:{b64Data}\x07";
        byte[] seq = Encoding.ASCII.GetBytes(payload);

        var events = new TerminalInputParser().Feed(seq);
        Assert.Single(events);
        Assert.Equal(TerminalInputKind.FileDrop, events[0].Kind);
        Assert.True(events[0].FileDrop.HasValue);
        var drop = events[0].FileDrop!.Value;

        // Conversion point: TerminalInputParser writes a temp file; ImageAttachment validates it.
        var attachmentResult = ImageAttachment.TryCreate(drop.FilePath);
        Assert.True(attachmentResult.IsSuccess);
        var attachment = attachmentResult.SuccessValue;
        Assert.Equal("png", attachment.Format);
        Assert.Equal(content.Length, attachment.SizeBytes);

        // TUI layer: the indicator accepts the validated attachment.
        var indicator = new AttachmentIndicator();
        indicator.Show(attachment);
        Assert.True(indicator.IsVisible);
        Assert.Same(attachment, indicator.Current);
    }

    [Fact]
    public void PathPasteFileDrop_ProducesImageAttachment_ForQuotedPath()
    {
        var path = RandomPath("jpg");
        File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10]);

        var drop = FileDropEvent.Create(path, null, FileDropSource.PathPaste);
        var result = ImageAttachment.TryCreate(drop.FilePath);

        Assert.True(result.IsSuccess);
        Assert.Equal("jpg", result.SuccessValue.Format);
    }

    // ---------- ImageAttachmentProcessor vision/text branch ----------

    [Theory]
    [InlineData("openai", "gpt-4o")]
    [InlineData("anthropic", "claude-3-sonnet")]
    [InlineData("google", "gemini-2")]
    [InlineData("ollama", "llava")]
    public void ImageAttachmentProcessor_VisionCapableProvider_EmbedsBase64ImageData(string providerName, string modelName)
    {
        var attachment = CreateValidPngAttachment();

        var enriched = ImageAttachmentProcessor.BuildEnrichedMessage("What is in this image?", attachment, providerName, modelName);

        // Should always end with the user text.
        Assert.EndsWith("What is in this image?", enriched, StringComparison.Ordinal);

        // Base64 data URI marker should be present.
        Assert.Contains("[image data:image/png;base64,", enriched, StringComparison.Ordinal);

        // Metadata line should expose path/format/size for diagnostics.
        Assert.Contains($"[image metadata: path={attachment.FilePath}, format=png", enriched, StringComparison.Ordinal);

        // The actual base64 payload should decode back to the original file bytes.
        var prefix = "[image data:image/png;base64,";
        int start = enriched.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        int end = enriched.IndexOf("]", start, StringComparison.Ordinal);
        var embedded = enriched[start..end];
        var decoded = Convert.FromBase64String(embedded);
        Assert.Equal(File.ReadAllBytes(attachment.FilePath), decoded);
    }

    [Theory]
    [InlineData("cerebras", "llama3.1-8b")]
    [InlineData("azure", "gpt-35-turbo")]
    [InlineData("localhost", "llama2-7b")]
    [InlineData("openai-compatible", "llama2-13b")]
    public void ImageAttachmentProcessor_TextOnlyProvider_ProvidesTextReference(string providerName, string modelName)
    {
        var attachment = CreateValidPngAttachment();

        var enriched = ImageAttachmentProcessor.BuildEnrichedMessage("What is in this image?", attachment, providerName, modelName);

        Assert.EndsWith("What is in this image?", enriched, StringComparison.Ordinal);

        // No base64 data URI marker for text-only providers.
        Assert.DoesNotContain("[image data:image/png;base64,", enriched, StringComparison.Ordinal);

        // A human-readable reference line should prefix the user message.
        Assert.Contains("[Image attached:", enriched, StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(attachment.FilePath), enriched, StringComparison.Ordinal);
        Assert.Contains($"path: {attachment.FilePath}", enriched, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unknown-provider", "gpt-4o")]
    public void ImageAttachmentProcessor_UnknownProviderWithVisionModel_UsesModelHeuristic(string providerName, string modelName)
    {
        var attachment = CreateValidPngAttachment();

        var enriched = ImageAttachmentProcessor.BuildEnrichedMessage("Describe", attachment, providerName, modelName);

        Assert.Contains("[image data:image/png;base64,", enriched, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageAttachmentProcessor_NullAttachment_ReturnsOriginalMessage()
    {
        var result = ImageAttachmentProcessor.BuildEnrichedMessage("Just text.", null!, "openai", "gpt-4o");
        Assert.Equal("Just text.", result);
    }

    // ---------- SimpleAssistantService forwards enriched message ----------

    [Theory]
    [InlineData("openai", "gpt-4o", true)]
    [InlineData("cerebras", "llama3.1-8b", false)]
    public async Task SimpleAssistantService_WithImageAttachment_SendsEnrichedMessageToLlm(
        string providerName,
        string modelName,
        bool expectVision)
    {
        var attachment = CreateValidPngAttachment();
        var captured = new System.Collections.Concurrent.ConcurrentBag<string>();
        var mockLlm = new Mock<ILlmProvider>();
        mockLlm.SetupGet(p => p.Name).Returns(providerName);
        mockLlm
            .Setup(p => p.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LlmRequest, CancellationToken>((req, _) =>
            {
                var last = req.Messages.LastOrDefault(m => m.Role == Role.User);
                captured.Add(last?.Content ?? string.Empty);
            })
            .ReturnsAsync(new LlmResponse
            {
                AssistantMessage = new Message { Role = Role.Assistant, Content = "Ack.", ToolCalls = new List<ToolCall>() },
                FinishReason = "stop",
                Model = modelName,
            });

        var feed = new FeedView();
        var assistant = CreateAssistant(mockLlm.Object, feed, providerName, modelName);

        var response = await assistant.ProcessMessageAsync("What is this?", attachment);

        Assert.Equal("Ack.", response);
        var message = Assert.Single(captured);
        if (expectVision)
        {
            Assert.Contains("[image data:image/png;base64,", message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("[Image attached:", message, StringComparison.Ordinal);
            Assert.DoesNotContain("[image data:image/png;base64,", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SimpleAssistantService_WithoutAttachment_SendsPlainMessageToLlm()
    {
        var captured = new System.Collections.Concurrent.ConcurrentBag<string>();
        var mockLlm = new Mock<ILlmProvider>();
        mockLlm.SetupGet(p => p.Name).Returns("openai");
        mockLlm
            .Setup(p => p.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LlmRequest, CancellationToken>((req, _) =>
            {
                var last = req.Messages.LastOrDefault(m => m.Role == Role.User);
                captured.Add(last?.Content ?? string.Empty);
            })
            .ReturnsAsync(new LlmResponse
            {
                AssistantMessage = new Message { Role = Role.Assistant, Content = "Ack.", ToolCalls = new List<ToolCall>() },
                FinishReason = "stop",
                Model = "gpt-4o",
            });

        var feed = new FeedView();
        var assistant = CreateAssistant(mockLlm.Object, feed, "openai", "gpt-4o");

        var response = await assistant.ProcessMessageAsync("Plain question.");

        Assert.Equal("Ack.", response);
        var message = Assert.Single(captured);
        Assert.Equal("Plain question.", message);
    }
}
