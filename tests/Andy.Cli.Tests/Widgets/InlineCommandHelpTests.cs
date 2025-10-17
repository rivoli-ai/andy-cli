using Andy.Cli.Widgets;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

public class InlineCommandHelpTests
{
    [Fact]
    public void GetHeight_WithNoCommands_ReturnsZero()
    {
        // Arrange
        var help = new InlineCommandHelp();

        // Act
        var height = help.GetHeight();

        // Assert
        Assert.Equal(0, height);
    }

    [Fact]
    public void GetHeight_AfterUpdateFilter_WithoutSlash_ReturnsZero()
    {
        // Arrange
        var help = new InlineCommandHelp();
        help.SetCommands(new[]
        {
            new InlineCommandHelp.CommandInfo { Name = "model", Description = "Manage models", Aliases = new[] { "m" } },
            new InlineCommandHelp.CommandInfo { Name = "help", Description = "Show help", Aliases = System.Array.Empty<string>() }
        });

        // Act
        help.UpdateFilter("some text");
        var height = help.GetHeight();

        // Assert
        Assert.Equal(0, height);
    }

    [Fact]
    public void UpdateFilter_WithSlashOnly_ShowsAllCommands()
    {
        // Arrange
        var help = new InlineCommandHelp();
        help.SetCommands(new[]
        {
            new InlineCommandHelp.CommandInfo { Name = "model", Description = "Manage models", Aliases = new[] { "m" } },
            new InlineCommandHelp.CommandInfo { Name = "tools", Description = "Manage tools", Aliases = new[] { "t" } },
            new InlineCommandHelp.CommandInfo { Name = "help", Description = "Show help", Aliases = System.Array.Empty<string>() }
        });

        // Act
        help.UpdateFilter("/");
        var height = help.GetHeight();

        // Assert - Should show all 3 commands + 2 for borders = 5
        Assert.Equal(5, height);
    }

    [Fact]
    public void UpdateFilter_WithPartialCommand_FiltersCommands()
    {
        // Arrange
        var help = new InlineCommandHelp();
        help.SetCommands(new[]
        {
            new InlineCommandHelp.CommandInfo { Name = "model", Description = "Manage models", Aliases = new[] { "m" } },
            new InlineCommandHelp.CommandInfo { Name = "tools", Description = "Manage tools", Aliases = new[] { "t" } },
            new InlineCommandHelp.CommandInfo { Name = "help", Description = "Show help", Aliases = System.Array.Empty<string>() }
        });

        // Act
        help.UpdateFilter("/mod");
        var height = help.GetHeight();

        // Assert - Should show only "model" command + 2 for borders = 3
        Assert.Equal(3, height);
    }

    [Fact]
    public void UpdateFilter_WithAlias_FindsCommand()
    {
        // Arrange
        var help = new InlineCommandHelp();
        help.SetCommands(new[]
        {
            new InlineCommandHelp.CommandInfo { Name = "model", Description = "Manage models", Aliases = new[] { "m" } },
            new InlineCommandHelp.CommandInfo { Name = "tools", Description = "Manage tools", Aliases = new[] { "t" } }
        });

        // Act
        help.UpdateFilter("/m");
        var height = help.GetHeight();

        // Assert - Should find "model" via alias "m" + 2 for borders = 3
        Assert.Equal(3, height);
    }

    [Fact]
    public void UpdateFilter_WithSpaceInCommand_ExtractsCommandOnly()
    {
        // Arrange
        var help = new InlineCommandHelp();
        help.SetCommands(new[]
        {
            new InlineCommandHelp.CommandInfo { Name = "model", Description = "Manage models", Aliases = new[] { "m" } }
        });

        // Act - Text after space should be ignored
        help.UpdateFilter("/model list");
        var height = help.GetHeight();

        // Assert - Should still match "model" + 2 for borders = 3
        Assert.Equal(3, height);
    }

    [Fact]
    public void UpdateFilter_WithNoMatch_ReturnsZero()
    {
        // Arrange
        var help = new InlineCommandHelp();
        help.SetCommands(new[]
        {
            new InlineCommandHelp.CommandInfo { Name = "model", Description = "Manage models", Aliases = new[] { "m" } }
        });

        // Act
        help.UpdateFilter("/xyz");
        var height = help.GetHeight();

        // Assert
        Assert.Equal(0, height);
    }

    [Fact]
    public void UpdateFilter_WithMoreThanFiveCommands_LimitsToFive()
    {
        // Arrange
        var help = new InlineCommandHelp();
        help.SetCommands(new[]
        {
            new InlineCommandHelp.CommandInfo { Name = "cmd1", Description = "Command 1", Aliases = System.Array.Empty<string>() },
            new InlineCommandHelp.CommandInfo { Name = "cmd2", Description = "Command 2", Aliases = System.Array.Empty<string>() },
            new InlineCommandHelp.CommandInfo { Name = "cmd3", Description = "Command 3", Aliases = System.Array.Empty<string>() },
            new InlineCommandHelp.CommandInfo { Name = "cmd4", Description = "Command 4", Aliases = System.Array.Empty<string>() },
            new InlineCommandHelp.CommandInfo { Name = "cmd5", Description = "Command 5", Aliases = System.Array.Empty<string>() },
            new InlineCommandHelp.CommandInfo { Name = "cmd6", Description = "Command 6", Aliases = System.Array.Empty<string>() },
            new InlineCommandHelp.CommandInfo { Name = "cmd7", Description = "Command 7", Aliases = System.Array.Empty<string>() }
        });

        // Act
        help.UpdateFilter("/cmd");
        var height = help.GetHeight();

        // Assert - Should show max 5 commands + 2 for borders = 7
        Assert.Equal(7, height);
    }
}
