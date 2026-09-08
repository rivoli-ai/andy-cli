using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets;

/// <summary>Plain, wrapped error text using the theme error foreground, without ANSI or Markdown interpretation.</summary>
public sealed class ErrorTextItem(string text) : IFeedItem
{
    private readonly string _text = Services.ProviderErrorFormatter.Plain(text);
    private int _width = -1;
    private List<string> _lines = [];
    private List<string> Lines(int width)
    {
        if (_width != width)
        {
            _width = width;
            _lines = TextWrap.Wrap(_text, Math.Max(1, width));
            if (_lines.Count == 0) _lines.Add("");
        }
        return _lines;
    }
    public int MeasureLineCount(int width) => Lines(width).Count;
    public void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder builder)
    {
        if (width <= 0 || maxLines <= 0 || startLine < 0) return;
        var lines = Lines(width);
        for (var i = startLine; i < lines.Count && i - startLine < maxLines; i++)
            builder.DrawText(new DL.TextRun(x, y + i - startLine, lines[i], Themes.Theme.Current.Error, null, DL.CellAttrFlags.None));
    }
}
