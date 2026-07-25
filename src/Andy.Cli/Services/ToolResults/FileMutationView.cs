using Andy.Cli.Widgets;

namespace Andy.Cli.Services.ToolResults;

/// <summary>
/// A file change captured around a mutating tool call, ready for the presenter to render.
///
/// write_file and replace_text overwrite the target and report neither the old nor the new
/// content, so the executor snapshots the file before the call and reads it back afterwards. The
/// result travels as structured data - a computed <see cref="FileDiff"/> plus, for a newly created
/// file, its content - so the presenter decides how to show it.
/// </summary>
/// <param name="DisplayPath">Path shown to the user, relative to the working directory where possible.</param>
/// <param name="Kind">Whether the call created the file or updated an existing one.</param>
/// <param name="Diff">The computed line diff.</param>
/// <param name="Content">
/// The file's new content, carried only for a creation: a diff against a file that did not exist
/// is all-plus noise, and a numbered listing says the same thing more clearly.
/// </param>
public sealed record FileMutationView(
    string DisplayPath,
    FileChangeKind Kind,
    FileDiff Diff,
    string? Content);
