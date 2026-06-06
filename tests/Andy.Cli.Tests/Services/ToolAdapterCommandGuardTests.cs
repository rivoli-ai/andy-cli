using System.Collections.Generic;
using Andy.Cli.Services.Adapters;
using Xunit;

namespace Andy.Cli.Tests.Services;

/// <summary>
/// Locks the guard that stops a mangled execute_command call (whose "command" argument is missing, empty, or
/// a degenerate token such as the literal "1") from reaching the permission prompt and the shell. The "1"
/// case reproduces the reported bug where asking the assistant to "run the tests" produced a permission
/// prompt for the command "1" and, on approval, an unexpected shell side effect.
/// </summary>
public class ToolAdapterCommandGuardTests
{
    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] kvps)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in kvps)
        {
            d[k] = v;
        }

        return d;
    }

    [Fact]
    public void Rejects_bare_numeric_command()
    {
        // The reported regression: a markdown numbered-list prefix ("1.") leaking through as the command.
        var reason = ToolAdapter.GetInvalidCommandReason(Args(("command", "1")));
        Assert.NotNull(reason);
        Assert.Contains("malformed", reason!);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("  7  ")]
    public void Rejects_other_pure_number_commands(string command)
    {
        Assert.NotNull(ToolAdapter.GetInvalidCommandReason(Args(("command", command))));
    }

    [Fact]
    public void Rejects_missing_command_argument()
    {
        var reason = ToolAdapter.GetInvalidCommandReason(Args(("working_directory", "/tmp")));
        Assert.NotNull(reason);
        Assert.Contains("without a 'command'", reason!);
    }

    [Fact]
    public void Rejects_null_command_argument()
    {
        Assert.NotNull(ToolAdapter.GetInvalidCommandReason(Args(("command", null))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_empty_command(string command)
    {
        var reason = ToolAdapter.GetInvalidCommandReason(Args(("command", command)));
        Assert.NotNull(reason);
        Assert.Contains("empty", reason!);
    }

    [Fact]
    public void Rejects_command_that_is_only_a_flag()
    {
        Assert.NotNull(ToolAdapter.GetInvalidCommandReason(Args(("command", "--help"))));
    }

    [Theory]
    [InlineData("dotnet test")]
    [InlineData("dotnet build")]
    [InlineData("ls -la")]
    [InlineData("gh pr list --limit 10")]
    [InlineData("/usr/bin/env python script.py")]
    [InlineData("git status")]
    [InlineData("FOO=bar dotnet test")] // leading env assignment, real executable after it
    [InlineData("./run.sh")]
    public void Accepts_well_formed_commands(string command)
    {
        Assert.Null(ToolAdapter.GetInvalidCommandReason(Args(("command", command))));
    }
}
