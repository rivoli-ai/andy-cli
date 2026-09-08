using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets;

/// <summary>Word-wraps already parsed Markdown glyphs without splitting inline markup.</summary>
internal static class MarkdownRowLayout
{
    public static List<List<DL.TextRun>> Wrap(IEnumerable<DL.TextRun> rendered, int width)
    {
        var rows = new List<List<DL.TextRun>>();
        int previousSourceRow = -1;
        foreach (var group in rendered.Where(r => r.Content.Length > 0).GroupBy(r => r.Y).OrderBy(g => g.Key))
        {
            for (int gap = previousSourceRow + 1; gap < group.Key; gap++) rows.Add(new());
            previousSourceRow = group.Key;
            var glyphs = group.OrderBy(r => r.X).SelectMany(r => r.Content.Select((c, i) =>
                new DL.TextRun(r.X + i, r.Y, c.ToString(), r.Fg, r.Bg, r.Attrs))).ToList();
            int indent = Math.Min(glyphs[0].X, Math.Max(0, width - 1));
            var text = string.Concat(glyphs.Select(g => g.Content));
            // Retain the renderer's hanging list indent on continuation rows.
            var marker = System.Text.RegularExpressions.Regex.Match(text, @"^(?:[\u2022\u2605]|\d+\.) ",
                System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
            int continuationIndent = marker.Success ? Math.Min(width - 1, indent + marker.Length) : indent;
            int offset = 0;
            while (offset < glyphs.Count)
            {
                int capacity = Math.Max(1, width - indent);
                int take = Math.Min(capacity, glyphs.Count - offset);
                int next = offset + take;
                if (next < glyphs.Count && !char.IsWhiteSpace(text[next]))
                {
                    int boundary = text.LastIndexOf(' ', next - 1, take);
                    if (boundary > offset) { take = boundary - offset; next = boundary; }
                }
                var row = new List<DL.TextRun>(take);
                for (int i = 0; i < take; i++)
                {
                    var glyph = glyphs[offset + i];
                    row.Add(new DL.TextRun(indent + i, rows.Count, glyph.Content, glyph.Fg, glyph.Bg, glyph.Attrs));
                }
                rows.Add(row);
                offset = next;
                while (offset < glyphs.Count && text[offset] == ' ') offset++;
                indent = continuationIndent;
            }
        }
        if (rows.Count == 0) rows.Add(new());
        return rows;
    }
}
