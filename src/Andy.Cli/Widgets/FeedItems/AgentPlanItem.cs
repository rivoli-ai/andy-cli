using Andy.Cli.Services;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets;

/// <summary>
/// Mutable, single-instance rendering of the agent's current structured plan.
/// Updates replace the snapshot in place so plan revisions do not pile up in the feed.
/// </summary>
internal sealed class AgentPlanItem : IFeedItem
{
    private readonly object _stateLock = new();
    private AgentPlanView _plan;
    private int _cachedWidth = -1;
    private int _cachedRevision = -1;
    private IReadOnlyList<PlanRow> _rows = Array.Empty<PlanRow>();

    public AgentPlanItem(AgentPlanView plan)
    {
        _plan = plan;
    }

    public int Revision
    {
        get
        {
            lock (_stateLock)
                return _plan.Revision;
        }
    }

    public void Update(AgentPlanView plan)
    {
        lock (_stateLock)
        {
            _plan = plan;
            _cachedRevision = -1;
        }
    }

    public int MeasureLineCount(int width)
    {
        lock (_stateLock)
            return Rows(width).Count;
    }

    public void RenderSlice(
        int x,
        int y,
        int width,
        int startLine,
        int maxLines,
        DL.DisplayList baseDl,
        DL.DisplayListBuilder b)
    {
        if (width <= 0 || maxLines <= 0 || startLine < 0)
            return;

        lock (_stateLock)
        {
            var rows = Rows(width);
            var theme = Themes.Theme.Current;
            var printed = 0;
            for (var index = startLine; index < rows.Count && printed < maxLines; index++)
            {
                var row = rows[index];
                var color = row.Kind switch
                {
                    PlanRowKind.Header => theme.Heading,
                    PlanRowKind.InProgress => theme.Info,
                    PlanRowKind.Completed => theme.TextDim,
                    _ => theme.Text,
                };
                var attributes = row.Kind is PlanRowKind.Header or PlanRowKind.InProgress
                    ? DL.CellAttrFlags.Bold
                    : DL.CellAttrFlags.None;
                b.DrawText(new DL.TextRun(
                    x,
                    y + printed,
                    row.Text,
                    color,
                    null,
                    attributes));
                printed++;
            }
        }
    }

    private IReadOnlyList<PlanRow> Rows(int width)
    {
        width = Math.Max(1, width);
        if (_cachedWidth == width && _cachedRevision == _plan.Revision)
            return _rows;

        var rows = new List<PlanRow>
        {
            new(Clip($"Plan (revision {_plan.Revision})", width), PlanRowKind.Header),
        };
        foreach (var item in _plan.Items)
        {
            var (prefix, kind) = item.Status switch
            {
                AgentPlanItemViewStatus.InProgress => ("[>] ", PlanRowKind.InProgress),
                AgentPlanItemViewStatus.Completed => ("[x] ", PlanRowKind.Completed),
                _ => ("[ ] ", PlanRowKind.Pending),
            };
            var available = Math.Max(1, width - prefix.Length);
            var wrapped = TextWrap.Wrap(item.Text, available);
            if (wrapped.Count == 0)
                wrapped.Add(string.Empty);

            rows.Add(new PlanRow(Clip(prefix + wrapped[0], width), kind));
            var continuationPrefix = new string(' ', Math.Min(prefix.Length, width));
            foreach (var continuation in wrapped.Skip(1))
                rows.Add(new PlanRow(Clip(continuationPrefix + continuation, width), kind));
        }

        _cachedWidth = width;
        _cachedRevision = _plan.Revision;
        _rows = rows;
        return _rows;
    }

    private static string Clip(string text, int width) =>
        text.Length <= width ? text : text[..width];

    private sealed record PlanRow(string Text, PlanRowKind Kind);

    private enum PlanRowKind
    {
        Header,
        Pending,
        InProgress,
        Completed,
    }
}
