using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services.Sessions;

namespace Andy.Cli.Commands;

/// <summary>
/// Session archive management (issue #285): export, import, fork, rename, and usage stats.
/// Backs both the interactive /session slash command and the one-shot
/// "andy-cli session ..." CLI command, so the two surfaces cannot drift.
///
/// Deliberately does NOT implement catalog/delete/resume/replay: those belong to the ACP
/// session work in issue #206. Listing here is a thin convenience that reuses
/// <see cref="SessionsCommand"/>'s renderer.
/// </summary>
public class SessionCommand : ICommand
{
    public const string UsageText =
        "Usage:\n" +
        "  session list\n" +
        "  session export [<id>] [--out <path>] [--markdown] [--tools] [--metadata]\n" +
        "  session import <archive-path> [--dry-run] [--title <title>]\n" +
        "  session fork [<id>] [--at <turn>] [--title <title>]\n" +
        "  session rename [<id>] <title>\n" +
        "  session stats [<id>] [--all]\n" +
        "\n" +
        "Notes:\n" +
        "  <id> defaults to the current session when running interactively.\n" +
        "  --at <turn> forks the history strictly BEFORE that 1-based user turn.\n" +
        "  --markdown writes a readable transcript instead of a portable archive;\n" +
        "  --tools adds tool calls/results and --metadata adds the model/usage header.";

    private readonly SessionStore _store;
    private readonly Func<string?>? _currentSessionId;

    public SessionCommand(SessionStore store, Func<string?>? currentSessionId = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _currentSessionId = currentSessionId;
    }

    public string Name => "session";

    public string Description => "Export, import, fork, rename, and measure saved sessions";

    public string[] Aliases => Array.Empty<string>();

    public Task<CommandResult> ExecuteAsync(string[] args, CancellationToken cancellationToken = default)
    {
        args ??= Array.Empty<string>();
        if (args.Length == 0)
        {
            return Task.FromResult(CommandResult.CreateSuccess(UsageText));
        }

        try
        {
            var subcommand = args[0].ToLowerInvariant();
            var rest = args.Skip(1).ToArray();
            return Task.FromResult(subcommand switch
            {
                "list" or "ls" => List(),
                "export" => Export(rest),
                "import" => Import(rest),
                "fork" => Fork(rest),
                "rename" or "title" => Rename(rest),
                "stats" or "usage" => Stats(rest),
                "help" or "--help" or "-h" => CommandResult.CreateSuccess(UsageText),
                _ => CommandResult.Failure($"Unknown subcommand '{args[0]}'.\n\n{UsageText}")
            });
        }
        catch (SessionArchiveException ex)
        {
            return Task.FromResult(CommandResult.Failure(ex.Message));
        }
        catch (NotSupportedException ex)
        {
            return Task.FromResult(CommandResult.Failure(ex.Message));
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(CommandResult.Failure(ex.Message));
        }
        catch (IOException ex)
        {
            return Task.FromResult(CommandResult.Failure("I/O error: " + ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(CommandResult.Failure("Access denied: " + ex.Message));
        }
    }

    private CommandResult List() =>
        CommandResult.CreateSuccess(new SessionsCommand(_store).FormatList());

    private CommandResult Export(string[] args)
    {
        var parsed = ParseOptions(args);
        var sessionId = ResolveSessionId(parsed.Positional.FirstOrDefault());
        if (sessionId is null)
        {
            return CommandResult.Failure("No session id given and no current session is active.");
        }

        var markdown = parsed.HasFlag("markdown") || parsed.HasFlag("md");
        var defaultName = markdown
            ? SessionArchive.DefaultMarkdownFileName(sessionId)
            : SessionArchive.DefaultFileName(sessionId);
        var outPath = parsed.GetValue("out") ?? parsed.GetValue("output") ?? defaultName;

        if (!markdown)
        {
            var result = SessionArchiveExporter.Export(_store, sessionId, outPath);
            return CommandResult.CreateSuccess(
                $"Exported session {result.SessionId} ({result.TurnCount} turn"
                + $"{(result.TurnCount == 1 ? "" : "s")}, {FormatBytes(result.Bytes)}) to:\n"
                + $"  {result.Path}\n"
                + $"  sha256 {result.Checksum}\n"
                + "The archive is redacted and contains no credentials.");
        }

        var options = new SessionMarkdownOptions
        {
            IncludeToolDetails = parsed.HasFlag("tools") || parsed.HasFlag("tool-details"),
            IncludeModelMetadata = parsed.HasFlag("metadata") || parsed.HasFlag("model-metadata")
        };
        var md = SessionArchiveExporter.ExportMarkdown(_store, sessionId, outPath, options);
        return CommandResult.CreateSuccess(
            $"Exported session {md.SessionId} as Markdown ({md.TurnCount} turn"
            + $"{(md.TurnCount == 1 ? "" : "s")}, {FormatBytes(md.Bytes)}) to:\n  {md.Path}");
    }

    private CommandResult Import(string[] args)
    {
        var parsed = ParseOptions(args);
        var path = parsed.Positional.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(path))
        {
            return CommandResult.Failure("Usage: session import <archive-path> [--dry-run] [--title <title>]");
        }

        var result = SessionArchiveImporter.ImportFile(
            _store,
            path!,
            dryRun: parsed.HasFlag("dry-run") || parsed.HasFlag("dryrun") || parsed.HasFlag("n"),
            title: parsed.GetValue("title"));
        return CommandResult.CreateSuccess(result.Describe());
    }

    private CommandResult Fork(string[] args)
    {
        var parsed = ParseOptions(args);
        var sessionId = ResolveSessionId(parsed.Positional.FirstOrDefault());
        if (sessionId is null)
        {
            return CommandResult.Failure("No session id given and no current session is active.");
        }

        int? atTurn = null;
        var atText = parsed.GetValue("at") ?? parsed.GetValue("turn");
        if (atText is not null)
        {
            if (!int.TryParse(atText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var turn))
            {
                return CommandResult.Failure($"--at expects a turn number, got '{atText}'.");
            }
            atTurn = turn;
        }

        var result = SessionForker.Fork(_store, sessionId, atTurn, parsed.GetValue("title"));
        return CommandResult.CreateSuccess(result.Describe());
    }

    private CommandResult Rename(string[] args)
    {
        var parsed = ParseOptions(args);
        var explicitTitle = parsed.GetValue("title");
        var positional = parsed.Positional;

        string? sessionId;
        string title;
        if (explicitTitle is not null)
        {
            sessionId = ResolveSessionId(positional.FirstOrDefault());
            title = explicitTitle;
        }
        else if (positional.Count >= 2 && SessionStore.IsValidSessionId(positional[0]) && _store.Exists(positional[0]))
        {
            sessionId = positional[0];
            title = string.Join(' ', positional.Skip(1));
        }
        else if (positional.Count >= 1)
        {
            sessionId = ResolveSessionId(null);
            title = string.Join(' ', positional);
        }
        else
        {
            return CommandResult.Failure("Usage: session rename [<id>] <title>");
        }

        if (sessionId is null)
        {
            return CommandResult.Failure("No session id given and no current session is active.");
        }

        if (!_store.Rename(sessionId, title))
        {
            return CommandResult.Failure($"Session '{sessionId}' was not found.");
        }

        return CommandResult.CreateSuccess(string.IsNullOrWhiteSpace(title)
            ? $"Cleared the title of session {sessionId}."
            : $"Renamed session {sessionId} to \"{title.Trim()}\".");
    }

    private CommandResult Stats(string[] args)
    {
        var parsed = ParseOptions(args);
        if (parsed.HasFlag("all"))
        {
            var sessions = _store.List();
            if (sessions.Count == 0)
            {
                return CommandResult.CreateSuccess(SessionsCommand.NoSessionsMessage);
            }
            return CommandResult.CreateSuccess(
                SessionStatsFormatter.FormatTotals(SessionStatsFormatter.Aggregate(sessions)));
        }

        var sessionId = ResolveSessionId(parsed.Positional.FirstOrDefault());
        if (sessionId is null)
        {
            // No current session (one-shot CLI): show the totals instead of failing.
            var sessions = _store.List();
            if (sessions.Count == 0)
            {
                return CommandResult.CreateSuccess(SessionsCommand.NoSessionsMessage);
            }
            return CommandResult.CreateSuccess(
                SessionStatsFormatter.FormatTotals(SessionStatsFormatter.Aggregate(sessions)));
        }

        var summary = _store.List().FirstOrDefault(s =>
            string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
        if (summary is null)
        {
            return CommandResult.Failure($"Session '{sessionId}' was not found.");
        }
        return CommandResult.CreateSuccess(SessionStatsFormatter.FormatSession(summary));
    }

    private string? ResolveSessionId(string? candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            if (!SessionStore.IsValidSessionId(candidate))
            {
                throw new ArgumentException($"Invalid session id: '{candidate}'.");
            }
            return candidate;
        }

        var current = _currentSessionId?.Invoke();
        return string.IsNullOrWhiteSpace(current) ? null : current;
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        return (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>Minimal "--flag" / "--key value" / positional splitter shared by the subcommands.</summary>
    internal static ParsedOptions ParseOptions(string[] args)
    {
        var positional = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("-", StringComparison.Ordinal))
            {
                positional.Add(arg);
                continue;
            }

            var name = arg.TrimStart('-');
            var equals = name.IndexOf('=');
            if (equals > 0)
            {
                values[name[..equals]] = name[(equals + 1)..];
                continue;
            }

            // Options that take a value are followed by a non-option token; everything
            // else is a boolean flag.
            if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal)
                && ValueOptions.Contains(name))
            {
                values[name] = args[++i];
            }
            else
            {
                flags.Add(name);
            }
        }

        return new ParsedOptions(positional, values, flags);
    }

    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "out", "output", "title", "at", "turn"
    };

    internal sealed record ParsedOptions(
        IReadOnlyList<string> Positional,
        IReadOnlyDictionary<string, string> Values,
        IReadOnlySet<string> Flags)
    {
        public bool HasFlag(string name) => Flags.Contains(name);
        public string? GetValue(string name) => Values.TryGetValue(name, out var value) ? value : null;
    }
}
