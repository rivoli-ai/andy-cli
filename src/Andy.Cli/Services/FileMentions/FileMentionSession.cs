using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Services.FileMentions;

/// <summary>
/// Bundles the pieces the @-mention feature needs for one session: the workspace listing used by
/// the picker and the resolver used at submission time. Both are anchored to the same root and
/// share ignore rules, so a file that cannot be picked also cannot be attached by typing its path
/// by hand.
/// </summary>
public sealed class FileMentionSession
{
    private readonly WorkspaceIgnoreRules _ignoreRules;

    public FileMentionSession(string? workspaceRoot = null, FileMentionResolverOptions? options = null)
    {
        Root = string.IsNullOrWhiteSpace(workspaceRoot)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(workspaceRoot);
        _ignoreRules = new WorkspaceIgnoreRules(Root);
        Index = new WorkspaceFileIndex(Root, _ignoreRules);
        Search = new FileMentionSearchService(Index);
        Resolver = new FileMentionResolver(Root, _ignoreRules, options);
    }

    /// <summary>Absolute workspace root.</summary>
    public string Root { get; }

    /// <summary>Cached workspace listing behind the picker.</summary>
    public WorkspaceFileIndex Index { get; }

    /// <summary>Fuzzy search used by the picker.</summary>
    public FileMentionSearchService Search { get; }

    /// <summary>Resolver used at submission time.</summary>
    public FileMentionResolver Resolver { get; }

    /// <summary>Resolve a prompt's mentions into structured attachments.</summary>
    public Task<ResolvedPrompt> ResolveAsync(string promptText, CancellationToken cancellationToken = default) =>
        Resolver.ResolveAsync(promptText, cancellationToken);

    /// <summary>
    /// A one-paragraph summary of what a resolution attached and what it refused, or null when the
    /// prompt had no mentions at all. Shown in the transcript so attaching is never silent - the
    /// user can always see which file contents left their machine.
    /// </summary>
    public static string? DescribeResolution(ResolvedPrompt resolved)
    {
        if (resolved is null || resolved.Attachments.Count == 0)
        {
            return null;
        }

        var attached = resolved.AttachedFiles;
        var problems = resolved.Problems;
        var sb = new StringBuilder();

        if (attached.Count > 0)
        {
            sb.Append("[files] Attached ").Append(attached.Count).Append(attached.Count == 1 ? " file: " : " files: ");
            sb.Append(string.Join(", ", attached.Select(Describe)));
        }

        foreach (var problem in problems)
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }
            sb.Append("[files] Skipped ").Append(problem.DisplayPath).Append(" - ").Append(problem.Note ?? problem.Status.ToString());
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static string Describe(FileMentionAttachment attachment) =>
        attachment.Range is LineRange range
            ? $"{attachment.DisplayPath} (lines {range.Start}-{range.End})"
            : attachment.DisplayPath;
}
