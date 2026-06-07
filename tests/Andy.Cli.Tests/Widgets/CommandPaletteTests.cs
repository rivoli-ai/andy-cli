using System;
using System.Threading.Tasks;
using Andy.Cli.Widgets;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

/// <summary>
/// Guards command-palette execution. Commands with async bodies must be registered
/// as <see cref="CommandPalette.CommandItem.AsyncAction"/> so the palette awaits them
/// to completion; an async lambda assigned to the synchronous
/// <see cref="CommandPalette.CommandItem.Action"/> is fire-and-forget (async void) and
/// its work can be lost when the palette closes — that was the "/list doesn't run" bug.
/// </summary>
public class CommandPaletteTests
{
    private static CommandPalette PaletteWith(params CommandPalette.CommandItem[] items)
    {
        var p = new CommandPalette();
        p.SetCommands(items);
        p.Open();
        return p;
    }

    [Fact]
    public void Query_list_SelectsTheAliasedCommand()
    {
        var p = PaletteWith(
            new CommandPalette.CommandItem { Name = "List Models", Aliases = new[] { "models", "list" }, AsyncAction = _ => Task.CompletedTask },
            new CommandPalette.CommandItem { Name = "Switch Model", Aliases = new[] { "switch" }, AsyncAction = _ => Task.CompletedTask });

        p.SetQuery("list");

        Assert.Equal("List Models", p.GetSelected()?.Name);
    }

    [Fact]
    public async Task ExecuteSelected_AwaitsAsyncActionToCompletion_AndCloses()
    {
        bool ran = false;
        var p = PaletteWith(new CommandPalette.CommandItem
        {
            Name = "List Models",
            Aliases = new[] { "list" },
            // Completes only after yielding; a fire-and-forget call would return before this runs.
            AsyncAction = async _ => { await Task.Yield(); await Task.Delay(10); ran = true; }
        });
        p.SetQuery("list");

        await p.ExecuteSelectedAsync();

        Assert.True(ran, "AsyncAction should be awaited to completion before ExecuteSelectedAsync returns");
        Assert.False(p.IsOpen, "palette should close after running a no-parameter command");
    }

    [Fact]
    public async Task ExecuteSelected_RunsSyncAction()
    {
        bool ran = false;
        var p = PaletteWith(new CommandPalette.CommandItem
        {
            Name = "Clear",
            Aliases = new[] { "clear" },
            Action = _ => ran = true
        });
        p.SetQuery("clear");

        await p.ExecuteSelectedAsync();

        Assert.True(ran);
        Assert.False(p.IsOpen);
    }

    [Fact]
    public async Task ExecuteSelected_WithRequiredParams_EntersParamModeInsteadOfRunning()
    {
        bool ran = false;
        var p = PaletteWith(new CommandPalette.CommandItem
        {
            Name = "Tool Info",
            Aliases = new[] { "info" },
            RequiredParams = new[] { "tool" },
            AsyncAction = _ => { ran = true; return Task.CompletedTask; }
        });
        p.SetQuery("info");

        await p.ExecuteSelectedAsync();

        Assert.True(p.IsWaitingForParams(), "a command with required params should switch to parameter input");
        Assert.False(ran, "it should not run until parameters are confirmed");

        // Confirm with a parameter -> now it runs and closes.
        p.SetParamInput("copy_file");
        await p.ExecuteSelectedAsync();
        Assert.True(ran);
        Assert.False(p.IsOpen);
    }
}
