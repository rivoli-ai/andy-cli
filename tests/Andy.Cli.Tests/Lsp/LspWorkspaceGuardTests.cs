using System;
using System.IO;
using Andy.Cli.Lsp;
using Xunit;

namespace Andy.Cli.Tests.Lsp;

/// <summary>
/// The containment rules from issue #282: neither a changed file nor a discovered server root may
/// leave the active workspace without an explicit opt-in.
/// </summary>
public sealed class LspWorkspaceGuardTests
{
    [Fact]
    public void FilesInsideTheWorkspaceAreAllowed()
    {
        using var workspace = new LspTestWorkspace();
        var path = workspace.WriteFile("nested/deep/file.fake", "x");

        Assert.True(LspWorkspaceGuard.IsWithinWorkspace(workspace.Root, path));
        Assert.True(LspWorkspaceGuard.IsWithinWorkspace(workspace.Root, workspace.Root));
    }

    [Fact]
    public void ParentTraversalIsRejected()
    {
        using var workspace = new LspTestWorkspace();
        var escape = Path.Combine(workspace.Root, "..", "..", "etc", "passwd");

        Assert.False(LspWorkspaceGuard.IsWithinWorkspace(workspace.Root, escape));
    }

    [Fact]
    public void SiblingDirectoriesWithASharedPrefixAreRejected()
    {
        // "/tmp/ws" must not accidentally contain "/tmp/ws-evil" through a naive prefix check.
        using var workspace = new LspTestWorkspace();
        var sibling = workspace.Root + "-evil";

        Assert.False(LspWorkspaceGuard.IsWithinWorkspace(workspace.Root, sibling));
    }

    [Fact]
    public void ASymlinkPointingOutOfTheWorkspaceIsRejected()
    {
        if (OperatingSystem.IsWindows()) return; // creating links needs elevation on Windows

        using var workspace = new LspTestWorkspace();
        using var outside = new LspTestWorkspace();
        var target = outside.WriteFile("secret.fake", "x");

        var link = Path.Combine(workspace.Root, "link.fake");
        File.CreateSymbolicLink(link, target);

        Assert.False(LspWorkspaceGuard.IsWithinWorkspace(workspace.Root, link));
    }

    [Fact]
    public void ASymlinkedDirectoryPointingOutOfTheWorkspaceIsRejected()
    {
        if (OperatingSystem.IsWindows()) return;

        using var workspace = new LspTestWorkspace();
        using var outside = new LspTestWorkspace();
        outside.WriteFile("secret.fake", "x");

        var link = Path.Combine(workspace.Root, "linked");
        Directory.CreateSymbolicLink(link, outside.Root);

        Assert.False(LspWorkspaceGuard.IsWithinWorkspace(workspace.Root, Path.Combine(link, "secret.fake")));
    }

    [Fact]
    public void ProjectRootStopsAtTheNearestMarker()
    {
        using var workspace = new LspTestWorkspace();
        workspace.WriteFile("service/project.marker", "");
        var file = workspace.WriteFile("service/src/a.fake", "x");

        var root = LspWorkspaceGuard.FindProjectRoot(workspace.Root, file, new[] { "project.marker" });

        Assert.Equal(Path.Combine(workspace.Root, "service"), root);
    }

    [Fact]
    public void ProjectRootMatchesGlobMarkers()
    {
        using var workspace = new LspTestWorkspace();
        workspace.WriteFile("service/Thing.sln", "");
        var file = workspace.WriteFile("service/src/a.fake", "x");

        var root = LspWorkspaceGuard.FindProjectRoot(workspace.Root, file, new[] { "*.sln" });

        Assert.Equal(Path.Combine(workspace.Root, "service"), root);
    }

    [Fact]
    public void ProjectRootNeverWalksAboveTheWorkspace()
    {
        // A marker in a PARENT of the workspace must not pull the server's root out of it.
        using var outer = new LspTestWorkspace();
        var workspaceRoot = Path.Combine(outer.Root, "inner");
        Directory.CreateDirectory(workspaceRoot);
        File.WriteAllText(Path.Combine(outer.Root, "go.mod"), "module x");

        var file = Path.Combine(workspaceRoot, "src", "a.fake");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");

        var root = LspWorkspaceGuard.FindProjectRoot(workspaceRoot, file, new[] { "go.mod" });

        Assert.Equal(workspaceRoot, root);
        Assert.True(LspWorkspaceGuard.IsWithinWorkspace(workspaceRoot, root!));
    }

    [Fact]
    public void ProjectRootIsNullForFilesOutsideTheWorkspace()
    {
        using var workspace = new LspTestWorkspace();
        using var elsewhere = new LspTestWorkspace();
        var file = elsewhere.WriteFile("a.fake", "x");

        Assert.Null(LspWorkspaceGuard.FindProjectRoot(workspace.Root, file, Array.Empty<string>()));
    }
}
