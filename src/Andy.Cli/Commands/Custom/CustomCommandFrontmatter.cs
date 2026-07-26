using System;
using System.Collections.Generic;
using System.Linq;

namespace Andy.Cli.Commands.Custom;

/// <summary>
/// Parsed YAML frontmatter of a Markdown command file.
/// </summary>
/// <remarks>
/// This is a deliberately tiny YAML subset (<c>key: value</c> scalars, optional quotes,
/// full-line <c>#</c> comments) rather than a real YAML parser: the CLI has no YAML package
/// reference, the supported schema is four scalar fields, and anything richer would only
/// widen what a checked-in template can express. Anything that is not a scalar assignment is
/// reported as a diagnostic and ignored instead of failing the load.
/// </remarks>
public sealed class CustomCommandFrontmatter
{
    /// <summary>The fields a command file may declare.</summary>
    public static readonly string[] KnownFields = { "description", "provider", "model", "mode" };

    /// <summary>
    /// Fields that other tools support but Andy deliberately refuses: they would let a
    /// checked-in Markdown file grant permissions, enable tools, pick a different agent, or
    /// run a shell. Issue #281 forbids all of that in the MVP, so they are reported as errors
    /// rather than silently ignored (a silent ignore reads as "it worked").
    /// </summary>
    public static readonly string[] RejectedFields =
    {
        "agent", "subagent", "allowed-tools", "allowed_tools", "allowedtools", "tools",
        "permission", "permissions", "shell", "bash", "command", "exec", "run",
        "disable-model-invocation", "template",
    };

    public string? Description { get; private set; }
    public string? Provider { get; private set; }
    public string? Model { get; private set; }
    public string? Mode { get; private set; }

    /// <summary>The Markdown body that follows the frontmatter block (or the whole file when absent).</summary>
    public string Body { get; private set; } = "";

    /// <summary>True when the file actually opened with a <c>---</c> frontmatter block.</summary>
    public bool HasFrontmatter { get; private set; }

    /// <summary>
    /// Split a command file into frontmatter and body. Never throws: malformed input produces
    /// diagnostics and the best-effort result.
    /// </summary>
    public static CustomCommandFrontmatter Parse(string content, string path, List<CustomCommandDiagnostic> diagnostics)
    {
        var result = new CustomCommandFrontmatter();
        content ??= "";

        // Tolerate a UTF-8 BOM and CRLF line endings.
        if (content.Length > 0 && content[0] == '\uFEFF')
            content = content.Substring(1);
        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');

        int start = 0;
        while (start < lines.Length && lines[start].Trim().Length == 0)
            start++;

        if (start >= lines.Length || lines[start].TrimEnd() != "---")
        {
            // No frontmatter: the whole file is the template. This is legal (an unnamed
            // command still gets a derived description), so it is not a diagnostic.
            result.Body = normalized;
            return result;
        }

        int end = -1;
        for (int i = start + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimEnd();
            if (trimmed == "---" || trimmed == "...")
            {
                end = i;
                break;
            }
        }

        if (end < 0)
        {
            diagnostics.Add(new CustomCommandDiagnostic(
                CustomCommandDiagnosticSeverity.Warning, path,
                "Frontmatter opened with '---' but never closed; the whole file is treated as the template body."));
            result.Body = normalized;
            return result;
        }

        result.HasFrontmatter = true;
        result.Body = string.Join("\n", lines.Skip(end + 1));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = start + 1; i < end; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
                continue;
            if (line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                continue;

            // Continuation/list/block lines are not part of the supported subset.
            if (char.IsWhiteSpace(line[0]) || line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            {
                diagnostics.Add(new CustomCommandDiagnostic(
                    CustomCommandDiagnosticSeverity.Warning, path,
                    $"Ignored frontmatter line {i + 1}: only simple 'key: value' entries are supported."));
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                diagnostics.Add(new CustomCommandDiagnostic(
                    CustomCommandDiagnosticSeverity.Warning, path,
                    $"Ignored frontmatter line {i + 1}: expected 'key: value'."));
                continue;
            }

            var key = line.Substring(0, colon).Trim();
            var rawValue = line.Substring(colon + 1).Trim();

            if (!seen.Add(key))
            {
                diagnostics.Add(new CustomCommandDiagnostic(
                    CustomCommandDiagnosticSeverity.Warning, path,
                    $"Duplicate frontmatter field '{key}'; the first value is used."));
                continue;
            }

            if (RejectedFields.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                diagnostics.Add(new CustomCommandDiagnostic(
                    CustomCommandDiagnosticSeverity.Error, path,
                    $"Frontmatter field '{key}' is not supported: a Markdown command cannot grant permissions, " +
                    "enable tools, choose an agent, or run a shell. The field is ignored."));
                continue;
            }

            if (!KnownFields.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                diagnostics.Add(new CustomCommandDiagnostic(
                    CustomCommandDiagnosticSeverity.Warning, path,
                    $"Unknown frontmatter field '{key}' ignored. Supported fields: {string.Join(", ", KnownFields)}."));
                continue;
            }

            var value = Unquote(rawValue);
            if (value.Length == 0)
            {
                diagnostics.Add(new CustomCommandDiagnostic(
                    CustomCommandDiagnosticSeverity.Warning, path,
                    $"Frontmatter field '{key}' has an empty value and is ignored."));
                continue;
            }

            switch (key.ToLowerInvariant())
            {
                case "description": result.Description = value; break;
                case "provider": result.Provider = value; break;
                case "model": result.Model = value; break;
                case "mode": result.Mode = value; break;
            }
        }

        return result;
    }

    /// <summary>Strip matching surrounding quotes and a trailing inline comment from a scalar.</summary>
    private static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            char first = value[0];
            char last = value[value.Length - 1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
                return value.Substring(1, value.Length - 2);
        }

        // Unquoted scalars may carry a trailing " # comment".
        int hash = value.IndexOf(" #", StringComparison.Ordinal);
        if (hash >= 0)
            value = value.Substring(0, hash);
        return value.Trim();
    }
}
