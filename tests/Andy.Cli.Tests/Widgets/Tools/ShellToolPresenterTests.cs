using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services.ToolResults;
using Andy.Cli.Themes;
using Andy.Cli.Widgets.Tools;
using Xunit;

namespace Andy.Cli.Tests.Widgets.Tools;

/// <summary>
/// Shell command presentation (#251). Every fact shown here comes from the structured payload
/// ExecuteCommandTool returns - exit_code, stdout, stderr, duration_ms, timed_out,
/// working_directory - none of which reached the feed before.
/// </summary>
public class ShellToolPresenterTests
{
    private static ToolCallSnapshot Snapshot(
        string command,
        int? exitCode = 0,
        string? stdout = null,
        string? stderr = null,
        double? durationMs = null,
        bool timedOut = false,
        string? workingDirectory = null,
        bool complete = true,
        bool successful = true)
    {
        var data = new Dictionary<string, object?>
        {
            ["command"] = command,
            ["exit_code"] = exitCode,
            ["stdout"] = stdout,
            ["stderr"] = stderr,
            ["duration_ms"] = durationMs,
            ["timed_out"] = timedOut,
            ["working_directory"] = workingDirectory
        };

        return new ToolCallSnapshot
        {
            ToolId = "execute_command_1",
            ToolName = "execute_command",
            Parameters = new Dictionary<string, object?> { ["command"] = command },
            IsComplete = complete,
            IsSuccessful = successful,
            Data = complete ? data : null
        };
    }

    private static ToolPresentation Present(ToolCallSnapshot snapshot, int width = 80, bool expanded = false)
        => new ShellToolPresenter().Present(snapshot, new ToolPresentationContext(width, expanded, Theme.Current));

    private static IReadOnlyList<string> BodyText(ToolPresentation p) => p.Body.Select(r => r.Text).ToList();

    [Fact]
    public void ClaimsTheShellToolIds()
    {
        var presenter = new ShellToolPresenter();

        Assert.True(presenter.CanPresent("execute_command"));
        Assert.True(presenter.CanPresent("bash_command"));
        Assert.False(presenter.CanPresent("read_file"));
    }

    [Fact]
    public void HeaderShowsTheWholeCommand()
    {
        var command = "dotnet build --configuration Release --no-restore /p:ContinuousIntegrationBuild=true";
        var presentation = Present(Snapshot(command));

        // The full command is present: no cut at 60 characters.
        Assert.Contains(command, presentation.Header.Text);
        Assert.StartsWith("Ran ", presentation.Header.Text);
    }

    [Fact]
    public void RunningCommandsUseThePresentTense()
    {
        var presentation = Present(Snapshot("sleep 5", complete: false));

        Assert.StartsWith("Running ", presentation.Header.Text);
    }

    [Fact]
    public void CommandIsSyntaxHighlighted()
    {
        var presentation = Present(Snapshot("dotnet build --no-restore"));

        // The executable and the flag carry distinct theme colors.
        var spans = presentation.Header.Spans;
        Assert.Contains(spans, s => s.Text == "dotnet" && s.Foreground == Theme.Current.SyntaxType);
        Assert.Contains(spans, s => s.Text == "--no-restore" && s.Foreground == Theme.Current.SyntaxKeyword);
    }

    [Fact]
    public void NonZeroExitCodeIsShown()
    {
        var presentation = Present(Snapshot("false", exitCode: 1, successful: false));

        Assert.Contains("exit 1", presentation.Trailing);
    }

    [Fact]
    public void SuccessfulCommandDoesNotAdvertiseItsExitCode()
    {
        var presentation = Present(Snapshot("true", exitCode: 0, stdout: "ok"));

        Assert.DoesNotContain("exit", presentation.Trailing ?? "");
    }

    [Fact]
    public void TimeoutIsNamedRatherThanShownAsAnExitCode()
    {
        // "exit 124" does not explain itself.
        var presentation = Present(Snapshot("sleep 600", exitCode: 124, timedOut: true, successful: false));

        Assert.Contains("timed out", presentation.Trailing);
    }

    [Fact]
    public void DurationComesFromTheToolsOwnMeasurement()
    {
        var presentation = Present(Snapshot("sleep 1", stdout: "done", durationMs: 1500));

        Assert.Contains("1.5s", presentation.Trailing);
    }

    [Fact]
    public void SubSecondDurationsAreNotReportedAsNoise()
    {
        var presentation = Present(Snapshot("true", stdout: "x", durationMs: 12));

        Assert.Null(presentation.Trailing);
    }

    [Fact]
    public void StdoutIsShownAsTheBody()
    {
        var presentation = Present(Snapshot("ls", stdout: "one\ntwo\nthree"));

        Assert.Equal(new[] { "one", "two", "three" }, BodyText(presentation));
    }

    [Fact]
    public void StderrIsShownInTheErrorColor()
    {
        var presentation = Present(Snapshot("cc x.c", exitCode: 1, stderr: "x.c:1: fatal error", successful: false));

        var errorRow = presentation.Body.Single(r => r.Text.Contains("fatal error"));
        Assert.Equal(Theme.Current.Error, errorRow.Spans[0].Foreground);
    }

    [Fact]
    public void StdoutAndStderrAreBothKept()
    {
        var presentation = Present(Snapshot("make", exitCode: 2, stdout: "building", stderr: "undefined reference", successful: false));

        var text = BodyText(presentation);
        Assert.Contains("building", text);
        Assert.Contains("undefined reference", text);
    }

    [Fact]
    public void SilentSuccessSaysSoExplicitly()
    {
        // Rendering nothing leaves the user unable to tell "no output" from "output was lost".
        var presentation = Present(Snapshot("touch f", stdout: null, stderr: null));

        Assert.Contains("(no output)", BodyText(presentation));
    }

    [Fact]
    public void FailureWithoutOutputStillExplainsItself()
    {
        var snapshot = Snapshot("nope", exitCode: 127, successful: false) with
        {
            ErrorMessage = "command not found: nope"
        };

        Assert.Contains(BodyText(Present(snapshot)), t => t.Contains("command not found"));
    }

    [Fact]
    public void WorkingDirectoryIsShownOnlyWhenItDiffers()
    {
        var elsewhere = Present(Snapshot("ls", stdout: "x", workingDirectory: "/tmp/somewhere-else"));
        Assert.Contains(BodyText(elsewhere), t => t.StartsWith("in "));

        var here = Present(Snapshot("ls", stdout: "x", workingDirectory: System.IO.Directory.GetCurrentDirectory()));
        Assert.DoesNotContain(BodyText(here), t => t.StartsWith("in "));
    }

    [Fact]
    public void AnsiColoredOutputIsDecodedNotPrintedRaw()
    {
        var presentation = Present(Snapshot("ls --color", stdout: "\u001b[32mgreen.txt\u001b[0m"));

        var row = presentation.Body.Single();
        Assert.Equal("green.txt", row.Text);
        Assert.Equal(Theme.Current.Success, row.Spans[0].Foreground);
    }

    [Fact]
    public void LongOutputKeepsTheTailWhereTheErrorIs()
    {
        var lines = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"step {i}"))
                    + "\nBuild FAILED.";
        var presentation = Present(Snapshot("dotnet build", exitCode: 1, stdout: lines, successful: false));

        var text = BodyText(presentation);
        Assert.Contains("Build FAILED.", text);
        Assert.Contains(text, t => t.Contains("more lines") || t.Contains("+"));
    }

    [Fact]
    public void ExpandedModeShowsMoreOfTheOutput()
    {
        var lines = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"step {i}"));
        var snapshot = Snapshot("dotnet build", stdout: lines);

        var collapsed = Present(snapshot, expanded: false);
        var expanded = Present(snapshot, expanded: true);

        Assert.True(expanded.Body.Count > collapsed.Body.Count);
    }

    [Fact]
    public void ShellResultReadsTheStructuredPayloadNotRenderedText()
    {
        var snapshot = Snapshot("git status", exitCode: 0, stdout: "clean", durationMs: 340, workingDirectory: "/repo");

        var result = ShellResult.From(snapshot);

        Assert.Equal("git status", result.Command);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("clean", result.StandardOutput);
        Assert.Equal(TimeSpan.FromMilliseconds(340), result.Duration);
        Assert.Equal("/repo", result.WorkingDirectory);
        Assert.False(result.Failed);
    }
}
