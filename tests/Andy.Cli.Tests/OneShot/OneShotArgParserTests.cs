// rivoli-ai/andy-cli#279: argument parsing for the lightweight one-shot form
// `andy-cli run [options] "<prompt>"`. The strict, config-driven contract
// (`run --headless --config <path>`) is parsed elsewhere and must stay reachable,
// which SelectsStrictHeadless decides.

using Andy.Cli.OneShot;
using Xunit;

namespace Andy.Cli.Tests.OneShot;

public class OneShotArgParserTests
{
    [Fact]
    public void SelectsStrictHeadless_TrueWhenHeadlessFlagPresent()
    {
        Assert.True(OneShotArgParser.SelectsStrictHeadless(["run", "--headless", "--config", "x.json"]));
    }

    [Fact]
    public void SelectsStrictHeadless_FalseForBarePrompt()
    {
        Assert.False(OneShotArgParser.SelectsStrictHeadless(["run", "explain this repository"]));
    }

    [Fact]
    public void SelectsStrictHeadless_IgnoresHeadlessAfterLiteralSeparator()
    {
        // Everything after `--` is prompt text, never a mode selector.
        Assert.False(OneShotArgParser.SelectsStrictHeadless(["run", "--", "--headless"]));
    }

    [Fact]
    public void Parse_PositionalWords_AreCollectedInOrder()
    {
        var parsed = OneShotArgParser.Parse(["run", "explain", "this", "repository"]);

        Assert.Null(parsed.Error);
        Assert.Equal(new[] { "explain", "this", "repository" }, parsed.PromptWords);
        Assert.Equal("explain this repository", OneShotPrompt.JoinWords(parsed.PromptWords));
    }

    [Fact]
    public void Parse_KnownOptions_AreBound()
    {
        var parsed = OneShotArgParser.Parse(
        [
            "run",
            "--provider",
            "anthropic",
            "--model",
            "claude-x",
            "--cwd",
            "/tmp",
            "--timeout",
            "42",
            "--max-iterations",
            "7",
            "--output",
            "/tmp/answer.txt",
            "--json",
            "--no-stdin",
            "review this"
        ]);

        Assert.Null(parsed.Error);
        Assert.Equal("anthropic", parsed.Provider);
        Assert.Equal("claude-x", parsed.Model);
        Assert.Equal("/tmp", parsed.Cwd);
        Assert.Equal(42, parsed.TimeoutSeconds);
        Assert.Equal(7, parsed.MaxIterations);
        Assert.Equal("/tmp/answer.txt", parsed.OutputFile);
        Assert.True(parsed.Ndjson);
        Assert.True(parsed.NoStdin);
        Assert.Equal(new[] { "review this" }, parsed.PromptWords);
    }

    [Fact]
    public void Parse_NdjsonAlias_IsAccepted()
    {
        Assert.True(OneShotArgParser.Parse(["run", "--ndjson", "hi"]).Ndjson);
    }

    [Fact]
    public void Parse_InlineValueForm_IsAccepted()
    {
        var parsed = OneShotArgParser.Parse(["run", "--model=gpt-x", "--timeout=9", "hi"]);

        Assert.Null(parsed.Error);
        Assert.Equal("gpt-x", parsed.Model);
        Assert.Equal(9, parsed.TimeoutSeconds);
    }

    [Fact]
    public void Parse_AllowTool_IsRepeatableAndCommaSeparatedAndDeduped()
    {
        var parsed = OneShotArgParser.Parse(
            ["run", "--allow-tool", "write_file,execute_command", "--allow-tool", "write_file", "go"]);

        Assert.Null(parsed.Error);
        Assert.Equal(new[] { "write_file", "execute_command" }, parsed.AllowedTools);
    }

    [Fact]
    public void Parse_LiteralSeparator_TreatsFollowingTokensAsPromptText()
    {
        var parsed = OneShotArgParser.Parse(["run", "--", "--json", "-x", "text"]);

        Assert.Null(parsed.Error);
        Assert.Equal(new[] { "--json", "-x", "text" }, parsed.PromptWords);
        Assert.False(parsed.Ndjson);
    }

    [Fact]
    public void Parse_UnknownFlag_IsRejected()
    {
        var parsed = OneShotArgParser.Parse(["run", "--weird", "hi"]);

        Assert.NotNull(parsed.Error);
        Assert.Contains("Unknown argument", parsed.Error);
    }

    [Fact]
    public void Parse_ConfigWithoutHeadless_ExplainsTheStrictContract()
    {
        var parsed = OneShotArgParser.Parse(["run", "--config", "x.json"]);

        Assert.NotNull(parsed.Error);
        Assert.Contains("--headless", parsed.Error);
    }

    [Theory]
    [InlineData("--provider")]
    [InlineData("--model")]
    [InlineData("--cwd")]
    [InlineData("--timeout")]
    [InlineData("--max-iterations")]
    [InlineData("--allow-tool")]
    [InlineData("--output")]
    public void Parse_OptionWithoutValue_IsRejected(string flag)
    {
        var parsed = OneShotArgParser.Parse(["run", flag]);

        Assert.NotNull(parsed.Error);
        Assert.Contains("requires a value", parsed.Error);
    }

    [Theory]
    [InlineData("--timeout", "0")]
    [InlineData("--timeout", "86401")]
    [InlineData("--max-iterations", "0")]
    [InlineData("--max-iterations", "10001")]
    public void Parse_OutOfRangeLimits_AreRejected(string flag, string value)
    {
        var parsed = OneShotArgParser.Parse(["run", flag, value, "hi"]);

        Assert.NotNull(parsed.Error);
        Assert.Contains("must be between", parsed.Error);
    }

    [Fact]
    public void Parse_NonNumericLimit_IsRejected()
    {
        var parsed = OneShotArgParser.Parse(["run", "--timeout", "soon", "hi"]);

        Assert.NotNull(parsed.Error);
        Assert.Contains("expects an integer", parsed.Error);
    }

    [Fact]
    public void Usage_DocumentsTheSeparatorAndTheDefaultPermissionProfile()
    {
        Assert.Contains(OneShotPrompt.StdinBeginMarker, OneShotArgParser.Usage);
        Assert.Contains(OneShotPrompt.StdinEndMarker, OneShotArgParser.Usage);
        Assert.Contains("fail-closed read-only", OneShotArgParser.Usage);
    }
}
