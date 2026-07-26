using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Services.FileMentions;

/// <summary>Size and count budgets applied while resolving <c>@</c> mentions.</summary>
public sealed class FileMentionResolverOptions
{
    /// <summary>Largest file, in bytes, that is attached whole. Default 256 KiB.</summary>
    public long MaxFileBytes { get; set; } = 256 * 1024;

    /// <summary>Total attached content, in bytes, allowed across one prompt. Default 1 MiB.</summary>
    public long MaxTotalBytes { get; set; } = 1024 * 1024;

    /// <summary>Largest number of files attached to one prompt. Default 20.</summary>
    public int MaxAttachments { get; set; } = 20;

    /// <summary>Bytes inspected when deciding whether a file is binary. Default 8 KiB.</summary>
    public int BinarySniffBytes { get; set; } = 8 * 1024;
}

/// <summary>
/// Turns prompt text containing <c>@path</c> mentions into a <see cref="ResolvedPrompt"/> with
/// structured file attachments. Content is read at resolution time (that is, when the prompt is
/// submitted), not when the mention was typed, so the model sees the file as it is at send time.
///
/// This type has no TUI dependency: the interactive composer, headless runs and custom commands
/// all resolve mentions through it.
/// </summary>
public sealed class FileMentionResolver
{
    private readonly string _workspaceRoot;
    private readonly WorkspaceIgnoreRules _ignoreRules;
    private readonly FileMentionResolverOptions _options;

    public FileMentionResolver(
        string workspaceRoot,
        WorkspaceIgnoreRules? ignoreRules = null,
        FileMentionResolverOptions? options = null)
    {
        _workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(workspaceRoot);
        _ignoreRules = ignoreRules ?? new WorkspaceIgnoreRules(_workspaceRoot);
        _options = options ?? new FileMentionResolverOptions();
    }

    /// <summary>Workspace root that mentions are resolved against and confined to.</summary>
    public string WorkspaceRoot => _workspaceRoot;

    /// <summary>Budgets in force for this resolver.</summary>
    public FileMentionResolverOptions Options => _options;

    /// <summary>
    /// Resolve every mention in <paramref name="promptText"/>. Never throws for bad input: an
    /// unresolvable mention becomes an attachment carrying a non-<see cref="FileMentionStatus.Attached"/>
    /// status and an explanatory note.
    /// </summary>
    public async Task<ResolvedPrompt> ResolveAsync(string promptText, CancellationToken cancellationToken = default)
    {
        string text = promptText ?? string.Empty;
        var tokens = FileMentionSyntax.FindAll(text);
        var attachments = new List<FileMentionAttachment>();
        var seen = new Dictionary<string, bool>(StringComparer.Ordinal);
        long totalBytes = 0;
        int attachedCount = 0;

        foreach (var token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string mentionText = text.Substring(token.Start, token.Length);
            string body = text.Substring(token.Start + 1, token.Length - 1);
            var (path, range, pathIncludingSuffix) = FileMentionSyntax.SplitBody(body);

            if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(pathIncludingSuffix))
            {
                continue;
            }

            // "@notes#12" is ambiguous: it can be line 12 of "notes" or a file literally called
            // "notes#12". Prefer the literal file when one exists, since a user who wants a range
            // of a file whose name ends in "#12" can always quote the path.
            if (range is not null &&
                !string.Equals(path, pathIncludingSuffix, StringComparison.Ordinal) &&
                ResolvesToExistingFile(pathIncludingSuffix) &&
                !ResolvesToExistingFile(path))
            {
                path = pathIncludingSuffix;
                range = null;
            }

            var attachment = await ResolveOneAsync(
                mentionText,
                path,
                range,
                seen,
                attachedCount,
                totalBytes,
                cancellationToken).ConfigureAwait(false);

            attachments.Add(attachment);

            if (attachment.IsAttached)
            {
                attachedCount++;
                totalBytes += Encoding.UTF8.GetByteCount(attachment.Content ?? string.Empty);
                string key = MakeKey(attachment.RelativePath ?? attachment.RequestedPath, attachment.Range);
                seen[key] = true;
            }
        }

        return new ResolvedPrompt(text, attachments);
    }

    /// <summary>Synchronous convenience wrapper around <see cref="ResolveAsync"/>.</summary>
    public ResolvedPrompt Resolve(string promptText) =>
        ResolveAsync(promptText).GetAwaiter().GetResult();

    private async Task<FileMentionAttachment> ResolveOneAsync(
        string mentionText,
        string requestedPath,
        LineRange? range,
        IReadOnlyDictionary<string, bool> seen,
        int attachedCount,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        string trimmed = requestedPath.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return Problem(mentionText, requestedPath, FileMentionStatus.Missing, "Empty path.", range);
        }

        string absolute;
        try
        {
            absolute = Path.GetFullPath(Path.Combine(_workspaceRoot, trimmed.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Problem(mentionText, requestedPath, FileMentionStatus.Missing, "The path is not valid.", range);
        }

        if (!CodeIndexPaths.IsContained(absolute, _workspaceRoot))
        {
            return Problem(
                mentionText,
                requestedPath,
                FileMentionStatus.OutsideWorkspace,
                "Only files inside the current workspace can be attached.",
                range);
        }

        string relative = Path.GetRelativePath(_workspaceRoot, absolute).Replace('\\', '/');
        if (relative == ".")
        {
            relative = string.Empty;
        }

        bool isDirectory = Directory.Exists(absolute);
        if (_ignoreRules.IsIgnored(relative, isDirectory))
        {
            return Problem(
                mentionText,
                requestedPath,
                FileMentionStatus.Ignored,
                "The path is excluded by ignore rules and was not read.",
                range,
                relative,
                absolute);
        }

        if (isDirectory)
        {
            return Problem(
                mentionText,
                requestedPath,
                FileMentionStatus.Directory,
                "Directories are not attached; mention a file inside it instead.",
                range,
                relative,
                absolute);
        }

        if (!File.Exists(absolute))
        {
            return Problem(
                mentionText,
                requestedPath,
                FileMentionStatus.Missing,
                "No such file in the workspace.",
                range,
                relative,
                absolute);
        }

        string key = MakeKey(relative, range);
        if (seen.ContainsKey(key))
        {
            return new FileMentionAttachment(
                mentionText,
                requestedPath,
                FileMentionStatus.Duplicate,
                relative,
                absolute,
                range,
                note: "Already attached earlier in this prompt.");
        }

        if (attachedCount >= _options.MaxAttachments)
        {
            return Problem(
                mentionText,
                requestedPath,
                FileMentionStatus.BudgetExceeded,
                $"More than {_options.MaxAttachments} files were mentioned; this one was not attached.",
                range,
                relative,
                absolute);
        }

        if (totalBytes >= _options.MaxTotalBytes)
        {
            return Problem(
                mentionText,
                requestedPath,
                FileMentionStatus.BudgetExceeded,
                $"The prompt already carries {_options.MaxTotalBytes} bytes of attached content.",
                range,
                relative,
                absolute);
        }

        long length;
        try
        {
            length = new FileInfo(absolute).Length;
            if (await LooksBinaryAsync(absolute, cancellationToken).ConfigureAwait(false))
            {
                return Problem(
                    mentionText,
                    requestedPath,
                    FileMentionStatus.Binary,
                    "The file is not text and was not attached.",
                    range,
                    relative,
                    absolute);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Problem(
                mentionText,
                requestedPath,
                FileMentionStatus.Unreadable,
                "The file could not be read.",
                range,
                relative,
                absolute);
        }

        try
        {
            if (range is LineRange requested)
            {
                var (content, clamped, lineCount) =
                    await ReadRangeAsync(absolute, requested, cancellationToken).ConfigureAwait(false);
                if (clamped is null)
                {
                    return Problem(
                        mentionText,
                        requestedPath,
                        FileMentionStatus.RangeOutOfBounds,
                        $"The file has {lineCount} line(s); line {requested.Start} does not exist.",
                        range,
                        relative,
                        absolute);
                }

                if (Encoding.UTF8.GetByteCount(content) > _options.MaxFileBytes)
                {
                    return Problem(
                        mentionText,
                        requestedPath,
                        FileMentionStatus.TooLarge,
                        $"The selected lines exceed the {_options.MaxFileBytes} byte per-file limit.",
                        clamped,
                        relative,
                        absolute);
                }

                return new FileMentionAttachment(
                    mentionText,
                    requestedPath,
                    FileMentionStatus.Attached,
                    relative,
                    absolute,
                    clamped,
                    content);
            }

            if (length > _options.MaxFileBytes)
            {
                return Problem(
                    mentionText,
                    requestedPath,
                    FileMentionStatus.TooLarge,
                    $"The file is {length} bytes, over the {_options.MaxFileBytes} byte limit. Mention a line range such as #L1-L200 to attach part of it.",
                    range,
                    relative,
                    absolute);
            }

            string text = await File.ReadAllTextAsync(absolute, cancellationToken).ConfigureAwait(false);
            return new FileMentionAttachment(
                mentionText,
                requestedPath,
                FileMentionStatus.Attached,
                relative,
                absolute,
                null,
                text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return Problem(
                mentionText,
                requestedPath,
                FileMentionStatus.Unreadable,
                "The file could not be read.",
                range,
                relative,
                absolute);
        }
    }

    private bool ResolvesToExistingFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        try
        {
            string absolute = Path.GetFullPath(
                Path.Combine(_workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            return CodeIndexPaths.IsContained(absolute, _workspaceRoot) && File.Exists(absolute);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private async Task<bool> LooksBinaryAsync(string absolutePath, CancellationToken cancellationToken)
    {
        int budget = Math.Max(256, _options.BinarySniffBytes);
        var buffer = new byte[budget];
        int read;
        await using (var stream = new FileStream(
            absolutePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true))
        {
            read = await stream.ReadAsync(buffer.AsMemory(0, budget), cancellationToken).ConfigureAwait(false);
        }

        if (read == 0)
        {
            return false;
        }

        // A NUL byte is the classic binary marker; beyond that, a high share of control bytes
        // that are not ordinary whitespace means the content is not usefully readable as text.
        int suspicious = 0;
        for (int i = 0; i < read; i++)
        {
            byte b = buffer[i];
            if (b == 0)
            {
                return true;
            }
            if (b < 0x09 || (b > 0x0D && b < 0x20) || b == 0x7F)
            {
                suspicious++;
            }
        }

        return suspicious * 100 / read > 10;
    }

    private static async Task<(string Content, LineRange? Clamped, int LineCount)> ReadRangeAsync(
        string absolutePath,
        LineRange requested,
        CancellationToken cancellationToken)
    {
        var selected = new List<string>();
        int lineNumber = 0;

        using (var reader = new StreamReader(absolutePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                lineNumber++;
                if (lineNumber >= requested.Start && lineNumber <= requested.End)
                {
                    selected.Add(line);
                }
                if (lineNumber >= requested.End)
                {
                    // Keep counting is unnecessary once the range is complete.
                    break;
                }
            }
        }

        if (selected.Count == 0)
        {
            return (string.Empty, null, lineNumber);
        }

        var clamped = new LineRange(requested.Start, requested.Start + selected.Count - 1);
        return (string.Join("\n", selected), clamped, lineNumber);
    }

    private static string MakeKey(string relativePath, LineRange? range) =>
        range is LineRange r ? $"{relativePath}#{r.Start}-{r.End}" : relativePath;

    private static FileMentionAttachment Problem(
        string mentionText,
        string requestedPath,
        FileMentionStatus status,
        string note,
        LineRange? range,
        string? relativePath = null,
        string? absolutePath = null) =>
        new(mentionText, requestedPath, status, relativePath, absolutePath, range, content: null, note: note);
}
