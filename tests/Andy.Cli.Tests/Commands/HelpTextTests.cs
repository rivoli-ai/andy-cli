using System.Linq;
using Andy.Cli.Commands;
using Xunit;

namespace Andy.Cli.Tests.Commands;

public class HelpTextTests
{
    [Fact]
    public void EveryCatalogCommand_AppearsInInteractiveHelp()
    {
        var help = HelpText.InteractiveHelpMarkdown();
        foreach (var command in SlashCommandCatalog.CreateInlineHelpCommands())
        {
            Assert.Contains("/" + command.Name, help);
        }
    }

    [Fact]
    public void Catalog_IncludesPermissionsAndSkills()
    {
        var names = SlashCommandCatalog.CreateInlineHelpCommands().Select(c => c.Name).ToArray();
        Assert.Contains("permissions", names);
        Assert.Contains("skills", names);
        Assert.Contains("mcp", names);
        Assert.Contains("theme", names);
    }

    [Fact]
    public void InteractiveHelp_DocumentsRecentlyAddedCommandsAndSubcommands()
    {
        var help = HelpText.InteractiveHelpMarkdown();

        Assert.Contains("/restart", help);

        // Permission management surface from issue #232.
        Assert.Contains("/permissions revoke", help);
        Assert.Contains("/permissions reset", help);

        // Skills surface from issue #238.
        Assert.Contains("/skills enable|disable", help);
        Assert.Contains("/skills diagnostics", help);

        // MCP was missing from the palette copy of the help before consolidation.
        Assert.Contains("/mcp list", help);
        Assert.Contains("/mcp status", help);
    }

    [Fact]
    public void InteractiveHelp_DocumentsKeyBindings()
    {
        var help = HelpText.InteractiveHelpMarkdown();
        foreach (var binding in new[] { "Ctrl+P", "Ctrl+]", "Ctrl+D", "F2", "F3", "Ctrl+O", "ESC", "Ctrl+K", "Ctrl+U" })
        {
            Assert.Contains(binding, help);
        }
    }

    [Fact]
    public void HelpTexts_AreAsciiOnly()
    {
        // Project rule: no emojis, ASCII characters only for terminal UI text.
        foreach (var text in new[] { HelpText.InteractiveHelpMarkdown(), HelpText.CommandLineHelp() })
        {
            var offenders = text.Where(ch => ch != '\n' && (ch < ' ' || ch > '~')).Distinct().ToArray();
            Assert.True(offenders.Length == 0,
                "Non-ASCII characters in help text: " + string.Join(", ", offenders.Select(c => $"U+{(int)c:X4}")));
        }
    }

    [Fact]
    public void CommandLineHelp_ListsEveryOneShotCommand()
    {
        var help = HelpText.CommandLineHelp();
        foreach (var command in new[] { "model", "tools", "permissions", "skills", "help" })
        {
            Assert.Contains(command, help);
        }
    }
}
