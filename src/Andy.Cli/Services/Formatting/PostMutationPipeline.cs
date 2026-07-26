using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Widgets;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services.Formatting;

/// <summary>
/// A file mutation that has just succeeded, handed to the shared post-mutation pipeline.
/// </summary>
/// <param name="ToolId">The tool that performed the mutation (write / edit / patch / rename / create).</param>
/// <param name="ResolvedPath">Absolute path of the mutated file.</param>
/// <param name="DisplayPath">Path as shown to the user, relative to the working directory where possible.</param>
/// <param name="BeforeText">The file's content captured before the mutation ("" when it did not exist).</param>
/// <param name="Existed">Whether the file existed before the mutation.</param>
/// <param name="WorkingDirectory">The session working directory.</param>
public sealed record PostMutationRequest(
    string ToolId,
    string ResolvedPath,
    string DisplayPath,
    string BeforeText,
    bool Existed,
    string WorkingDirectory);

/// <summary>
/// Mutable state threaded through the post-mutation steps. Steps read the final on-disk content and
/// may inspect the formatter results; the pipeline computes the diff from
/// <see cref="FinalContent"/> after every step has run.
/// </summary>
public sealed class PostMutationContext
{
    public PostMutationContext(PostMutationRequest request) => Request = request;

    public PostMutationRequest Request { get; }

    /// <summary>The file's content as it stands on disk right now (after formatting). Null when unreadable.</summary>
    public string? FinalContent { get; internal set; }

    /// <summary>Per-formatter outcomes, in execution order. Empty when nothing matched.</summary>
    public IReadOnlyList<FormatterRunResult> FormatterResults { get; internal set; } = Array.Empty<FormatterRunResult>();

    /// <summary>Whether the mutation created the file or updated an existing one.</summary>
    public FileChangeKind Kind => Request.Existed ? FileChangeKind.Update : FileChangeKind.Create;
}

/// <summary>
/// A step that runs after formatting and before the diff is computed.
///
/// This is the extension point the sibling features plug into; see
/// <see cref="PostMutationStepOrder"/> for the reserved slots.
/// </summary>
public interface IPostMutationStep
{
    /// <summary>Name used in logs.</summary>
    string Name { get; }

    /// <summary>Sort key; see <see cref="PostMutationStepOrder"/>.</summary>
    int Order { get; }

    Task RunAsync(PostMutationContext context, CancellationToken cancellationToken);
}

/// <summary>
/// The ordering contract for the post-mutation pipeline, kept in one place so the sibling features
/// can slot in without re-deriving it.
///
/// Issue #283 requires formatting to happen "after a successful file mutation and BEFORE final diff
/// rendering, LSP notification, and session snapshot finalization" - so that the diff the user sees
/// and the bytes the language server and the snapshot record are all the same final bytes.
///
/// <code>
///   tool mutation succeeds
///        |
///   [100] Formatting            (this feature, #283 - built in to the pipeline)
///        |
///   re-read final on-disk bytes
///        |
///   [200] SnapshotFinalize      SEAM for #276 - register an IPostMutationStep with this Order
///        |
///   [300] LspNotify             SEAM for #282 - register an IPostMutationStep with this Order
///        |
///   [400] diff computed from the final bytes and rendered
/// </code>
/// </summary>
public static class PostMutationStepOrder
{
    /// <summary>Formatting. Owned by the pipeline itself; not a registerable step.</summary>
    public const int Formatting = 100;

    /// <summary>
    /// INTEGRATION SEAM (issue #276 - snapshot transaction boundaries). Register the step that
    /// finalizes the session snapshot at this order so it records the post-format bytes.
    /// </summary>
    public const int SnapshotFinalize = 200;

    /// <summary>
    /// INTEGRATION SEAM (issue #282 - LSP notification ordering). Register the step that notifies
    /// the language server at this order so it is told about the post-format bytes exactly once.
    /// </summary>
    public const int LspNotify = 300;

    /// <summary>Diff computation and rendering. Owned by the pipeline itself.</summary>
    public const int DiffRendering = 400;
}

/// <summary>
/// The result of the post-mutation pipeline: the final on-disk state of the file, the diff computed
/// from those final bytes, and what each formatter did.
/// </summary>
/// <param name="DisplayPath">Path as shown to the user.</param>
/// <param name="Kind">Create or update.</param>
/// <param name="Diff">Diff from the pre-mutation content to the FINAL on-disk content.</param>
/// <param name="FinalContent">The final on-disk content, or null when it could not be read.</param>
/// <param name="FormatterResults">One entry per formatter that was considered runnable.</param>
public sealed record PostMutationResult(
    string DisplayPath,
    FileChangeKind Kind,
    FileDiff Diff,
    string? FinalContent,
    IReadOnlyList<FormatterRunResult> FormatterResults)
{
    /// <summary>True when at least one formatter failed to do its job.</summary>
    public bool FormattingFailed => FormatterResults.Any(r => r.IsFailure);

    /// <summary>True when at least one formatter rewrote the file.</summary>
    public bool FormattingChangedContent => FormatterResults.Any(r => r.Outcome == FormatterOutcome.Changed);

    /// <summary>
    /// The bounded, redacted report handed back to the agent when formatting failed, or null when
    /// every formatter succeeded. Never null-and-silent on a failure: the agent must not be able to
    /// conclude the file was formatted when it was not.
    /// </summary>
    public string? AgentReport => FormatterDiagnostics.BuildAgentReport(DisplayPath, FormatterResults);
}

/// <summary>
/// The single post-mutation pipeline shared by every file-mutating tool (write, replace, patch,
/// rename, create).
///
/// It exists so that the diff the user sees is computed from the FINAL on-disk bytes rather than
/// from what the tool intended to write. Before this pipeline, a formatter running later (by hand,
/// or by a pre-commit hook) made the rendered diff diverge from the file on disk.
///
/// Everything is best-effort with respect to display: an unreadable, binary, or oversized file
/// yields a null result and the caller simply shows no diff, exactly as before. Formatter FAILURES
/// are not best-effort - they are always reported.
/// </summary>
public sealed class PostMutationPipeline
{
    /// <summary>
    /// Files larger than this are neither diffed nor formatted. Matches the executor's existing diff
    /// cap so a file that was never diffable does not suddenly start launching processes.
    /// </summary>
    public const long MaxFileBytes = 512 * 1024;

    private readonly FormatterRunner? _formatterRunner;
    private readonly IReadOnlyList<IPostMutationStep> _steps;
    private readonly ILogger? _logger;

    public PostMutationPipeline(
        FormatterRunner? formatterRunner,
        IEnumerable<IPostMutationStep>? steps = null,
        ILogger? logger = null)
    {
        _formatterRunner = formatterRunner;
        _steps = (steps ?? Array.Empty<IPostMutationStep>())
            .OrderBy(s => s.Order)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToArray();
        _logger = logger;
    }

    /// <summary>A pipeline that only computes the diff: no formatters, no extra steps.</summary>
    public static PostMutationPipeline DiffOnly { get; } = new(null);

    /// <summary>
    /// Run the pipeline for one successful mutation. Returns null when there is nothing meaningful
    /// to show (file gone, too large, binary, or no visible change) - the same conditions under
    /// which the executor previously rendered nothing.
    /// </summary>
    public async Task<PostMutationResult?> RunAsync(PostMutationRequest request, CancellationToken cancellationToken)
    {
        var context = new PostMutationContext(request);

        // Phase 100: formatting. Runs first so every later phase sees the final bytes.
        if (_formatterRunner is not null && IsFormattable(request.ResolvedPath))
        {
            try
            {
                context.FormatterResults = await _formatterRunner
                    .RunAsync(request.ResolvedPath, request.WorkingDirectory, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[FORMATTER] Formatting pipeline failed for {Path}", request.ResolvedPath);
            }
        }

        // Re-read the FINAL on-disk bytes. This is the whole point of the pipeline: the diff below,
        // and every step in between, describe what is actually on disk.
        context.FinalContent = TryReadFinalContent(request.ResolvedPath);

        // Phases 200 (#276 snapshot finalize) and 300 (#282 LSP notify) plug in here, between the
        // final read and the diff, in Order sequence.
        foreach (var step in _steps)
        {
            try
            {
                await step.RunAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[POST_MUTATION] Step {Step} failed for {Path}", step.Name, request.ResolvedPath);
            }
        }

        if (context.FinalContent is null)
        {
            // Nothing renderable (deleted, binary, oversized). Formatter failures still need to
            // reach the agent, so a result is returned whenever there is something to report.
            if (context.FormatterResults.Count == 0)
            {
                return null;
            }

            return new PostMutationResult(
                request.DisplayPath, context.Kind, new FileDiff(Array.Empty<DiffLine>(), 0, 0),
                null, context.FormatterResults);
        }

        // Phase 400: the diff, computed from the final bytes.
        var diff = UnifiedDiff.Compute(request.BeforeText, context.FinalContent);
        if (diff.IsEmpty && context.FormatterResults.Count == 0)
        {
            return null;
        }

        return new PostMutationResult(
            request.DisplayPath,
            context.Kind,
            diff,
            // Only a creation needs the content: an update reads better as a diff.
            context.Kind == FileChangeKind.Create ? context.FinalContent : null,
            context.FormatterResults);
    }

    private bool IsFormattable(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            if (new FileInfo(path).Length > MaxFileBytes)
            {
                return false;
            }

            return _formatterRunner!.HasFormattersFor(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? TryReadFinalContent(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            if (new FileInfo(path).Length > MaxFileBytes)
            {
                return null;
            }

            var text = File.ReadAllText(path);
            return text.Contains('\0') ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
