using System;
using System.IO;
using System.Text;

namespace Andy.Cli.Tests.Services.FileMentions;

/// <summary>
/// Disposable temporary directory used as a workspace root by the @file mention tests.
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "AndyFileMentions_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>Absolute workspace root.</summary>
    public string Root { get; }

    /// <summary>Write a text file at a workspace-relative path, creating directories as needed.</summary>
    public string WriteFile(string relativePath, string content)
    {
        string full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Write raw bytes at a workspace-relative path.</summary>
    public string WriteBytes(string relativePath, byte[] content)
    {
        string full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    /// <summary>Create a workspace-relative directory.</summary>
    public string CreateDirectory(string relativePath)
    {
        string full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>Build a file whose content is at least <paramref name="minimumBytes"/> long.</summary>
    public string WriteLargeFile(string relativePath, int minimumBytes)
    {
        var sb = new StringBuilder();
        int line = 0;
        while (sb.Length < minimumBytes)
        {
            sb.Append("line ").Append(line++).Append(" of filler content\n");
        }
        return WriteFile(relativePath, sb.ToString());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort: a leftover temp directory must not fail a test run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
