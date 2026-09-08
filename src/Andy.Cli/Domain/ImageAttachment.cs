namespace Andy.Cli.Domain;

/// <summary>A validated, bounded snapshot of a local image selected by the user.</summary>
public sealed class ImageAttachment
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public string FilePath { get; }
    public string MimeType { get; }
    public long SizeBytes => Data.Length;
    public ReadOnlyMemory<byte> Data { get; }
    private ImageAttachment(string path, string mime, byte[] data) { FilePath = path; MimeType = mime; Data = data; }

    public static bool TryCreate(string path, out ImageAttachment? image, out string? error)
    {
        image = null;
        error = null;
        try
        {
            path = Path.GetFullPath(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is <= 0 or > MaxBytes) { error = "Image must be between 1 byte and 10 MiB."; return false; }
            var bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
            if (file.ReadByte() != -1) { error = "Image changed while being read."; return false; }
            var ext = Path.GetExtension(path).ToLowerInvariant();
            string? mime = ext switch
            {
                ".png" when bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) => "image/png",
                ".jpg" or ".jpeg" when bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }) => "image/jpeg",
                ".gif" when bytes.AsSpan().StartsWith("GIF87a"u8) || bytes.AsSpan().StartsWith("GIF89a"u8) => "image/gif",
                ".webp" when bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8) => "image/webp",
                _ => null
            };
            if (mime == null) { error = "Expected a PNG, JPEG, GIF, or WebP image with matching contents."; return false; }
            image = new ImageAttachment(path, mime, bytes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { error = "Cannot read the selected image."; return false; }
    }
}
