using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services.Formatting;

namespace Andy.Cli.Commands;

/// <summary>
/// Inspect the formatters Andy will run after it writes a file.
///
/// The interesting subcommand is <c>status</c>: given a file, it prints which formatter matched and
/// WHY - extension match, which config layer defined it, whether the command resolves on this
/// machine, and the order it runs in. That answers the two questions users actually ask when
/// formatting does not happen ("is it configured?" and "is the tool installed?") without making
/// them read a config file.
/// </summary>
public class FormattersCommand : ICommand
{
    private readonly string _projectRoot;
    private readonly Func<string, FormatterCatalog> _catalogFactory;

    public string Name => "formatters";
    public string Description => "Show which formatters run after Andy writes a file";
    public string[] Aliases => new[] { "formatter", "fmt" };

    public FormattersCommand(string? projectRoot = null, Func<string, FormatterCatalog>? catalogFactory = null)
    {
        _projectRoot = projectRoot ?? Directory.GetCurrentDirectory();
        // Rebuilt per invocation so a config edit takes effect without restarting the session.
        _catalogFactory = catalogFactory ?? (root => FormatterCatalog.ForProject(root));
    }

    public Task<CommandResult> ExecuteAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var sub = (args.Length > 0 ? args[0] : "status").ToLowerInvariant();
        return Task.FromResult(sub switch
        {
            "status" => Status(args.Skip(1).FirstOrDefault()),
            "list" or "ls" => List(),
            "path" or "paths" or "where" => Paths(),
            "help" or "?" or "-h" or "--help" => Help(),
            // A bare path is the common case: "/formatters src/Foo.cs".
            _ => Status(args[0]),
        });
    }

    private CommandResult Status(string? filePath)
    {
        var catalog = _catalogFactory(_projectRoot);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return List();
        }

        var absolute = Path.IsPathRooted(filePath)
            ? filePath
            : Path.GetFullPath(Path.Combine(_projectRoot, filePath));

        var sb = new StringBuilder();
        sb.AppendLine($"Formatters for {filePath}");
        sb.AppendLine();

        var matches = catalog.Explain(absolute);
        if (matches.Count == 0)
        {
            sb.AppendLine("  No formatters are configured or detected.");
            sb.AppendLine();
            AppendPaths(sb);
            return CommandResult.CreateSuccess(sb.ToString().TrimEnd());
        }

        var runnable = matches.Where(m => m.WillRun).ToList();
        if (runnable.Count == 0)
        {
            sb.AppendLine("  Nothing will run for this file.");
        }
        else
        {
            sb.AppendLine($"  Will run, in this order ({runnable.Count}):");
            for (int i = 0; i < runnable.Count; i++)
            {
                var m = runnable[i];
                sb.AppendLine($"    {i + 1}. {m.Definition.Name}");
                sb.AppendLine($"       command  {m.Definition.DescribeCommandLine(absolute)}");
                sb.AppendLine($"       why      {m.Reason}");
                sb.AppendLine($"       timeout  {m.Definition.Timeout.TotalSeconds:0.#}s");
            }
        }

        var skipped = matches.Where(m => !m.WillRun).ToList();
        if (skipped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  Not run ({skipped.Count}):");
            foreach (var m in skipped)
            {
                sb.AppendLine($"    {m.Definition.Name,-20} {m.Reason}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Formatters run after a successful write and before the diff is rendered, so the");
        sb.AppendLine("diff you see is the file's final on-disk content. Each formatter process is");
        sb.AppendLine("authorized through the same permission rules as any other command Andy runs.");
        return CommandResult.CreateSuccess(sb.ToString().TrimEnd());
    }

    private CommandResult List()
    {
        var catalog = _catalogFactory(_projectRoot);
        var sb = new StringBuilder();
        sb.AppendLine("Configured formatters (in run order):");
        sb.AppendLine();

        if (catalog.Definitions.Count == 0)
        {
            sb.AppendLine("  none");
        }
        else
        {
            sb.AppendLine($"  {"ORDER",-6} {"NAME",-20} {"SOURCE",-16} {"EXTENSIONS",-28} STATE");
            foreach (var d in catalog.Definitions)
            {
                var extensions = d.Extensions.Count == 0 ? "-" : string.Join(",", d.Extensions);
                if (extensions.Length > 27)
                {
                    extensions = extensions[..24] + "...";
                }

                var installed = FormatterAvailability.Resolve(d.Command) is not null;
                var state = !d.Enabled ? "disabled" : installed ? "ready" : "not installed";
                sb.AppendLine($"  {d.Order,-6} {d.Name,-20} {FormatterCatalog.SourceLabel(d.Source),-16} {extensions,-28} {state}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Use 'formatters status <file>' to see which of these apply to one file and why.");
        sb.AppendLine();
        AppendPaths(sb);
        return CommandResult.CreateSuccess(sb.ToString().TrimEnd());
    }

    private CommandResult Paths()
    {
        var sb = new StringBuilder();
        AppendPaths(sb);
        return CommandResult.CreateSuccess(sb.ToString().TrimEnd());
    }

    private void AppendPaths(StringBuilder sb)
    {
        sb.AppendLine("Configuration (project overrides user; both override locally detected defaults):");
        sb.AppendLine($"  project  {FormatterConfigLoader.ProjectPath(_projectRoot)}");
        try
        {
            sb.AppendLine($"  user     {FormatterConfigLoader.UserPath()}");
        }
        catch (Exception)
        {
            sb.AppendLine("  user     (unavailable)");
        }

        sb.AppendLine("Andy never installs a formatter; a command that is not on PATH is skipped.");
    }

    private static CommandResult Help()
    {
        var sb = new StringBuilder();
        sb.AppendLine("formatters - inspect the formatters that run after Andy writes a file");
        sb.AppendLine();
        sb.AppendLine("Usage:");
        sb.AppendLine("  formatters status <file>   Explain which formatters match that file, and why");
        sb.AppendLine("  formatters <file>          Same as 'status <file>'");
        sb.AppendLine("  formatters list            List every configured/detected formatter");
        sb.AppendLine("  formatters path            Show the configuration file locations");
        sb.AppendLine("  formatters help            Show this help");
        sb.AppendLine();
        sb.AppendLine("Configuration is a JSON file with a 'formatters' object keyed by name:");
        sb.AppendLine("  { \"formatters\": { \"csharpier\": { \"command\": \"csharpier\",");
        sb.AppendLine("      \"arguments\": [\"format\", \"$FILE\"], \"extensions\": [\".cs\"],");
        sb.AppendLine("      \"timeoutSeconds\": 60, \"order\": 10, \"enabled\": true } } }");
        sb.AppendLine();
        sb.AppendLine("$FILE is replaced with the absolute path of the file being formatted; when no");
        sb.AppendLine("argument mentions it, the path is appended as the last argument.");
        return CommandResult.CreateSuccess(sb.ToString().TrimEnd());
    }
}
