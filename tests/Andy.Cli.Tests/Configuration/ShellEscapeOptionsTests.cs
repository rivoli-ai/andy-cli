using System.Collections.Generic;
using Andy.Cli.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Andy.Cli.Tests.Configuration;

/// <summary>
/// The disable switch for interactive shell escape (issue #286). Two independent sources can turn
/// the feature off and neither may be overridden by the other, so a managed config file cannot be
/// undone by an environment variable in a user's shell profile.
/// </summary>
public class ShellEscapeOptionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (key, value) in values) dict[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static System.Func<string, string?> Env(string? shellEscape) =>
        name => name == ShellEscapeOptions.EnvironmentVariable ? shellEscape : null;

    [Fact]
    public void Resolve_WithNoConfiguration_EnablesWithDefaults()
    {
        var options = ShellEscapeOptions.Resolve(configuration: null, Env(null));

        Assert.True(options.Enabled);
        Assert.Equal(ShellEscapeOptions.DefaultTimeoutSeconds, options.TimeoutSeconds);
        Assert.Equal(ShellEscapeOptions.DefaultMaxOutputCharacters, options.EffectiveMaxOutputCharacters);
    }

    [Fact]
    public void Resolve_WithConfigurationDisabled_DisablesFeature()
    {
        var options = ShellEscapeOptions.Resolve(Config(("ShellEscape:Enabled", "false")), Env(null));

        Assert.False(options.Enabled);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("off")]
    [InlineData("no")]
    [InlineData("disabled")]
    [InlineData("  OFF  ")]
    public void Resolve_WithFalseyEnvironmentVariable_DisablesFeature(string value)
    {
        var options = ShellEscapeOptions.Resolve(Config(("ShellEscape:Enabled", "true")), Env(value));

        Assert.False(options.Enabled);
    }

    [Fact]
    public void Resolve_EnvironmentVariableCannotReEnableAConfigDisable()
    {
        // The two switches are ANDed: an operator's managed config wins over a user's environment.
        var options = ShellEscapeOptions.Resolve(Config(("ShellEscape:Enabled", "false")), Env("1"));

        Assert.False(options.Enabled);
    }

    [Fact]
    public void Resolve_ReadsTimeoutAndOutputCap()
    {
        var options = ShellEscapeOptions.Resolve(Config(
            ("ShellEscape:TimeoutSeconds", "45"),
            ("ShellEscape:MaxOutputCharacters", "1234")), Env(null));

        Assert.Equal(45, options.TimeoutSeconds);
        Assert.Equal(45, (int)options.Timeout.TotalSeconds);
        Assert.Equal(1234, options.EffectiveMaxOutputCharacters);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("nonsense")]
    public void Resolve_WithNonsensicalLimits_FallsBackToDefaults(string value)
    {
        var options = ShellEscapeOptions.Resolve(Config(
            ("ShellEscape:TimeoutSeconds", value),
            ("ShellEscape:MaxOutputCharacters", value)), Env(null));

        Assert.Equal(ShellEscapeOptions.DefaultTimeoutSeconds, options.TimeoutSeconds);
        Assert.Equal(ShellEscapeOptions.DefaultMaxOutputCharacters, options.EffectiveMaxOutputCharacters);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("maybe", null)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("ON", true)]
    [InlineData("0", false)]
    public void ParseBool_RecognizesTheShapesPeopleWrite(string? input, bool? expected)
    {
        Assert.Equal(expected, ShellEscapeOptions.ParseBool(input));
    }
}
