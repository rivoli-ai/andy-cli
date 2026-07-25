using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Xunit;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Tests.Widgets;

public sealed class AgentPlanItemTests
{
    [Fact]
    public void Render_UsesAsciiMarkersAndPreservesPlanOrder()
    {
        var item = new AgentPlanItem(Plan(
            3,
            ("first", "Inspect", AgentPlanItemViewStatus.Completed),
            ("second", "Implement", AgentPlanItemViewStatus.InProgress),
            ("third", "Verify", AgentPlanItemViewStatus.Pending)));

        var text = Render(item);

        Assert.Contains("Plan (revision 3)", text);
        Assert.True(text.IndexOf("[x] Inspect", StringComparison.Ordinal) <
                    text.IndexOf("[>] Implement", StringComparison.Ordinal));
        Assert.True(text.IndexOf("[>] Implement", StringComparison.Ordinal) <
                    text.IndexOf("[ ] Verify", StringComparison.Ordinal));
        Assert.DoesNotContain('\u2611', text);
    }

    [Fact]
    public void Update_ReplacesSnapshotInSameItem()
    {
        var item = new AgentPlanItem(Plan(
            1,
            ("task", "Do work", AgentPlanItemViewStatus.Pending)));

        item.Update(Plan(
            2,
            ("task", "Do work", AgentPlanItemViewStatus.Completed)));

        var text = Render(item);
        Assert.Equal(2, item.Revision);
        Assert.Contains("[x] Do work", text);
        Assert.DoesNotContain("[ ] Do work", text);
    }

    [Fact]
    public void FeedView_RevisionsUpdateOneItemAndEmptyPlanRemovesIt()
    {
        var feed = new FeedView();

        feed.UpdateAgentPlan(Plan(
            1,
            ("task", "Do work", AgentPlanItemViewStatus.Pending)));
        feed.UpdateAgentPlan(Plan(
            2,
            ("task", "Do work", AgentPlanItemViewStatus.InProgress)));

        var planItem = Assert.Single(feed.GetItemsForTesting().OfType<AgentPlanItem>());
        Assert.Equal(2, planItem.Revision);

        feed.UpdateAgentPlan(Plan(3));

        Assert.Empty(feed.GetItemsForTesting().OfType<AgentPlanItem>());
    }

    [Fact]
    public void MeasureLineCount_MatchesRenderedRowsWhenTextWraps()
    {
        const int width = 14;
        var item = new AgentPlanItem(Plan(
            1,
            ("task", "A plan item with enough words to wrap", AgentPlanItemViewStatus.Pending)));
        var builder = new DL.DisplayListBuilder();
        var measured = item.MeasureLineCount(width);

        item.RenderSlice(
            0,
            0,
            width,
            0,
            measured,
            new DL.DisplayListBuilder().Build(),
            builder);

        var rows = builder.Build().Ops.OfType<DL.TextRun>().ToArray();
        Assert.Equal(measured, rows.Length);
        Assert.All(rows, row => Assert.True(row.Content.Length <= width));
    }

    private static AgentPlanView Plan(
        int revision,
        params (string Id, string Text, AgentPlanItemViewStatus Status)[] items) =>
        new(
            revision,
            items.Select(item => new AgentPlanItemView(
                item.Id,
                item.Text,
                item.Status)).ToArray());

    private static string Render(AgentPlanItem item, int width = 80)
    {
        var builder = new DL.DisplayListBuilder();
        item.RenderSlice(
            0,
            0,
            width,
            0,
            item.MeasureLineCount(width),
            new DL.DisplayListBuilder().Build(),
            builder);
        return string.Join(
            "\n",
            builder.Build().Ops.OfType<DL.TextRun>().Select(run => run.Content));
    }
}
