using System.Linq;
using Andy.Cli.Widgets;
using Andy.Permissions.Model;
using DL = Andy.Tui.DisplayList;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Tests for the transcript record appended after a permission decision (issue #224): the feed item
/// must state the choice made (approved once / approved for session / denied), name the tool, list
/// the command(s) that were asked about, and color-code them consistently with the dialog.
/// </summary>
public class PermissionDecisionItemTests
{
    private static PermissionRequest Request(string summary, params string[] commands)
    {
        var resources = commands
            .Select(c => new EvaluatedResource(
                new ResourceAccess(ResourceKind.Command, c), PermissionOutcome.Ask, null, true))
            .ToArray();
        return new PermissionRequest(
            "execute_command", "Execute Command", summary,
            new PermissionEvaluation(PermissionOutcome.Ask, resources));
    }

    private static System.Collections.Generic.List<DL.TextRun> Render(PermissionDecisionItem item, int width = 80)
    {
        var b = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, width, 0, item.MeasureLineCount(width), new DL.DisplayListBuilder().Build(), b);
        return b.Build().Ops.OfType<DL.TextRun>().Where(r => !string.IsNullOrEmpty(r.Content)).ToList();
    }

    [Fact]
    public void Approved_once_header_names_tool_and_choice()
    {
        var item = new PermissionDecisionItem(
            Request("Run a shell command", "git status"),
            new PermissionDecision(true, PersistScope.Once));

        Assert.Equal("[approved once] Execute Command", item.HeaderText);
    }

    [Fact]
    public void Approved_session_header_names_tool_and_choice()
    {
        var item = new PermissionDecisionItem(
            Request("Run a shell command", "git status"),
            new PermissionDecision(true, PersistScope.Session));

        Assert.Equal("[approved for session] Execute Command", item.HeaderText);
    }

    [Fact]
    public void Denied_header_names_tool_and_choice()
    {
        var item = new PermissionDecisionItem(
            Request("Run a shell command", "git status"),
            new PermissionDecision(false, PersistScope.Once));

        Assert.Equal("[denied] Execute Command", item.HeaderText);
    }

    [Fact]
    public void Approved_header_renders_in_success_color_and_lists_command()
    {
        var theme = Andy.Cli.Themes.Theme.Current;
        var item = new PermissionDecisionItem(
            Request("Run a shell command", "git status"),
            new PermissionDecision(true, PersistScope.Once));

        var runs = Render(item);

        Assert.Contains(runs, r => r.Content.Contains("[approved once] Execute Command") && r.Fg.Equals(theme.Success));
        Assert.Contains(runs, r => r.Content.Contains("$ git status") && r.Fg.Equals(theme.Info));
    }

    [Fact]
    public void Denied_header_renders_in_error_color()
    {
        var theme = Andy.Cli.Themes.Theme.Current;
        var item = new PermissionDecisionItem(
            Request("Run a shell command", "git status"),
            new PermissionDecision(false, PersistScope.Once));

        var runs = Render(item);

        Assert.Contains(runs, r => r.Content.Contains("[denied] Execute Command") && r.Fg.Equals(theme.Error));
    }

    [Fact]
    public void Dangerous_command_renders_in_error_color()
    {
        var theme = Andy.Cli.Themes.Theme.Current;
        var item = new PermissionDecisionItem(
            Request("Run a shell command", "rm -rf /tmp/build"),
            new PermissionDecision(true, PersistScope.Once));

        var runs = Render(item);

        Assert.Contains(runs, r => r.Content.Contains("$ rm -rf /tmp/build") && r.Fg.Equals(theme.Error));
    }

    [Fact]
    public void Non_command_ask_resources_are_recorded()
    {
        var request = new PermissionRequest(
            "write_file", "Write File", "Write 12 lines to src/Foo.cs",
            new PermissionEvaluation(PermissionOutcome.Ask, new[]
            {
                new EvaluatedResource(
                    new ResourceAccess(ResourceKind.Path, "src/Foo.cs"), PermissionOutcome.Ask, null, true),
            }));
        var item = new PermissionDecisionItem(request, new PermissionDecision(true, PersistScope.Once));

        var runs = Render(item);

        Assert.Contains(runs, r => r.Content.Contains("path: src/Foo.cs"));
    }

    [Fact]
    public void MeasureLineCount_matches_rows_drawn()
    {
        var item = new PermissionDecisionItem(
            Request("Run a shell command", "git status", "rm -rf /tmp/x"),
            new PermissionDecision(false, PersistScope.Once));

        int width = 40;
        int measured = item.MeasureLineCount(width);

        var b = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, width, 0, measured, new DL.DisplayListBuilder().Build(), b);
        var rows = b.Build().Ops.OfType<DL.TextRun>()
            .Where(r => !string.IsNullOrEmpty(r.Content))
            .Select(r => r.Y).Distinct().ToList();

        Assert.True(measured >= 3); // header + two command lines
        Assert.True(rows.Count <= measured, $"drew {rows.Count} rows but measured {measured}");
        Assert.All(rows, y => Assert.InRange(y, 0, measured - 1));
    }

    [Fact]
    public void RenderSlice_respects_requested_window()
    {
        var item = new PermissionDecisionItem(
            Request("Run a shell command", "git status", "git log", "git diff"),
            new PermissionDecision(true, PersistScope.Session));

        var b = new DL.DisplayListBuilder();
        item.RenderSlice(0, 50, 80, startLine: 1, maxLines: 2, new DL.DisplayListBuilder().Build(), b);
        var rows = b.Build().Ops.OfType<DL.TextRun>()
            .Where(r => !string.IsNullOrEmpty(r.Content))
            .Select(r => r.Y).Distinct().ToList();

        Assert.True(rows.Count <= 2);
        Assert.All(rows, y => Assert.InRange(y, 50, 51));
    }
}
