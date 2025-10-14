using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Tools.Core;
using Andy.Tools.Library;

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
            // Resolve path
            var fullPath = Path.IsPathRooted(path)
                ? path
                : Path.Combine(Directory.GetCurrentDirectory(), path);

            // Normalize path
            fullPath = Path.GetFullPath(fullPath);

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