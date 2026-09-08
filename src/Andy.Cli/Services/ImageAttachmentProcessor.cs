using Andy.Engine;
using Andy.Cli.Domain;
using Andy.Model.Llm;
using Andy.Model.Model;

namespace Andy.Cli.Services;

public static class ImageAttachmentProcessor
{
    public static async Task<IReadOnlyList<MessagePart>> AppendAsync(ILlmProvider provider,
        IReadOnlyList<MessagePart> parts, ImageAttachment image, CancellationToken ct = default)
    {
        var result = parts.ToList();
        if (provider is IVisionCapableLlmProvider vision && await vision.SupportsImageInputAsync(ct))
            result.Add(new ImagePart { MimeType = image.MimeType, ImageData = image.Data.ToArray() });
        else
            result.Add(new TextPart($"[Image attachment: {image.FilePath} ({image.MimeType}). This provider does not accept image input.]"));
        return result;
    }
}
