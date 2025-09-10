using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services;

/// <summary>
/// Service for indexing and searching code using Roslyn
/// </summary>
public class CodeIndexingService : IHostedService, IDisposable
{
    private readonly ILogger<CodeIndexingService>? _logger;
    private readonly ConcurrentDictionary<string, SymbolInfo> _symbolIndex = new();
    private readonly ConcurrentDictionary<string, List<ReferenceInfo>> _referenceIndex = new();
    private FileSystemWatcher? _fileWatcher;
    private readonly SemaphoreSlim _indexLock = new(1);
    private bool _isIndexed = false;
    private string _indexedDirectory = "";

    public bool IsIndexed => _isIndexed;
    public int SymbolCount => _symbolIndex.Count;

    public CodeIndexingService(ILogger<CodeIndexingService>? logger = null)
    {
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Starting code indexing service");
        
        // Index current directory on startup
        var currentDir = Directory.GetCurrentDirectory();
        await IndexDirectoryAsync(currentDir, cancellationToken);
        
        // Set up file watcher for real-time updates
        SetupFileWatcher(currentDir);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Stopping code indexing service");
        _fileWatcher?.Dispose();
        return Task.CompletedTask;
    }

    public async Task IndexDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            _logger?.LogInformation("Indexing directory: {Directory}", directoryPath);
            _indexedDirectory = directoryPath;
            
            // Clear existing index
            _symbolIndex.Clear();
            _referenceIndex.Clear();
            
            // Find all C# files
            var csFiles = Directory.GetFiles(directoryPath, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("/obj/") && !f.Contains("/bin/") && !f.Contains("/."))
                .ToList();
            
            _logger?.LogInformation("Found {Count} C# files to index", csFiles.Count);
            
            // Parse and index each file
            foreach (var file in csFiles)
            {
                await IndexFileAsync(file, cancellationToken);
            }
            
            _isIndexed = true;
            _logger?.LogInformation("Indexing complete. {SymbolCount} symbols indexed", _symbolIndex.Count);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private async Task IndexFileAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            var code = await File.ReadAllTextAsync(filePath, cancellationToken);
            var tree = CSharpSyntaxTree.ParseText(code, cancellationToken: cancellationToken);
            var root = await tree.GetRootAsync(cancellationToken);
            
            // Index classes
            var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
            foreach (var classDecl in classes)
            {
                var className = classDecl.Identifier.Text;
                var namespaceName = GetNamespace(classDecl);
                var line = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                
                var symbolInfo = new SymbolInfo
                {
                    Name = className,
                    FullName = $"{namespaceName}.{className}",
                    Kind = "class",
                    FilePath = filePath,
                    Line = line,
                    Namespace = namespaceName,
                    IsPublic = HasPublicModifier(classDecl.Modifiers),
                    Signature = GetSignature(classDecl)
                };
                
                _symbolIndex.TryAdd(symbolInfo.FullName, symbolInfo);
                
                // Index methods within the class
                var methods = classDecl.DescendantNodes().OfType<MethodDeclarationSyntax>();
                foreach (var method in methods)
                {
                    var methodName = method.Identifier.Text;
                    var methodLine = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    
                    var methodInfo = new SymbolInfo
                    {
                        Name = methodName,
                        FullName = $"{symbolInfo.FullName}.{methodName}",
                        Kind = "method",
                        FilePath = filePath,
                        Line = methodLine,
                        Namespace = namespaceName,
                        ContainingType = className,
                        IsPublic = HasPublicModifier(method.Modifiers),
                        Signature = GetSignature(method)
                    };
                    
                    _symbolIndex.TryAdd(methodInfo.FullName, methodInfo);
                }
                
                // Index properties
                var properties = classDecl.DescendantNodes().OfType<PropertyDeclarationSyntax>();
                foreach (var prop in properties)
                {
                    var propName = prop.Identifier.Text;
                    var propLine = prop.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    
                    var propInfo = new SymbolInfo
                    {
                        Name = propName,
                        FullName = $"{symbolInfo.FullName}.{propName}",
                        Kind = "property",
                        FilePath = filePath,
                        Line = propLine,
                        Namespace = namespaceName,
                        ContainingType = className,
                        IsPublic = HasPublicModifier(prop.Modifiers),
                        Signature = GetSignature(prop)
                    };
                    
                    _symbolIndex.TryAdd(propInfo.FullName, propInfo);
                }
            }
            
            // Index interfaces
            var interfaces = root.DescendantNodes().OfType<InterfaceDeclarationSyntax>();
            foreach (var interfaceDecl in interfaces)
            {
                var interfaceName = interfaceDecl.Identifier.Text;
                var namespaceName = GetNamespace(interfaceDecl);
                var line = interfaceDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                
                var symbolInfo = new SymbolInfo
                {
                    Name = interfaceName,
                    FullName = $"{namespaceName}.{interfaceName}",
                    Kind = "interface",
                    FilePath = filePath,
                    Line = line,
                    Namespace = namespaceName,
                    IsPublic = HasPublicModifier(interfaceDecl.Modifiers),
                    Signature = GetSignature(interfaceDecl)
                };
                
                _symbolIndex.TryAdd(symbolInfo.FullName, symbolInfo);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to index file: {FilePath}", filePath);
        }
    }

    public Task<List<SymbolInfo>> SearchSymbolsAsync(string pattern, CancellationToken cancellationToken = default)
    {
        var results = new List<SymbolInfo>();
        
        // Convert pattern to regex-like matching
        var searchPattern = pattern.Replace("*", ".*").Replace("?", ".");
        var isWildcard = pattern.Contains("*") || pattern.Contains("?");
        
        foreach (var symbol in _symbolIndex.Values)
        {
            if (isWildcard)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(symbol.Name, searchPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    results.Add(symbol);
                }
            }
            else
            {
                if (symbol.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                    symbol.FullName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(symbol);
                }
            }
        }
        
        return Task.FromResult(results);
    }

    public Task<ProjectStructure> GetProjectStructureAsync(CancellationToken cancellationToken = default)
    {
        var structure = new ProjectStructure
        {
            RootPath = _indexedDirectory
        };
        
        // Group symbols by namespace
        var namespaceGroups = _symbolIndex.Values
            .Where(s => s.Kind == "class" || s.Kind == "interface")
            .GroupBy(s => s.Namespace)
            .OrderBy(g => g.Key);
        
        foreach (var nsGroup in namespaceGroups)
        {
            var ns = new NamespaceInfo
            {
                Name = nsGroup.Key,
                Classes = nsGroup.Where(s => s.Kind == "class").Select(s => s.Name).ToList(),
                Interfaces = nsGroup.Where(s => s.Kind == "interface").Select(s => s.Name).ToList(),
                Files = nsGroup.Select(s => s.FilePath).Distinct().ToList()
            };
            structure.Namespaces.Add(ns);
        }
        
        // Add file structure
        var files = _symbolIndex.Values.Select(s => s.FilePath).Distinct();
        foreach (var file in files)
        {
            var fileInfo = new FileInfo(file);
            structure.Files.Add(new FileStructure
            {
                Path = file,
                Name = fileInfo.Name,
                RelativePath = Path.GetRelativePath(_indexedDirectory, file),
                SymbolCount = _symbolIndex.Values.Count(s => s.FilePath == file)
            });
        }
        
        return Task.FromResult(structure);
    }

    public Task<List<ReferenceInfo>> FindReferencesAsync(string symbolName, CancellationToken cancellationToken = default)
    {
        // Simple implementation - would need full semantic analysis for accurate results
        var references = new List<ReferenceInfo>();
        
        // For now, just search for text occurrences
        // In a real implementation, we'd use Roslyn's FindReferences API
        if (_referenceIndex.TryGetValue(symbolName, out var cached))
        {
            return Task.FromResult(cached);
        }
        
        // This is a placeholder - real implementation would use semantic analysis
        _logger?.LogWarning("Reference search not fully implemented - returning text-based search results");
        
        return Task.FromResult(references);
    }

    public Task<ClassHierarchy> GetClassHierarchyAsync(string className, CancellationToken cancellationToken = default)
    {
        var hierarchy = new ClassHierarchy { ClassName = className };
        
        // Find the class
        var classSymbol = _symbolIndex.Values.FirstOrDefault(s => 
            s.Kind == "class" && s.Name == className);
        
        if (classSymbol != null)
        {
            hierarchy.FilePath = classSymbol.FilePath;
            hierarchy.Namespace = classSymbol.Namespace;
            
            // This is simplified - real implementation would parse base types
            // and interfaces from the syntax tree
            _logger?.LogWarning("Class hierarchy not fully implemented - returning basic info only");
        }
        
        return Task.FromResult(hierarchy);
    }

    private void SetupFileWatcher(string directory)
    {
        _fileWatcher = new FileSystemWatcher(directory)
        {
            Filter = "*.cs",
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
        };
        
        _fileWatcher.Changed += OnFileChanged;
        _fileWatcher.Created += OnFileChanged;
        _fileWatcher.Deleted += OnFileDeleted;
        _fileWatcher.Renamed += OnFileRenamed;
        
        _fileWatcher.EnableRaisingEvents = true;
        _logger?.LogInformation("File watcher enabled for: {Directory}", directory);
    }

    private async void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (e.FullPath.Contains("/obj/") || e.FullPath.Contains("/bin/"))
            return;
        
        _logger?.LogDebug("File changed: {FilePath}", e.FullPath);
        
        // Re-index the changed file
        await IndexFileAsync(e.FullPath, CancellationToken.None);
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        _logger?.LogDebug("File deleted: {FilePath}", e.FullPath);
        
        // Remove symbols from this file
        var symbolsToRemove = _symbolIndex.Values
            .Where(s => s.FilePath == e.FullPath)
            .Select(s => s.FullName)
            .ToList();
        
        foreach (var symbolName in symbolsToRemove)
        {
            _symbolIndex.TryRemove(symbolName, out _);
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        _logger?.LogDebug("File renamed: {OldPath} -> {NewPath}", e.OldFullPath, e.FullPath);
        
        // Update file paths in index
        var symbolsToUpdate = _symbolIndex.Values.Where(s => s.FilePath == e.OldFullPath);
        foreach (var symbol in symbolsToUpdate)
        {
            symbol.FilePath = e.FullPath;
        }
    }

    private string GetNamespace(SyntaxNode node)
    {
        var namespaceDecl = node.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
        if (namespaceDecl != null)
            return namespaceDecl.Name.ToString();
        
        var fileScopedNamespace = node.Ancestors().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();
        if (fileScopedNamespace != null)
            return fileScopedNamespace.Name.ToString();
        
        return "";
    }

    private bool HasPublicModifier(SyntaxTokenList modifiers)
    {
        return modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));
    }

    private string GetSignature(MemberDeclarationSyntax member)
    {
        // Get a simplified signature
        var firstLine = member.ToString().Split('\n')[0].Trim();
        if (firstLine.Length > 100)
            firstLine = firstLine.Substring(0, 100) + "...";
        return firstLine;
    }

    public void Dispose()
    {
        _fileWatcher?.Dispose();
        _indexLock?.Dispose();
    }
}

// Data structures
public class SymbolInfo
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Kind { get; set; } = ""; // class, interface, method, property, etc.
    public string FilePath { get; set; } = "";
    public int Line { get; set; }
    public string Namespace { get; set; } = "";
    public string? ContainingType { get; set; }
    public bool IsPublic { get; set; }
    public string Signature { get; set; } = "";
}

public class ReferenceInfo
{
    public string FilePath { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
    public string Context { get; set; } = "";
}

public class ProjectStructure
{
    public string RootPath { get; set; } = "";
    public List<NamespaceInfo> Namespaces { get; set; } = new();
    public List<FileStructure> Files { get; set; } = new();
}

public class NamespaceInfo
{
    public string Name { get; set; } = "";
    public List<string> Classes { get; set; } = new();
    public List<string> Interfaces { get; set; } = new();
    public List<string> Files { get; set; } = new();
}

public class FileStructure
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public int SymbolCount { get; set; }
}

public class ClassHierarchy
{
    public string ClassName { get; set; } = "";
    public string? FilePath { get; set; }
    public string? Namespace { get; set; }
    public List<string> BaseClasses { get; set; } = new();
    public List<string> Interfaces { get; set; } = new();
    public List<string> DerivedClasses { get; set; } = new();
}