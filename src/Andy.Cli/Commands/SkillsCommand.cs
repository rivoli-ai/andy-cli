using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Skills;
using Andy.Skills.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Andy.Cli.Commands;

/// <summary>
/// List, inspect, and enable/disable Agent Skills (Andy.Skills). Skills are directories
/// containing a SKILL.md manifest, discovered from &lt;workspace&gt;/.andy/skills and
/// ~/.andy/skills. The agent sees only skill names and descriptions up front and loads a
/// skill's full instructions on demand through the `skill` tool; disabling a skill here puts
/// it on a CLI-side disable list that the tools' catalog honors, so a disabled skill cannot
/// be loaded by the agent. Enabled/disabled state is a CLI concept: Andy.Skills itself has
/// no per-skill flag, so the list is persisted in .andy/skills.disabled.json.
/// </summary>
public class SkillsCommand : ICommand
{
    private readonly IServiceProvider? _serviceProvider;
    private readonly string _workspaceDirectory;

    public string Name => "skills";
    public string Description => "List, inspect, and enable/disable agent skills";
    public string[] Aliases => new[] { "skill" };

    public SkillsCommand(IServiceProvider? serviceProvider = null, string? workspaceDirectory = null)
    {
        _serviceProvider = serviceProvider;
        _workspaceDirectory = workspaceDirectory ?? Directory.GetCurrentDirectory();
    }

    public async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var sub = (args.Length > 0 ? args[0] : "list").ToLowerInvariant();
        return sub switch
        {
            "list" or "ls" => await ListSkillsAsync(cancellationToken),
            "info" or "show" => await ShowSkillAsync(args, cancellationToken),
            "enable" => await SetEnabledAsync(args, enabled: true, cancellationToken),
            "disable" => await SetEnabledAsync(args, enabled: false, cancellationToken),
            "diagnostics" or "diag" => await ShowDiagnosticsAsync(cancellationToken),
            "reload" or "refresh" => await ReloadAsync(cancellationToken),
            "help" or "?" or "-h" or "--help" => Help(),
            _ => CommandResult.Failure($"Unknown subcommand: {sub}. Use 'skills help' for usage."),
        };
    }

    private ISkillCatalog? Catalog() => _serviceProvider?.GetService<ISkillCatalog>();

    private string[] Roots() => SkillDiscovery.DefaultRoots(_workspaceDirectory).ToArray();

    private async Task<CommandResult> ListSkillsAsync(CancellationToken ct)
    {
        var catalog = Catalog();
        if (catalog is null)
            return CommandResult.Failure("Skill catalog is not available.");

        var filtered = catalog as FilteredSkillCatalog;
        var skills = filtered != null
            ? await filtered.GetAllSkillsAsync(ct)
            : await catalog.GetSkillsAsync(ct);
        var diagnostics = await catalog.GetDiagnosticsAsync(ct);

        var sb = new StringBuilder();
        if (skills.Count == 0)
        {
            sb.AppendLine("No skills found.");
            sb.AppendLine();
            sb.AppendLine("Skills are directories containing a SKILL.md manifest, discovered from:");
            foreach (var root in Roots())
                sb.AppendLine($"  {root}");
        }
        else
        {
            sb.AppendLine($"Skills ({skills.Count}):");
            sb.AppendLine();
            var nameWidth = Math.Min(28, skills.Max(s => s.Name.Length));
            foreach (var skill in skills.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                var state = filtered != null && filtered.IsDisabled(skill.Name) ? "  [disabled]" : "";
                sb.AppendLine($"  {skill.Name.PadRight(nameWidth)}  {skill.Description}{state}");
            }
            sb.AppendLine();
            sb.AppendLine("Use 'skills info <name>' for details, 'skills enable|disable <name>' to toggle.");
        }

        if (diagnostics.Count > 0)
            sb.AppendLine($"{diagnostics.Count} discovery diagnostic(s); run 'skills diagnostics' to view.");

        return CommandResult.CreateSuccess(sb.ToString().TrimEnd());
    }

    private async Task<CommandResult> ShowSkillAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 2)
            return CommandResult.Failure("Usage: skills info <name>");

        var catalog = Catalog();
        if (catalog is null)
            return CommandResult.Failure("Skill catalog is not available.");

        var name = args[1];
        var skill = await FindIncludingDisabledAsync(catalog, name, ct);
        if (skill is null)
            return CommandResult.Failure($"No skill named '{name}'. Run 'skills list' to see what is available.");

        var filtered = catalog as FilteredSkillCatalog;
        var sb = new StringBuilder();
        sb.AppendLine($"Skill: {skill.Name}");
        sb.AppendLine($"  Description : {skill.Description}");
        sb.AppendLine($"  State       : {(filtered != null && filtered.IsDisabled(skill.Name) ? "disabled" : "enabled")}");
        sb.AppendLine($"  Directory   : {skill.DirectoryPath}");
        sb.AppendLine($"  Manifest    : {skill.ManifestPath}");
        if (!string.IsNullOrWhiteSpace(skill.License))
            sb.AppendLine($"  License     : {skill.License}");
        if (skill.AllowedTools is { Count: > 0 })
            sb.AppendLine($"  Allowed tools: {string.Join(", ", skill.AllowedTools)}");
        if (skill.Metadata is { Count: > 0 })
        {
            sb.AppendLine("  Metadata    :");
            foreach (var kv in skill.Metadata)
                sb.AppendLine($"    {kv.Key}: {kv.Value}");
        }
        return CommandResult.CreateSuccess(sb.ToString().TrimEnd());
    }

    private async Task<CommandResult> SetEnabledAsync(string[] args, bool enabled, CancellationToken ct)
    {
        var verb = enabled ? "enable" : "disable";
        if (args.Length < 2)
            return CommandResult.Failure($"Usage: skills {verb} <name>");

        var catalog = Catalog();
        if (catalog is null)
            return CommandResult.Failure("Skill catalog is not available.");
        if (catalog is not FilteredSkillCatalog filtered)
            return CommandResult.Failure(
                "Enable/disable is not available: the skill catalog in use has no disable list. " +
                "(Andy.Skills has no per-skill enabled state; the CLI implements it via a disable-list " +
                "catalog, which is not registered in this mode.)");

        var name = args[1];
        var skill = await FindIncludingDisabledAsync(catalog, name, ct);
        if (skill is null)
            return CommandResult.Failure($"No skill named '{name}'. Run 'skills list' to see what is available.");

        var changed = filtered.SetDisabled(skill.Name, disabled: !enabled);
        if (!changed)
            return CommandResult.CreateSuccess($"Skill '{skill.Name}' is already {(enabled ? "enabled" : "disabled")}.");

        var sb = new StringBuilder();
        sb.AppendLine($"Skill '{skill.Name}' {(enabled ? "enabled" : "disabled")}.");
        if (!enabled)
            sb.AppendLine("The skill and skill_file tools will refuse to load it from now on.");
        sb.AppendLine($"(Disable list: {filtered.DisableListPath})");
        sb.AppendLine("Note: the skill summary already embedded in the current session's system prompt");
        sb.AppendLine("refreshes on the next session or model switch.");
        return CommandResult.CreateSuccess(sb.ToString().TrimEnd());
    }

    private async Task<CommandResult> ShowDiagnosticsAsync(CancellationToken ct)
    {
        var catalog = Catalog();
        if (catalog is null)
            return CommandResult.Failure("Skill catalog is not available.");

        var diagnostics = await catalog.GetDiagnosticsAsync(ct);
        if (diagnostics.Count == 0)
            return CommandResult.CreateSuccess("No skill discovery diagnostics.");

        var sb = new StringBuilder();
        sb.AppendLine($"Skill discovery diagnostics ({diagnostics.Count}):");
        foreach (var d in diagnostics)
        {
            sb.AppendLine($"  [{d.Severity}] {d.Path}");
            sb.AppendLine($"    {d.Message}");
        }
        return CommandResult.CreateSuccess(sb.ToString().TrimEnd());
    }

    private async Task<CommandResult> ReloadAsync(CancellationToken ct)
    {
        var catalog = Catalog();
        if (catalog is null)
            return CommandResult.Failure("Skill catalog is not available.");

        catalog.Invalidate();
        var skills = await catalog.GetSkillsAsync(ct);
        return CommandResult.CreateSuccess($"Skill catalog reloaded: {skills.Count} enabled skill(s).");
    }

    private CommandResult Help()
    {
        var sb = new StringBuilder();
        sb.AppendLine("skills - list, inspect, and enable/disable agent skills");
        sb.AppendLine();
        sb.AppendLine("Usage:");
        sb.AppendLine("  skills [list]           Show discovered skills (disabled ones are marked)");
        sb.AppendLine("  skills info <name>      Show a skill's details (path, license, tools, metadata)");
        sb.AppendLine("  skills enable <name>    Remove a skill from the disable list");
        sb.AppendLine("  skills disable <name>   Put a skill on the disable list (the agent cannot load it)");
        sb.AppendLine("  skills diagnostics      Show problems found during skill discovery");
        sb.AppendLine("  skills reload           Re-scan the skill roots");
        sb.AppendLine("  skills help             Show this help");
        sb.AppendLine();
        sb.AppendLine("Skills are directories containing a SKILL.md manifest (YAML frontmatter with");
        sb.AppendLine("name/description, then markdown instructions), discovered from:");
        foreach (var root in Roots())
            sb.AppendLine($"  {root}");
        sb.AppendLine("Earlier roots win on a name conflict. To add or remove a skill, create or");
        sb.AppendLine("delete its directory; 'skills reload' picks up the change.");
        sb.AppendLine();
        sb.AppendLine("Enabled/disabled state is a CLI-side concept (Andy.Skills has no per-skill");
        sb.AppendLine("flag); disabled names are persisted in .andy/skills.disabled.json and the");
        sb.AppendLine("skill tools' catalog refuses to load skills on that list.");
        return CommandResult.CreateSuccess(sb.ToString().TrimEnd());
    }

    /// <summary>
    /// Look a skill up by name across ALL discovered skills, including disabled ones, so
    /// info/enable work on disabled skills (the plain catalog FindAsync hides them).
    /// </summary>
    private async Task<Skill?> FindIncludingDisabledAsync(ISkillCatalog catalog, string name, CancellationToken ct)
    {
        var skills = catalog is FilteredSkillCatalog filtered
            ? await filtered.GetAllSkillsAsync(ct)
            : await catalog.GetSkillsAsync(ct);
        return skills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
