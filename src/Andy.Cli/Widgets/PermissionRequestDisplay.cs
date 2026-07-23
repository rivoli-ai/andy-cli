using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Permissions.Model;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets
{
    /// <summary>
    /// Coarse risk level of a command line shown in a permission prompt. Drives display color only;
    /// it never influences the permission decision itself.
    /// </summary>
    public enum CommandRiskLevel
    {
        /// <summary>No known destructive pattern; shown in the standard command color.</summary>
        Normal,

        /// <summary>Matches a known destructive/privileged pattern; shown in the danger color.</summary>
        Dangerous,
    }

    /// <summary>
    /// Heuristic classifier that flags command lines with well-known destructive or privileged
    /// patterns (rm, dd, sudo, git reset --hard, force pushes, ...) so permission prompts can color
    /// them distinctly. Display-only: a Normal result is NOT a safety guarantee, and Dangerous does
    /// not block anything - the user still decides. Compound commands (a && b; c | d) are classified
    /// per segment; any dangerous segment marks the whole line Dangerous.
    /// </summary>
    public static class CommandRiskClassifier
    {
        /// <summary>
        /// Executables that are destructive or privilege-escalating regardless of arguments.
        /// Matched by path-free basename, case-insensitively, so <c>/bin/rm</c> and <c>RM</c> match.
        /// </summary>
        private static readonly HashSet<string> DangerousExecutables = new(StringComparer.OrdinalIgnoreCase)
        {
            // File/disk destruction
            "rm", "rmdir", "unlink", "shred", "srm", "dd", "fdisk", "parted", "diskutil",
            "truncate", "del", "erase", "deltree", "rd", "format",
            // Privilege escalation
            "sudo", "su", "doas",
            // Ownership/mode changes
            "chmod", "chown", "chgrp",
            // Process/system control
            "kill", "killall", "pkill", "shutdown", "reboot", "halt", "poweroff",
        };

        /// <summary>
        /// (executable, subcommand) pairs that are destructive even though the executable also has
        /// harmless subcommands (e.g. <c>git status</c> is fine but <c>git reset</c> is not).
        /// </summary>
        private static readonly IReadOnlyDictionary<string, HashSet<string>> DangerousSubcommands =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["git"] = new(StringComparer.OrdinalIgnoreCase) { "reset", "clean" },
                ["docker"] = new(StringComparer.OrdinalIgnoreCase) { "rm", "rmi", "prune", "system" },
                ["kubectl"] = new(StringComparer.OrdinalIgnoreCase) { "delete", "drain" },
                ["terraform"] = new(StringComparer.OrdinalIgnoreCase) { "destroy" },
                ["helm"] = new(StringComparer.OrdinalIgnoreCase) { "uninstall", "delete" },
            };

        /// <summary>
        /// Flags that make an otherwise routine <c>git push</c> destructive (history rewrite or
        /// remote deletion).
        /// </summary>
        private static readonly HashSet<string> ForcePushFlags = new(StringComparer.Ordinal)
        {
            "--force", "-f", "--force-with-lease", "--delete", "-d", "--mirror", "--prune",
        };

        /// <summary>Classifies a full command line; any dangerous segment makes the whole line Dangerous.</summary>
        public static CommandRiskLevel Classify(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return CommandRiskLevel.Normal;
            }

            foreach (var segment in SplitSegments(command))
            {
                if (ClassifySegment(segment) == CommandRiskLevel.Dangerous)
                {
                    return CommandRiskLevel.Dangerous;
                }
            }

            return CommandRiskLevel.Normal;
        }

        /// <summary>Splits a shell command line on &amp;&amp;, ||, ;, | and newlines into simple segments.</summary>
        private static IEnumerable<string> SplitSegments(string command)
        {
            return command
                .Replace("\r\n", "\n").Replace('\r', '\n')
                .Split(new[] { "&&", "||", ";", "|", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static CommandRiskLevel ClassifySegment(string segment)
        {
            var tokens = segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int i = 0;

            // Skip leading environment assignments (FOO=bar) preceding the executable.
            while (i < tokens.Length && LooksLikeAssignment(tokens[i]))
            {
                i++;
            }

            if (i >= tokens.Length)
            {
                return CommandRiskLevel.Normal;
            }

            var exe = Basename(tokens[i]);
            i++;

            if (DangerousExecutables.Contains(exe))
            {
                return CommandRiskLevel.Dangerous;
            }

            // Executables with destructive subcommands: check the first non-flag token after the exe.
            string? subcommand = tokens.Skip(i).FirstOrDefault(t => t.Length > 0 && t[0] != '-');
            if (subcommand is not null &&
                DangerousSubcommands.TryGetValue(exe, out var bad) && bad.Contains(subcommand))
            {
                return CommandRiskLevel.Dangerous;
            }

            // git push is routine unless it rewrites or deletes remote history.
            if (exe.Equals("git", StringComparison.OrdinalIgnoreCase) &&
                subcommand is not null && subcommand.Equals("push", StringComparison.OrdinalIgnoreCase) &&
                tokens.Skip(i).Any(t => ForcePushFlags.Contains(t)))
            {
                return CommandRiskLevel.Dangerous;
            }

            return CommandRiskLevel.Normal;
        }

        private static bool LooksLikeAssignment(string token)
        {
            int eq = token.IndexOf('=');
            if (eq <= 0)
            {
                return false;
            }

            for (int i = 0; i < eq; i++)
            {
                char c = token[i];
                bool ok = i == 0 ? (char.IsAsciiLetter(c) || c == '_') : (char.IsAsciiLetterOrDigit(c) || c == '_');
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Path-free executable name, so <c>/bin/rm</c> and <c>.\rm.exe</c> classify like <c>rm</c>.</summary>
        private static string Basename(string token)
        {
            int cut = Math.Max(token.LastIndexOf('/'), token.LastIndexOf('\\'));
            var name = cut >= 0 ? token[(cut + 1)..] : token;
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        }
    }

    /// <summary>Display role of one line in the permission dialog body; selects its color.</summary>
    public enum PermissionLineKind
    {
        /// <summary>Plain action summary text.</summary>
        Summary,

        /// <summary>A command line the user is being asked to approve.</summary>
        Command,

        /// <summary>A command line that matches a known destructive/privileged pattern.</summary>
        DangerousCommand,
    }

    /// <summary>One wrapped display line of the permission dialog body.</summary>
    public sealed record PermissionDialogLine(string Text, PermissionLineKind Kind);

    /// <summary>
    /// Builds the body of the tool-permission dialog: the wrapped action summary plus one
    /// color-coded "$ command" line per command segment that forced the Ask, so the user can see at
    /// a glance exactly what they are approving and whether it looks destructive (issue #237).
    /// </summary>
    public static class PermissionDialogContent
    {
        /// <summary>Extracts the distinct command values that forced the Ask, in evaluation order.</summary>
        public static IReadOnlyList<string> AskedCommands(PermissionRequest request)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var commands = new List<string>();
            foreach (var resource in request.Evaluation.Resources)
            {
                if (resource.Outcome == PermissionOutcome.Ask &&
                    resource.Access.Kind == ResourceKind.Command &&
                    !string.IsNullOrWhiteSpace(resource.Access.Value) &&
                    seen.Add(resource.Access.Value))
                {
                    commands.Add(resource.Access.Value);
                }
            }

            return commands;
        }

        /// <summary>
        /// Wrapped, color-tagged dialog body: summary lines, then a blank line and each asked
        /// command as a "$ ..." line whose kind reflects its <see cref="CommandRiskClassifier"/>
        /// risk. When the summary is nothing more than the single asked command, the summary is
        /// dropped so the command is not shown twice.
        /// </summary>
        public static IReadOnlyList<PermissionDialogLine> BuildBodyLines(PermissionRequest request, int width)
        {
            var lines = new List<PermissionDialogLine>();
            var commands = AskedCommands(request);

            var summary = request.ActionSummary ?? string.Empty;
            bool summaryIsJustTheCommand = commands.Count == 1 &&
                string.Equals(summary.Trim(), commands[0].Trim(), StringComparison.Ordinal);

            if (!summaryIsJustTheCommand && !string.IsNullOrWhiteSpace(summary))
            {
                foreach (var line in TextWrap.Wrap(summary, width))
                {
                    lines.Add(new PermissionDialogLine(line, PermissionLineKind.Summary));
                }
            }

            if (commands.Count > 0)
            {
                if (lines.Count > 0)
                {
                    lines.Add(new PermissionDialogLine(string.Empty, PermissionLineKind.Summary));
                }

                foreach (var command in commands)
                {
                    var kind = CommandRiskClassifier.Classify(command) == CommandRiskLevel.Dangerous
                        ? PermissionLineKind.DangerousCommand
                        : PermissionLineKind.Command;
                    foreach (var line in TextWrap.Wrap("$ " + command, width))
                    {
                        lines.Add(new PermissionDialogLine(line, kind));
                    }
                }
            }

            if (lines.Count == 0)
            {
                // Never render an empty body; fall back to the raw summary (even if blank-ish).
                foreach (var line in TextWrap.Wrap(summary, width))
                {
                    lines.Add(new PermissionDialogLine(line, PermissionLineKind.Summary));
                }
            }

            return lines;
        }

        /// <summary>Foreground color for a body line, following the theme's Info/Error conventions.</summary>
        public static DL.Rgb24 ColorFor(PermissionLineKind kind)
        {
            var theme = Themes.Theme.Current;
            return kind switch
            {
                PermissionLineKind.Command => theme.Info,
                PermissionLineKind.DangerousCommand => theme.Error,
                _ => new DL.Rgb24(200, 200, 210),
            };
        }
    }
}
