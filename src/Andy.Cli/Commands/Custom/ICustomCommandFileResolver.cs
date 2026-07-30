using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Andy.Cli.Commands.Custom;

/// <summary>
/// Resolves <c>@path</c> mentions found in an expanded command template into structured file
/// parts.
/// </summary>
/// <remarks>
/// INTEGRATION SEAM for issue #277. #277 introduces a shared structured file-mention resolver
/// for the whole CLI; when it lands, register its implementation of this interface with the
/// catalog (<see cref="CustomCommandCatalog.FileResolver"/>) and delete
/// <see cref="WorkspaceFileMentionResolver"/>. The result shape (<see cref="PromptFilePart"/>)
/// was chosen to match what #277 proposes so the swap does not change call sites.
/// </remarks>
public interface ICustomCommandFileResolver
{
    /// <summary>
    /// Resolve every mention in <paramref name="text"/>. Implementations must not throw;
    /// problems are reported through <paramref name="diagnostics"/>.
    /// </summary>
    IReadOnlyList<PromptFilePart> Resolve(
        string text,
        string workspaceDirectory,
        CustomCommandLimits limits,
        List<CustomCommandDiagnostic> diagnostics);
}

/// <summary>
/// Minimal local <c>@file</c> resolver used until #277's shared resolver exists.
/// </summary>
/// <remarks>
/// Security posture (issue #281): mentions resolve relative to the workspace root only, a
/// resolved path that escapes the workspace is refused, and the size limits in
/// <see cref="CustomCommandLimits"/> are checked with <c>FileInfo.Length</c> BEFORE the file
/// is read, so an oversized file never reaches prompt construction.
/// </remarks>
public sealed class WorkspaceFileMentionResolver : ICustomCommandFileResolver
{
    /// <summary>
    /// A mention is <c>@</c> at a word boundary followed by a path. Email-like text
    /// (<c>a@b.com</c>) is excluded by requiring the <c>@</c> to start a word.
    /// </summary>
    private static readonly Regex MentionPattern = new(
        @"(?<![A-Za-z0-9_./\\-])@(?<path>[A-Za-z0-9_./\\-]*[A-Za-z0-9_-])",
        RegexOptions.Compiled);

    public IReadOnlyList<PromptFilePart> Resolve(
        string text,
        string workspaceDirectory,
        CustomCommandLimits limits,
        List<CustomCommandDiagnostic> diagnostics)
    {
        var parts = new List<PromptFilePart>();
        if (string.IsNullOrEmpty(text))
            return parts;

        limits ??= CustomCommandLimits.Default;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        string root;
        try
        {
            root = Path.GetFullPath(workspaceDirectory);
        }
        catch
        {
            return parts;
        }

        foreach (Match match in MentionPattern.Matches(text))
        {
            var mentionPath = match.Groups["path"].Value;
            var mention = "@" + mentionPath;
            if (!seen.Add(mentionPath))
                continue;

            if (parts.Count >= limits.MaxReferencedFiles)
            {
                diagnostics.Add(new CustomCommandDiagnostic(
                    CustomCommandDiagnosticSeverity.Warning, mention,
                    $"Ignored: at most {limits.MaxReferencedFiles} file mentions are resolved per command."));
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(root, mentionPath));
            }
            catch
            {
                diagnostics.Add(new CustomCommandDiagnostic(
                    CustomCommandDiagnosticSeverity.Warning, mention, "Not a usable path; left as plain text."));
                continue;
            }

            if (!IsInside(root, full))
            {
                diagnostics.Add(new CustomCommandDiagnostic(
                    CustomCommandDiagnosticSeverity.Error, mention,
                    "Refused: file mentions in commands may only reference files inside the workspace."));
                continue;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(full);
                if (!info.Exists)
                {
                    diagnostics.Add(new CustomCommandDiagnostic(
                        CustomCommandDiagnosticSeverity.Info, mention, "No such file; left as plain text."));
                    continue;
                }
            }
            catch
            {
                diagnostics.Add(new CustomCommandDiagnostic(
                    CustomCommandDiagnosticSeverity.Warning, mention, "Could not be inspected; left as plain text."));
                continue;
            }

            // Size gate BEFORE reading anything (issue #281 security constraint).
            if (totalBytes + Math.Min(info.Length, limits.MaxReferencedFileBytes) > limits.MaxTotalReferencedBytes)
            {
                diagnostics.Add(new CustomCommandDiagnostic(
                    CustomCommandDiagnosticSeverity.Warning, mention,
                    $"Skipped: the combined file-mention budget of {limits.MaxTotalReferencedBytes} bytes is exhausted."));
                continue;
            }

            bool truncated = info.Length > limits.MaxReferencedFileBytes;
            string content;
            try
            {
                content = ReadCapped(full, limits.MaxReferencedFileBytes);
            }
            catch
            {
                diagnostics.Add(new CustomCommandDiagnostic(
                    CustomCommandDiagnosticSeverity.Warning, mention, "Could not be read; left as plain text."));
                continue;
            }

            if (truncated)
            {
                diagnostics.Add(new CustomCommandDiagnostic(
                    CustomCommandDiagnosticSeverity.Warning, mention,
                    $"Truncated to {limits.MaxReferencedFileBytes} bytes (file is {info.Length} bytes)."));
            }

            totalBytes += content.Length;
            parts.Add(new PromptFilePart(mention, full, content, info.Length, truncated));
        }

        return parts;
    }

    private static bool IsInside(string root, string candidate)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return candidate.StartsWith(normalizedRoot, comparison);
    }

    private static string ReadCapped(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[maxBytes];
        int read = 0;
        while (read < maxBytes)
        {
            int n = stream.Read(buffer, read, maxBytes - read);
            if (n <= 0) break;
            read += n;
        }
        return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }
}
