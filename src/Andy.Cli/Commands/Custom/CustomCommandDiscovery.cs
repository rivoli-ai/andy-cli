using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Andy.Cli.Commands.Custom;

/// <summary>The outcome of one discovery pass: the commands plus everything that went wrong.</summary>
public sealed class CustomCommandDiscoveryResult
{
    public CustomCommandDiscoveryResult(
        IReadOnlyList<CustomCommandDefinition> commands,
        IReadOnlyList<CustomCommandDiagnostic> diagnostics,
        IReadOnlyList<string> roots)
    {
        Commands = commands;
        Diagnostics = diagnostics;
        Roots = roots;
    }

    /// <summary>Commands ordered by name (ordinal, case-insensitive); the order is stable.</summary>
    public IReadOnlyList<CustomCommandDefinition> Commands { get; }

    public IReadOnlyList<CustomCommandDiagnostic> Diagnostics { get; }

    /// <summary>The directories that were scanned, user root first.</summary>
    public IReadOnlyList<string> Roots { get; }
}

/// <summary>
/// Scans <c>~/.andy/commands/**/*.md</c> and <c>&lt;workspace&gt;/.andy/commands/**/*.md</c> and
/// turns each Markdown file into a <see cref="CustomCommandDefinition"/>.
/// </summary>
/// <remarks>
/// Naming: the path relative to the root, minus the <c>.md</c> extension, lower-cased, with
/// directory separators replaced by <c>:</c>. So <c>git/commit.md</c> becomes <c>/git:commit</c>
/// (also accepted as <c>/git/commit</c> when typed).
///
/// Precedence: project beats user, deterministically. Two files in the SAME root that
/// normalize to the same name are resolved by ordinal path order, and the loser is reported.
///
/// Reserved names: a file that would shadow a built-in slash command or one of its aliases is
/// rejected outright, so a checked-in repository can never repoint <c>/permissions</c> or
/// <c>/exit</c> at a prompt template.
///
/// This class has no TUI or DI dependency; interactive, headless, and ACP callers all use it.
/// </remarks>
public static class CustomCommandDiscovery
{
    /// <summary>The directory name under the user home / workspace that holds command files.</summary>
    public const string CommandsDirectoryName = "commands";

    /// <summary>A single command-name segment: lowercase alphanumerics plus <c>. _ -</c>.</summary>
    private static readonly Regex SegmentPattern = new(@"^[a-z0-9][a-z0-9._-]*$", RegexOptions.Compiled);

    /// <summary>The user command root (<c>~/.andy/commands</c>).</summary>
    public static string UserRoot(string? homeDirectory = null)
        => Path.Combine(
            homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".andy",
            CommandsDirectoryName);

    /// <summary>The project command root (<c>&lt;workspace&gt;/.andy/commands</c>).</summary>
    public static string ProjectRoot(string workspaceDirectory)
        => Path.Combine(workspaceDirectory, ".andy", CommandsDirectoryName);

    /// <summary>Roots in scan order (user first, project second so project wins).</summary>
    public static IReadOnlyList<string> DefaultRoots(string workspaceDirectory, string? homeDirectory = null)
    {
        var user = UserRoot(homeDirectory);
        var project = ProjectRoot(workspaceDirectory);
        // A workspace that IS the home directory would otherwise scan the same root twice.
        return string.Equals(Path.GetFullPath(user), Path.GetFullPath(project), StringComparison.OrdinalIgnoreCase)
            ? new[] { project }
            : new[] { user, project };
    }

    /// <summary>
    /// Scan the default roots. Never throws: an unreadable root or a malformed file becomes a
    /// diagnostic so a bad template cannot prevent the CLI from starting.
    /// </summary>
    public static CustomCommandDiscoveryResult Discover(
        string workspaceDirectory,
        string? homeDirectory = null,
        CustomCommandLimits? limits = null,
        IReadOnlyCollection<string>? reservedNames = null)
    {
        limits ??= CustomCommandLimits.Default;
        reservedNames ??= SlashCommandCatalog.ReservedCommandNames;

        var diagnostics = new List<CustomCommandDiagnostic>();
        var roots = DefaultRoots(workspaceDirectory, homeDirectory);
        var byName = new Dictionary<string, CustomCommandDefinition>(StringComparer.OrdinalIgnoreCase);
        var shadowed = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        for (int r = 0; r < roots.Count; r++)
        {
            var root = roots[r];
            var source = string.Equals(Path.GetFullPath(root), Path.GetFullPath(ProjectRoot(workspaceDirectory)),
                StringComparison.OrdinalIgnoreCase)
                ? CustomCommandSource.Project
                : CustomCommandSource.User;

            foreach (var file in EnumerateCommandFiles(root, limits, diagnostics))
            {
                var name = ToCommandName(root, file, diagnostics);
                if (name is null)
                    continue;

                if (reservedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    diagnostics.Add(new CustomCommandDiagnostic(
                        CustomCommandDiagnosticSeverity.Error, file,
                        $"'/{name}' is a built-in command (or an alias of one) and cannot be redefined by a Markdown file."));
                    continue;
                }

                var definition = Load(file, name, source, limits, diagnostics);
                if (definition is null)
                    continue;

                if (byName.TryGetValue(name, out var existing))
                {
                    // Later root (project) wins; same root is decided by ordinal path order.
                    bool replace = existing.Source != source
                        ? source == CustomCommandSource.Project
                        : string.CompareOrdinal(file, existing.FilePath) < 0;

                    var winner = replace ? definition : existing;
                    var loser = replace ? existing : definition;

                    diagnostics.Add(new CustomCommandDiagnostic(
                        existing.Source != source
                            ? CustomCommandDiagnosticSeverity.Info
                            : CustomCommandDiagnosticSeverity.Warning,
                        loser.FilePath,
                        $"Duplicate command '/{name}': shadowed by {winner.FilePath} ({winner.SourceLabel})."));

                    if (!shadowed.TryGetValue(name, out var list))
                        shadowed[name] = list = new List<string>();
                    list.Add(loser.FilePath);
                    byName[name] = winner;
                }
                else
                {
                    byName[name] = definition;
                }
            }
        }

        var commands = byName.Values
            .Select(d => shadowed.TryGetValue(d.Name, out var paths)
                ? new CustomCommandDefinition(d.Name, d.Description, d.Template, d.FilePath, d.Source,
                    d.Provider, d.Model, d.Mode, paths.OrderBy(p => p, StringComparer.Ordinal).ToArray())
                : d)
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.FilePath, StringComparer.Ordinal)
            .ToArray();

        return new CustomCommandDiscoveryResult(commands, diagnostics, roots);
    }

    /// <summary>
    /// Enumerate the <c>.md</c> files under a root, depth- and count-limited, in a stable
    /// ordinal path order.
    /// </summary>
    private static IReadOnlyList<string> EnumerateCommandFiles(
        string root, CustomCommandLimits limits, List<CustomCommandDiagnostic> diagnostics)
    {
        var files = new List<string>();
        if (!Directory.Exists(root))
            return files;

        try
        {
            var pending = new Queue<(string Dir, int Depth)>();
            pending.Enqueue((root, 0));
            while (pending.Count > 0)
            {
                var (dir, depth) = pending.Dequeue();
                foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
                    files.Add(file);

                if (depth >= limits.MaxDirectoryDepth)
                {
                    var subdirs = Directory.EnumerateDirectories(dir).Take(1).ToArray();
                    if (subdirs.Length > 0)
                        diagnostics.Add(new CustomCommandDiagnostic(
                            CustomCommandDiagnosticSeverity.Warning, dir,
                            $"Not scanned deeper than {limits.MaxDirectoryDepth} directory levels."));
                    continue;
                }

                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    // Symlinked directories are skipped: a link could point anywhere on disk.
                    var info = new DirectoryInfo(sub);
                    if (info.LinkTarget is not null)
                    {
                        diagnostics.Add(new CustomCommandDiagnostic(
                            CustomCommandDiagnosticSeverity.Info, sub,
                            "Skipped: symlinked directories are not scanned for commands."));
                        continue;
                    }
                    pending.Enqueue((sub, depth + 1));
                }
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add(new CustomCommandDiagnostic(
                CustomCommandDiagnosticSeverity.Warning, root, $"Could not be scanned: {ex.Message}"));
        }

        files.Sort(StringComparer.Ordinal);
        if (files.Count > limits.MaxCommandFiles)
        {
            diagnostics.Add(new CustomCommandDiagnostic(
                CustomCommandDiagnosticSeverity.Warning, root,
                $"Only the first {limits.MaxCommandFiles} of {files.Count} command files were loaded."));
            files = files.Take(limits.MaxCommandFiles).ToList();
        }
        return files;
    }

    /// <summary>
    /// Turn a file path into a command name, or null (with a diagnostic) when the path cannot
    /// be a command name.
    /// </summary>
    internal static string? ToCommandName(string root, string file, List<CustomCommandDiagnostic> diagnostics)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(root, file);
        }
        catch
        {
            return null;
        }

        relative = relative.Substring(0, relative.Length - ".md".Length);
        var segments = relative
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().ToLowerInvariant())
            .ToArray();

        if (segments.Length == 0)
        {
            diagnostics.Add(new CustomCommandDiagnostic(
                CustomCommandDiagnosticSeverity.Error, file, "Empty command name."));
            return null;
        }

        foreach (var segment in segments)
        {
            if (!SegmentPattern.IsMatch(segment))
            {
                diagnostics.Add(new CustomCommandDiagnostic(
                    CustomCommandDiagnosticSeverity.Error, file,
                    $"'{segment}' is not a usable command name segment. Use lowercase letters, digits, '.', '_', or '-', " +
                    "starting with a letter or digit. Spaces are not allowed in command file names."));
                return null;
            }
        }

        return string.Join(":", segments);
    }

    /// <summary>
    /// Read and parse one command file. Returns null when the file must be rejected (too
    /// large, unreadable, or empty).
    /// </summary>
    private static CustomCommandDefinition? Load(
        string file,
        string name,
        CustomCommandSource source,
        CustomCommandLimits limits,
        List<CustomCommandDiagnostic> diagnostics)
    {
        try
        {
            // Size gate BEFORE the read: an oversized template never reaches prompt construction.
            var info = new FileInfo(file);
            if (info.Length > limits.MaxTemplateBytes)
            {
                diagnostics.Add(new CustomCommandDiagnostic(
                    CustomCommandDiagnosticSeverity.Error, file,
                    $"Rejected: the template is {info.Length} bytes, over the {limits.MaxTemplateBytes}-byte limit."));
                return null;
            }

            var content = File.ReadAllText(file);
            var parsed = CustomCommandFrontmatter.Parse(content, file, diagnostics);
            var body = parsed.Body.Trim('\n');

            if (body.Trim().Length == 0)
            {
                diagnostics.Add(new CustomCommandDiagnostic(
                    CustomCommandDiagnosticSeverity.Error, file,
                    "Rejected: the command has an empty prompt template."));
                return null;
            }

            var description = parsed.Description ?? DeriveDescription(body);
            return new CustomCommandDefinition(name, description, body, file, source,
                parsed.Provider, parsed.Model, parsed.Mode);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new CustomCommandDiagnostic(
                CustomCommandDiagnosticSeverity.Error, file, $"Could not be read: {ex.Message}"));
            return null;
        }
    }

    /// <summary>First meaningful body line, trimmed of Markdown heading markers, as a fallback description.</summary>
    private static string DeriveDescription(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var text = line.Trim().TrimStart('#', '>', '*', '-', ' ').Trim();
            if (text.Length == 0)
                continue;
            return text.Length > 80 ? text.Substring(0, 77) + "..." : text;
        }
        return "Custom command";
    }
}
