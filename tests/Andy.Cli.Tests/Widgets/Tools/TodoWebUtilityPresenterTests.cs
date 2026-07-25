using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services.ToolResults;
using Andy.Cli.Themes;
using Andy.Cli.Widgets;
using Andy.Cli.Widgets.Tools;
using Xunit;

namespace Andy.Cli.Tests.Widgets.Tools;

/// <summary>
/// Presenters for the plan (#258), the web tools (#259), the utility tools (#265) and skills
/// (#263).
/// </summary>
public class TodoWebUtilityPresenterTests
{
    private static ToolCallSnapshot Snapshot(string tool, object? data = null,
        Dictionary<string, object?>? parameters = null, Dictionary<string, object?>? metadata = null,
        bool complete = true, bool successful = true) => new()
        {
            ToolId = tool + "_1",
            ToolName = tool,
            Parameters = parameters ?? new Dictionary<string, object?>(),
            Metadata = metadata ?? new Dictionary<string, object?>(),
            IsComplete = complete,
            IsSuccessful = successful,
            Data = data
        };

    private static ToolPresentation Present(IToolPresenter presenter, ToolCallSnapshot snapshot,
        int width = 90, bool expanded = false)
        => presenter.Present(snapshot, new ToolPresentationContext(width, expanded, Theme.Current));

    private static object Todo(string text, string status) => new { text, status };

    // ---- todo_management (#258) ----------------------------------------------------------

    [Fact]
    public void PlanRendersAsAChecklist()
    {
        // No checklist widget existed anywhere in the feed before; the plan never appeared.
        var snapshot = Snapshot("todo_management",
            data: new Dictionary<string, object?>
            {
                ["todos"] = new[]
                {
                    Todo("Wire up the registry", "completed"),
                    Todo("Add the shell renderer", "inprogress"),
                    Todo("Add the diff renderer", "pending")
                }
            },
            parameters: new Dictionary<string, object?> { ["action"] = "add_batch" });

        var body = Present(new TodoToolPresenter(), snapshot).Body.Select(r => r.Text).ToList();

        Assert.Equal("[x] Wire up the registry", body[0]);
        Assert.Equal("[>] Add the shell renderer", body[1]);
        Assert.Equal("[ ] Add the diff renderer", body[2]);
    }

    [Fact]
    public void CurrentItemIsStyledSoTheFocusIsObvious()
    {
        var snapshot = Snapshot("todo_management",
            data: new Dictionary<string, object?>
            {
                ["todos"] = new[] { Todo("done thing", "completed"), Todo("current thing", "inprogress") }
            },
            parameters: new Dictionary<string, object?> { ["action"] = "update" });

        var body = Present(new TodoToolPresenter(), snapshot).Body;

        Assert.Equal(Theme.Current.Ghost, body[0].Spans[0].Foreground);
        Assert.Equal(Theme.Current.Accent, body[1].Spans[0].Foreground);
        Assert.True(body[1].Spans[1].Attributes.HasFlag(Andy.Tui.DisplayList.CellAttrFlags.Bold));
    }

    [Fact]
    public void PlanHeaderShowsProgress()
    {
        var snapshot = Snapshot("todo_management",
            data: new Dictionary<string, object?>
            {
                ["todos"] = new[] { Todo("a", "completed"), Todo("b", "completed"), Todo("c", "pending") }
            },
            parameters: new Dictionary<string, object?> { ["action"] = "add" });

        Assert.Equal("2/3 done", Present(new TodoToolPresenter(), snapshot).Trailing);
    }

    [Fact]
    public void BlockedItemsAreCalledOut()
    {
        var snapshot = Snapshot("todo_management",
            data: new Dictionary<string, object?> { ["todos"] = new[] { Todo("waiting on CI", "blocked") } },
            parameters: new Dictionary<string, object?> { ["action"] = "update" });

        var row = Present(new TodoToolPresenter(), snapshot).Body[0];

        Assert.StartsWith("[!]", row.Text);
        Assert.Equal(Theme.Current.Warning, row.Spans[0].Foreground);
    }

    [Fact]
    public void ReadingTheListStaysOnOneRow()
    {
        var snapshot = Snapshot("todo_management",
            data: new Dictionary<string, object?> { ["todos"] = new[] { Todo("a", "pending"), Todo("b", "completed") } },
            parameters: new Dictionary<string, object?> { ["action"] = "list" });

        var presentation = Present(new TodoToolPresenter(), snapshot);

        Assert.Empty(presentation.Body);
        Assert.Equal("1/2 done", presentation.Trailing);
    }

    [Fact]
    public void EarlierPlansCollapseWhenANewOneArrives()
    {
        // Every revision stays in the transcript, but only the current one is drawn in full.
        var feed = new FeedView();
        feed.AddToolExecutionStart("todo_management_1", "todo_management",
            new Dictionary<string, object?> { ["action"] = "add_batch" });
        feed.CompleteToolCall("todo_management_1", new ToolCallCompletion
        {
            IsSuccessful = true,
            Data = new Dictionary<string, object?> { ["todos"] = new[] { Todo("first plan", "pending") } }
        });

        var first = feed.GetItemsForTesting().OfType<ToolCallItem>().Single();
        Assert.NotEmpty(first.DebugRows(80).Skip(1));   // drawn in full while it is current

        feed.AddToolExecutionStart("todo_management_2", "todo_management",
            new Dictionary<string, object?> { ["action"] = "add_batch" });

        Assert.True(first.Snapshot.IsSuperseded);
        Assert.Contains("superseded", string.Join("\n", first.DebugRows(80)));
        // It collapses to its header rather than being removed - deleting feed history would
        // shift everything the user has already scrolled past.
        Assert.Equal(2, feed.GetItemsForTesting().OfType<ToolCallItem>().Count());
    }

    // ---- http_request / json_processor (#259) --------------------------------------------

    [Fact]
    public void HttpShowsStatusSizeAndDuration()
    {
        var snapshot = Snapshot("http_request",
            data: new Dictionary<string, object?>
            {
                ["url"] = "https://api.example.com/v1/users",
                ["status_code"] = 200,
                ["content"] = "{}",
                ["content_length"] = 4300
            },
            parameters: new Dictionary<string, object?> { ["url"] = "https://api.example.com/v1/users" }) with
        {
            Duration = TimeSpan.FromMilliseconds(312)
        };

        var presentation = Present(new WebToolPresenter(), snapshot);

        Assert.Contains("GET", presentation.Header.Text);
        Assert.Contains("api.example.com", presentation.Header.Text);
        Assert.Contains("200", presentation.Trailing);
        Assert.Contains("4.2 KB", presentation.Trailing);
        Assert.Contains("312ms", presentation.Trailing);
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(404, false)]
    [InlineData(503, false)]
    public void StatusClassPicksTheColor(int status, bool success)
    {
        var snapshot = Snapshot("http_request",
            data: new Dictionary<string, object?> { ["url"] = "https://x.test", ["status_code"] = status });

        var header = Present(new WebToolPresenter(), snapshot).Header;
        var expected = success ? Theme.Current.Success : Theme.Current.Error;

        Assert.Contains(header.Spans, s => s.Text.Contains(status.ToString()) && s.Foreground == expected);
    }

    [Fact]
    public void LongUrlsAreElidedInTheMiddleNotCutAtTheEnd()
    {
        // Cutting the end throws away the endpoint, which is the part that identifies the call.
        var url = "https://api.example.com/v1/organizations/12345/projects/67890/deployments/latest";

        var elided = WebToolPresenter.ElideMiddle(url, 40);

        Assert.Equal(40, elided.Length);
        Assert.StartsWith("https://api.example", elided);   // the host survives
        Assert.EndsWith("latest", elided);                  // so does the endpoint
        Assert.Contains("...", elided);

        // A URL that already fits is left exactly as it is.
        Assert.Equal("https://x.test/a", WebToolPresenter.ElideMiddle("https://x.test/a", 40));
    }

    [Fact]
    public void JsonBodiesArePrettyPrinted()
    {
        var compact = """{"a":1,"b":[2,3]}""";

        var pretty = WebToolPresenter.PrettyPrintJson(compact);

        Assert.Contains("\n", pretty);
        Assert.Contains("\"a\": 1", pretty);
    }

    [Fact]
    public void NonJsonBodiesArePassedThroughUnchanged()
    {
        // A truncated body or an HTML error page must not be mangled by a failed parse.
        const string html = "<html><body>502 Bad Gateway</body></html>";

        Assert.Equal(html, WebToolPresenter.PrettyPrintJson(html));
        Assert.Equal("{not valid json", WebToolPresenter.PrettyPrintJson("{not valid json"));
    }

    [Fact]
    public void JsonProcessorReportsTheResultShape()
    {
        var snapshot = Snapshot("json_processor",
            data: new[] { 1, 2, 3, 4 },
            parameters: new Dictionary<string, object?> { ["operation"] = "query" });

        var presentation = Present(new WebToolPresenter(), snapshot);

        Assert.Equal("Process JSON (query)", presentation.Header.Text);
        Assert.Equal("4 items", presentation.Trailing);
    }

    // ---- utilities (#265) ----------------------------------------------------------------

    [Fact]
    public void DateTimeShowsTheAnswerNotTheOperationAlone()
    {
        var snapshot = Snapshot("date_time", data: "2026-07-24 14:32:07 CEST",
            parameters: new Dictionary<string, object?> { ["operation"] = "now" });

        Assert.Contains("2026-07-24 14:32:07 CEST", Present(new UtilityToolPresenter(), snapshot).Header.Text);
    }

    [Fact]
    public void HashResultsAreNotEchoedAtFullLength()
    {
        // These inputs and outputs are frequently secrets; a transcript should not carry them whole.
        var snapshot = Snapshot("encoding_tool", data: new string('a', 500),
            parameters: new Dictionary<string, object?> { ["operation"] = "sha256" });

        var header = Present(new UtilityToolPresenter(), snapshot).Header.Text;

        Assert.StartsWith("Hash (sha256)", header);
        Assert.True(header.Length < 100, "the answer must be capped");
        Assert.EndsWith("...", header);
    }

    [Fact]
    public void ProcessInfoCountsWhatItFound()
    {
        var snapshot = Snapshot("process_info",
            data: new Dictionary<string, object?> { ["items"] = new object[] { new { name = "dotnet" }, new { name = "node" } } });

        Assert.Contains("2 processes", Present(new UtilityToolPresenter(), snapshot).Header.Text);
    }

    [Fact]
    public void SystemInfoNamesThePlatform()
    {
        var snapshot = Snapshot("system_info",
            data: new Dictionary<string, object?> { ["os_description"] = "Darwin 24.6.0", ["architecture"] = "Arm64" });

        Assert.Contains("Darwin 24.6.0 (Arm64)", Present(new UtilityToolPresenter(), snapshot).Header.Text);
    }

    // ---- skills (#263) -------------------------------------------------------------------

    [Fact]
    public void SkillLoadIsNamed()
    {
        var snapshot = Snapshot("skill", parameters: new Dictionary<string, object?> { ["name"] = "code-review" });

        Assert.Equal("Skill \"code-review\"", Present(new SkillToolPresenter(), snapshot).Header.Text);
    }

    [Fact]
    public void DisabledSkillPointsAtTheCommandThatFixesIt()
    {
        var snapshot = Snapshot("skill", successful: false,
            parameters: new Dictionary<string, object?> { ["name"] = "code-review" }) with
        {
            ErrorMessage = "Skill 'code-review' is disabled"
        };

        var body = Present(new SkillToolPresenter(), snapshot).Body.Select(r => r.Text).ToList();

        Assert.Contains(body, t => t.Contains("disabled"));
        Assert.Contains(body, t => t.Contains("/skills enable code-review"));
    }

    [Fact]
    public void MissingSkillIsAnOrdinaryError()
    {
        var snapshot = Snapshot("skill", successful: false,
            parameters: new Dictionary<string, object?> { ["name"] = "nope" }) with
        {
            ErrorMessage = "Skill 'nope' was not found"
        };

        var body = Present(new SkillToolPresenter(), snapshot).Body.Select(r => r.Text).ToList();

        Assert.Contains(body, t => t.Contains("not found"));
        Assert.DoesNotContain(body, t => t.Contains("/skills enable"));
    }
}
