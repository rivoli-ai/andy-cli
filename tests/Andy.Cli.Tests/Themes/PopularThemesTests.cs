using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Andy.Cli.Commands;
using Andy.Cli.Services;
using Andy.Cli.Themes;
using Xunit;

namespace Andy.Cli.Tests.Themes;

public class PopularThemesTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "andy-theme-test-" + Guid.NewGuid().ToString("N"), "theme-memory.json");

    // ----- New themes are registered -----

    [Theory]
    [InlineData("dracula")]
    [InlineData("nord")]
    [InlineData("gruvbox-dark")]
    [InlineData("catppuccin-mocha")]
    [InlineData("tokyo-night")]
    [InlineData("solarized-dark")]
    public void GetByName_FindsPopularThemes(string name)
    {
        var theme = Theme.GetByName(name);
        Assert.NotNull(theme);
        Assert.Equal(name, theme!.Name);
    }

    [Fact]
    public void AvailableThemes_IncludesBuiltinsAndPopular()
    {
        Assert.Contains("dark", Theme.AvailableThemes);
        Assert.Contains("light", Theme.AvailableThemes);
        Assert.Contains("dracula", Theme.AvailableThemes);
        // Built-ins (2) plus the 32 popular ports.
        Assert.Equal(34, Theme.AvailableThemes.Count);
    }

    [Fact]
    public void AvailableThemes_NamesAreUnique()
    {
        var names = Theme.AvailableThemes.ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void DraculaPalette_MatchesCanonicalColors()
    {
        var d = Theme.GetByName("dracula")!;
        Assert.Equal(new Andy.Tui.DisplayList.Rgb24(0x28, 0x2a, 0x36), d.Background);
        Assert.Equal(new Andy.Tui.DisplayList.Rgb24(0xf8, 0xf8, 0xf2), d.Text);
        Assert.Equal(new Andy.Tui.DisplayList.Rgb24(0xff, 0x55, 0x55), d.Error);
    }

    // ----- Transparent-background option ("the check") -----

    [Fact]
    public void DarkThemes_OfferTransparency_LightThemesDoNot()
    {
        Assert.True(Theme.GetByName("dracula")!.OffersTransparentBackground);
        Assert.True(Theme.GetByName("gruvbox-dark")!.OffersTransparentBackground);
        Assert.False(Theme.GetByName("gruvbox-light")!.OffersTransparentBackground);
        Assert.False(Theme.GetByName("catppuccin-latte")!.OffersTransparentBackground);
    }

    [Fact]
    public void WithTransparentBackground_NullsMainSurfaces_KeepsChrome()
    {
        var solid = Theme.GetByName("dracula")!;
        Assert.False(solid.HasTransparentBackground);

        var transparent = solid.WithTransparentBackground();
        Assert.True(transparent.HasTransparentBackground);
        Assert.Null(transparent.Background);
        Assert.Null(transparent.HeaderBackground);
        Assert.Null(transparent.DialogBackground);
        Assert.Null(transparent.PromptBackground);
        // Chrome keeps a shade for legibility.
        Assert.Equal(solid.StatusLineBackground, transparent.StatusLineBackground);
        // Original instance is untouched.
        Assert.False(solid.HasTransparentBackground);
    }

    [Fact]
    public void Resolve_AppliesTransparency_OnlyWhenOffered()
    {
        Assert.True(Theme.Resolve("dracula", transparentBackground: true)!.HasTransparentBackground);
        Assert.False(Theme.Resolve("dracula", transparentBackground: false)!.HasTransparentBackground);
        // Light theme opts out: stays solid even when requested.
        Assert.False(Theme.Resolve("gruvbox-light", transparentBackground: true)!.HasTransparentBackground);
        Assert.Null(Theme.Resolve("does-not-exist", true));
    }

    // ----- Command: switching and toggling -----

    [Fact]
    public async Task Command_SwitchToPopularTheme_SetsCurrentAndPersists()
    {
        var original = Theme.Current;
        var path = TempPath();
        try
        {
            var service = new ThemeMemoryService(path);
            var command = new ThemeCommand(service);

            var result = await command.ExecuteAsync(new[] { "nord" });

            Assert.True(result.Success);
            Assert.Equal("nord", Theme.Current.Name);
            Assert.Equal("nord", service.LoadTheme());
        }
        finally
        {
            Theme.Current = original;
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public async Task Command_TransparentOn_TogglesAndPersists()
    {
        var original = Theme.Current;
        var path = TempPath();
        try
        {
            var service = new ThemeMemoryService(path);
            var command = new ThemeCommand(service);

            await command.ExecuteAsync(new[] { "dracula" });
            Assert.False(Theme.Current.HasTransparentBackground);

            var result = await command.ExecuteAsync(new[] { "transparent", "on" });

            Assert.True(result.Success);
            Assert.True(Theme.Current.HasTransparentBackground);
            Assert.True(service.LoadTransparentBackground());
            Assert.Equal("dracula", service.LoadTheme());

            // And it persists across a fresh switch back to the same base theme.
            await command.ExecuteAsync(new[] { "dracula" });
            Assert.True(Theme.Current.HasTransparentBackground);
        }
        finally
        {
            Theme.Current = original;
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public async Task Command_TransparentOff_RestoresSolidBackground()
    {
        var original = Theme.Current;
        var path = TempPath();
        try
        {
            var service = new ThemeMemoryService(path);
            var command = new ThemeCommand(service);

            await command.ExecuteAsync(new[] { "nord", });
            await command.ExecuteAsync(new[] { "transparent", "on" });
            Assert.True(Theme.Current.HasTransparentBackground);

            var result = await command.ExecuteAsync(new[] { "transparent", "off" });

            Assert.True(result.Success);
            Assert.False(Theme.Current.HasTransparentBackground);
            Assert.False(service.LoadTransparentBackground());
        }
        finally
        {
            Theme.Current = original;
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public async Task Command_Transparent_OnThemeThatDoesNotOffer_Fails()
    {
        var original = Theme.Current;
        var path = TempPath();
        try
        {
            var service = new ThemeMemoryService(path);
            var command = new ThemeCommand(service);

            await command.ExecuteAsync(new[] { "gruvbox-light" });
            var result = await command.ExecuteAsync(new[] { "transparent", "on" });

            Assert.False(result.Success);
            Assert.Contains("does not offer", result.Message);
        }
        finally
        {
            Theme.Current = original;
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public async Task Command_List_ShowsTransparencyCheck()
    {
        var original = Theme.Current;
        var path = TempPath();
        try
        {
            var service = new ThemeMemoryService(path);
            var command = new ThemeCommand(service);
            await command.ExecuteAsync(new[] { "dracula" });
            await command.ExecuteAsync(new[] { "transparent", "on" });

            var result = await command.ExecuteAsync(Array.Empty<string>());

            Assert.True(result.Success);
            Assert.Contains("Transparent background: [x] on", result.Message);
            Assert.Contains("[offers transparent bg]", result.Message);
        }
        finally
        {
            Theme.Current = original;
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }
}
