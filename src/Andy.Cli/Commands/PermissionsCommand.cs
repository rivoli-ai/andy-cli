using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Permissions.Model;
using Andy.Permissions.Store;
using Microsoft.Extensions.DependencyInjection;

namespace Andy.Cli.Commands;

/// <summary>
/// View and modify the tool permission rules the CLI enforces. Lists the effective layered rule set
/// (Builtin, User, Project, Local, Injected, Session) and lets the user persist allow/ask/deny
/// rules to the user, project, or local layer, revoke (remove) individual rules, or reset all
/// file-backed layers back to the Builtin defaults. File edits are followed by a store reload so
/// changes take effect in the running session. Session grants live in memory inside the permission
/// store, which exposes no removal API, so they cannot be revoked individually; they last until
/// the application exits.
/// </summary>
public class PermissionsCommand : ICommand
{
    private readonly IServiceProvider? _serviceProvider;

    public string Name => "permissions";
    public string Description => "View and modify tool permission rules";
    public string[] Aliases => new[] { "perms", "perm" };

    public PermissionsCommand(IServiceProvider? serviceProvider = null)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var sub = (args.Length > 0 ? args[0] : "list").ToLowerInvariant();
        return sub switch
        {
            "list" or "ls" or "show" => ListRules(),
            "allow" => await SetRuleAsync(args, PermissionOutcome.Allow, cancellationToken),
            "ask" => await SetRuleAsync(args, PermissionOutcome.Ask, cancellationToken),
            "deny" => await SetRuleAsync(args, PermissionOutcome.Deny, cancellationToken),
            "remove" or "rm" or "delete" or "del" or "revoke" => RemoveRule(args),
            "clear" => ClearScope(args),
            "reset" => ResetRules(),
            "path" or "paths" or "where" => ShowPaths(),
            "help" or "?" or "-h" or "--help" => Help(),
            _ => CommandResult.Failure($"Unknown subcommand: {sub}. Use 'permissions help' for usage."),
        };
    }

    private IPermissionStore? Store() => _serviceProvider?.GetService<IPermissionStore>();

    /// <summary>
    /// Refresh the in-memory merged rule view after a rule file was edited or deleted, so the
    /// change takes effect in the running session (not just after a restart). Session and
    /// injected layers are kept in memory by the store and are unaffected.
    /// </summary>
    private void ReloadStore() => (Store() as FilePermissionStore)?.Reload();

    private CommandResult ListRules()
    {
        var store = Store();
        if (store is null)
            return CommandResult.Failure("Permission store is not available.");

        var rules = store.GetRules();
        if (rules.Count == 0)
            return CommandResult.CreateSuccess("No permission rules are defined.");

        var sb = new StringBuilder();
        sb.AppendLine("Effective permission rules (higher layers win):");
        sb.AppendLine();

        // Group by layer in precedence order so users can see what overrides what.
        var order = new[] { PermissionLayer.Managed, PermissionLayer.Session, PermissionLayer.Injected,
                            PermissionLayer.Local, PermissionLayer.Project, PermissionLayer.User, PermissionLayer.Builtin };
        foreach (var layer in order)
        {
            var inLayer = rules.Where(r => r.Layer == layer)
                               .OrderByDescending(r => r.Specificity)
                               .ThenBy(r => r.Tool)
                               .ToList();
            if (inLayer.Count == 0) continue;

            sb.AppendLine($"[{layer}]");
            foreach (var r in inLayer)
                sb.AppendLine($"  {r.Outcome,-5}  {r.Format()}");
            sb.AppendLine();
        }

        if (rules.Any(r => r.Layer == PermissionLayer.Session))
        {
            sb.AppendLine("Note: [Session] grants come from interactive prompt decisions. The permission");
            sb.AppendLine("store keeps them in memory with no removal API, so they cannot be revoked");
            sb.AppendLine("individually; they last until the application exits.");
            sb.AppendLine();
        }

        sb.AppendLine("Modify:  permissions allow|ask|deny|revoke <tool[(specifier)]> [--scope user|project|local]");
        sb.AppendLine("Reset:   permissions reset   (delete all user/project/local rule files)");
        return CommandResult.CreateSuccess(sb.ToString().TrimEnd());
    }

    private async Task<CommandResult> SetRuleAsync(string[] args, PermissionOutcome outcome, CancellationToken ct)
    {
        var store = Store();
        if (store is null)
            return CommandResult.Failure("Permission store is not available.");

        // args: [verb, spec, --scope, value] in any order after the verb.
        var positional = args.Skip(1).Where(a => !a.StartsWith("-")).ToArray();
        if (positional.Length == 0)
            return CommandResult.Failure($"Usage: permissions {outcome.ToString().ToLowerInvariant()} <tool[(specifier)]> [--scope user|project|local]");

        if (!TryParseScope(args, out var scope, out var scopeError))
            return CommandResult.Failure(scopeError);

        var (tool, specifier) = ParseSpec(positional[0]);
        if (string.IsNullOrWhiteSpace(tool))
            return CommandResult.Failure("Tool name is required, e.g. 'permissions allow execute_command(*)'.");

        try
        {
            await store.AppendRuleAsync(tool, specifier, outcome, scope, ct);
        }
        catch (Exception ex)
        {
            return CommandResult.Failure($"Failed to write rule: {ex.Message}");
        }

        return CommandResult.CreateSuccess(
            $"{outcome} {tool}({specifier}) written to the {scope} layer. Run 'permissions list' to verify.");
    }

    private CommandResult RemoveRule(string[] args)
    {
        var positional = args.Skip(1).Where(a => !a.StartsWith("-")).ToArray();
        if (positional.Length == 0)
            return CommandResult.Failure("Usage: permissions revoke <tool[(specifier)]> [--scope user|project|local]");
        if (!TryParseScope(args, out var scope, out var scopeError))
            return CommandResult.Failure(scopeError);

        var (tool, specifier) = ParseSpec(positional[0]);
        var rule = $"{tool}({specifier})";
        var path = PermissionRuleFile.PathForScope(scope, Directory.GetCurrentDirectory());

        var file = PermissionRuleFile.Load(path);
        if (!file.Remove(rule))
            return CommandResult.CreateSuccess($"No rule {rule} found in the {scope} layer.\n  ({path})");
        try
        {
            file.Save(path);
        }
        catch (Exception ex)
        {
            return CommandResult.Failure($"Failed to update {path}: {ex.Message}");
        }
        ReloadStore();
        return CommandResult.CreateSuccess($"Removed {rule} from the {scope} layer. Run 'permissions list' to verify.");
    }

    private CommandResult ClearScope(string[] args)
    {
        if (!TryParseScope(args, out var scope, out var scopeError))
            return CommandResult.Failure(scopeError);
        var path = PermissionRuleFile.PathForScope(scope, Directory.GetCurrentDirectory());
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            return CommandResult.Failure($"Failed to clear {path}: {ex.Message}");
        }
        ReloadStore();
        return CommandResult.CreateSuccess($"Cleared all rules in the {scope} layer.\n  ({path})");
    }

    /// <summary>
    /// Reset to defaults: delete the user, project, and local rule files, leaving only the
    /// Builtin layer (plus any admin Managed, Injected, or in-memory Session rules, which are
    /// not file-backed and are out of this command's reach).
    /// </summary>
    private CommandResult ResetRules()
    {
        var cwd = Directory.GetCurrentDirectory();
        return ResetRulesAt(
            PermissionRuleFile.PathForScope(PersistScope.User, cwd),
            PermissionRuleFile.PathForScope(PersistScope.Project, cwd),
            PermissionRuleFile.PathForScope(PersistScope.Local, cwd));
    }

    /// <summary>Core of "permissions reset"; the explicit paths make this testable without touching real config.</summary>
    internal CommandResult ResetRulesAt(string userPath, string projectPath, string localPath)
    {
        var sb = new StringBuilder();
        var targets = new (string Scope, string Path)[]
        {
            ("user", userPath),
            ("project", projectPath),
            ("local", localPath),
        };

        foreach (var (scopeName, path) in targets)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                sb.AppendLine($"  {scopeName,-8} no rule file ({(string.IsNullOrEmpty(path) ? "unset" : path)})");
                continue;
            }
            try
            {
                File.Delete(path);
                sb.AppendLine($"  {scopeName,-8} deleted {path}");
            }
            catch (Exception ex)
            {
                return CommandResult.Failure($"Failed to delete {path}: {ex.Message}\n{sb}");
            }
        }

        ReloadStore();
        sb.AppendLine();
        sb.AppendLine("Permission rules reset to defaults (Builtin layer only).");
        sb.AppendLine("Not affected: Managed (admin) rules, Injected rules, and in-memory Session");
        sb.AppendLine("grants, which last until the application exits.");
        return CommandResult.CreateSuccess(sb.ToString().TrimEnd());
    }

    private CommandResult ShowPaths()
    {
        var opts = new PermissionStoreOptions().WithProjectDirectory(Directory.GetCurrentDirectory());
        var sb = new StringBuilder();
        sb.AppendLine("Permission rule files (edit these directly to remove or hand-tune rules):");
        sb.AppendLine($"  user     {opts.UserFilePath ?? "(unset)"}");
        sb.AppendLine($"  project  {opts.ProjectFilePath ?? "(unset)"}");
        sb.AppendLine($"  local    {opts.LocalFilePath ?? "(unset)"}   (intended for .gitignore)");
        sb.AppendLine($"  managed  {opts.ManagedFilePath ?? "(unset)"}");
        sb.AppendLine();
        sb.AppendLine("Precedence (lowest to highest): Builtin < User < Project < Local < Injected < Session < Managed");
        return CommandResult.CreateSuccess(sb.ToString().TrimEnd());
    }

    private CommandResult Help()
    {
        var sb = new StringBuilder();
        sb.AppendLine("permissions - view and modify tool permission rules");
        sb.AppendLine();
        sb.AppendLine("Usage:");
        sb.AppendLine("  permissions [list]                                  Show effective rules by layer");
        sb.AppendLine("  permissions allow <tool[(specifier)]> [--scope S]   Persist an Allow rule");
        sb.AppendLine("  permissions ask   <tool[(specifier)]> [--scope S]   Persist an Ask (prompt) rule");
        sb.AppendLine("  permissions deny  <tool[(specifier)]> [--scope S]   Persist a Deny rule");
        sb.AppendLine("  permissions revoke <tool[(specifier)]> [--scope S]  Delete a rule from a layer file");
        sb.AppendLine("                                                      (alias: remove, rm, delete)");
        sb.AppendLine("  permissions clear [--scope S]                       Delete ALL rules in a layer file");
        sb.AppendLine("  permissions reset                                   Delete the user, project, AND local");
        sb.AppendLine("                                                      rule files (back to Builtin defaults)");
        sb.AppendLine("  permissions path                                    Show the rule file locations");
        sb.AppendLine("  permissions help                                    Show this help");
        sb.AppendLine();
        sb.AppendLine("Scope (S): user (default), project, or local. Builtin/Managed/Injected rules are");
        sb.AppendLine("not file-backed here and cannot be edited by this command. Session grants (from");
        sb.AppendLine("interactive 'allow' prompt decisions) are in-memory only: the permission store");
        sb.AppendLine("has no removal API for them, so they last until the application exits.");
        sb.AppendLine("Specifier defaults to * (any args). Examples:");
        sb.AppendLine("  permissions allow execute_command(*)");
        sb.AppendLine("  permissions deny  write_file --scope project");
        sb.AppendLine("  permissions revoke execute_command(*)");
        return CommandResult.CreateSuccess(sb.ToString().TrimEnd());
    }

    /// <summary>Split "tool(specifier)" into parts; a bare "tool" defaults the specifier to "*".</summary>
    internal static (string tool, string specifier) ParseSpec(string raw)
    {
        var s = raw.Trim();
        int lp = s.IndexOf('(');
        if (lp > 0 && s.EndsWith(")"))
            return (s.Substring(0, lp).Trim(), s.Substring(lp + 1, s.Length - lp - 2).Trim());
        return (s, "*");
    }

    /// <summary>Read --scope/-s; defaults to User. Only the persistable layers are accepted.</summary>
    internal static bool TryParseScope(string[] args, out PersistScope scope, out string error)
    {
        scope = PersistScope.User;
        error = string.Empty;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--scope" or "-s")
            {
                if (i + 1 >= args.Length) { error = "Missing value after --scope (expected user, project, or local)."; return false; }
                var v = args[i + 1].ToLowerInvariant();
                switch (v)
                {
                    case "user": scope = PersistScope.User; return true;
                    case "project": scope = PersistScope.Project; return true;
                    case "local": scope = PersistScope.Local; return true;
                    default: error = $"Unknown scope '{v}'. Use user, project, or local."; return false;
                }
            }
        }
        return true;
    }
}
