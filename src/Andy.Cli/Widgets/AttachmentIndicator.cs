using Andy.Cli.Domain;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets;

public sealed class AttachmentIndicator
{
    public ImageAttachment? Current { get; private set; }
    public bool Visible => Current != null;
    public void Show(ImageAttachment image) => Current = image ?? throw new ArgumentNullException(nameof(image));
    public ImageAttachment? Take() { var image = Current; Current = null; return image; }
    public void Clear() => Current = null;
    public void Render(int x, int y, int width, DL.DisplayListBuilder builder)
    {
        if (Current is not { } image || width <= 0) return;
        var text = $"[image: {Path.GetFileName(image.FilePath)}, {image.SizeBytes:N0} bytes; Backspace on empty prompt removes]";
        text = string.Concat(text.Select(c => char.IsControl(c) ? ' ' : c));
        builder.DrawText(new DL.TextRun(x, y, text[..Math.Min(width, text.Length)], Themes.Theme.Current.TextDim, null, DL.CellAttrFlags.None));
    }
}
