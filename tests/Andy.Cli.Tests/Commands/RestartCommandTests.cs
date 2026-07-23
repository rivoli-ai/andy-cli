using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Commands;
using Xunit;

namespace Andy.Cli.Tests.Commands;

public class RestartCommandTests
{
    [Fact]
    public void Constructor_NullDelegate_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new RestartCommand(null!));
    }

    [Fact]
    public void Metadata_IsRegisteredAsRestart()
    {
        var cmd = new RestartCommand(_ => Task.CompletedTask);

        Assert.Equal("restart", cmd.Name);
        Assert.False(string.IsNullOrWhiteSpace(cmd.Description));
        Assert.Empty(cmd.Aliases);
        Assert.IsAssignableFrom<ICommand>(cmd);
    }

    [Fact]
    public void Metadata_IsPlainAscii()
    {
        var cmd = new RestartCommand(_ => Task.CompletedTask);

        Assert.All(cmd.Description, ch => Assert.True(ch <= 127, $"Non-ASCII character '{ch}' in description"));
        Assert.All(RestartCommand.SuccessMessage, ch => Assert.True(ch <= 127, $"Non-ASCII character '{ch}' in success message"));
    }

    [Fact]
    public async Task ExecuteAsync_NoArgs_InvokesResetExactlyOnceAndSucceeds()
    {
        int calls = 0;
        var cmd = new RestartCommand(_ =>
        {
            calls++;
            return Task.CompletedTask;
        });

        var result = await cmd.ExecuteAsync(Array.Empty<string>());

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, calls);
        Assert.Contains("restarted", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_PassesCancellationTokenToDelegate()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken observed = default;
        var cmd = new RestartCommand(ct =>
        {
            observed = ct;
            return Task.CompletedTask;
        });

        await cmd.ExecuteAsync(Array.Empty<string>(), cts.Token);

        Assert.Equal(cts.Token, observed);
    }

    [Fact]
    public async Task ExecuteAsync_WithArgs_FailsWithoutInvokingReset()
    {
        int calls = 0;
        var cmd = new RestartCommand(_ =>
        {
            calls++;
            return Task.CompletedTask;
        });

        var result = await cmd.ExecuteAsync(new[] { "now" });

        Assert.False(result.Success);
        Assert.Equal(0, calls);
        Assert.Contains("Usage: /restart", result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_DelegateThrows_ReturnsFailureWithReason()
    {
        var cmd = new RestartCommand(_ => throw new InvalidOperationException("provider unavailable"));

        var result = await cmd.ExecuteAsync(Array.Empty<string>());

        Assert.False(result.Success);
        Assert.Contains("provider unavailable", result.Message);
    }
}

public class SlashCommandCatalogTests
{
    [Fact]
    public void InlineHelp_ContainsRestartCommand()
    {
        var commands = SlashCommandCatalog.CreateInlineHelpCommands();

        var restart = Assert.Single(commands, c => c.Name == "restart");
        Assert.False(string.IsNullOrWhiteSpace(restart.Description));
    }

    [Fact]
    public void InlineHelp_ContainsCoreCommands()
    {
        var names = SlashCommandCatalog.CreateInlineHelpCommands().Select(c => c.Name).ToArray();

        Assert.Contains("clear", names);
        Assert.Contains("restart", names);
        Assert.Contains("help", names);
        Assert.Contains("exit", names);
        Assert.Contains("model", names);
    }

    [Fact]
    public void InlineHelp_NamesAndAliasesAreUnique()
    {
        var commands = SlashCommandCatalog.CreateInlineHelpCommands();
        var all = commands.Select(c => c.Name)
            .Concat(commands.SelectMany(c => c.Aliases))
            .ToArray();

        Assert.Equal(all.Length, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
