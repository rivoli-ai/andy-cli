using System.Collections.Generic;
using System.Text;

namespace Andy.Cli.Widgets
{
    /// <summary>Text wrapping helpers for dialogs and panels.</summary>
    public static class TextWrap
    {
        /// <summary>
        /// Word-wrap text to the given column width, hard-breaking tokens that are longer
        /// than the width (paths, URLs and commands often have no spaces) so nothing is
        /// truncated. Existing line breaks are preserved; blank lines are kept.
        /// </summary>
        public static List<string> Wrap(string s, int width)
        {
            if (width < 1) width = 1;
            var outl = new List<string>();
            foreach (var rawLine in (s ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (rawLine.Length == 0) { outl.Add(string.Empty); continue; }
                var cur = new StringBuilder();
                foreach (var word in rawLine.Split(' '))
                {
                    var w = word;
                    while (w.Length > width)
                    {
                        if (cur.Length > 0) { outl.Add(cur.ToString()); cur.Clear(); }
                        outl.Add(w.Substring(0, width));
                        w = w.Substring(width);
                    }
                    if (cur.Length == 0) cur.Append(w);
                    else if (cur.Length + 1 + w.Length <= width) { cur.Append(' '); cur.Append(w); }
                    else { outl.Add(cur.ToString()); cur.Clear(); cur.Append(w); }
                }
                outl.Add(cur.ToString());
            }
            return outl;
        }
    }
}
