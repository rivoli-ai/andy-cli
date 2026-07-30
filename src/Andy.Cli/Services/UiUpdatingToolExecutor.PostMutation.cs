using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services.Formatting;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services
{
    /// <summary>
    /// The executor's half of the shared post-mutation pipeline (issue #283).
    ///
    /// Every single-file mutating tool - write, create, patch, replace, rename - funnels through
    /// <see cref="RunPostMutationAsync"/>, which runs the configured formatters and then computes
    /// the diff from the FINAL on-disk bytes. Keeping this in a partial file leaves the (already
    /// large) main executor file untouched apart from the two call sites.
    /// </summary>
    public partial class UiUpdatingToolExecutor
    {
        /// <summary>
        /// Run the post-mutation pipeline for a successful mutation. Falls back to the diff-only
        /// pipeline when no pipeline was injected, which reproduces the pre-formatter behaviour
        /// exactly (read the file back, diff it, show nothing when the diff is empty).
        /// </summary>
        private async Task<PostMutationResult?> RunPostMutationAsync(
            FileMutationCapture capture,
            string toolId,
            ToolExecutionContext? context,
            Dictionary<string, object?> parameters)
        {
            var pipeline = _postMutationPipeline ?? PostMutationPipeline.DiffOnly;
            var workingDirectory = string.IsNullOrEmpty(context?.WorkingDirectory)
                ? _workingDirectory.Current
                : context!.WorkingDirectory;

            var request = new PostMutationRequest(
                ToolId: toolId,
                ResolvedPath: capture.ResolvedPath,
                DisplayPath: capture.DisplayPath,
                BeforeText: capture.BeforeText,
                Existed: capture.Existed,
                WorkingDirectory: workingDirectory);

            // The tool's own cancellation token governs the formatters too: cancelling a turn must
            // kill a formatter process, not leave it running past the turn that started it.
            var cancellationToken = context?.CancellationToken ?? CancellationToken.None;

            try
            {
                return await pipeline.RunAsync(request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // A cancelled turn simply shows no diff; the cancellation itself is reported by the
                // executor's normal cancellation path.
                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[UI_EXECUTOR] Post-mutation pipeline failed for {Path}", capture.ResolvedPath);
                return null;
            }
        }

        /// <summary>
        /// Adapt a pipeline result to the presenter's view model. Null when there is no visible
        /// change to render (identical write, no-op edit, pure rename, unreadable file).
        /// </summary>
        private static ToolResults.FileMutationView? ToFileMutationView(PostMutationResult? result)
        {
            if (result is null || result.FinalContent is null && result.Diff.IsEmpty)
            {
                return null;
            }

            if (result.Diff.IsEmpty)
            {
                return null;
            }

            return new ToolResults.FileMutationView(
                result.DisplayPath,
                result.Kind,
                result.Diff,
                // Only a creation needs the content: an update is better read as a diff.
                result.Kind == Widgets.FileChangeKind.Create ? result.FinalContent : null);
        }
    }
}
