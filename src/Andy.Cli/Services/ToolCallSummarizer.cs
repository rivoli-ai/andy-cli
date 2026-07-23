using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Andy.Cli.Services;

/// <summary>
/// Builds a concise, human-readable one-line description of a tool call
/// ("Reading src/Program.cs", "Running: dotnet build", ...) used as the collapsed
/// header of tool call feed items (issue #223). Raw tool names and arguments stay
/// available in the expanded view; this class only produces the friendly line.
///
/// Every tool registered in <see cref="ToolCatalog"/> gets a specific present-progressive
/// phrase; unknown tools fall back to the snake_case name turned into words plus the
/// most salient argument - never a raw JSON dump.
/// </summary>
public static class ToolCallSummarizer
{
    /// <summary>Maximum characters of a shell command shown inline.</summary>
    public const int MaxCommandLength = 60;

    /// <summary>Maximum characters of a query/pattern/url argument shown inline.</summary>
    public const int MaxArgumentLength = 48;

    /// <summary>Maximum characters of a displayed path before it is squeezed to ".../parent/name".</summary>
    public const int MaxPathLength = 50;

    private static readonly IReadOnlyDictionary<string, object?> Empty =
        new Dictionary<string, object?>();

    /// <summary>
    /// Produce the human-readable summary for a tool call. <paramref name="toolName"/> may be
    /// a bare tool id ("read_file") or an execution id with a numeric suffix ("read_file_1").
    /// </summary>
    public static string Summarize(string? toolName, IReadOnlyDictionary<string, object?>? parameters)
    {
        var name = NormalizeToolName(toolName);
        var p = parameters ?? Empty;

        if (name.StartsWith("dataframe_", StringComparison.Ordinal))
            return SummarizeDataFrame(name.Substring("dataframe_".Length), p);
        if (name.StartsWith("pdf_", StringComparison.Ordinal))
            return SummarizePdf(name, p);

        switch (name)
        {
            // File system
            case "read_file":
                return WithTarget("Reading", PathArg(p, "file_path", "path", "filepath", "file", "filename"), "file");
            case "write_file":
                return WithTarget("Writing", PathArg(p, "file_path", "path", "filepath", "file", "filename"), "file");
            case "delete_file":
                return WithTarget("Deleting", PathArg(p, "file_path", "path", "filepath", "file", "filename"), "file");
            case "copy_file":
                return TwoPathSummary("Copying", p);
            case "move_file":
                return TwoPathSummary("Moving", p);
            case "list_directory":
                return WithTarget("Listing", PathArg(p, "path", "directory_path", "directory", "dir", "folder"), "current directory");
            case "create_directory":
                return WithTarget("Creating directory", PathArg(p, "path", "directory", "dir", "folder", "name"), "");

            // Git
            case "git_diff":
                {
                    var target = PathArg(p, "path", "file_path", "repository_path", "repo_path");
                    return string.IsNullOrEmpty(target) ? "Getting git diff" : $"Getting git diff for {target}";
                }

            // System
            case "execute_command":
            case "bash_command":
                {
                    var cmd = Str(p, "command", "cmd", "command_line", "script");
                    return string.IsNullOrEmpty(cmd) ? "Running a command" : $"Running: {Truncate(cmd, MaxCommandLength)}";
                }
            case "process_info":
                return "Inspecting running processes";
            case "system_info":
                return "Getting system information";

            // Text processing
            case "format_text":
                {
                    var op = Str(p, "operation", "action", "format");
                    return string.IsNullOrEmpty(op) ? "Formatting text" : $"Formatting text ({op})";
                }
            case "replace_text":
                {
                    var pattern = Str(p, "search_pattern", "old_string", "pattern", "find", "search");
                    var target = PathArg(p, "target_path", "file_path", "path", "file");
                    var s = string.IsNullOrEmpty(pattern) ? "Replacing text" : $"Replacing {Quote(pattern)}";
                    return string.IsNullOrEmpty(target) ? s : $"{s} in {target}";
                }
            case "search_text":
                {
                    var pattern = Str(p, "search_pattern", "pattern", "query", "search", "text");
                    var target = PathArg(p, "search_path", "path", "directory", "target_path", "file_path");
                    var s = string.IsNullOrEmpty(pattern) ? "Searching text" : $"Searching for {Quote(pattern)}";
                    return string.IsNullOrEmpty(target) ? s : $"{s} in {target}";
                }

            // Utilities
            case "datetime_tool":
            case "date_time":
                {
                    var op = Str(p, "operation", "action");
                    return string.IsNullOrEmpty(op) ? "Getting date/time" : $"Getting date/time ({op})";
                }
            case "encoding_tool":
                {
                    var op = Str(p, "operation", "action");
                    if (string.IsNullOrEmpty(op)) return "Encoding/decoding text";
                    if (op.Contains("decode", StringComparison.OrdinalIgnoreCase)) return $"Decoding text ({op})";
                    if (op.Contains("encode", StringComparison.OrdinalIgnoreCase)) return $"Encoding text ({op})";
                    if (op.Contains("hash", StringComparison.OrdinalIgnoreCase) ||
                        op.Contains("md5", StringComparison.OrdinalIgnoreCase) ||
                        op.Contains("sha", StringComparison.OrdinalIgnoreCase) ||
                        op.Contains("bcrypt", StringComparison.OrdinalIgnoreCase))
                        return $"Computing hash ({op})";
                    if (op.Contains("generate", StringComparison.OrdinalIgnoreCase)) return $"Generating value ({op})";
                    return $"Transforming text ({op})";
                }

            // Web
            case "http_request":
                {
                    var url = Str(p, "url", "uri", "endpoint");
                    var method = Str(p, "method", "http_method")?.ToUpperInvariant();
                    if (string.IsNullOrEmpty(url)) return "Sending HTTP request";
                    var target = Truncate(url, MaxArgumentLength);
                    return string.IsNullOrEmpty(method) || method == "GET"
                        ? $"Fetching {target}"
                        : $"Sending {method} request to {target}";
                }
            case "json_processor":
                {
                    var op = Str(p, "operation", "action");
                    return string.IsNullOrEmpty(op) ? "Processing JSON" : $"Processing JSON ({op})";
                }

            // Todos
            case "todo_management":
                {
                    var action = Str(p, "action", "operation");
                    if (string.IsNullOrEmpty(action)) return "Updating todo list";
                    return action.Contains("list", StringComparison.OrdinalIgnoreCase) ||
                           action.Contains("get", StringComparison.OrdinalIgnoreCase)
                        ? "Reading todo list"
                        : "Updating todo list";
                }

            // Code index (CLI tool)
            case "code_index":
                {
                    var query = Str(p, "query", "pattern", "symbol", "name");
                    if (!string.IsNullOrEmpty(query)) return $"Searching code for {Quote(query)}";
                    var queryType = Str(p, "query_type", "operation");
                    if (!string.IsNullOrEmpty(queryType))
                    {
                        if (queryType.Contains("structure", StringComparison.OrdinalIgnoreCase))
                            return "Getting project structure";
                        if (queryType.Contains("hierarchy", StringComparison.OrdinalIgnoreCase))
                            return "Getting type hierarchy";
                        return "Searching code index";
                    }
                    var path = PathArg(p, "path", "directory", "dir");
                    return string.IsNullOrEmpty(path) ? "Searching code index" : $"Indexing {path}";
                }
        }

        return FallbackSummary(name, p);
    }

    private static string SummarizeDataFrame(string op, IReadOnlyDictionary<string, object?> p)
    {
        var ds = Str(p, "dataset_id", "dataset", "id", "name") ?? "";
        string Of(string verb) => string.IsNullOrEmpty(ds) ? verb : $"{verb} {ds}";

        switch (op)
        {
            case "load_csv":
            case "load_json":
            case "load_parquet":
            case "load_delta":
                {
                    var fmt = op.Substring("load_".Length) switch
                    {
                        "csv" => "CSV",
                        "json" => "JSON",
                        "parquet" => "Parquet",
                        _ => "Delta",
                    };
                    var path = PathArg(p, "path", "file_path", "source", "url");
                    var s = string.IsNullOrEmpty(path) ? $"Loading {fmt} data" : $"Loading {fmt} {path}";
                    return string.IsNullOrEmpty(ds) ? s : $"{s} as {ds}";
                }
            case "schema": return string.IsNullOrEmpty(ds) ? "Inspecting dataset schema" : $"Inspecting schema of {ds}";
            case "profile": return Of("Profiling") + (string.IsNullOrEmpty(ds) ? " dataset" : "");
            case "preview": return Of("Previewing") + (string.IsNullOrEmpty(ds) ? " dataset" : "");
            case "value_counts": return string.IsNullOrEmpty(ds) ? "Counting values" : $"Counting values in {ds}";
            case "assert": return string.IsNullOrEmpty(ds) ? "Checking data assertions" : $"Checking assertions on {ds}";
            case "list": return "Listing datasets";
            case "select": return string.IsNullOrEmpty(ds) ? "Selecting columns" : $"Selecting columns from {ds}";
            case "filter": return Of("Filtering") + (string.IsNullOrEmpty(ds) ? " dataset" : "");
            case "with_column": return string.IsNullOrEmpty(ds) ? "Adding a column" : $"Adding column to {ds}";
            case "rename": return string.IsNullOrEmpty(ds) ? "Renaming columns" : $"Renaming columns in {ds}";
            case "group_by": return Of("Grouping") + (string.IsNullOrEmpty(ds) ? " dataset" : "");
            case "window": return string.IsNullOrEmpty(ds) ? "Computing window functions" : $"Computing window functions on {ds}";
            case "pivot": return Of("Pivoting") + (string.IsNullOrEmpty(ds) ? " dataset" : "");
            case "unpivot": return Of("Unpivoting") + (string.IsNullOrEmpty(ds) ? " dataset" : "");
            case "unnest": return Of("Unnesting") + (string.IsNullOrEmpty(ds) ? " dataset" : "");
            case "join":
                {
                    var left = Str(p, "left_dataset_id", "left", "dataset_id") ?? "datasets";
                    var right = Str(p, "right_dataset_id", "right");
                    return string.IsNullOrEmpty(right) ? $"Joining {left}" : $"Joining {left} with {right}";
                }
            case "sample": return Of("Sampling") + (string.IsNullOrEmpty(ds) ? " dataset" : "");
            case "sort": return Of("Sorting") + (string.IsNullOrEmpty(ds) ? " dataset" : "");
            case "distinct": return string.IsNullOrEmpty(ds) ? "Removing duplicate rows" : $"Removing duplicates from {ds}";
            case "union": return "Combining datasets";
            case "fillna": return string.IsNullOrEmpty(ds) ? "Filling missing values" : $"Filling missing values in {ds}";
            case "dropna": return string.IsNullOrEmpty(ds) ? "Dropping missing values" : $"Dropping missing values from {ds}";
            case "export":
                {
                    var path = PathArg(p, "path", "output_path", "file_path", "destination");
                    var s = string.IsNullOrEmpty(ds) ? "Exporting dataset" : $"Exporting {ds}";
                    return string.IsNullOrEmpty(path) ? s : $"{s} to {path}";
                }
            case "drop": return string.IsNullOrEmpty(ds) ? "Dropping dataset" : $"Dropping dataset {ds}";
        }
        return FallbackSummary("dataframe_" + op, p);
    }

    private static string SummarizePdf(string name, IReadOnlyDictionary<string, object?> p)
    {
        var path = PathArg(p, "path", "file_path", "file");
        var target = string.IsNullOrEmpty(path) ? "PDF" : path;
        switch (name)
        {
            case "pdf_extract_text": return $"Extracting text from {target}";
            case "pdf_extract_tables": return $"Extracting tables from {target}";
            case "pdf_info": return $"Reading PDF info for {target}";
            case "pdf_outline": return $"Reading outline of {target}";
            case "pdf_reflow": return $"Extracting reading-order text from {target}";
            case "pdf_search":
                {
                    var query = Str(p, "query", "pattern", "text");
                    return string.IsNullOrEmpty(query)
                        ? $"Searching {target}"
                        : $"Searching {target} for {Quote(query)}";
                }
        }
        return FallbackSummary(name, p);
    }

    /// <summary>
    /// Unknown tool: snake_case -> words ("my_custom_tool" -> "My custom tool") plus the most
    /// salient scalar argument if one exists. Never dumps the full argument set.
    /// </summary>
    private static string FallbackSummary(string name, IReadOnlyDictionary<string, object?> p)
    {
        var words = Humanize(name);
        var salient = Str(p,
            "file_path", "path", "target_path", "source_path", "directory_path", "directory",
            "command", "query", "search_pattern", "pattern", "url", "dataset_id",
            "name", "operation", "action", "text", "input");
        if (string.IsNullOrEmpty(salient))
        {
            // No well-known key: take the first short scalar value so the line still
            // hints at the target (skip structured/long values).
            foreach (var kv in p)
            {
                if (kv.Key.StartsWith("__", StringComparison.Ordinal)) continue;
                var v = kv.Value?.ToString();
                if (string.IsNullOrWhiteSpace(v)) continue;
                var trimmed = v.TrimStart();
                if (trimmed.StartsWith("{", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[", StringComparison.Ordinal)) continue;
                salient = v;
                break;
            }
        }
        return string.IsNullOrEmpty(salient) ? words : $"{words}: {Truncate(salient, MaxArgumentLength)}";
    }

    /// <summary>
    /// Lowercase, trim, and strip a trailing execution-counter suffix ("read_file_1" -> "read_file").
    /// </summary>
    public static string NormalizeToolName(string? toolName)
    {
        var name = (toolName ?? "").Trim().ToLowerInvariant();
        int i = name.Length;
        while (i > 0 && char.IsDigit(name[i - 1])) i--;
        if (i > 0 && i < name.Length && name[i - 1] == '_')
            name = name.Substring(0, i - 1);
        return name;
    }

    /// <summary>
    /// Shorten a path for display: relative to the current working directory when inside it,
    /// "~"-relative when under the home directory, and squeezed to ".../parent/name" when
    /// it is still longer than <see cref="MaxPathLength"/>.
    /// </summary>
    public static string ShortenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var s = path.Trim();
        try
        {
            var sep = Path.DirectorySeparatorChar;
            var cwd = Directory.GetCurrentDirectory().TrimEnd(sep);
            if (!string.IsNullOrEmpty(cwd))
            {
                if (string.Equals(s.TrimEnd(sep), cwd, StringComparison.Ordinal)) return ".";
                if (s.StartsWith(cwd + sep, StringComparison.Ordinal))
                    s = s.Substring(cwd.Length + 1);
            }
            if (s.Length > MaxPathLength)
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd(sep);
                if (!string.IsNullOrEmpty(home) && s.StartsWith(home + sep, StringComparison.Ordinal))
                    s = "~" + s.Substring(home.Length);
            }
        }
        catch
        {
            // Path inspection is best-effort; fall through with the raw string.
        }

        if (s.Length > MaxPathLength)
        {
            var parts = s.Split('/', '\\').Where(x => x.Length > 0).ToArray();
            if (parts.Length > 2)
                s = $".../{parts[parts.Length - 2]}/{parts[parts.Length - 1]}";
        }
        return s;
    }

    /// <summary>Truncate to <paramref name="max"/> chars (with "...") after collapsing newlines.</summary>
    public static string Truncate(string? value, int max)
    {
        var s = (value ?? "").Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (s.Length <= max) return s;
        return s.Substring(0, Math.Max(0, max - 3)) + "...";
    }

    private static string Quote(string value) => "\"" + Truncate(value, MaxArgumentLength) + "\"";

    private static string WithTarget(string verb, string target, string fallbackTarget)
    {
        if (!string.IsNullOrEmpty(target)) return $"{verb} {target}";
        return string.IsNullOrEmpty(fallbackTarget) ? verb : $"{verb} {fallbackTarget}";
    }

    private static string TwoPathSummary(string verb, IReadOnlyDictionary<string, object?> p)
    {
        var src = PathArg(p, "source_path", "source", "src", "from", "file_path", "path");
        var dst = PathArg(p, "destination_path", "destination", "dest", "to", "target");
        if (string.IsNullOrEmpty(src)) return $"{verb} file";
        return string.IsNullOrEmpty(dst) ? $"{verb} {src}" : $"{verb} {src} to {dst}";
    }

    private static string PathArg(IReadOnlyDictionary<string, object?> p, params string[] keys)
        => ShortenPath(Str(p, keys));

    private static string? Str(IReadOnlyDictionary<string, object?> p, params string[] keys)
    {
        foreach (var key in keys)
        {
            foreach (var kv in p)
            {
                if (!string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                var s = kv.Value?.ToString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
        }
        return null;
    }

    private static string Humanize(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Tool call";
        var words = name.Split(new[] { '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "Tool call";
        var joined = string.Join(" ", words);
        return char.ToUpperInvariant(joined[0]) + joined.Substring(1);
    }
}
