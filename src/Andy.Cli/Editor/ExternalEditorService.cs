using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Editor;

/// <summary>What happened during an external-editor round trip.</summary>
public enum ExternalEditorOutcome
{
    /// <summary>The edit succeeded; the composer should adopt the returned document.</summary>
    Applied,
    /// <summary>Neither VISUAL nor EDITOR is usable; the message carries setup guidance.</summary>
    NotConfigured,
    /// <summary>The editor could not be started; the composer is unchanged.</summary>
    LaunchFailed,
    /// <summary>The editor exited nonzero or was killed by a signal; the composer is unchanged.</summary>
    EditorFailed,
    /// <summary>The saved file exceeded the size limit; the composer is unchanged.</summary>
    TooLarge,
    /// <summary>The round trip was cancelled; the composer is unchanged.</summary>
    Cancelled,
    /// <summary>An unexpected error (I/O, permissions); the composer is unchanged.</summary>
    Error,
}

/// <summary>Result of <see cref="ExternalEditorService.EditAsync"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Document">
/// The document the composer should hold: the edited one when <see cref="ExternalEditorOutcome.Applied"/>,
/// otherwise the untouched original.
/// </param>
/// <param name="Message">User-facing message (empty when there is nothing to say).</param>
public sealed record ExternalEditorResult(ExternalEditorOutcome Outcome, ComposerDocument Document, string Message)
{
    /// <summary>True when the composer should adopt <see cref="Document"/>.</summary>
    public bool Applied => Outcome == ExternalEditorOutcome.Applied;
}

/// <summary>
/// Runs the "edit the prompt in $VISUAL/$EDITOR" round trip (issue #287).
///
/// <para>Order of operations, with restoration guaranteed by nested finally blocks:</para>
/// <list type="number">
///   <item><description>Resolve the editor. Failing here costs nothing: no temp file, no terminal hand-off.</description></item>
///   <item><description>Write the editable text to an owner-only temp file.</description></item>
///   <item><description>Suspend raw input, mouse reporting, the alternate screen and cursor management.</description></item>
///   <item><description>Launch the editor directly (no shell) and wait.</description></item>
///   <item><description>Restore the terminal and request a repaint - on success, nonzero exit, launch
///     failure, cancellation, signal and exception alike.</description></item>
///   <item><description>Only on a clean exit: read the file back, enforce the size limit and rebuild the
///     document, preserving structured parts.</description></item>
///   <item><description>Delete the temp file on every path.</description></item>
/// </list>
/// </summary>
public sealed class ExternalEditorService
{
    /// <summary>Default cap on the saved file (1 MiB). Bigger prompts are rejected, not truncated.</summary>
    public const int DefaultMaxEditedBytes = 1024 * 1024;

    private readonly EditorResolver _resolver;
    private readonly IEditorProcessRunner _runner;
    private readonly TerminalSuspendController _terminal;
    private readonly string? _tempRoot;
    private readonly int _maxEditedBytes;

    public ExternalEditorService(
        EditorResolver resolver,
        IEditorProcessRunner runner,
        TerminalSuspendController terminal,
        string? tempRoot = null,
        int maxEditedBytes = DefaultMaxEditedBytes)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _tempRoot = tempRoot;
        _maxEditedBytes = maxEditedBytes > 0 ? maxEditedBytes : DefaultMaxEditedBytes;
    }

    /// <summary>Edit <paramref name="document"/> in the user's editor and return the result.</summary>
    public async Task<ExternalEditorResult> EditAsync(
        ComposerDocument document,
        CancellationToken cancellationToken = default)
    {
        document ??= ComposerDocument.Empty;

        var resolution = _resolver.Resolve();
        if (!resolution.Success)
            return new ExternalEditorResult(ExternalEditorOutcome.NotConfigured, document, resolution.Message ?? string.Empty);

        EditorTempFile? temp = null;
        try
        {
            temp = EditorTempFile.Create(document.ToEditableText(), _tempRoot);
        }
        catch (Exception ex)
        {
            return new ExternalEditorResult(
                ExternalEditorOutcome.Error,
                document,
                $"Could not create a temporary file for the editor: {ex.Message}");
        }

        try
        {
            EditorProcessResult run;
            // The terminal belongs to the editor for exactly this block. The finally restores
            // it whether the editor exited cleanly, failed, was signalled, or threw.
            var scope = _terminal.Suspend();
            try
            {
                run = await _runner.RunAsync(resolution.FileName, resolution.Arguments, temp.Path, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new ExternalEditorResult(
                    ExternalEditorOutcome.Cancelled,
                    document,
                    "Editing was cancelled; the prompt is unchanged.");
            }
            catch (Exception ex)
            {
                return new ExternalEditorResult(
                    ExternalEditorOutcome.Error,
                    document,
                    $"The editor could not be run: {ex.Message}. The prompt is unchanged.");
            }
            finally
            {
                scope.Dispose();
            }

            if (!run.Started)
            {
                return new ExternalEditorResult(
                    ExternalEditorOutcome.LaunchFailed,
                    document,
                    $"{resolution.Variable}: {run.FailureMessage} The prompt is unchanged.\n\n" +
                    EditorSetupGuidance.QuotingHelp());
            }

            if (!run.Succeeded)
            {
                string how = run.TerminatedBySignal
                    ? $"was terminated by signal {run.ExitCode - 128}"
                    : $"exited with code {run.ExitCode}";
                return new ExternalEditorResult(
                    ExternalEditorOutcome.EditorFailed,
                    document,
                    $"The editor {how}; the prompt is unchanged.");
            }

            long length = temp.Length;
            if (length > _maxEditedBytes)
            {
                return new ExternalEditorResult(
                    ExternalEditorOutcome.TooLarge,
                    document,
                    $"The edited prompt is {length} bytes, over the {_maxEditedBytes} byte limit; the prompt is unchanged.");
            }

            string edited;
            try
            {
                edited = temp.ReadAllText();
            }
            catch (Exception ex)
            {
                return new ExternalEditorResult(
                    ExternalEditorOutcome.Error,
                    document,
                    $"The edited file could not be read back: {ex.Message}. The prompt is unchanged.");
            }

            var updated = document.ApplyEditedText(TrimEditorTrailingNewline(edited));
            return new ExternalEditorResult(ExternalEditorOutcome.Applied, updated, string.Empty);
        }
        finally
        {
            temp.Dispose();
        }
    }

    /// <summary>
    /// Drop a single trailing newline. Most editors (vim, nano, emacs) unconditionally terminate
    /// the last line, so without this every round trip would silently append a blank line. Only
    /// ONE newline is removed, so a prompt the user deliberately ended with a blank line keeps it.
    /// </summary>
    internal static string TrimEditorTrailingNewline(string text)
    {
        string normalized = ComposerDocument.NormalizeNewlines(text ?? string.Empty);
        return normalized.EndsWith("\n", StringComparison.Ordinal)
            ? normalized.Substring(0, normalized.Length - 1)
            : normalized;
    }
}
