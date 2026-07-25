using System;
using System.Collections.Generic;
using System.Text;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets.Tools
{
    /// <summary>
    /// Decodes ANSI escape sequences in tool output into styled lines (issue #250).
    ///
    /// Any tool that shells out to a colorizing program - `ls --color`, `git --color`,
    /// `dotnet build`, most test runners - returns escape bytes in its stdout. Before this
    /// existed nothing on the tool-result path stripped or decoded them, so they were pushed
    /// into the display list verbatim and corrupted the frame.
    ///
    /// SGR color codes are mapped onto THEME roles rather than fixed terminal colors: a program
    /// that prints red means "this is bad", and the theme's error color says that legibly on
    /// whatever background the user actually runs. Indexed (256-color) and truecolor sequences
    /// have no such semantic mapping, so their literal RGB is used. Non-SGR sequences (cursor
    /// movement, screen erase) are discarded - they are instructions to a real terminal that make
    /// no sense inside a scrollback widget.
    /// </summary>
    public static class AnsiText
    {
        private const char Escape = '\u001b';

        /// <summary>True when the text contains an escape character worth decoding.</summary>
        public static bool ContainsEscapes(string? text) => text is not null && text.IndexOf(Escape) >= 0;

        /// <summary>
        /// Split terminal output into rows and decode each one.
        ///
        /// Rows are split on '\n' only. A lone '\r' is treated as a carriage return that rewrites
        /// the row (what progress bars do), not as a line break - otherwise a single progress bar
        /// would expand into hundreds of feed lines.
        /// </summary>
        public static IReadOnlyList<StyledLine> DecodeLines(string? text, Themes.Theme? theme = null)
        {
            var rows = new List<StyledLine>();
            if (string.IsNullOrEmpty(text)) return rows;

            foreach (var row in text.Replace("\r\n", "\n").Split('\n'))
                rows.Add(Decode(row, theme));
            return rows;
        }

        /// <summary>Decode a single row (no newlines) into styled spans.</summary>
        public static StyledLine Decode(string? row, Themes.Theme? theme = null)
        {
            if (string.IsNullOrEmpty(row)) return StyledLine.Empty;
            theme ??= Themes.Theme.Current;

            var spans = new List<StyledSpan>();
            var pending = new StringBuilder();
            DL.Rgb24? foreground = null;
            var attributes = DL.CellAttrFlags.None;
            bool faint = false;

            void Flush()
            {
                if (pending.Length == 0) return;
                spans.Add(new StyledSpan(pending.ToString(), Dim(foreground, faint, theme), attributes));
                pending.Clear();
            }

            for (int i = 0; i < row.Length; i++)
            {
                char c = row[i];

                if (c == '\r')
                {
                    // Carriage return: the rest of the row overwrites what came before it. Real
                    // terminals overwrite only the columns the new text covers, but progress bars
                    // - the overwhelming reason this appears - rewrite the whole row, so
                    // discarding is both simpler and what the user expects to see.
                    Flush();
                    spans.Clear();
                    continue;
                }

                if (c == '\t')
                {
                    // Tabs would desync the cursor advance from the glyph count in the display list.
                    pending.Append("    ");
                    continue;
                }

                if (c != Escape)
                {
                    if (!char.IsControl(c)) pending.Append(c);
                    continue;
                }

                // An escape sequence starts here. Consume it whole; only SGR ("...m") changes style.
                if (!TryReadEscape(row, i, out int consumed, out string parameters, out char final))
                {
                    // A truncated sequence at end of row: drop the remainder rather than printing it.
                    break;
                }
                i += consumed - 1;

                if (final != 'm') continue;

                Flush();
                ApplySgr(parameters, theme, ref foreground, ref attributes, ref faint);
            }

            Flush();
            return new StyledLine(spans);
        }

        /// <summary>Remove every escape sequence, leaving the plain characters.</summary>
        public static string Strip(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (!ContainsEscapes(text)) return text;

            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != Escape) { sb.Append(text[i]); continue; }
                if (!TryReadEscape(text, i, out int consumed, out _, out _)) break;
                i += consumed - 1;
            }
            return sb.ToString();
        }

        // Reads one escape sequence starting at `start`. Handles CSI ("ESC [ ... final"), OSC
        // ("ESC ] ... BEL | ESC \") and the two-character sequences; returns how many characters
        // the sequence occupies.
        private static bool TryReadEscape(string text, int start, out int consumed,
            out string parameters, out char final)
        {
            consumed = 0;
            parameters = string.Empty;
            final = '\0';
            if (start >= text.Length || text[start] != Escape) return false;

            int i = start + 1;
            if (i >= text.Length) return false;

            char kind = text[i];
            if (kind == '[')
            {
                i++;
                int paramStart = i;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == ';' || text[i] == ':' || text[i] == '?')) i++;
                if (i >= text.Length) return false;
                parameters = text.Substring(paramStart, i - paramStart);
                final = text[i];
                consumed = i - start + 1;
                return true;
            }

            if (kind == ']')
            {
                // OSC: runs until BEL or ST (ESC \).
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '\a') { consumed = i - start + 1; final = '\a'; return true; }
                    if (text[i] == Escape && i + 1 < text.Length && text[i + 1] == '\\')
                    {
                        consumed = i - start + 2;
                        final = '\\';
                        return true;
                    }
                    i++;
                }
                return false;
            }

            // Two-character escape (ESC c, ESC =, ...).
            consumed = 2;
            final = kind;
            return true;
        }

        private static void ApplySgr(string parameters, Themes.Theme theme,
            ref DL.Rgb24? foreground, ref DL.CellAttrFlags attributes, ref bool faint)
        {
            // A bare "ESC[m" means reset, exactly like "ESC[0m".
            var parts = parameters.Length == 0
                ? new[] { "0" }
                : parameters.Split(';');

            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out int code)) continue;

                switch (code)
                {
                    case 0:
                        foreground = null;
                        attributes = DL.CellAttrFlags.None;
                        faint = false;
                        break;
                    case 1: attributes |= DL.CellAttrFlags.Bold; break;
                    case 2: faint = true; break;
                    case 3: attributes |= DL.CellAttrFlags.Italic; break;
                    case 4: attributes |= DL.CellAttrFlags.Underline; break;
                    case 21: attributes |= DL.CellAttrFlags.DoubleUnderline; break;
                    case 22: attributes &= ~DL.CellAttrFlags.Bold; faint = false; break;
                    case 23: attributes &= ~DL.CellAttrFlags.Italic; break;
                    case 24: attributes &= ~(DL.CellAttrFlags.Underline | DL.CellAttrFlags.DoubleUnderline); break;
                    case 39: foreground = null; break;
                    case >= 30 and <= 37: foreground = BasicColor(code - 30, bright: false, theme); break;
                    case >= 90 and <= 97: foreground = BasicColor(code - 90, bright: true, theme); break;
                    case 38:
                        if (TryReadExtendedColor(parts, ref i, out var extended)) foreground = extended;
                        break;
                    case 48:
                        // Background colors are parsed so their arguments are consumed, but not
                        // applied: the feed owns its own background, and honoring a program's
                        // background would fight the theme surface.
                        TryReadExtendedColor(parts, ref i, out _);
                        break;
                }
            }
        }

        // "38;5;n" (256-color) and "38;2;r;g;b" (truecolor). `index` is advanced past the arguments.
        private static bool TryReadExtendedColor(string[] parts, ref int index, out DL.Rgb24 color)
        {
            color = default;
            if (index + 1 >= parts.Length || !int.TryParse(parts[index + 1], out int mode)) return false;

            if (mode == 5 && index + 2 < parts.Length && int.TryParse(parts[index + 2], out int paletteIndex))
            {
                color = FromXterm256(paletteIndex);
                index += 2;
                return true;
            }

            if (mode == 2 && index + 4 < parts.Length
                && int.TryParse(parts[index + 2], out int r)
                && int.TryParse(parts[index + 3], out int g)
                && int.TryParse(parts[index + 4], out int b))
            {
                color = new DL.Rgb24((byte)Clamp(r), (byte)Clamp(g), (byte)Clamp(b));
                index += 4;
                return true;
            }

            return false;
        }

        // The eight basic ANSI colors carry intent, not a specific hue: a build printing red
        // means failure. Mapping them onto theme roles keeps that intent legible under any
        // palette, instead of pinning a fixed RGB that may vanish into the user's background.
        private static DL.Rgb24 BasicColor(int index, bool bright, Themes.Theme theme) => index switch
        {
            0 => bright ? theme.TextDim : theme.Ghost,        // black / bright black
            1 => theme.Error,                                  // red
            2 => theme.Success,                                // green
            3 => theme.Warning,                                // yellow
            4 => theme.Info,                                   // blue
            5 => theme.Accent,                                 // magenta
            6 => theme.Primary,                                // cyan
            _ => bright ? theme.TextBright : theme.Text        // white
        };

        // Standard xterm 256-color cube: 0-15 basic, 16-231 a 6x6x6 RGB cube, 232-255 grayscale.
        private static DL.Rgb24 FromXterm256(int index)
        {
            index = Clamp(index);
            if (index < 16)
            {
                var theme = Themes.Theme.Current;
                return BasicColor(index % 8, index >= 8, theme);
            }
            if (index < 232)
            {
                int n = index - 16;
                int r = n / 36, g = (n % 36) / 6, b = n % 6;
                static byte Level(int v) => (byte)(v == 0 ? 0 : 55 + v * 40);
                return new DL.Rgb24(Level(r), Level(g), Level(b));
            }
            byte gray = (byte)(8 + (index - 232) * 10);
            return new DL.Rgb24(gray, gray, gray);
        }

        // SGR 2 (faint) has no display-list attribute, so it is expressed as a darkened color.
        private static DL.Rgb24? Dim(DL.Rgb24? color, bool faint, Themes.Theme theme)
        {
            if (!faint) return color;
            var c = color ?? theme.Text;
            return new DL.Rgb24((byte)(c.R * 6 / 10), (byte)(c.G * 6 / 10), (byte)(c.B * 6 / 10));
        }

        private static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
    }
}
