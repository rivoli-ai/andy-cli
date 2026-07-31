using System;
using System.Collections.Generic;
using System.Linq;

namespace Andy.Cli.Commands.Custom;

/// <summary>
/// Where a Markdown slash command was discovered. Project commands live in
/// <c>&lt;workspace&gt;/.andy/commands</c> and are checked into the repository; user commands
/// live in <c>~/.andy/commands</c> and are personal. Project wins on a name conflict.
/// </summary>
public enum CustomCommandSource
{
    /// <summary>Discovered under the user's home directory (~/.andy/commands).</summary>
    User = 0,

    /// <summary>Discovered under the workspace (&lt;workspace&gt;/.andy/commands).</summary>
    Project = 1,
}

/// <summary>Severity of a discovery/expansion problem. Nothing here ever aborts startup.</summary>
public enum CustomCommandDiagnosticSeverity
{
    /// <summary>Informational (for example: a user command shadowed by a project command).</summary>
    Info = 0,

    /// <summary>The command still loaded, but part of it was ignored.</summary>
    Warning = 1,

    /// <summary>The file was rejected and is not available as a command.</summary>
    Error = 2,
}

/// <summary>
/// A problem found while discovering or expanding Markdown commands. Diagnostics are
/// collected and surfaced through <c>/commands diagnostics</c>; discovery never throws so a
/// broken template cannot take the TUI down at startup.
/// </summary>
public sealed class CustomCommandDiagnostic
{
    public CustomCommandDiagnostic(CustomCommandDiagnosticSeverity severity, string path, string message)
    {
        Severity = severity;
        Path = path ?? "";
        Message = message ?? "";
    }

    public CustomCommandDiagnosticSeverity Severity { get; }

    /// <summary>The file (or command name) the diagnostic is about.</summary>
    public string Path { get; }

    public string Message { get; }

    public override string ToString() => $"[{Severity}] {Path}: {Message}";
}

/// <summary>
/// A slash command defined by a Markdown template file. Immutable; produced by
/// <see cref="CustomCommandDiscovery"/> and expanded by <see cref="CustomCommandExpander"/>.
/// Deliberately free of any TUI or DI dependency so the interactive dispatcher, a future
/// headless runner, and the ACP server can all share the same parser and expansion rules.
/// </summary>
public sealed class CustomCommandDefinition
{
    public CustomCommandDefinition(
        string name,
        string description,
        string template,
        string filePath,
        CustomCommandSource source,
        string? provider = null,
        string? model = null,
        string? mode = null,
        IReadOnlyList<string>? shadowedFilePaths = null)
    {
        Name = name;
        Description = description ?? "";
        Template = template ?? "";
        FilePath = filePath ?? "";
        Source = source;
        Provider = string.IsNullOrWhiteSpace(provider) ? null : provider.Trim();
        Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        Mode = string.IsNullOrWhiteSpace(mode) ? null : mode.Trim();
        ShadowedFilePaths = shadowedFilePaths ?? Array.Empty<string>();
    }

    /// <summary>
    /// Fully qualified command name. Nested directories become colon-separated segments,
    /// so <c>.andy/commands/git/commit.md</c> is <c>/git:commit</c>.
    /// </summary>
    public string Name { get; }

    /// <summary>Frontmatter <c>description</c>, or a summary derived from the body.</summary>
    public string Description { get; }

    /// <summary>The Markdown body, verbatim, before argument expansion.</summary>
    public string Template { get; }

    /// <summary>Absolute path of the Markdown file that defines the command.</summary>
    public string FilePath { get; }

    public CustomCommandSource Source { get; }

    /// <summary>Optional preferred provider (advisory metadata; it does not switch providers by itself).</summary>
    public string? Provider { get; }

    /// <summary>Optional preferred model (advisory metadata).</summary>
    public string? Model { get; }

    /// <summary>Optional mode hint (advisory metadata; it can never widen permissions).</summary>
    public string? Mode { get; }

    /// <summary>Files that define the same name but lost the precedence contest.</summary>
    public IReadOnlyList<string> ShadowedFilePaths { get; }

    /// <summary>Human-readable source label, for example <c>project</c> or <c>user</c>.</summary>
    public string SourceLabel => Source == CustomCommandSource.Project ? "project" : "user";

    /// <summary>Attribution line shown in the transcript and attached to expanded prompts.</summary>
    public string SourceAttribution => $"{SourceLabel} command /{Name} ({FilePath})";

    /// <summary>Alternate spelling accepted on input: <c>/git/commit</c> for <c>/git:commit</c>.</summary>
    public string SlashPathForm => Name.Replace(':', '/');

    /// <summary>The highest positional placeholder ($1..$9) referenced by the template, or 0.</summary>
    public int MaxPositional => CustomCommandExpander.MaxPositionalReferenced(Template);

    /// <summary>True when the template consumes the whole argument string via $ARGUMENTS.</summary>
    public bool UsesArguments => CustomCommandExpander.ReferencesArguments(Template);
}

/// <summary>
/// A file pulled in by an <c>@path</c> mention inside a template. Kept as a structured part
/// (rather than being spliced into the prose) so that the caller keeps path, size, and
/// truncation information, and so #277's structured resolver can replace the local resolver
/// without changing the shape of the result.
/// </summary>
public sealed class PromptFilePart
{
    public PromptFilePart(string mention, string resolvedPath, string content, long fileBytes, bool truncated)
    {
        Mention = mention;
        ResolvedPath = resolvedPath;
        Content = content;
        FileBytes = fileBytes;
        Truncated = truncated;
    }

    /// <summary>The literal mention as written in the template, for example <c>@src/Program.cs</c>.</summary>
    public string Mention { get; }

    /// <summary>Absolute path the mention resolved to.</summary>
    public string ResolvedPath { get; }

    /// <summary>File text (possibly truncated to the referenced-file limit).</summary>
    public string Content { get; }

    /// <summary>Size of the file on disk in bytes.</summary>
    public long FileBytes { get; }

    /// <summary>True when <see cref="Content"/> was cut short by the size limit.</summary>
    public bool Truncated { get; }
}

/// <summary>
/// The result of expanding a command template with a user-supplied argument string.
/// <see cref="Text"/> is the expanded body; <see cref="Files"/> keeps the resolved
/// <c>@file</c> mentions as structured parts; <see cref="SourceAttribution"/> records which
/// file the prompt came from.
/// </summary>
public sealed class ExpandedCommandPrompt
{
    public ExpandedCommandPrompt(
        CustomCommandDefinition command,
        string text,
        IReadOnlyList<PromptFilePart> files,
        IReadOnlyList<CustomCommandDiagnostic> diagnostics)
    {
        Command = command;
        Text = text ?? "";
        Files = files ?? Array.Empty<PromptFilePart>();
        Diagnostics = diagnostics ?? Array.Empty<CustomCommandDiagnostic>();
    }

    public CustomCommandDefinition Command { get; }

    /// <summary>The expanded template body (mentions left in place as written).</summary>
    public string Text { get; }

    /// <summary>Resolved <c>@file</c> mentions, in first-appearance order, de-duplicated.</summary>
    public IReadOnlyList<PromptFilePart> Files { get; }

    /// <summary>Non-fatal problems found while expanding (unresolved mentions, size limits).</summary>
    public IReadOnlyList<CustomCommandDiagnostic> Diagnostics { get; }

    /// <summary>Where this prompt came from, kept with the prompt for auditing.</summary>
    public string SourceAttribution => Command.SourceAttribution;

    /// <summary>
    /// Flatten to the single string sent to the model: the expanded body, then one fenced
    /// block per referenced file, then the source attribution. The file parts stay
    /// individually addressable on <see cref="Files"/> for callers that can send structured
    /// content instead.
    /// </summary>
    public string ToPromptText()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Text.TrimEnd());
        foreach (var file in Files)
        {
            sb.Append("\n\n");
            sb.Append($"--- {file.Mention} ({file.ResolvedPath}){(file.Truncated ? " [truncated]" : "")} ---\n");
            sb.Append("```\n");
            sb.Append(file.Content.TrimEnd('\n'));
            sb.Append("\n```");
        }
        sb.Append("\n\n(Source: ");
        sb.Append(SourceAttribution);
        sb.Append(')');
        return sb.ToString();
    }
}

/// <summary>
/// Hard limits enforced BEFORE any prompt is constructed (issue #281 security constraint).
/// A template larger than <see cref="MaxTemplateBytes"/> is never read into memory as a
/// command, and referenced files are size-checked with <c>FileInfo.Length</c> before the
/// read so a huge or runaway file cannot be pulled into a prompt.
/// </summary>
public sealed class CustomCommandLimits
{
    public static CustomCommandLimits Default { get; } = new();

    /// <summary>Maximum size of a command Markdown file. Larger files are rejected with a diagnostic.</summary>
    public int MaxTemplateBytes { get; init; } = 64 * 1024;

    /// <summary>Maximum bytes read from a single <c>@file</c> mention; the rest is truncated.</summary>
    public int MaxReferencedFileBytes { get; init; } = 64 * 1024;

    /// <summary>Maximum number of <c>@file</c> mentions resolved per expansion.</summary>
    public int MaxReferencedFiles { get; init; } = 10;

    /// <summary>Maximum total bytes across all resolved mentions in one expansion.</summary>
    public int MaxTotalReferencedBytes { get; init; } = 256 * 1024;

    /// <summary>Maximum number of files scanned per command root (a runaway-directory guard).</summary>
    public int MaxCommandFiles { get; init; } = 500;

    /// <summary>Maximum directory nesting under a command root.</summary>
    public int MaxDirectoryDepth { get; init; } = 8;
}
