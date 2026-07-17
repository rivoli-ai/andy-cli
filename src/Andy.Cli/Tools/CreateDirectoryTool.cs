using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Tools.Core;
using Andy.Tools.Library;
using Andy.Tools.Library.Common;

namespace Andy.Cli.Tools;

/// <summary>
/// Tool for creating directories
/// </summary>
public class CreateDirectoryTool : ToolBase
{
    public override ToolMetadata Metadata => new()
    {
        Id = "create_directory",
        Name = "Create Directory",
        Description = "Creates a new directory at the specified path",
        Category = ToolCategory.FileSystem,
        Version = "1.0.0",
        RequiredPermissions = ToolPermissionFlags.FileSystemWrite,
        Parameters = new[]
        {
            new ToolParameter
            {
                Name = "path",
                Type = "string",
                Description = "The path where the directory should be created",
                Required = true
            },
            new ToolParameter
            {
                Name = "create_parents",
                Type = "boolean",
                Description = "Create parent directories if they don't exist",
                Required = false,
                DefaultValue = true
            }
        },
        Examples = new[]
        {
            new ToolExample
            {
                Description = "Create a simple directory",
                Parameters = new Dictionary<string, object?>
                {
                    ["path"] = "new_folder"
                }
            },
            new ToolExample
            {
                Description = "Create nested directories",
                Parameters = new Dictionary<string, object?>
                {
                    ["path"] = "parent/child/grandchild",
                    ["create_parents"] = true
                }
            }
        }
    };

    protected override Task<ToolResult> ExecuteInternalAsync(
        Dictionary<string, object?> parameters,
        ToolExecutionContext context)
    {
        // Get parameters
        var path = parameters["path"]?.ToString() ?? string.Empty;
        var createParents = parameters.ContainsKey("create_parents")
            ? Convert.ToBoolean(parameters["create_parents"])
            : true;

        try
        {
            // Resolve relative paths against the tool execution context's working directory so that
            // containment is evaluated against the workspace rather than the process-wide current
            // directory. Fall back to the current directory only when no working directory is supplied.
            var workingDirectory = string.IsNullOrEmpty(context.WorkingDirectory)
                ? Directory.GetCurrentDirectory()
                : context.WorkingDirectory;

            // Normalize path. Absolute paths are kept as supplied (containment is enforced below);
            // relative paths are combined with the working directory. GetFullPath collapses "." and
            // ".." segments.
            var fullPath = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(workingDirectory, path));

            // Enforce workspace containment before touching the file system. This denies absolute-path
            // escapes, ".." traversal that leaves the allowed roots, and symlink escapes: the
            // containment check canonicalises the deepest existing ancestor via realpath before
            // comparing, so a symlink inside an allowed root that points outside it cannot be used to
            // escape. The same logic runs for headless and interactive execution because both paths
            // funnel through this shared tool.
            if (!IsWithinAllowedRoots(fullPath, workingDirectory, context.Permissions))
            {
                // Report only the user-supplied path. Do not echo the resolved absolute path: for a
                // denied traversal or symlink escape that would disclose the real canonical filesystem
                // location outside the workspace back to the caller.
                return Task.FromResult(ToolResult.Failure(
                    $"Access denied: '{path}' is outside the allowed workspace"));
            }

            // Check if directory already exists
            if (Directory.Exists(fullPath))
            {
                return Task.FromResult(ToolResult.Success($"Directory already exists: {fullPath}"));
            }

            // Create directory
            if (createParents)
            {
                Directory.CreateDirectory(fullPath);
            }
            else
            {
                // Check if parent exists
                var parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                {
                    return Task.FromResult(ToolResult.Failure($"Parent directory does not exist: {parent}"));
                }

                Directory.CreateDirectory(fullPath);
            }

            // Verify creation
            if (Directory.Exists(fullPath))
            {
                var info = new DirectoryInfo(fullPath);

                var result = new Dictionary<string, object?>
                {
                    ["path"] = fullPath,
                    ["created"] = true,
                    ["creation_time"] = info.CreationTime,
                    ["attributes"] = info.Attributes.ToString()
                };

                return Task.FromResult(ToolResult.Success(
                    $"Directory created successfully: {fullPath}",
                    result));
            }
            else
            {
                return Task.FromResult(ToolResult.Failure("Directory creation failed for unknown reason"));
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(ToolResult.Failure($"Access denied: {ex.Message}"));
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(ToolResult.Failure($"Invalid path: {ex.Message}"));
        }
        catch (PathTooLongException ex)
        {
            return Task.FromResult(ToolResult.Failure($"Path too long: {ex.Message}"));
        }
        catch (DirectoryNotFoundException ex)
        {
            return Task.FromResult(ToolResult.Failure($"Directory not found: {ex.Message}"));
        }
        catch (IOException ex)
        {
            return Task.FromResult(ToolResult.Failure($"IO error: {ex.Message}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Failure($"Unexpected error: {ex.Message}"));
        }
    }

    /// <summary>
    /// Determines whether <paramref name="fullPath"/> is contained within the allowed roots for this
    /// execution. When explicit allowed paths are configured on the permissions they are authoritative
    /// and multiple roots are honoured; otherwise the working directory is treated as the single
    /// allowed root. Containment is symlink-aware: the candidate and each root are resolved to their
    /// real, symlink-free locations (with the deepest existing ancestor canonicalised) before the
    /// boundary comparison, so a symlink inside an allowed root that points outside cannot be abused.
    /// </summary>
    internal static bool IsWithinAllowedRoots(
        string fullPath,
        string workingDirectory,
        ToolPermissions? permissions)
    {
        // Explicitly configured allowed roots win. IsPathWithinAllowedPaths resolves the real path of
        // both the candidate and every configured root, so it honours multiple roots and defeats
        // symlink escapes.
        if (permissions?.AllowedPaths is { Count: > 0 })
        {
            return ToolHelpers.IsPathWithinAllowedPaths(fullPath, permissions);
        }

        // No explicit roots configured: confine to the working directory. If neither a working directory
        // nor allowed paths are available we cannot compute a boundary, so fail closed and deny rather
        // than allowing creation at an arbitrary absolute path.
        if (string.IsNullOrEmpty(workingDirectory))
        {
            return false;
        }

        return ToolHelpers.IsPathWithinBoundary(
            ToolHelpers.ResolveRealPath(fullPath),
            ToolHelpers.ResolveRealPath(workingDirectory));
    }

    public override IList<string> ValidateParameters(Dictionary<string, object?> parameters)
    {
        var errors = new List<string>();

        // Call base validation first
        var baseErrors = base.ValidateParameters(parameters);
        errors.AddRange(baseErrors);

        // Get path parameter
        if (parameters.TryGetValue("path", out var pathValue))
        {
            var path = pathValue?.ToString();

            if (string.IsNullOrWhiteSpace(path))
            {
                errors.Add("Path parameter cannot be empty");
            }
            else
            {
                // Check for invalid characters
                var invalidChars = Path.GetInvalidPathChars();
                if (path.Any(c => invalidChars.Contains(c)))
                {
                    errors.Add("Path contains invalid characters");
                }
            }
        }

        return errors;
    }
}