using System;
using System.Collections.Generic;

namespace Andy.Cli.Services.ToolResults;

/// <summary>
/// Everything the feed knows about one tool call, carried in the shape the tool produced it.
///
/// This is the boundary type for issue #249/#250: <see cref="Data"/> and <see cref="Metadata"/>
/// hold the ORIGINAL objects returned by Andy.Tools (<c>ToolExecutionResult.Data</c> /
/// <c>ToolResult.Metadata</c>), not a rendered string. Renderers read them once through
/// <see cref="ToolData"/>; nothing downstream re-parses display text.
///
/// Before this type existed the same facts were reconstructed four separate times - in
/// UiUpdatingToolExecutor, ToolExecutionTracker.FormatResultSummary, FeedView.UpdateToolResult and
/// RunningToolItem.ExtractStatistics - each by string or regex scraping, and each with a different
/// answer. Metadata (which carries most of the counts: line_count, total_matches, file_count, ...)
/// never reached the UI at all.
/// </summary>
public sealed record ToolCallSnapshot
{
    /// <summary>UI-side execution id (the tool name plus an execution counter, e.g. "read_file_1").</summary>
    public required string ToolId { get; init; }

    /// <summary>Tool id as registered ("read_file"), already normalized of any counter suffix.</summary>
    public required string ToolName { get; init; }

    /// <summary>Arguments the tool was invoked with, minus nothing - renderers filter "__" keys themselves.</summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>False while the tool is still running.</summary>
    public bool IsComplete { get; init; }

    /// <summary>Meaningful only when <see cref="IsComplete"/>.</summary>
    public bool IsSuccessful { get; init; }

    /// <summary>The tool's structured payload, exactly as returned. Usually a Dictionary&lt;string, object?&gt;.</summary>
    public object? Data { get; init; }

    /// <summary>The tool's metadata dictionary, exactly as returned.</summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>The tool's own error message when it failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Human-readable message the tool returned alongside its data. Kept as a last-resort fallback
    /// for renderers that have nothing structured to show; never the primary source.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>Measured execution time, available as soon as the tool returns.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>When the call started, used to animate the elapsed clock while it runs.</summary>
    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>The execution was cancelled rather than failing on its own terms.</summary>
    public bool WasCancelled { get; init; }

    /// <summary>The permission gate denied the call. Rendered distinctly from a failure (#264).</summary>
    public bool WasDenied { get; init; }

    /// <summary>
    /// The file change this call made, when it was a mutating tool. Computed by the executor
    /// because the tool itself returns neither the old nor the new content.
    /// </summary>
    public FileMutationView? FileMutation { get; init; }

    /// <summary>
    /// A later call of the same kind has replaced what this one showed.
    ///
    /// Used for the plan (#258): every revision is kept in the transcript, but only the current
    /// one is drawn in full, so a long session is not dominated by five copies of a todo list.
    /// The superseded ones collapse to their header rather than being removed, because deleting
    /// feed history would shift everything the user has already scrolled past.
    /// </summary>
    public bool IsSuperseded { get; init; }

    /// <summary>A completed call that neither failed nor was cancelled or denied.</summary>
    public bool Succeeded => IsComplete && IsSuccessful && !WasCancelled && !WasDenied;

    // Andy.Tools splits a result across two places and is not consistent about which: the
    // ToolResults.TextSuccess helper merges the tool's metadata INTO Data, while ListSuccess
    // leaves it on Metadata. Presenters should not have to know which helper a tool happened to
    // use, so these accessors look in Data first and fall back to Metadata.

    /// <summary>Read a string from the result payload.</summary>
    public string? ResultString(params string[] keys)
        => ToolData.GetString(Data, keys) ?? ToolData.GetString(Metadata, keys);

    /// <summary>Read an integer from the result payload.</summary>
    public int? ResultInt(params string[] keys)
        => ToolData.GetInt(Data, keys) ?? ToolData.GetInt(Metadata, keys);

    /// <summary>Read a long from the result payload.</summary>
    public long? ResultLong(params string[] keys)
        => ToolData.GetLong(Data, keys) ?? ToolData.GetLong(Metadata, keys);

    /// <summary>Read a boolean from the result payload.</summary>
    public bool? ResultBool(params string[] keys)
        => ToolData.GetBool(Data, keys) ?? ToolData.GetBool(Metadata, keys);

    /// <summary>Read a duration from the result payload.</summary>
    public TimeSpan? ResultDuration(params string[] keys)
        => ToolData.GetDuration(Data, keys) ?? ToolData.GetDuration(Metadata, keys);

    /// <summary>Read a list from the result payload.</summary>
    public IReadOnlyList<object?> ResultList(params string[] keys)
    {
        var items = ToolData.GetList(Data, keys);
        return items.Count > 0 ? items : ToolData.GetList(Metadata, keys);
    }

    /// <summary>Read a string argument the tool was called with.</summary>
    public string? Argument(params string[] keys) => ToolData.GetString(Parameters, keys);

    /// <summary>Terminal state used to pick the status glyph and color.</summary>
    public ToolCallStatus Status =>
        !IsComplete ? ToolCallStatus.Running
        : WasDenied ? ToolCallStatus.Denied
        : WasCancelled ? ToolCallStatus.Cancelled
        : IsSuccessful ? ToolCallStatus.Success
        : ToolCallStatus.Failed;
}

/// <summary>Terminal state of a tool call, mapped to a glyph and theme color by the renderers.</summary>
public enum ToolCallStatus
{
    /// <summary>Still executing.</summary>
    Running,

    /// <summary>Completed successfully.</summary>
    Success,

    /// <summary>The tool itself reported a failure.</summary>
    Failed,

    /// <summary>The execution was cancelled or interrupted.</summary>
    Cancelled,

    /// <summary>The permission gate refused the call; the tool never ran.</summary>
    Denied
}
