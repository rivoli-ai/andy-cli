using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Andy.Cli.Tools;
using Andy.Permissions.Authorization;
using Andy.Permissions.Model;
using Andy.Tools.Core;
using Xunit;

namespace Andy.Cli.Tests.Tools;

/// <summary>
/// Verifies that CreateDirectoryTool confines directory creation to the workspace / configured allowed
/// roots: relative paths resolve against the execution context's working directory, and absolute-path,
/// ".." traversal, and symlink escapes are denied after canonical (realpath) resolution.
/// </summary>
public class CreateDirectoryToolTests : IDisposable
{
    private readonly string _root;
    private readonly string _outside;
    private readonly CreateDirectoryTool _tool = new();

    public CreateDirectoryToolTests()
    {
        // A dedicated workspace root and a sibling "outside" directory that must never be reachable.
        var baseDir = Path.Combine(Path.GetTempPath(), $"CreateDirTest_{Guid.NewGuid():N}");
        _root = Path.Combine(baseDir, "workspace");
        _outside = Path.Combine(baseDir, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);

        _tool.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try
        {
            var baseDir = Path.GetDirectoryName(_root);
            if (baseDir != null && Directory.Exists(baseDir))
            {
                Directory.Delete(baseDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static ToolExecutionContext Context(string workingDirectory, params string[] allowedPaths)
    {
        var permissions = new ToolPermissions { FileSystemAccess = true };
        if (allowedPaths.Length > 0)
        {
            permissions.AllowedPaths = new HashSet<string>(allowedPaths);
        }

        return new ToolExecutionContext
        {
            WorkingDirectory = workingDirectory,
            Permissions = permissions
        };
    }

    [Fact]
    public async Task ValidNestedCreation_WithinWorkingDirectory_Succeeds()
    {
        var parameters = new Dictionary<string, object?> { ["path"] = "a/b/c" };

        var result = await _tool.ExecuteAsync(parameters, Context(_root));

        Assert.True(result.IsSuccessful, $"Expected success but got: {result.Message}");
        Assert.True(Directory.Exists(Path.Combine(_root, "a", "b", "c")));
    }

    [Fact]
    public async Task RelativePath_ResolvesAgainstWorkingDirectory_NotCurrentDirectory()
    {
        // The process current directory is not the workspace root; a relative path must land under the
        // context working directory.
        var parameters = new Dictionary<string, object?> { ["path"] = "resolved_here" };

        var result = await _tool.ExecuteAsync(parameters, Context(_root));

        Assert.True(result.IsSuccessful, $"Expected success but got: {result.Message}");
        Assert.True(Directory.Exists(Path.Combine(_root, "resolved_here")));
    }

    [Fact]
    public async Task MissingIntermediateParents_CreatedWithinRoot()
    {
        var parameters = new Dictionary<string, object?>
        {
            ["path"] = "deep/nested/tree/leaf",
            ["create_parents"] = true
        };

        var result = await _tool.ExecuteAsync(parameters, Context(_root));

        Assert.True(result.IsSuccessful, $"Expected success but got: {result.Message}");
        Assert.True(Directory.Exists(Path.Combine(_root, "deep", "nested", "tree", "leaf")));
    }

    [Fact]
    public async Task AbsolutePathEscape_IsDenied()
    {
        var escapeTarget = Path.Combine(_outside, "sneaky");
        var parameters = new Dictionary<string, object?> { ["path"] = escapeTarget };

        var result = await _tool.ExecuteAsync(parameters, Context(_root));

        Assert.False(result.IsSuccessful);
        Assert.Contains("Access denied", result.ErrorMessage);
        Assert.False(Directory.Exists(escapeTarget));
    }

    [Fact]
    public async Task DotDotTraversalEscape_IsDenied()
    {
        // ".." climbs out of the workspace into the sibling "outside" directory.
        var parameters = new Dictionary<string, object?> { ["path"] = Path.Combine("..", "outside", "via_traversal") };

        var result = await _tool.ExecuteAsync(parameters, Context(_root));

        Assert.False(result.IsSuccessful);
        Assert.Contains("Access denied", result.ErrorMessage);
        Assert.False(Directory.Exists(Path.Combine(_outside, "via_traversal")));
    }

    [Fact]
    public async Task SymlinkEscape_IsDenied()
    {
        // A symlink inside the workspace points at the outside directory; creating "through" it must be
        // rejected because the canonical (realpath) target is outside the root.
        var linkPath = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(linkPath, _outside);

        var parameters = new Dictionary<string, object?> { ["path"] = Path.Combine("link", "escaped") };

        var result = await _tool.ExecuteAsync(parameters, Context(_root));

        Assert.False(result.IsSuccessful);
        Assert.Contains("Access denied", result.ErrorMessage);
        Assert.False(Directory.Exists(Path.Combine(_outside, "escaped")));
    }

    [Fact]
    public async Task MultipleAllowedRoots_AreHonored()
    {
        // Two configured roots; the working directory is the first, but creation targets the second.
        var secondRoot = Path.Combine(Path.GetDirectoryName(_root)!, "second");
        Directory.CreateDirectory(secondRoot);
        var target = Path.Combine(secondRoot, "child");

        var parameters = new Dictionary<string, object?> { ["path"] = target };

        var result = await _tool.ExecuteAsync(parameters, Context(_root, _root, secondRoot));

        Assert.True(result.IsSuccessful, $"Expected success but got: {result.Message}");
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public async Task AbsolutePathOutsideAllowedRoots_IsDenied()
    {
        // AllowedPaths is authoritative: an absolute path outside every configured root is denied even
        // though FileSystemAccess is granted.
        var target = Path.Combine(_outside, "not_allowed");
        var parameters = new Dictionary<string, object?> { ["path"] = target };

        var result = await _tool.ExecuteAsync(parameters, Context(_root, _root));

        Assert.False(result.IsSuccessful);
        Assert.Contains("Access denied", result.ErrorMessage);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void ActionResolver_ExtractsCreateDirectoryPath_AsPathResource()
    {
        // Mirrors the CLI wiring in AddAndyCliPermissions: register create_directory's "path" parameter
        // as a Path resource so permission evaluation receives the canonical target.
        var resolver = new DefaultToolActionResolver();

        // The packaged default has no mapping for create_directory.
        Assert.Empty(resolver.Resolve("create_directory", new Dictionary<string, object>
        {
            ["path"] = "/tmp/whatever"
        }));

        resolver.Register("create_directory", ("path", ResourceKind.Path));

        var resources = resolver.Resolve("create_directory", new Dictionary<string, object>
        {
            ["path"] = "/tmp/whatever"
        });

        Assert.Contains(resources, r => r.Kind == ResourceKind.Path && r.Value == "/tmp/whatever");

        // Built-in mappings remain intact after using the parameterless constructor plus Register.
        var writeResources = resolver.Resolve("write_file", new Dictionary<string, object>
        {
            ["file_path"] = "/tmp/x.txt"
        });
        Assert.Contains(writeResources, r => r.Kind == ResourceKind.Path);
    }
}
