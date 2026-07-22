using System;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Widgets
{
    /// <summary>A streaming message that can be updated progressively.</summary>
    public sealed class StreamingMessageItem : IFeedItem
    {
        private readonly System.Text.StringBuilder _content = new();
        private bool _completed = false;
        private bool _hidden = false;
        private string[] _lines = Array.Empty<string>();

        /// <summary>Append content to the streaming message.</summary>
        public void AppendContent(string content)
        {
            if (!_completed)
            {
                _content.Append(content);
                UpdateLines();
            }
        }

        /// <summary>Mark the streaming message as complete.</summary>
        public void Complete()
        {
            _completed = true;
        }

        /// <summary>Hide the streaming message (used when content will be reformatted).</summary>
        public void Hide()
        {
            _hidden = true;
            _lines = Array.Empty<string>();
        }

        /// <summary>Get the full content of the streaming message.</summary>
        public string GetContent()
        {
            return _content.ToString();
        }

        private void UpdateLines()
        {
            if (!_hidden)
            {
                _lines = _content.ToString().Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            }
        }

        /// <inheritdoc />
        public int MeasureLineCount(int width)
        {
            if (_content.Length == 0) return 1;

            // Count wrapped lines
            int totalLines = 0;
            foreach (var line in _lines)
            {
                if (string.IsNullOrEmpty(line))
                    totalLines++;
                else
                    totalLines += Math.Max(1, (int)Math.Ceiling((double)line.Length / Math.Max(1, width)));
            }
            return Math.Max(1, totalLines);
        }

        /// <inheritdoc />
        public void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
        {
            if (_content.Length == 0)
            {
                // Show a loading indicator if still streaming and no content yet
                if (!_completed)
                {
                    b.DrawText(new DL.TextRun(x, y, "...", new DL.Rgb24(150, 150, 150), null, DL.CellAttrFlags.None));
                }
                return;
            }

            // Render as simple markdown-styled text on the theme background (opaque
            // themes); otherwise the renderer's default black background shows through.
            var renderer = new Andy.Tui.Widgets.MarkdownRenderer();
            var theme = Themes.Theme.Current;
            if (theme.Background is { } mdBg)
                renderer.SetColors(theme.Text, mdBg, theme.Accent);
            renderer.SetHeaderColors(theme.Heading, theme.Heading, theme.Heading);
            renderer.SetText(FeedMarkdown.BalanceCodeFences(FeedMarkdown.Normalize(_content.ToString())));
            // Convert the renderer's Underline (emphasis) attribute to Bold + link color so the
            // streaming view matches the finalized markdown: bold, distinct color, no underline.
            MarkdownLinkStyle.RenderWithoutUnderline(b, theme.Accent, temp =>
                renderer.Render(new L.Rect(x, y, width, maxLines), baseDl, temp));

            // Don't show cursor at all - it's distracting and ugly
            // Cursor should only appear when user is actually typing in an input field
        }
    }
}
