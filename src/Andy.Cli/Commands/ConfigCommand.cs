using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Configuration;

namespace Andy.Cli.Commands;

/// <summary>
/// Inspects the layered configuration (rivoli-ai/andy-cli#280).
///
/// <c>config validate</c> reports every problem found in every layer, with source,
/// line, column and key path, and fails when any of them is an error.
/// <c>config show --effective --sources</c> prints the merged values and where each
/// one came from. Both go through <see cref="ConfigReportFormatter"/>, so neither
/// can print an API key, a header value or a value resolved from <c>{env:NAME}</c>.
/// </summary>
public class ConfigCommand : ICommand
{
    private readonly Func<ConfigLoadRequest, EffectiveConfiguration> _loader;
    private readonly string _workspaceDirectory;

    public string Name => "config";
    public string Description => "Show and validate the layered configuration";
    public string[] Aliases => new[] { "/config" };

    public ConfigCommand()
        : this(Directory.GetCurrentDirectory(), null)
    {
    }

    public ConfigCommand(
        string workspaceDirectory,
        Func<ConfigLoadRequest, EffectiveConfiguration>? loader = null)
    {
        _workspaceDirectory = workspaceDirectory;
        _loader = loader ?? (request => new AndyConfigurationService().Load(request));
    }

    public Task<CommandResult> ExecuteAsync(string[] args, CancellationToken cancellationToken = default)
    {
        args ??= Array.Empty<string>();
        var subcommand = args.Length == 0 ? "show" : args[0].Trim().ToLowerInvariant();
        var flags = new HashSet<string>(
            args.Skip(1).Where(a => a.StartsWith("--", StringComparison.Ordinal)),
            StringComparer.OrdinalIgnoreCase);

        if (subcommand is "-h" or "--help" or "help")
        {
            return Task.FromResult(CommandResult.CreateSuccess(Usage()));
        }

        if (subcommand is "path" or "paths" or "sources")
        {
            var located = Load(args);
            return Task.FromResult(CommandResult.CreateSuccess(
                ConfigReportFormatter.FormatSources(located)));
        }

        if (subcommand is "schema")
        {
            return Task.FromResult(CommandResult.CreateSuccess(ConfigSchema.Text));
        }

        if (subcommand is "validate" or "check")
        {
            var effective = Load(args);
            var report = ConfigReportFormatter.FormatDiagnostics(effective);
            if (effective.HasErrors)
            {
                var count = effective.Errors.Count();
                return Task.FromResult(CommandResult.Failure(
                    report + Environment.NewLine
                        + $"Configuration is invalid: {count} error{(count == 1 ? "" : "s")}."));
            }

            var builder = new StringBuilder();
            if (report.Length > 0)
            {
                builder.AppendLine(report);
            }
            builder.Append("Configuration is valid.");
            return Task.FromResult(CommandResult.CreateSuccess(builder.ToString()));
        }

        if (subcommand is "show" or "list" or "ls")
        {
            var effective = Load(args);
            // --sources implies provenance; --effective is accepted (and is the
            // default) so the documented invocation reads the way the issue writes it.
            var includeSources = flags.Contains("--sources") || flags.Contains("--source");
            var text = flags.Contains("--json")
                ? ConfigReportFormatter.FormatJson(effective, includeSources)
                : ConfigReportFormatter.FormatEffective(effective, includeSources);
            return Task.FromResult(CommandResult.CreateSuccess(text));
        }

        return Task.FromResult(CommandResult.Failure(
            $"Unknown config subcommand '{subcommand}'." + Environment.NewLine + Usage()));
    }

    private EffectiveConfiguration Load(IReadOnlyList<string> args) =>
        _loader(new ConfigLoadRequest
        {
            WorkspaceDirectory = _workspaceDirectory,
            CommandLineArguments = args.ToArray(),
        });

    private static string Usage()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Usage: andy-cli config <subcommand>");
        builder.AppendLine();
        builder.AppendLine("  show [--effective] [--sources] [--json]");
        builder.AppendLine("      Print the merged configuration. --sources annotates every value");
        builder.AppendLine("      with the file, line and column it came from.");
        builder.AppendLine("  validate");
        builder.AppendLine("      Check every layer against the schema. Exits non-zero on any error.");
        builder.AppendLine("  sources");
        builder.AppendLine("      List the configuration files consulted, lowest precedence first.");
        builder.AppendLine("  schema");
        builder.AppendLine("      Print the JSON Schema this build validates against.");
        builder.AppendLine();
        builder.AppendLine("Precedence: packaged defaults < user < project < environment < CLI arguments.");
        builder.AppendLine("Files: ~/.andy/andy.jsonc, <workspace>/andy.jsonc, <workspace>/.andy/andy.jsonc");
        builder.Append("API keys, tokens, headers and values resolved from {env:NAME} are never printed.");
        return builder.ToString();
    }
}
