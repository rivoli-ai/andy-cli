using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services.Shell;
using Andy.Cli.Services.ToolResults;
using Andy.Cli.Themes;
using Andy.Cli.Widgets;
using Andy.Cli.Widgets.Tools;
using Xunit;

namespace Andy.Cli.Tests.Widgets.Tools;

/// <summary>
/// Feed presentation of a command the USER ran in shell mode (issue #286). The row must be
/// unmistakably the user's own - in the middle of a long session, in a copied transcript, and next
/// to the model's own shell rows - and it must report the outcome accurately: exit code, denial,
/// timeout, truncation.
/// </summary>
public class UserShellPresenterTests
{
    private static UserShellCommandResult Result(
        string command = "git status",
        UserShellOutcome outcome = UserShellOutcome.Succeeded,
        int? exitCode = 0,
        string stdout = "",
        string stderr = "",
        bool timedOut = false,
        int truncatedOut = 0,
        string? error = null,
        string workingDirectory = "/work",
        TimeSpan? duration = null)
        => new(command, outcome, exitCode, stdout, stderr,
               duration ?? TimeSpan.FromMilliseconds(20), workingDirectory, timedOut,
               truncatedOut, 0, error, DateTimeOffset.UnixEpoch);

    private static ToolPresentation PresentComplete(UserShellCommandResult result, int width = 80)
    {
        var item = UserShellFeedRow.CreateRunning(result.Command, result.WorkingDirectory);
        UserShellFeedRow.Complete(item, result);
        return new UserShellPresenter().Present(item.Snapshot,
            new ToolPresentationContext(width, false, Theme.Current));
    }

    private static IReadOnlyList<string> BodyText(ToolPresentation p) => p.Body.Select(r => r.Text).ToList();

    [Fact]
    public void DoesNotClaimAnyToolFromTheRegistry()
    {
        // Registering it would hijack the MODEL's execute_command rows, which must keep reading
        // "Ran <command>" rather than being attributed to the user.
        var presenter = new UserShellPresenter();

        Assert.False(presenter.CanPresent("execute_command"));
        Assert.False(presenter.CanPresent("user_shell"));
        Assert.Null(ToolPresenterRegistry.Default.TryResolve("user_shell"));
    }

    [Fact]
    public void Header_CarriesTheShellModeMarkerAndTheCommand()
    {
        var presentation = PresentComplete(Result(command: "git status"));

        Assert.StartsWith("! ", presentation.Header.Text);
        Assert.Contains("git status", presentation.Header.Text);
    }

    [Fact]
    public void Trailing_AttributesEveryRowToTheUser()
    {
        var presentation = PresentComplete(Result());

        Assert.Contains(UserShellPresenter.AttributionLabel, presentation.Trailing);
    }

    [Fact]
    public void Trailing_OmitsTheExitCodeOnSuccessAndShowsItOnFailure()
    {
        Assert.DoesNotContain("exit", PresentComplete(Result()).Trailing!);

        var failed = PresentComplete(Result(outcome: UserShellOutcome.Failed, exitCode: 2));
        Assert.Contains("exit 2", failed.Trailing);
    }

    [Fact]
    public void Trailing_NamesATimeoutRatherThanShowingABareExitCode()
    {
        var presentation = PresentComplete(Result(
            outcome: UserShellOutcome.Cancelled, exitCode: null, timedOut: true));

        Assert.Contains("timed out", presentation.Trailing);
    }

    [Fact]
    public void Trailing_ReportsHowMuchOutputWasDropped()
    {
        var presentation = PresentComplete(Result(stdout: "abc", truncatedOut: 4096));

        Assert.Contains("4,096 chars omitted", presentation.Trailing);
    }

    [Fact]
    public void Trailing_ShowsDurationOnlyWhenItIsLongEnoughToMatter()
    {
        Assert.DoesNotContain("s", PresentComplete(Result(duration: TimeSpan.FromMilliseconds(5))).Trailing!
            .Replace(UserShellPresenter.AttributionLabel, ""));

        var slow = PresentComplete(Result(duration: TimeSpan.FromSeconds(3)));
        Assert.Contains("3.0s", slow.Trailing);
    }

    [Fact]
    public void Body_ShowsStdoutAndStderr()
    {
        var presentation = PresentComplete(Result(
            outcome: UserShellOutcome.Failed, exitCode: 1, stdout: "on_stdout", stderr: "on_stderr"));

        var body = string.Join("\n", BodyText(presentation));
        Assert.Contains("on_stdout", body);
        Assert.Contains("on_stderr", body);
    }

    [Fact]
    public void Body_MarksASilentSuccessExplicitly()
    {
        var presentation = PresentComplete(Result());

        Assert.Contains(BodyText(presentation), line => line.Contains("no output", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Body_ExplainsADenialInsteadOfLookingLikeANoOp()
    {
        var presentation = PresentComplete(Result(
            command: "rm -rf /", outcome: UserShellOutcome.Denied, exitCode: null,
            error: "blocked by permission policy"));

        var body = string.Join(" ", BodyText(presentation));
        Assert.Contains("permission", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("denied", presentation.Trailing!);
    }

    [Fact]
    public void Body_ExplainsACancellation()
    {
        var presentation = PresentComplete(Result(
            outcome: UserShellOutcome.Cancelled, exitCode: null));

        Assert.Contains(BodyText(presentation), line => line.Contains("Interrupted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Body_NotesTheDirectoryWhenTheCommandRanSomewhereElse()
    {
        var presentation = PresentComplete(Result(workingDirectory: "/tmp/somewhere-else"));

        Assert.Contains(BodyText(presentation), line => line.StartsWith("in ", StringComparison.Ordinal));
    }

    [Fact]
    public void RunningRow_ShowsTheCommandBeforeItFinishes()
    {
        var item = UserShellFeedRow.CreateRunning("sleep 5", "/work");

        var presentation = new UserShellPresenter().Present(item.Snapshot,
            new ToolPresentationContext(80, false, Theme.Current));

        Assert.Contains("sleep 5", presentation.Header.Text);
        Assert.False(item.Snapshot.IsComplete);
        Assert.Equal(UserShellPresenter.AttributionLabel, presentation.Trailing);
    }

    [Fact]
    public void Row_IsMarkedAsUserInvokedAndKeepsTheRealToolName()
    {
        var item = UserShellFeedRow.CreateRunning("ls", "/work");

        // The marker is what distinguishes it; the tool name stays "execute_command" so the
        // permission gate's awaiting-approval signal still finds the row.
        Assert.True(item.Snapshot.Parameters.ContainsKey(UserShellPresenter.UserInvokedParameterKey));
        Assert.Equal(UserShellCommandRunner.ToolId, item.Snapshot.ToolName);
        Assert.StartsWith(UserShellFeedRow.RowIdPrefix, item.Snapshot.ToolId);
    }

    [Fact]
    public void RowIds_AreUniquePerCommand()
    {
        var first = UserShellFeedRow.CreateRunning("a", "/work");
        var second = UserShellFeedRow.CreateRunning("b", "/work");

        Assert.NotEqual(first.Snapshot.ToolId, second.Snapshot.ToolId);
    }

    [Fact]
    public void Complete_MapsTheOutcomeOntoTheRowStatus()
    {
        Assert.Equal(ToolCallStatus.Success, StatusFor(UserShellOutcome.Succeeded));
        Assert.Equal(ToolCallStatus.Failed, StatusFor(UserShellOutcome.Failed));
        Assert.Equal(ToolCallStatus.Denied, StatusFor(UserShellOutcome.Denied));
        Assert.Equal(ToolCallStatus.Cancelled, StatusFor(UserShellOutcome.Cancelled));

        static ToolCallStatus StatusFor(UserShellOutcome outcome)
        {
            var item = UserShellFeedRow.CreateRunning("x", "/work");
            UserShellFeedRow.Complete(item, Result(outcome: outcome, exitCode: null));
            return item.Snapshot.Status;
        }
    }

    [Fact]
    public void MeasureAndRender_AgreeOnLineCount()
    {
        // The IFeedItem contract: a divergence here produces phantom blank rows in the feed.
        var item = UserShellFeedRow.CreateRunning("echo hi", "/work");
        UserShellFeedRow.Complete(item, Result(stdout: "line one\nline two\nline three\n"));

        const int width = 60;
        var measured = item.MeasureLineCount(width);

        var baseDl = new Andy.Tui.DisplayList.DisplayListBuilder().Build();
        var builder = new Andy.Tui.DisplayList.DisplayListBuilder();
        item.RenderSlice(0, 0, width, 0, measured, baseDl, builder);

        Assert.True(measured > 0);
        // Asking for rows past the end must be a no-op rather than an overrun.
        item.RenderSlice(0, 0, width, measured, 5, baseDl, builder);
    }

    [Fact]
    public void FeedView_AcceptsTheRowWithoutAnySpecialCasing()
    {
        var feed = new FeedView();
        var item = UserShellFeedRow.CreateRunning("git status", "/work");

        feed.AddItem(item);

        Assert.Contains(item, feed.GetItemsForTesting());
    }
}
