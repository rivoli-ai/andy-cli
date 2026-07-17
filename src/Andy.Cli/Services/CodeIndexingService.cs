using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services;

/// <summary>
/// Service for indexing and searching code using Roslyn.
///
/// Lifecycle: this type implements IHostedService, but the CLI builds a plain ServiceProvider and
/// never starts hosted services, so StartAsync is not relied upon for indexing. Indexing is driven
/// lazily and per-workspace by <see cref="Andy.Cli.Tools.CodeIndexTool"/>, which calls
/// <see cref="IndexDirectoryAsync"/> for the tool execution workspace. The file-system watcher for
/// incremental updates is wired up inside <see cref="IndexDirectoryAsync"/> so it is active whenever
/// a directory has been indexed, independent of hosted-service startup.
/// </summary>
public class CodeIndexingService : IHostedService, IDisposable
{
    private readonly ILogger<CodeIndexingService>? _logger;
    private readonly ConcurrentDictionary<string, SymbolInfo> _symbolIndex = new();
    private FileSystemWatcher? _fileWatcher;
    private readonly SemaphoreSlim _indexLock = new(1);
    private bool _isIndexed = false;
    private string _indexedDirectory = "";
    private readonly List<string> _indexErrors = new();

    public bool IsIndexed => _isIndexed;
    public int SymbolCount => _symbolIndex.Count;

    /// <summary>The directory currently indexed (empty when nothing has been indexed yet).</summary>
    public string IndexedDirectory => _indexedDirectory;

    /// <summary>Non-fatal errors (inaccessible directories, per-file parse failures) from the last index pass.</summary>
    public IReadOnlyList<string> IndexErrors
    {
        get { lock (_indexErrors) { return _indexErrors.ToList(); } }
    }

    public CodeIndexingService(ILogger<CodeIndexingService>? logger = null)
    {
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Starting code indexing service");

        // Index current directory on startup (only reached when hosted-service startup is used).
        var currentDir = Directory.GetCurrentDirectory();
        await IndexDirectoryAsync(currentDir, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Stopping code indexing service");
        _fileWatcher?.Dispose();
        _fileWatcher = null;
        return Task.CompletedTask;
    }

    public async Task IndexDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(directoryPath);
        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            _logger?.LogInformation("Indexing directory: {Directory}", fullPath);
            _indexedDirectory = fullPath;

            // Clear existing index
            _symbolIndex.Clear();
            lock (_indexErrors) { _indexErrors.Clear(); }

            // Enumerate C# files without letting a single inaccessible directory abort the whole walk.
            var csFiles = EnumerateCSharpFiles(fullPath);

            _logger?.LogInformation("Found {Count} C# files to index", csFiles.Count);

            // Parse and index each file. A parse/read failure on one file must not abort the index.
            foreach (var file in csFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await IndexFileAsync(file, cancellationToken);
            }

            _isIndexed = true;
            _logger?.LogInformation("Indexing complete. {SymbolCount} symbols indexed", _symbolIndex.Count);
        }
        finally
        {
            _indexLock.Release();
        }

        // Wire up (or re-point) the incremental file watcher for the indexed directory. Done outside
        // the lock so watcher callbacks never contend with the initial index pass.
        SetupFileWatcher(fullPath);
    }

    /// <summary>
    /// Recursively enumerate .cs files, skipping obj/bin/hidden directories, and continuing past
    /// directories that cannot be read (permissions, races) instead of throwing.
    /// </summary>
    private List<string> EnumerateCSharpFiles(string root)
    {
        var results = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.cs"))
                {
                    results.Add(file);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
            {
                RecordError($"Failed to enumerate files in {dir}: {ex.Message}");
                continue;
            }

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase) ||
                        (name.StartsWith('.') && name.Length > 1))
                    {
                        continue;
                    }
                    stack.Push(sub);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
            {
                RecordError($"Failed to enumerate subdirectories in {dir}: {ex.Message}");
            }
        }

        return results;
    }

    private void RecordError(string message)
    {
        _logger?.LogWarning("{IndexError}", message);
        lock (_indexErrors) { _indexErrors.Add(message); }
    }

    /// <summary>
    /// Remove all symbols recorded for a file. Used before re-indexing a changed file and when a
    /// file is deleted/renamed so the index never retains stale symbols.
    /// </summary>
    private void RemoveSymbolsForFile(string filePath)
    {
        var toRemove = _symbolIndex
            .Where(kvp => string.Equals(kvp.Value.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            _symbolIndex.TryRemove(key, out _);
        }
    }

    private async Task IndexFileAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            // Always clear any prior symbols for this file first so an incremental re-index of a
            // changed file cannot leave stale (removed/renamed) symbols behind.
            RemoveSymbolsForFile(filePath);

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
                    Signature = GetSignature(classDecl),
                    BaseTypes = GetBaseTypeNames(classDecl.BaseList)
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
                    Signature = GetSignature(interfaceDecl),
                    BaseTypes = GetBaseTypeNames(interfaceDecl.BaseList)
                };

                _symbolIndex.TryAdd(symbolInfo.FullName, symbolInfo);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A single unreadable/unparseable file must not abort the whole index.
            RecordError($"Failed to index file {filePath}: {ex.Message}");
        }
    }

    public Task<List<SymbolInfo>> SearchSymbolsAsync(string pattern, CancellationToken cancellationToken = default)
    {
        var results = new List<SymbolInfo>();
        pattern ??= "";

        var isWildcard = pattern.Contains('*') || pattern.Contains('?');

        Regex? regex = null;
        if (isWildcard)
        {
            // Build a safe regex from a glob. Escape everything first so pattern metacharacters
            // (e.g. '(', '[', '+') cannot crash regex construction, then translate the escaped
            // wildcards. Fall back to a substring match if construction still fails.
            regex = TryBuildWildcardRegex(pattern);
        }

        foreach (var symbol in _symbolIndex.Values)
        {
            if (regex != null)
            {
                bool matched;
                try
                {
                    matched = regex.IsMatch(symbol.Name);
                }
                catch (RegexMatchTimeoutException)
                {
                    matched = false;
                }

                if (matched)
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

    /// <summary>
    /// Convert a glob pattern (supporting * and ?) into an anchored, case-insensitive regex without
    /// ever throwing on malformed input. Returns null if a valid regex cannot be built.
    /// </summary>
    private Regex? TryBuildWildcardRegex(string pattern)
    {
        try
        {
            // Escape all regex metacharacters, then reintroduce wildcard semantics on the escaped
            // placeholders that Regex.Escape produces for '*' ("\*") and '?' ("\?").
            var escaped = Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".");

            return new Regex("^" + escaped + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            RecordError($"Invalid search pattern '{pattern}': {ex.Message}");
            return null;
        }
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
                RelativePath = string.IsNullOrEmpty(_indexedDirectory) ? file : Path.GetRelativePath(_indexedDirectory, file),
                SymbolCount = _symbolIndex.Values.Count(s => s.FilePath == file)
            });
        }

        return Task.FromResult(structure);
    }

    public Task<ClassHierarchy> GetClassHierarchyAsync(string className, CancellationToken cancellationToken = default)
    {
        var hierarchy = new ClassHierarchy { ClassName = className };

        // Find the class (or interface) by simple name.
        var typeSymbol = _symbolIndex.Values.FirstOrDefault(s =>
            (s.Kind == "class" || s.Kind == "interface") && s.Name == className);

        if (typeSymbol != null)
        {
            hierarchy.FilePath = typeSymbol.FilePath;
            hierarchy.Namespace = typeSymbol.Namespace;

            // Base types are captured at index time from the declaration's base list. We cannot
            // resolve which entries are classes vs interfaces without a semantic model, so we
            // classify heuristically: names starting with 'I' followed by an uppercase letter are
            // treated as interfaces, the rest as base classes.
            foreach (var baseName in typeSymbol.BaseTypes)
            {
                if (LooksLikeInterface(baseName))
                {
                    hierarchy.Interfaces.Add(baseName);
                }
                else
                {
                    hierarchy.BaseClasses.Add(baseName);
                }
            }
        }

        // Derived types: any indexed class/interface whose base list references this name.
        hierarchy.DerivedClasses = _symbolIndex.Values
            .Where(s => (s.Kind == "class" || s.Kind == "interface") && s.Name != className)
            .Where(s => s.BaseTypes.Any(b => string.Equals(StripGenericArity(b), className, StringComparison.Ordinal)))
            .Select(s => s.Name)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        return Task.FromResult(hierarchy);
    }

    private static bool LooksLikeInterface(string name)
    {
        var bare = StripGenericArity(name);
        return bare.Length >= 2 && bare[0] == 'I' && char.IsUpper(bare[1]);
    }

    private static string StripGenericArity(string name)
    {
        var idx = name.IndexOf('<');
        return idx >= 0 ? name[..idx] : name;
    }

    private static List<string> GetBaseTypeNames(BaseListSyntax? baseList)
    {
        var names = new List<string>();
        if (baseList == null)
        {
            return names;
        }

        foreach (var baseType in baseList.Types)
        {
            var text = baseType.Type.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                names.Add(text.Trim());
            }
        }

        return names;
    }

    private void SetupFileWatcher(string directory)
    {
        try
        {
            _fileWatcher?.Dispose();

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
        catch (Exception ex)
        {
            // A missing/inaccessible directory must not prevent indexing from succeeding.
            RecordError($"Failed to enable file watcher for {directory}: {ex.Message}");
            _fileWatcher = null;
        }
    }

    private static bool IsExcludedPath(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/bin/", StringComparison.OrdinalIgnoreCase);
    }

    private async void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (IsExcludedPath(e.FullPath))
            return;

        _logger?.LogDebug("File changed: {FilePath}", e.FullPath);

        try
        {
            // IndexFileAsync removes prior symbols for this file before re-adding, so a change never
            // leaves stale symbols.
            await _indexLock.WaitAsync();
            try
            {
                await IndexFileAsync(e.FullPath, CancellationToken.None);
            }
            finally
            {
                _indexLock.Release();
            }
        }
        catch (Exception ex)
        {
            RecordError($"Failed to re-index changed file {e.FullPath}: {ex.Message}");
        }
    }

    private async void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        _logger?.LogDebug("File deleted: {FilePath}", e.FullPath);

        await _indexLock.WaitAsync();
        try
        {
            RemoveSymbolsForFile(e.FullPath);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private async void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        _logger?.LogDebug("File renamed: {OldPath} -> {NewPath}", e.OldFullPath, e.FullPath);

        await _indexLock.WaitAsync();
        try
        {
            // Drop everything indexed under the old path, then re-index the new path from scratch so
            // symbols that were removed/renamed inside the file are not retained.
            RemoveSymbolsForFile(e.OldFullPath);
        }
        finally
        {
            _indexLock.Release();
        }

        if (!IsExcludedPath(e.FullPath) && File.Exists(e.FullPath))
        {
            await _indexLock.WaitAsync();
            try
            {
                await IndexFileAsync(e.FullPath, CancellationToken.None);
            }
            catch (Exception ex)
            {
                RecordError($"Failed to index renamed file {e.FullPath}: {ex.Message}");
            }
            finally
            {
                _indexLock.Release();
            }
        }
    }

    /// <summary>
    /// Incrementally re-index a single file (removing any prior symbols for it first). Exposed for
    /// callers and tests that drive incremental updates without the file-system watcher.
    /// </summary>
    public async Task UpdateFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            await IndexFileAsync(Path.GetFullPath(filePath), cancellationToken);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    /// <summary>Remove all symbols for a file from the index (e.g. after a delete).</summary>
    public async Task RemoveFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            RemoveSymbolsForFile(Path.GetFullPath(filePath));
        }
        finally
        {
            _indexLock.Release();
        }
    }

    /// <summary>Remove symbols for the old path and index the new path (e.g. after a rename).</summary>
    public async Task RenameFileAsync(string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            RemoveSymbolsForFile(Path.GetFullPath(oldPath));
            if (File.Exists(newPath))
            {
                await IndexFileAsync(Path.GetFullPath(newPath), cancellationToken);
            }
        }
        finally
        {
            _indexLock.Release();
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

    /// <summary>Base classes/interfaces declared in the type's base list (syntax-level names).</summary>
    public List<string> BaseTypes { get; set; } = new();
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
