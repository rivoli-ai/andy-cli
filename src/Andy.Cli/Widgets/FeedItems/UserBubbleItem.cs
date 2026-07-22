using System;
using System.Collections.Generic;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets
{
    /// <summary>User message bubble with rounded-ish border and colored label.</summary>
    public sealed class UserBubbleItem : IFeedItem
    {
        private readonly string _text;
        private readonly int _messageNumber;
        // Wrapped body cached per width: the prompt submits the full (possibly multi-line or
        // long, soft-wrapped) message, so the bubble must WRAP it - truncating to the bubble
        // width previously dropped everything past the first visual row.
        private int _cachedWidth = -1;
        private List<string> _cachedBody = new();

        public UserBubbleItem(string text, int messageNumber = 0)
        {
            _text = text ?? string.Empty;
            _messageNumber = messageNumber;
        }

        private string Label => _messageNumber > 0 ? $"You (#{_messageNumber}): " : "You: ";

        private List<string> Body(int width)
        {
            if (width == _cachedWidth) return _cachedBody;
            int inner = Math.Max(1, width - 4);
            // The label shares the first row with the start of the message, so the first wrapped
            // row gets that much less room; the rest use the full inner width.
            int firstWidth = Math.Max(1, inner - Label.Length);
            _cachedBody = WrapInline(_text, firstWidth, inner);
            if (_cachedBody.Count == 0) _cachedBody.Add(string.Empty);
            _cachedWidth = width;
            return _cachedBody;
        }

        // top border + wrapped body rows (label shares the first one) + bottom border
        public int MeasureLineCount(int width) => Body(width).Count + 2;

        public void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            var body = Body(width);
            int total = body.Count + 2;
            int end = Math.Min(total, startLine + maxLines);
            var borderColor = new DL.Rgb24(120, 180, 255); // light blue
            var labelColor = new DL.Rgb24(150, 200, 255);
            var textColor = new DL.Rgb24(220, 220, 220);
            int inner = Math.Max(0, width - 2);

            for (int i = startLine; i < end; i++)
            {
                int row = y + (i - startLine);
                if (i == 0)
                {
                    b.DrawText(new DL.TextRun(x, row, "╭" + new string('─', inner) + "╮", borderColor, null, DL.CellAttrFlags.None));
                    continue;
                }
                if (i == total - 1)
                {
                    b.DrawText(new DL.TextRun(x, row, "╰" + new string('─', inner) + "╯", borderColor, null, DL.CellAttrFlags.None));
                    continue;
                }

                // Side borders for every interior row.
                if (width >= 1) b.DrawText(new DL.TextRun(x, row, "│", borderColor, null, DL.CellAttrFlags.None));
                if (width >= 2) b.DrawText(new DL.TextRun(x + width - 1, row, "│", borderColor, null, DL.CellAttrFlags.None));

                int idx = i - 1; // index into wrapped body rows
                if (idx == 0)
                {
                    // First row: "You (#n): " label followed by the start of the message.
                    string label = Label;
                    b.DrawText(new DL.TextRun(x + 2, row, label, labelColor, null, DL.CellAttrFlags.Bold));
                    if (body.Count > 0)
                        b.DrawText(new DL.TextRun(x + 2 + label.Length, row, body[0], textColor, null, DL.CellAttrFlags.None));
                }
                else if (idx < body.Count)
                {
                    b.DrawText(new DL.TextRun(x + 2, row, body[idx], textColor, null, DL.CellAttrFlags.None));
                }
            }
        }

        // Word-wrap where the FIRST output row uses firstWidth and the rest use restWidth,
        // hard-breaking over-long tokens; preserves explicit newlines. Mirrors TextWrap but with
        // a narrower first row to leave room for the inline label.
        private static List<string> WrapInline(string text, int firstWidth, int restWidth)
        {
            firstWidth = Math.Max(1, firstWidth);
            restWidth = Math.Max(1, restWidth);
            var outl = new List<string>();
            int Cur() => outl.Count == 0 ? firstWidth : restWidth;

            foreach (var raw in (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (raw.Length == 0) { outl.Add(string.Empty); continue; }
                var cur = new System.Text.StringBuilder();
                foreach (var word in raw.Split(' '))
                {
                    var w = word;
                    while (w.Length > Cur())
                    {
                        if (cur.Length > 0) { outl.Add(cur.ToString()); cur.Clear(); }
                        int take = Cur();
                        outl.Add(w.Substring(0, take));
                        w = w.Substring(take);
                    }
                    if (cur.Length == 0) cur.Append(w);
                    else if (cur.Length + 1 + w.Length <= Cur()) { cur.Append(' ').Append(w); }
                    else { outl.Add(cur.ToString()); cur.Clear(); cur.Append(w); }
                }
                outl.Add(cur.ToString());
            }
            if (outl.Count == 0) outl.Add(string.Empty);
            return outl;
        }
    }
}
