using System;
using System.IO;
using System.Linq;
using Andy.Cli.Configuration;
using Xunit;

namespace Andy.Cli.Tests.Configuration;

/// <summary>
/// The user and project scopes must merge the same way on macOS, Linux and Windows.
/// The two things that differ between them are the path SHAPE the caller passes in
/// (separators, trailing slashes, relative segments) and whether two spellings of
/// the same path are the same file. Both are pinned here.
/// </summary>
public class ConfigDiscoveryTests
{
    [Fact]
    public void DiscoveryOrder_IsUserThenProjectRootThenProjectDotAndy()
    {
        var discovered = ConfigLayerBuilder.DiscoverFiles(
            Path.Combine(Path.GetTempPath(), "home-280"),
            Path.Combine(Path.GetTempPath(), "ws-280"));

        Assert.Equal(
            new[] { ConfigSourceKind.User, ConfigSourceKind.Project, ConfigSourceKind.Project },
            discovered.Select(d => d.Kind).ToArray());
        Assert.EndsWith(
            Path.Combine(".andy", ConfigSchema.FileName),
            discovered[0].Path,
            StringComparison.Ordinal);
        Assert.EndsWith(ConfigSchema.FileName, discovered[1].Path, StringComparison.Ordinal);
        Assert.EndsWith(
            Path.Combine(".andy", ConfigSchema.FileName),
            discovered[2].Path,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoveredPaths_AreAlwaysAbsolute()
    {
        var discovered = ConfigLayerBuilder.DiscoverFiles("home-280", "ws-280");

        Assert.All(discovered, entry => Assert.True(Path.IsPathRooted(entry.Path)));
    }

    [Theory]
    [InlineData("ws")]
    [InlineData("ws/")]
    [InlineData("ws/.")]
    [InlineData("ws/nested/..")]
    public void EquivalentWorkspaceSpellings_ProduceIdenticalPaths(string relativeWorkspace)
    {
        var root = Path.Combine(Path.GetTempPath(), "andy-shape-280", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, relativeWorkspace.Replace('/', Path.DirectorySeparatorChar));

        var discovered = ConfigLayerBuilder.DiscoverFiles(root, workspace);

        var expected = Path.GetFullPath(Path.Combine(root, "ws", ConfigSchema.FileName));
        Assert.Equal(expected, discovered[1].Path);
    }

    [Fact]
    public void WorkspaceThatIsAlsoTheUserConfigFolder_YieldsNoDuplicateLayer()
    {
        // ~/.andy is a legitimate working directory. Without de-duplication the same
        // file would be loaded twice, once as user and once as project, and its own
        // values would appear to override themselves from a different layer.
        var home = Path.Combine(Path.GetTempPath(), "andy-dup-280", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(home, ".andy");

        var discovered = ConfigLayerBuilder.DiscoverFiles(home, workspace);

        Assert.Equal(
            discovered.Select(d => d.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            discovered.Count);
    }

    [Fact]
    public void SameFileReachedThroughARelativeWorkspace_LoadsOnce()
    {
        using var workspace = new ConfigTestWorkspace();
        workspace.WriteProject("""{ "ui": { "theme": "nord" } }""");

        var direct = new AndyConfigurationService().Load(new ConfigLoadRequest
        {
            WorkspaceDirectory = workspace.WorkspaceDirectory,
            UserHomeDirectory = workspace.HomeDirectory,
            AppSettingsPath = string.Empty,
            EnvironmentOverride = workspace.Environment,
        });

        var roundabout = new AndyConfigurationService().Load(new ConfigLoadRequest
        {
            WorkspaceDirectory = Path.Combine(workspace.WorkspaceDirectory, ".andy", ".."),
            UserHomeDirectory = workspace.HomeDirectory,
            AppSettingsPath = string.Empty,
            EnvironmentOverride = workspace.Environment,
        });

        Assert.Equal(direct.Sources.Count, roundabout.Sources.Count);
        Assert.Equal(
            direct.OriginOf("ui.theme")!.Source.FilePath,
            roundabout.OriginOf("ui.theme")!.Source.FilePath);
        Assert.Equal(direct.Config.Ui.Theme, roundabout.Config.Ui.Theme);
    }

    [Fact]
    public void MissingFiles_AreSimplyAbsentFromTheSourceList()
    {
        using var workspace = new ConfigTestWorkspace();

        var effective = workspace.Load();

        Assert.DoesNotContain(effective.Sources, s => s.Kind == ConfigSourceKind.User);
        Assert.DoesNotContain(effective.Sources, s => s.Kind == ConfigSourceKind.Project);
        Assert.False(effective.HasErrors);
    }

    [Fact]
    public void WindowsStyleBackslashSeparators_ResolveToTheSamePlaceAsForwardSlashes()
    {
        var root = Path.Combine(Path.GetTempPath(), "andy-sep-280", Guid.NewGuid().ToString("N"));

        var forward = ConfigPathResolver.TryResolve("state/sessions", root, out var forwardPath, out _);
        var native = ConfigPathResolver.TryResolve(
            Path.Combine("state", "sessions"), root, out var nativePath, out _);

        Assert.True(forward);
        Assert.True(native);
        Assert.Equal(nativePath, forwardPath);
    }
}
