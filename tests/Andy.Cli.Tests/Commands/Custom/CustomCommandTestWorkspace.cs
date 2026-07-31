using System;
using System.IO;
using Andy.Cli.Commands.Custom;

namespace Andy.Cli.Tests.Commands.Custom;

/// <summary>
/// An isolated home + workspace pair with real <c>.andy/commands</c> directories, so the
/// discovery tests exercise the actual filesystem walk rather than a stubbed one.
/// </summary>
public sealed class CustomCommandTestWorkspace : IDisposable
{
    public CustomCommandTestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "andy-cmd-" + Guid.NewGuid().ToString("N"));
        Home = Path.Combine(Root, "home");
        Workspace = Path.Combine(Root, "workspace");
        UserCommands = Path.Combine(Home, ".andy", "commands");
        ProjectCommands = Path.Combine(Workspace, ".andy", "commands");
        Directory.CreateDirectory(UserCommands);
        Directory.CreateDirectory(ProjectCommands);
    }

    public string Root { get; }
    public string Home { get; }
    public string Workspace { get; }
    public string UserCommands { get; }
    public string ProjectCommands { get; }

    /// <summary>Write a user-scope command file at a path relative to ~/.andy/commands.</summary>
    public string WriteUser(string relativePath, string content) => Write(UserCommands, relativePath, content);

    /// <summary>Write a project-scope command file at a path relative to .andy/commands.</summary>
    public string WriteProject(string relativePath, string content) => Write(ProjectCommands, relativePath, content);

    /// <summary>Write an ordinary workspace file (used by the @file mention tests).</summary>
    public string WriteWorkspaceFile(string relativePath, string content) => Write(Workspace, relativePath, content);

    private static string Write(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public CustomCommandCatalog Catalog(CustomCommandLimits? limits = null)
        => new(Workspace, Home, limits);

    public CustomCommandDiscoveryResult Discover(CustomCommandLimits? limits = null)
        => CustomCommandDiscovery.Discover(Workspace, Home, limits);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}
