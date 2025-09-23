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
                Description = "Type of query: 'symbols' (find classes/methods/properties), 'structure' (get project structure), 'references' (find usages), 'hierarchy' (class inheritance)",
                Required = true,
                AllowedValues = new[] { "symbols", "structure", "references", "hierarchy" }
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
                Description = "Find references to a specific class",
                Parameters = new Dictionary<string, object?>
                {
                    ["query_type"] = "references",
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

        // Ensure index is initialized
        if (!_indexingService.IsIndexed)
        {
            await _indexingService.IndexDirectoryAsync(Directory.GetCurrentDirectory());
        }

        var queryType = parameters.GetValueOrDefault("query_type")?.ToString() ?? "symbols";
        var pattern = parameters.GetValueOrDefault("pattern")?.ToString() ?? "*";
        var scope = parameters.GetValueOrDefault("scope")?.ToString() ?? "all";
        var includePrivate = parameters.GetValueOrDefault("include_private") as bool? ?? false;

        try
        {
            var cancellationToken = context?.CancellationToken ?? CancellationToken.None;
            object result = queryType switch
            {
                "symbols" => await SearchSymbolsAsync(pattern, scope, includePrivate, cancellationToken),
                "structure" => await GetProjectStructureAsync(scope, cancellationToken),
                "references" => await FindReferencesAsync(pattern, scope, cancellationToken),
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

        return new
        {
            query = pattern,
            scope = scope,
            count = symbols.Count,
            symbols = symbols.Select(s => new
            {
                name = s.Name,
                kind = s.Kind,
                filePath = s.FilePath,
                line = s.Line,
                @namespace = s.Namespace,
                isPublic = s.IsPublic,
                signature = s.Signature
            }).Take(100) // Limit results
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

        return new
        {
            scope = scope,
            structure = structure
        };
    }

    private async Task<object> FindReferencesAsync(string symbolName, string scope, CancellationToken cancellationToken)
    {
        var references = await _indexingService!.FindReferencesAsync(symbolName, cancellationToken);
        
        if (scope != "all")
        {
            references = references.Where(r => r.FilePath.Contains(scope, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return new
        {
            symbol = symbolName,
            scope = scope,
            count = references.Count,
            references = references.Select(r => new
            {
                filePath = r.FilePath,
                line = r.Line,
                column = r.Column,
                context = r.Context
            }).Take(100) // Limit results
        };
    }

    private async Task<object> GetClassHierarchyAsync(string className, CancellationToken cancellationToken)
    {
        var hierarchy = await _indexingService!.GetClassHierarchyAsync(className, cancellationToken);
        
        return new
        {
            className = className,
            hierarchy = hierarchy
        };
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