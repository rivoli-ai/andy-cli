using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Andy.Cli.Modes;

/// <summary>The outcome of a mode policy check for one tool call.</summary>
/// <param name="Allowed">True when the mode permits the call to reach the permission engine.</param>
/// <param name="Reason">
/// A user- and model-facing explanation. Always populated when <paramref name="Allowed"/> is false.
/// </param>
public readonly record struct ModeToolVerdict(bool Allowed, string? Reason)
{
    public static ModeToolVerdict Allow() => new(true, null);
    public static ModeToolVerdict Deny(string reason) => new(false, reason);
}

/// <summary>
/// The read-only tool policy that Plan mode enforces (issue #278).
///
/// Design: <b>fail closed</b>. A tool is allowed only when it appears on the explicit read-only
/// list (or on the caller-supplied opt-in list). Anything else - a mutating built-in, a shell
/// tool, an MCP tool, a CLI subprocess tool, a tool added by a future package upgrade - is denied.
/// MCP tools in particular declare no capability metadata (see
/// <c>Andy.Cli.Headless.Tools.McpRemoteTool</c>, which leaves <c>RequiredPermissions</c> at
/// <c>None</c>), so their read-only-ness cannot be proven from metadata and must not be assumed.
///
/// This class only CLASSIFIES. Enforcement lives in <see cref="ModeToolGate"/> and its two
/// adapters (<see cref="ModeGatedToolExecutor"/> and <see cref="ModeGatedPermissionAuthorizer"/>),
/// which run ahead of the permission engine so no allow rule can override a Plan-mode deny.
/// </summary>
public sealed class PlanModeToolPolicy
{
    /// <summary>
    /// Built-in and CLI tools that only observe state. Every entry has been checked to have no
    /// filesystem-write, process-execution, or remote-write effect. Tools that merely READ files
    /// belong here; tools that can create, modify, move or delete anything do not.
    /// </summary>
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.OrdinalIgnoreCase)
    {
        // Filesystem reads
        "read_file",
        "read_many_files",
        "list_directory",
        "search_files",
        "search_text",
        "file_info",

        // Git inspection (never mutates the work tree or the object store)
        "git_diff",
        "git_status",
        "git_log",
        "git_show",
        "git_blame",

        // Code intelligence
        "code_index",

        // Host inspection
        "system_info",
        "process_info",

        // Pure computation over inputs the model already supplies
        "format_text",
        "json_processor",
        "date_time",
        "datetime_tool",
        "encoding",
        "encoding_tool",

        // Agent Skills: reads skill definitions from disk, executes nothing
        "skill",
        "skill_file",

        // PDF reading (Andy.Tools.Pdf) - extraction only, no writes
        "pdf_info",
        "pdf_extract_text",
        "pdf_reflow",
        "pdf_outline",
        "pdf_extract_tables",
        "pdf_search",

        // Dataframe inspection / in-memory transformation (Andy.Tools.Data). These operate on an
        // in-process DuckDB catalog and produce new in-memory datasets; none of them writes to
        // disk. dataframe_export is deliberately ABSENT - it writes files.
        "dataframe_list",
        "dataframe_schema",
        "dataframe_profile",
        "dataframe_preview",
        "dataframe_value_counts",
        "dataframe_assert",
        "dataframe_select",
        "dataframe_filter",
        "dataframe_with_column",
        "dataframe_rename",
        "dataframe_group_by",
        "dataframe_window",
        "dataframe_pivot",
        "dataframe_unpivot",
        "dataframe_unnest",
        "dataframe_join",
        "dataframe_sample",
        "dataframe_sort",
        "dataframe_distinct",
        "dataframe_union",
        "dataframe_fillna",
        "dataframe_dropna",
        "dataframe_drop",
        "dataframe_load_csv",
        "dataframe_load_json",
        "dataframe_load_parquet",
        "dataframe_load_delta",
    };

    /// <summary>
    /// Tools that are known to mutate. Listed separately from the fail-closed default purely so
    /// the deny message can name the concrete reason instead of "unclassified".
    /// </summary>
    private static readonly Dictionary<string, string> KnownMutatingTools =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["write_file"] = "it writes files",
            ["delete_file"] = "it deletes files",
            ["move_file"] = "it moves or renames files",
            ["copy_file"] = "it creates files",
            ["create_directory"] = "it creates directories",
            ["file_editor"] = "it edits files in place",
            ["edit_file"] = "it edits files in place",
            ["apply_patch"] = "it applies patches to files",
            ["replace_text"] = "it rewrites file contents",
            ["execute_command"] = "it runs shell commands, which can mutate anything",
            ["bash"] = "it runs shell commands, which can mutate anything",
            ["shell"] = "it runs shell commands, which can mutate anything",
            ["dataframe_export"] = "it writes datasets to disk",
            ["todo_management"] = "it writes the persisted todo list",
        };

    /// <summary>
    /// Tools whose read-only-ness depends on their arguments. The value inspects the parameter bag
    /// and returns a deny reason, or null when this particular call is observably read-only.
    /// </summary>
    private static readonly Dictionary<string, Func<IReadOnlyDictionary<string, object?>?, string?>>
        ParameterSensitiveTools = new(StringComparer.OrdinalIgnoreCase)
        {
            ["http_request"] = InspectHttpRequest,
        };

    /// <summary>HTTP methods that do not change server state.</summary>
    private static readonly HashSet<string> SafeHttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "HEAD", "OPTIONS",
    };

    /// <summary>
    /// Parameter names that mean "write the result somewhere". Their presence turns an otherwise
    /// read-only call into a filesystem mutation.
    /// </summary>
    private static readonly string[] OutputPathParameters =
    {
        "output_file", "output_path", "save_to", "save_path", "destination_path", "target_path",
    };

    private readonly HashSet<string> _additionalReadOnlyTools;

    /// <summary>
    /// The policy with no opt-in additions - the one used unless a caller supplies configuration.
    /// </summary>
    public static PlanModeToolPolicy Default { get; } = new();

    /// <param name="additionalReadOnlyTools">
    /// Tool ids an operator has explicitly declared read-only (see <see cref="ModeConfigFile"/>).
    /// Used to re-enable specific MCP or CLI tools that the fail-closed default would deny.
    /// </param>
    public PlanModeToolPolicy(IEnumerable<string>? additionalReadOnlyTools = null)
    {
        _additionalReadOnlyTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (additionalReadOnlyTools is not null)
        {
            foreach (var tool in additionalReadOnlyTools)
            {
                if (!string.IsNullOrWhiteSpace(tool))
                {
                    _additionalReadOnlyTools.Add(tool.Trim());
                }
            }
        }
    }

    /// <summary>Tool ids an operator opted back in as read-only.</summary>
    public IReadOnlyCollection<string> AdditionalReadOnlyTools => _additionalReadOnlyTools;

    /// <summary>
    /// Classifies one tool call for Plan mode. Never throws: an unusable parameter bag is treated
    /// as unknown, which fails closed.
    /// </summary>
    public ModeToolVerdict Evaluate(string toolId, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return ModeToolVerdict.Deny(
                "Plan mode denied a tool call with no tool id (Plan mode fails closed on anything it cannot classify).");
        }

        var id = toolId.Trim();

        if (KnownMutatingTools.TryGetValue(id, out var mutationReason))
        {
            return ModeToolVerdict.Deny(
                $"Plan mode is read-only and denies '{id}' because {mutationReason}. "
                + "Switch to Build mode with '/mode build' to make changes.");
        }

        // A write-destination argument turns any tool into a mutation, whatever its id says.
        var outputParameter = FindOutputPathParameter(parameters);
        if (outputParameter is not null)
        {
            return ModeToolVerdict.Deny(
                $"Plan mode is read-only and denies '{id}' because the call writes to a file via "
                + $"the '{outputParameter}' argument. Switch to Build mode with '/mode build' to make changes.");
        }

        if (ParameterSensitiveTools.TryGetValue(id, out var inspect))
        {
            var reason = SafeInspect(inspect, parameters);
            return reason is null
                ? ModeToolVerdict.Allow()
                : ModeToolVerdict.Deny(
                    $"Plan mode is read-only and denies this '{id}' call because {reason}. "
                    + "Switch to Build mode with '/mode build' to make changes.");
        }

        if (ReadOnlyTools.Contains(id) || _additionalReadOnlyTools.Contains(id))
        {
            return ModeToolVerdict.Allow();
        }

        // Fail closed. Unclassified covers MCP tools, CLI subprocess tools, and anything a package
        // upgrade adds: their effects are unknown, so Plan mode refuses rather than guesses.
        return ModeToolVerdict.Deny(
            $"Plan mode is read-only and denies '{id}' because it is not on the read-only tool list. "
            + "Plan mode fails closed: tools whose effects it cannot verify (including MCP and CLI tools) "
            + "are denied. Switch to Build mode with '/mode build', or declare the tool read-only in "
            + $"{ModeConfigFile.FileName}.");
    }

    private static string? SafeInspect(
        Func<IReadOnlyDictionary<string, object?>?, string?> inspect,
        IReadOnlyDictionary<string, object?>? parameters)
    {
        try
        {
            return inspect(parameters);
        }
        catch
        {
            // An unreadable parameter bag must never be treated as proof of safety.
            return "its arguments could not be inspected";
        }
    }

    /// <summary>
    /// http_request is read-only only for safe HTTP verbs. Anything else (POST/PUT/PATCH/DELETE,
    /// or a verb we do not recognize) can change remote state and is denied.
    /// </summary>
    private static string? InspectHttpRequest(IReadOnlyDictionary<string, object?>? parameters)
    {
        var method = ReadString(parameters, "method");
        if (string.IsNullOrWhiteSpace(method))
        {
            // The tool defaults to GET when no method is supplied.
            return null;
        }

        return SafeHttpMethods.Contains(method.Trim())
            ? null
            : $"the HTTP method '{method.Trim().ToUpper(CultureInfo.InvariantCulture)}' can change remote state";
    }

    private static string? FindOutputPathParameter(IReadOnlyDictionary<string, object?>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
        {
            return null;
        }

        foreach (var name in OutputPathParameters)
        {
            var value = ReadString(parameters, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return name;
            }
        }

        return null;
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?>? parameters, string name)
    {
        if (parameters is null)
        {
            return null;
        }

        // Tool parameter bags are not consistently case-normalized across transports, so match
        // case-insensitively rather than trusting the dictionary's comparer.
        foreach (var pair in parameters)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value switch
                {
                    null => null,
                    string s => s,
                    _ => pair.Value.ToString(),
                };
            }
        }

        return null;
    }

    /// <summary>Every tool id the built-in read-only list covers (diagnostics and tests).</summary>
    public static IReadOnlyCollection<string> BuiltInReadOnlyToolIds => ReadOnlyTools.ToArray();

    /// <summary>Every tool id explicitly classified as mutating (diagnostics and tests).</summary>
    public static IReadOnlyCollection<string> KnownMutatingToolIds => KnownMutatingTools.Keys.ToArray();
}
