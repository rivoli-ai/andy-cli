using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets.FeedItems
{
    /// <summary>
    /// Interface for feed items that can be rendered in the FeedView.
    /// </summary>
    public interface IFeedItem
    {
        /// <summary>Measure how many lines this item would occupy at a given width.</summary>
        int MeasureLineCount(int width);

        /// <summary>
        /// Render a slice of this item: starting at a line offset, for up to maxLines.
        /// Implementations should clip horizontally to width and not draw outside the provided region.
        /// </summary>
        void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b);
    }
}
