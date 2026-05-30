using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Andy.Cli.Commands;
using Andy.Cli.Services;
using Andy.Cli.Themes;
using Xunit;

namespace Andy.Cli.Tests.Themes;

public class ThemeSwitchingTests
{
    // ----- Theme lookup by name -----

    [Fact]
    public void GetByName_KnownName_ReturnsTheme()
    {
        var theme = Theme.GetByName("dark");
        Assert.NotNull(theme);
        Assert.Equal("dark", theme!.Name);
        Assert.Same(Theme.Dark, theme);
    }

    [Theory]
    [InlineData("DARK")]
    [InlineData("Dark")]
    [InlineData("  light  ")]
    [InlineData("LIGHT")]
    public void GetByName_IsCaseInsensitiveAndTrims(string name)
    {
        var theme = Theme.GetByName(name);
        Assert.NotNull(theme);
        Assert.Equal(name.Trim().ToLowerInvariant(), theme!.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("does-not-exist")]
    public void GetByName_UnknownOrEmpty_ReturnsNull(string? name)
    {
        Assert.Null(Theme.GetByName(name));
    }

    [Fact]
    public void AvailableThemes_ContainsDarkAndLight()
    {
        Assert.Contains("dark", Theme.AvailableThemes);
        Assert.Contains("light", Theme.AvailableThemes);
    }

    // ----- Persistence round-trip -----

    [Fact]
    public void SaveThenLoad_ReturnsSameThemeName()
    {
        var path = Path.Combine(Path.GetTempPath(), "andy-theme-test-" + Guid.NewGuid().ToString("N"), "theme-memory.json");
        try
        {
            var service = new ThemeMemoryService(path);
            service.SaveTheme("light");

            var reloaded = new ThemeMemoryService(path);
            Assert.Equal("light", reloaded.LoadTheme());
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Load_NoFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), "andy-theme-test-" + Guid.NewGuid().ToString("N"), "theme-memory.json");
        try
        {
            var service = new ThemeMemoryService(path);
            Assert.Null(service.LoadTheme());
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // ----- Command behavior -----

    [Fact]
    public async Task Command_SwitchKnownTheme_SetsCurrentAndPersists()
    {
        var original = Theme.Current;
        var path = Path.Combine(Path.GetTempPath(), "andy-theme-test-" + Guid.NewGuid().ToString("N"), "theme-memory.json");
        try
        {
            var service = new ThemeMemoryService(path);
            var command = new ThemeCommand(service);

            var result = await command.ExecuteAsync(new[] { "light" });

            Assert.True(result.Success);
            Assert.Same(Theme.Light, Theme.Current);
            Assert.Equal("light", service.LoadTheme());
        }
        finally
        {
            Theme.Current = original;
            var dir = Path.GetDirectoryName(path);
            if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Command_SwitchUnknownTheme_ReportsErrorAndDoesNotChangeCurrent()
    {
        var original = Theme.Current;
        var path = Path.Combine(Path.GetTempPath(), "andy-theme-test-" + Guid.NewGuid().ToString("N"), "theme-memory.json");
        try
        {
            Theme.Current = Theme.Dark;
            var service = new ThemeMemoryService(path);
            var command = new ThemeCommand(service);

            var result = await command.ExecuteAsync(new[] { "not-a-theme" });

            Assert.False(result.Success);
            Assert.Contains("Unknown theme", result.Message);
            Assert.Same(Theme.Dark, Theme.Current);
            Assert.Null(service.LoadTheme());
        }
        finally
        {
            Theme.Current = original;
            var dir = Path.GetDirectoryName(path);
            if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Command_NoArgs_ListsThemesAndMarksCurrent()
    {
        var original = Theme.Current;
        try
        {
            Theme.Current = Theme.Dark;
            var command = new ThemeCommand(new ThemeMemoryService(
                Path.Combine(Path.GetTempPath(), "andy-theme-test-" + Guid.NewGuid().ToString("N"), "theme-memory.json")));

            var result = await command.ExecuteAsync(Array.Empty<string>());

            Assert.True(result.Success);
            Assert.Contains("dark", result.Message);
            Assert.Contains("light", result.Message);
            Assert.Contains("(current)", result.Message);
        }
        finally
        {
            Theme.Current = original;
        }
    }
}
