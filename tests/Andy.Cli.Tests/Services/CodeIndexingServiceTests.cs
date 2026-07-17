using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Xunit;

namespace Andy.Cli.Tests.Services;

/// <summary>
/// #175: incremental-update correctness (no stale symbols on change/delete/rename), error isolation
/// (a bad file or inaccessible directory does not abort the index), safe wildcard handling and
/// cancellation for <see cref="CodeIndexingService"/>.
/// </summary>
public class CodeIndexingServiceTests : IDisposable
{
    private readonly string _root;

    public CodeIndexingServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"CodeIdxSvc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task IndexDirectory_UsesGivenDirectory()
    {
        WriteFile("A.cs", "namespace N { public class ClassA { } }");
        var svc = new CodeIndexingService();

        await svc.IndexDirectoryAsync(_root);

        Assert.True(svc.IsIndexed);
        Assert.Equal(Path.GetFullPath(_root), svc.IndexedDirectory);
        var symbols = await svc.SearchSymbolsAsync("ClassA");
        Assert.Single(symbols);
    }

    // Incremental update after a change must drop symbols that no longer exist in the file.
    [Fact]
    public async Task UpdateFile_AfterChange_RemovesStaleSymbols()
    {
        var path = WriteFile("A.cs",
            "namespace N { public class ClassA { public void MethodOld() { } } }");
        var svc = new CodeIndexingService();
        await svc.IndexDirectoryAsync(_root);

        Assert.NotEmpty(await svc.SearchSymbolsAsync("MethodOld"));

        // Rewrite the file: MethodOld is gone, MethodNew appears.
        File.WriteAllText(path,
            "namespace N { public class ClassA { public void MethodNew() { } } }");
        await svc.UpdateFileAsync(path);

        Assert.Empty(await svc.SearchSymbolsAsync("MethodOld"));
        Assert.NotEmpty(await svc.SearchSymbolsAsync("MethodNew"));
    }

    [Fact]
    public async Task RemoveFile_AfterDelete_RemovesStaleSymbols()
    {
        WriteFile("A.cs", "namespace N { public class ClassA { } }");
        var bPath = WriteFile("B.cs", "namespace N { public class ClassB { } }");
        var svc = new CodeIndexingService();
        await svc.IndexDirectoryAsync(_root);

        Assert.NotEmpty(await svc.SearchSymbolsAsync("ClassB"));

        File.Delete(bPath);
        await svc.RemoveFileAsync(bPath);

        Assert.Empty(await svc.SearchSymbolsAsync("ClassB"));
        Assert.NotEmpty(await svc.SearchSymbolsAsync("ClassA"));
    }

    [Fact]
    public async Task RenameFile_RemovesOldSymbols_AndIndexesNew()
    {
        var oldPath = WriteFile("A.cs", "namespace N { public class ClassA { } }");
        var svc = new CodeIndexingService();
        await svc.IndexDirectoryAsync(_root);

        Assert.NotEmpty(await svc.SearchSymbolsAsync("ClassA"));

        var newPath = Path.Combine(_root, "A2.cs");
        File.Move(oldPath, newPath);
        File.WriteAllText(newPath, "namespace N { public class ClassRenamed { } }");
        await svc.RenameFileAsync(oldPath, newPath);

        // Old symbol keyed to the old file is gone; the new file's symbol is present.
        Assert.Empty(await svc.SearchSymbolsAsync("ClassA"));
        var renamed = await svc.SearchSymbolsAsync("ClassRenamed");
        Assert.Single(renamed);
        Assert.Equal(Path.GetFullPath(newPath), renamed[0].FilePath);
    }

    // A per-file failure (here: updating a non-existent file) must be isolated, not thrown.
    [Fact]
    public async Task UpdateFile_MissingFile_DoesNotThrow_AndIsIsolated()
    {
        WriteFile("A.cs", "namespace N { public class ClassA { } }");
        var svc = new CodeIndexingService();
        await svc.IndexDirectoryAsync(_root);

        var missing = Path.Combine(_root, "DoesNotExist.cs");
        await svc.UpdateFileAsync(missing); // must not throw

        // The rest of the index is intact.
        Assert.NotEmpty(await svc.SearchSymbolsAsync("ClassA"));
        Assert.Contains(svc.IndexErrors, e => e.Contains("DoesNotExist.cs"));
    }

    // An inaccessible directory must not abort indexing of the accessible files (Unix only).
    [Fact]
    public async Task IndexDirectory_InaccessibleSubdir_DoesNotAbort()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return; // Unix file-mode based test.
        }

        WriteFile("Good.cs", "namespace N { public class GoodClass { } }");
        var secret = Path.Combine(_root, "secret");
        Directory.CreateDirectory(secret);
        File.WriteAllText(Path.Combine(secret, "Hidden.cs"), "namespace N { public class HiddenClass { } }");

        // Remove all permissions so enumeration of the subdirectory fails.
        File.SetUnixFileMode(secret, UnixFileMode.None);
        try
        {
            var svc = new CodeIndexingService();
            await svc.IndexDirectoryAsync(_root);

            Assert.True(svc.IsIndexed);
            Assert.NotEmpty(await svc.SearchSymbolsAsync("GoodClass"));
        }
        finally
        {
            // Restore permissions so cleanup can delete the directory.
            File.SetUnixFileMode(secret,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task SearchSymbols_InvalidWildcard_DoesNotThrow()
    {
        WriteFile("A.cs", "namespace N { public class ClassA { } }");
        var svc = new CodeIndexingService();
        await svc.IndexDirectoryAsync(_root);

        // These contain regex metacharacters; the glob->regex conversion must not throw.
        var ex = await Record.ExceptionAsync(async () =>
        {
            await svc.SearchSymbolsAsync("Class(*");
            await svc.SearchSymbolsAsync("[A-");
            await svc.SearchSymbolsAsync("*+?");
        });
        Assert.Null(ex);
    }

    [Fact]
    public async Task SearchSymbols_LiteralWildcard_MatchesPrefix()
    {
        WriteFile("A.cs", "namespace N { public class ClassAlpha { } public class ClassBeta { } }");
        var svc = new CodeIndexingService();
        await svc.IndexDirectoryAsync(_root);

        var matches = await svc.SearchSymbolsAsync("Class*");
        var names = matches.Select(m => m.Name).ToList();
        Assert.Contains("ClassAlpha", names);
        Assert.Contains("ClassBeta", names);
    }

    [Fact]
    public async Task IndexDirectory_Cancellation_Throws()
    {
        WriteFile("A.cs", "namespace N { public class ClassA { } }");
        var svc = new CodeIndexingService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.IndexDirectoryAsync(_root, cts.Token));
    }

    // #175 review: a wildcard must broaden (not narrow) recall - it keeps substring/match-anywhere
    // semantics, so an internal wildcard matches names that merely contain the surrounding fragments.
    [Fact]
    public async Task SearchSymbols_InternalWildcard_MatchesAnywhere()
    {
        WriteFile("A.cs", "namespace N { public class C { public void MyGetFooAsyncHelper() { } } }");
        var svc = new CodeIndexingService();
        await svc.IndexDirectoryAsync(_root);

        var matches = await svc.SearchSymbolsAsync("Get*Async");
        Assert.Contains(matches, m => m.Name == "MyGetFooAsyncHelper");
    }

    // #175 review: a wildcard must never return fewer results than the equivalent plain substring.
    [Fact]
    public async Task SearchSymbols_Wildcard_IsSupersetOfSubstring()
    {
        WriteFile("A.cs",
            "namespace N { public class GetHandler { } public class MyGetter { } public class Other { } }");
        var svc = new CodeIndexingService();
        await svc.IndexDirectoryAsync(_root);

        var plain = (await svc.SearchSymbolsAsync("Get")).Select(m => m.Name).ToHashSet();
        var wild = (await svc.SearchSymbolsAsync("Get*")).Select(m => m.Name).ToHashSet();

        Assert.True(plain.IsSubsetOf(wild), "wildcard search must not narrow recall");
        Assert.Contains("GetHandler", wild);
        Assert.Contains("MyGetter", wild);
    }

    // #175 review: a nested blocked path under the workspace must NOT be indexed (containment is
    // enforced on every directory/file visited, not just on the index root).
    [Fact]
    public async Task IndexDirectory_BlockedNestedPath_IsNotIndexed()
    {
        WriteFile("Good.cs", "namespace N { public class GoodClass { } }");
        // Use a non-hidden name so the block is proven by the path policy, not the hidden-dir skip.
        var secretDir = Path.Combine(_root, "secrets");
        Directory.CreateDirectory(secretDir);
        File.WriteAllText(Path.Combine(secretDir, "Secret.cs"),
            "namespace N { public class SecretClass { } }");

        var svc = new CodeIndexingService();
        var policy = new CodeIndexPathPolicy(allowedPaths: null, blockedPaths: new[] { secretDir });
        await svc.IndexDirectoryAsync(_root, policy);

        Assert.NotEmpty(await svc.SearchSymbolsAsync("GoodClass"));
        Assert.Empty(await svc.SearchSymbolsAsync("SecretClass"));
    }

    // #175 review: a directory symlink cycle must not spin the walk forever - the walk terminates and
    // still indexes the real files (Unix only; symlink creation).
    [Fact]
    public async Task IndexDirectory_SymlinkCycle_Terminates()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return; // symlink creation is unreliable without privileges on Windows.
        }

        WriteFile("Real.cs", "namespace N { public class RealClass { } }");
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);

        // sub/loop -> _root, forming a cycle _root -> sub -> loop(-> _root) -> ...
        var loop = Path.Combine(sub, "loop");
        Directory.CreateSymbolicLink(loop, _root);

        var svc = new CodeIndexingService();
        var indexTask = svc.IndexDirectoryAsync(_root);
        var finished = await Task.WhenAny(indexTask, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(indexTask, finished); // completed rather than looping until the timeout
        await indexTask;

        Assert.True(svc.IsIndexed);
        Assert.NotEmpty(await svc.SearchSymbolsAsync("RealClass"));
    }

    [Fact]
    public async Task Hierarchy_ReportsBaseAndDerived()
    {
        WriteFile("Types.cs", @"
namespace H
{
    public interface IShape { }
    public class Shape { }
    public class Circle : Shape, IShape { }
    public class UnitCircle : Circle { }
}");
        var svc = new CodeIndexingService();
        await svc.IndexDirectoryAsync(_root);

        var circle = await svc.GetClassHierarchyAsync("Circle");
        Assert.Contains("Shape", circle.BaseClasses);
        Assert.Contains("IShape", circle.Interfaces);
        Assert.Contains("UnitCircle", circle.DerivedClasses);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }
}
