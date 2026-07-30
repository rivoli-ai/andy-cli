using Andy.Cli.Modes;
using Andy.Cli.Widgets;
using Xunit;

namespace Andy.Cli.Tests.Modes;

/// <summary>
/// The visible mode indicator (issue #278). Build is the unrestricted default and shows nothing;
/// a restricted mode must always be visible and must never be dropped when the terminal is narrow.
/// </summary>
public class ModeStatusIndicatorTests
{
    private static ContextStatusBar Bar()
    {
        var bar = new ContextStatusBar();
        bar.Update(1000, 500, 200_000, 3);
        bar.SetModelInfo("gpt-4o", "openai");
        return bar;
    }

    [Fact]
    public void BuildMode_ShowsNoBadge()
    {
        var bar = Bar();
        bar.SetAgentModeBadge(null);

        Assert.DoesNotContain(bar.BuildSegments(), s => s.Kind == StatusSegmentKind.AgentMode);
    }

    [Fact]
    public void PlanMode_ShowsTheBadge()
    {
        var bar = Bar();
        bar.SetAgentModeBadge(AgentModeCatalog.Plan.Badge);

        var segment = Assert.Single(bar.BuildSegments(), s => s.Kind == StatusSegmentKind.AgentMode);
        Assert.Contains("PLAN", segment.Text);
    }

    [Fact]
    public void ModeBadge_SurvivesAVeryNarrowTerminal()
    {
        var bar = Bar();
        bar.SetAgentModeBadge(AgentModeCatalog.Plan.Badge);

        var fitted = ContextStatusBar.FitSegments(bar.BuildSegments(), maxWidth: 10);

        Assert.Contains(fitted, s => s.Kind == StatusSegmentKind.AgentMode);
    }

    [Fact]
    public void ModeBadge_RendersInTheLeftZoneAlongsideStatusText()
    {
        var bar = Bar();
        bar.SetAgentModeBadge(AgentModeCatalog.Plan.Badge);
        bar.SetStatusText("Thinking");

        var (left, right, _, _) = ContextStatusBar.AlignSegments(bar.BuildSegments(), avail: 120);

        Assert.Contains(left, s => s.Kind == StatusSegmentKind.AgentMode);
        Assert.DoesNotContain(right, s => s.Kind == StatusSegmentKind.AgentMode);
    }

    [Fact]
    public void ClearingTheBadge_RemovesIt()
    {
        var bar = Bar();
        bar.SetAgentModeBadge(AgentModeCatalog.Plan.Badge);
        bar.SetAgentModeBadge("   ");

        Assert.DoesNotContain(bar.BuildSegments(), s => s.Kind == StatusSegmentKind.AgentMode);
    }
}
