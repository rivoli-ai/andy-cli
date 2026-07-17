using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Tools.Core;
using Andy.Tools.Library;
using Microsoft.Extensions.DependencyInjection;

namespace Andy.Cli.Tools;

/// <summary>
/// Tool for searching and querying the codebase structure using semantic index
/// </summary>
public class CodeIndexTool : ToolBase
{
    private readonly IServiceProvider? _serviceProvider;
    private CodeIndexingService? _indexingService;

    public override ToolMetadata Metadata => new()
    {
        Id = "code_index",
        Name = "Code Index Search",
        Description = "Search and query the codebase structure using semantic index. Use this for understanding code organization, finding symbols, and exploring relationships.",
        Category = ToolCategory.FileSystem,
        Version = "1.0.0",
        RequiredPermissions = ToolPermissionFlags.FileSystemRead,
        Parameters = new[]
        {
            new ToolParameter
            {
                Name = "query_type",
                Type = "string",
                Description = "Type of query: 'symbols' (find classes/methods/properties), 'structure' (get project structure), 'hierarchy' (class/interface inheritance: base types and derived types)",
                Required = true,
                AllowedValues = new[] { "symbols", "structure", "hierarchy" }
            },
            new ToolParameter
            {
                Name = "pattern",
                Type = "string",
                Description = "Search pattern or symbol name (supports wildcards: * and ?)",
                Required = false
            },
            new ToolParameter
            {
                Name = "scope",
                Type = "string",
                Description = "Scope to search in (file path, directory, or 'all' for entire project)",
                Required = false,
                DefaultValue = "all"
            },
            new ToolParameter
            {
                Name = "include_private",
                Type = "boolean",
                Description = "Include private members in search results",
                Required = false,
                DefaultValue = false
            }
        },
        Examples = new[]
        {
            new ToolExample
            {
                Description = "Find all classes in the codebase",
                Parameters = new Dictionary<string, object?>
                {
                    ["query_type"] = "symbols",
                    ["pattern"] = "*"
                }
            },
            new ToolExample
            {
                Description = "Get project structure",
                Parameters = new Dictionary<string, object?>
                {
                    ["query_type"] = "structure"
                }
            },
            new ToolExample
            {
                Description = "Get the inheritance hierarchy for a class",
                Parameters = new Dictionary<string, object?>
                {
                    ["query_type"] = "hierarchy",
                    ["pattern"] = "AiConversationService"
                }
            }
        }
    };

    public CodeIndexTool()
    {
    }

    public CodeIndexTool(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    protected override async Task<ToolResult> ExecuteInternalAsync(
        Dictionary<string, object?> parameters,
        ToolExecutionContext context)
    {
        // Get or create the indexing service
        if (_indexingService == null)
        {
            if (_serviceProvider != null)
            {
                _indexingService = _serviceProvider.GetService<CodeIndexingService>();
            }
            _indexingService ??= new CodeIndexingService();
        }

        var cancellationToken = context?.CancellationToken ?? CancellationToken.None;

        // Index roots come from the tool execution workspace, not the process working directory, so
        // headless runs index the tree they are pointed at. Fall back to the process cwd only when the
        // context does not carry a working directory.
        string indexRoot;
        CodeIndexPathPolicy? pathPolicy;
        try
        {
            indexRoot = ResolveIndexRoot(context);
            pathPolicy = BuildPathPolicy(context);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolResult.Failure(ex.Message);
        }

        try
        {
            // Re-index when nothing is indexed yet or the requested workspace differs from what is
            // currently indexed (e.g. a different headless run / different context).
            if (!_indexingService.IsIndexed ||
                !string.Equals(_indexingService.IndexedDirectory, indexRoot, CodeIndexPaths.Comparison))
            {
                await _indexingService.IndexDirectoryAsync(indexRoot, pathPolicy, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Failure("Code index query was cancelled");
        }

        var queryType = parameters.GetValueOrDefault("query_type")?.ToString() ?? "symbols";
        var pattern = parameters.GetValueOrDefault("pattern")?.ToString() ?? "*";
        var scope = parameters.GetValueOrDefault("scope")?.ToString() ?? "all";
        var includePrivate = parameters.GetValueOrDefault("include_private") as bool? ?? false;

        try
        {
            object result = queryType switch
            {
                "symbols" => await SearchSymbolsAsync(pattern, scope, includePrivate, cancellationToken),
                "structure" => await GetProjectStructureAsync(scope, cancellationToken),
                "hierarchy" => await GetClassHierarchyAsync(pattern, cancellationToken),
                _ => throw new ArgumentException($"Unknown query type: {queryType}")
            };

            var resultDict = new Dictionary<string, object?>
            {
                ["query_type"] = queryType,
                ["data"] = result
            };

            // Return ToolResult with Data property properly set
            return new ToolResult
            {
                IsSuccessful = true,
                Data = resultDict,
                Message = "Code index query completed"
            };
        }
        catch (Exception ex)
        {
            return ToolResult.Failure($"Code index query failed: {ex.Message}");
        }
    }

    private async Task<object> SearchSymbolsAsync(string pattern, string scope, bool includePrivate, CancellationToken cancellationToken)
    {
        var symbols = await _indexingService!.SearchSymbolsAsync(pattern, cancellationToken);

        if (!includePrivate)
        {
            symbols = symbols.Where(s => s.IsPublic).ToList();
        }

        if (scope != "all")
        {
            symbols = symbols.Where(s => s.FilePath.Contains(scope, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return new Dictionary<string, object?>
        {
            ["query"] = pattern,
            ["scope"] = scope,
            ["count"] = symbols.Count,
            ["symbols"] = symbols.Select(s => new Dictionary<string, object?>
            {
                ["name"] = s.Name,
                ["kind"] = s.Kind,
                ["filePath"] = s.FilePath,
                ["line"] = s.Line,
                ["namespace"] = s.Namespace,
                ["isPublic"] = s.IsPublic,
                ["signature"] = s.Signature
            }).Take(100).ToList() // Limit results
        };
    }

    private async Task<object> GetProjectStructureAsync(string scope, CancellationToken cancellationToken)
    {
        var structure = await _indexingService!.GetProjectStructureAsync(cancellationToken);

        if (scope != "all")
        {
            // Filter structure to specific scope
            structure = FilterStructureByScope(structure, scope);
        }

        // Build a COMPACT, useful structure view. The raw ProjectStructure repeats every file's
        // absolute path in each namespace AND in a separate top-level Files list (~60KB for a
        // mid-size repo), which overflowed the model's tool-output budget and got truncated - so
        // the model never saw the real structure. Instead emit counts, a directory-level rollup
        // (the "directory view"), and per-namespace class/interface NAMES (short and high-value),
        // with no long repeated paths.
        var root = structure.RootPath ?? Directory.GetCurrentDirectory();
        string Rel(string p)
        {
            try { return Path.GetRelativePath(root, p); } catch { return p; }
        }

        int totalClasses = structure.Namespaces.Sum(n => n.Classes.Count);
        int totalInterfaces = structure.Namespaces.Sum(n => n.Interfaces.Count);

        var directories = structure.Files
            .Select(f =>
            {
                var dir = Path.GetDirectoryName(Rel(f.Path));
                return string.IsNullOrEmpty(dir) ? "." : dir!;
            })
            .GroupBy(d => d)
            .OrderBy(g => g.Key)
            .Select(g => new Dictionary<string, object?> { ["path"] = g.Key, ["files"] = g.Count() })
            .ToList();

        return new Dictionary<string, object?>
        {
            ["scope"] = scope,
            ["root"] = root,
            ["summary"] = $"{structure.Namespaces.Count} namespaces, {structure.Files.Count} files, " +
                          $"{totalClasses} classes, {totalInterfaces} interfaces",
            ["namespace_count"] = structure.Namespaces.Count,
            ["file_count"] = structure.Files.Count,
            ["class_count"] = totalClasses,
            ["interface_count"] = totalInterfaces,
            ["directories"] = directories,
            ["namespaces"] = structure.Namespaces.Select(n => new Dictionary<string, object?>
            {
                ["name"] = n.Name,
                ["classes"] = n.Classes,
                ["interfaces"] = n.Interfaces,
            }).ToList(),
        };
    }

    private async Task<object> GetClassHierarchyAsync(string className, CancellationToken cancellationToken)
    {
        var hierarchy = await _indexingService!.GetClassHierarchyAsync(className, cancellationToken);

        return new Dictionary<string, object?>
        {
            ["className"] = className,
            ["found"] = hierarchy.FilePath != null,
            ["namespace"] = hierarchy.Namespace,
            ["filePath"] = hierarchy.FilePath,
            ["baseClasses"] = hierarchy.BaseClasses,
            ["interfaces"] = hierarchy.Interfaces,
            ["derivedClasses"] = hierarchy.DerivedClasses
        };
    }

    /// <summary>
    /// Resolve the directory to index from the tool execution workspace and enforce the allowed-path
    /// policy. The root is taken from <see cref="ToolExecutionContext.WorkingDirectory"/> (falling
    /// back to the process working directory), then validated against the context's allowed/blocked
    /// paths so the tool cannot index outside the sandbox.
    /// </summary>
    private static string ResolveIndexRoot(ToolExecutionContext? context)
    {
        var workingDir = string.IsNullOrWhiteSpace(context?.WorkingDirectory)
            ? Directory.GetCurrentDirectory()
            : context!.WorkingDirectory!;

        var root = Path.GetFullPath(workingDir);

        var permissions = context?.Permissions;
        if (permissions != null)
        {
            // Blocked paths are always blocked, regardless of allowed paths.
            if (permissions.BlockedPaths is { Count: > 0 } blocked &&
                blocked.Any(b => CodeIndexPaths.IsContained(root, b)))
            {
                throw new UnauthorizedAccessException(
                    $"Code index root '{root}' is inside a blocked path and cannot be indexed");
            }

            // If an allow-list is configured, the root must be contained within one of the entries.
            if (permissions.AllowedPaths is { Count: > 0 } allowed &&
                !allowed.Any(a => CodeIndexPaths.IsContained(root, a)))
            {
                throw new UnauthorizedAccessException(
                    $"Code index root '{root}' is outside the allowed paths and cannot be indexed");
            }
        }

        return root;
    }

    /// <summary>
    /// Build the allowed/blocked path policy from the execution context so the policy is enforced on
    /// every directory and file visited during the walk, not just on the index root. Returns null when
    /// the context carries no permissions.
    /// </summary>
    private static CodeIndexPathPolicy? BuildPathPolicy(ToolExecutionContext? context)
    {
        var permissions = context?.Permissions;
        if (permissions == null)
        {
            return null;
        }

        return new CodeIndexPathPolicy(permissions.AllowedPaths, permissions.BlockedPaths);
    }

    private ProjectStructure FilterStructureByScope(ProjectStructure structure, string scope)
    {
        // Simple filtering implementation
        return new ProjectStructure
        {
            RootPath = structure.RootPath,
            Namespaces = structure.Namespaces
                .Where(ns => ns.Files.Any(f => f.Contains(scope, StringComparison.OrdinalIgnoreCase)))
                .ToList(),
            Files = structure.Files
                .Where(f => f.Path.Contains(scope, StringComparison.OrdinalIgnoreCase))
                .ToList()
        };
    }
}