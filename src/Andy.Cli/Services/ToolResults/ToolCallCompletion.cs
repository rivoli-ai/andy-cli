using System;
using System.Collections.Generic;

namespace Andy.Cli.Services.ToolResults;

/// <summary>
/// Everything a finished tool execution produced, forwarded to the feed unflattened.
///
/// This mirrors the fields of Andy.Tools' <c>ToolExecutionResult</c> that matter for display.
/// The point of the type is that the executor stops deciding what the result "means": it hands
/// over Data, Metadata, the error, the timing and the cancellation flag, and the presenter for
/// that tool decides what to say. Metadata in particular never reached the UI before, which is
/// why counts like line_count, total_matches and file_count had to be recovered by regex.
/// </summary>
public sealed record ToolCallCompletion
{
    /// <summary>Whether the tool reported success.</summary>
    public required bool IsSuccessful { get; init; }

    /// <summary>The tool's structured payload, exactly as returned.</summary>
    public object? Data { get; init; }

    /// <summary>The tool's metadata dictionary, exactly as returned.</summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    /// <summary>The tool's error message when it failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The tool's human-readable message, if it set one.</summary>
    public string? Message { get; init; }

    /// <summary>Measured execution time.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>The execution was cancelled or interrupted rather than failing on its own terms.</summary>
    public bool WasCancelled { get; init; }

    /// <summary>The permission gate refused the call and the tool never ran.</summary>
    public bool WasDenied { get; init; }

    /// <summary>
    /// The change a file-mutating tool made, computed locally by the executor.
    ///
    /// This is the one piece of display data the tool cannot supply itself: write_file and
    /// replace_text overwrite the file and return neither the old nor the new content, so a diff
    /// only exists if something captures "before" around the call. It travels here as structured
    /// data for the presenter rather than being rendered into a separate feed item, so the change
    /// stays attached to the call that made it.
    /// </summary>
    public FileMutationView? FileMutation { get; init; }

    /// <summary>Fold this completion into a running snapshot.</summary>
    public ToolCallSnapshot ApplyTo(ToolCallSnapshot snapshot) => snapshot with
    {
        FileMutation = FileMutation,
        IsComplete = true,
        IsSuccessful = IsSuccessful,
        Data = Data,
        Metadata = Metadata ?? snapshot.Metadata,
        ErrorMessage = ErrorMessage,
        Message = Message,
        Duration = Duration,
        WasCancelled = WasCancelled,
        WasDenied = WasDenied
    };
}
