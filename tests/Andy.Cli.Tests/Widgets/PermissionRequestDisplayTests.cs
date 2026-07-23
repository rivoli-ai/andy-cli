using System.Linq;
using Andy.Cli.Widgets;
using Andy.Permissions.Model;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Tests for the permission-dialog command coloring introduced for issue #237: the dialog body is
/// built from color-tagged lines, command lines get a distinct kind, and commands matching known
/// destructive patterns are tagged dangerous (rendered red) so the user can assess them at a glance.
/// </summary>
public class PermissionRequestDisplayTests
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

    [Theory]
    [InlineData("ls -la")]
    [InlineData("git status")]
    [InlineData("git push origin main")]
    [InlineData("dotnet build")]
    [InlineData("npm install")]
    [InlineData("cat /etc/hosts")]
    [InlineData("")]
    public void Classify_returns_normal_for_routine_commands(string command)
    {
        Assert.Equal(CommandRiskLevel.Normal, CommandRiskClassifier.Classify(command));
    }

    [Theory]
    [InlineData("rm -rf /tmp/x")]
    [InlineData("sudo apt install foo")]
    [InlineData("dd if=/dev/zero of=/dev/sda")]
    [InlineData("chmod 777 /etc")]
    [InlineData("git reset --hard HEAD~3")]
    [InlineData("git clean -fdx")]
    [InlineData("git push --force origin main")]
    [InlineData("git push origin main --force-with-lease")]
    [InlineData("docker rm -f mycontainer")]
    [InlineData("kubectl delete pod mypod")]
    [InlineData("terraform destroy")]
    [InlineData("/bin/rm file.txt")]           // path-qualified executable still classifies
    [InlineData("FOO=bar rm file.txt")]        // leading env assignment is skipped
    [InlineData("ls && rm -rf build")]         // any dangerous segment flags the whole line
    [InlineData("echo hi; shutdown -h now")]
    public void Classify_returns_dangerous_for_destructive_commands(string command)
    {
        Assert.Equal(CommandRiskLevel.Dangerous, CommandRiskClassifier.Classify(command));
    }

    [Fact]
    public void BuildBodyLines_tags_command_lines_with_command_kind()
    {
        var request = Request("Run a shell command", "git status");
        var lines = PermissionDialogContent.BuildBodyLines(request, 80);

        Assert.Contains(lines, l => l.Kind == PermissionLineKind.Summary && l.Text.Contains("Run a shell command"));
        Assert.Contains(lines, l => l.Kind == PermissionLineKind.Command && l.Text == "$ git status");
        Assert.DoesNotContain(lines, l => l.Kind == PermissionLineKind.DangerousCommand);
    }

    [Fact]
    public void BuildBodyLines_tags_destructive_commands_as_dangerous()
    {
        var request = Request("Run a shell command", "rm -rf /tmp/build");
        var lines = PermissionDialogContent.BuildBodyLines(request, 80);

        Assert.Contains(lines, l => l.Kind == PermissionLineKind.DangerousCommand && l.Text == "$ rm -rf /tmp/build");
    }

    [Fact]
    public void BuildBodyLines_drops_summary_that_merely_repeats_the_single_command()
    {
        var request = Request("git status", "git status");
        var lines = PermissionDialogContent.BuildBodyLines(request, 80);

        Assert.Single(lines);
        Assert.Equal("$ git status", lines[0].Text);
        Assert.Equal(PermissionLineKind.Command, lines[0].Kind);
    }

    [Fact]
    public void BuildBodyLines_without_command_resources_falls_back_to_summary()
    {
        var request = new PermissionRequest(
            "write_file", "Write File", "Write 12 lines to src/Foo.cs",
            new PermissionEvaluation(PermissionOutcome.Ask, new[]
            {
                new EvaluatedResource(
                    new ResourceAccess(ResourceKind.Path, "src/Foo.cs"), PermissionOutcome.Ask, null, true),
            }));

        var lines = PermissionDialogContent.BuildBodyLines(request, 80);

        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.Equal(PermissionLineKind.Summary, l.Kind));
    }

    [Fact]
    public void BuildBodyLines_wraps_long_commands_and_keeps_their_kind()
    {
        var longCommand = "rm -rf " + string.Join(" ", Enumerable.Repeat("/tmp/some/deep/path", 10));
        var request = Request("Run a shell command", longCommand);
        var lines = PermissionDialogContent.BuildBodyLines(request, 24);

        var commandLines = lines.Where(l => l.Kind == PermissionLineKind.DangerousCommand).ToList();
        Assert.True(commandLines.Count > 1, "long command should wrap across multiple lines");
        Assert.All(commandLines, l => Assert.True(l.Text.Length <= 24));
    }

    [Fact]
    public void ColorFor_uses_theme_info_for_commands_and_theme_error_for_dangerous()
    {
        var theme = Andy.Cli.Themes.Theme.Current;
        Assert.Equal(theme.Info, PermissionDialogContent.ColorFor(PermissionLineKind.Command));
        Assert.Equal(theme.Error, PermissionDialogContent.ColorFor(PermissionLineKind.DangerousCommand));
        Assert.NotEqual(
            PermissionDialogContent.ColorFor(PermissionLineKind.Command),
            PermissionDialogContent.ColorFor(PermissionLineKind.Summary));
    }

    [Fact]
    public void AskedCommands_returns_distinct_ask_commands_only()
    {
        var resources = new[]
        {
            new EvaluatedResource(
                new ResourceAccess(ResourceKind.Command, "git status"), PermissionOutcome.Allow, null, true),
            new EvaluatedResource(
                new ResourceAccess(ResourceKind.Command, "rm -rf x"), PermissionOutcome.Ask, null, true),
            new EvaluatedResource(
                new ResourceAccess(ResourceKind.Command, "rm -rf x"), PermissionOutcome.Ask, null, true),
            new EvaluatedResource(
                new ResourceAccess(ResourceKind.Path, "/etc/hosts"), PermissionOutcome.Ask, null, true),
        };
        var request = new PermissionRequest(
            "execute_command", "Execute Command", "compound",
            new PermissionEvaluation(PermissionOutcome.Ask, resources));

        var commands = PermissionDialogContent.AskedCommands(request);

        Assert.Equal(new[] { "rm -rf x" }, commands);
    }
}
