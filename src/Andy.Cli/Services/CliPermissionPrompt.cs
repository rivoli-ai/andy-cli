using System.Collections.Concurrent;
using Andy.Permissions.DependencyInjection;
using Andy.Permissions.Model;
using Andy.Permissions.Prompt;
using Andy.Permissions.Store;
using Microsoft.Extensions.DependencyInjection;

namespace Andy.Cli.Services;

/// <summary>
/// A single pending tool-permission request awaiting a decision from the UI thread. The agent runs on a
/// background task; it posts one of these to the <see cref="PermissionRequestBroker"/> and awaits
/// <see cref="Completion"/>, which the main render/input loop completes after showing a modal.
/// </summary>
public sealed class PendingPermissionRequest
{
    public required PermissionRequest Request { get; init; }

    public TaskCompletionSource<PermissionDecision> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Thread-safe hand-off between the agent's background tool-execution task and the main TUI loop, which
/// owns the terminal input/render. The permission engine already serializes prompts, so at most one
/// request is pending at a time.
/// </summary>
public sealed class PermissionRequestBroker
{
    private readonly ConcurrentQueue<PendingPermissionRequest> _queue = new();

    public bool HasPending => !_queue.IsEmpty;

    public void Enqueue(PendingPermissionRequest request) => _queue.Enqueue(request);

    public bool TryDequeue(out PendingPermissionRequest? request) => _queue.TryDequeue(out request);
}

/// <summary>
/// Interactive <see cref="IPermissionPrompt"/> for the TUI: posts the request to the broker and awaits the
/// main loop's decision. Cancellation resolves to a deny.
///
/// When the user picks "Allow (session)", the permission engine on its own remembers a rule whose command
/// specifier is the *full* command line (e.g. <c>execute_command(gh pr list --limit 10:*)</c>). Because
/// command matching is a word-boundary prefix match, a near-identical follow-up such as
/// <c>gh pr list --limit 20</c> does not match and the user is prompted again. To stop that re-prompting,
/// after a session Allow this prompt also installs a broader session rule keyed to the *command class*
/// rather than the exact arguments (see <see cref="GrantBroadenedSessionRules"/>).
/// </summary>
public sealed class CliPermissionPrompt : IPermissionPrompt
{
    private readonly PermissionRequestBroker _broker;
    private readonly IPermissionStore? _store;

    public CliPermissionPrompt(PermissionRequestBroker broker, IPermissionStore? store = null)
    {
        _broker = broker;
        _store = store;
    }

    public Task<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken cancellationToken = default)
    {
        var pending = new PendingPermissionRequest { Request = request };
        cancellationToken.Register(() => pending.Completion.TrySetResult(PermissionDecision.DenyOnce));
        _broker.Enqueue(pending);

        // After the UI resolves the decision, broaden a session Allow so similar commands are not re-prompted.
        return pending.Completion.Task.ContinueWith(t =>
        {
            var decision = t.Result;
            if (decision.Allowed && decision.Persist == PersistScope.Session)
            {
                GrantBroadenedSessionRules(request, _store);
            }

            return decision;
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    /// <summary>
    /// Grant granularity for a session "Allow": tool id + command class, where the command class is an
    /// allowlisted executable plus its first subcommand token (e.g. <c>gh pr</c>, <c>git status</c>,
    /// <c>npm run</c>). The rule is session-scoped only (in-memory, lost on exit) and applies only to the
    /// same tool. When <see cref="CommandClass"/> declines to broaden (returns empty), no extra rule is
    /// installed and only the engine's own exact full-command rule stands.
    ///
    /// Rationale: matching on the executable + first subcommand stops re-prompting for argument-only
    /// variations of the *same* action a user already approved, while staying narrow enough that approving
    /// <c>gh pr list</c> does NOT silently authorize an unrelated <c>gh repo delete</c> or a different
    /// executable such as <c>rm -rf</c>. Each command segment the engine flagged for Ask is broadened
    /// independently, so a compound command only broadens the parts that actually required consent.
    /// </summary>
    internal static void GrantBroadenedSessionRules(PermissionRequest request, IPermissionStore? store)
    {
        if (store is null)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in request.Evaluation.Resources)
        {
            if (resource.Outcome != PermissionOutcome.Ask || resource.Access.Kind != ResourceKind.Command)
            {
                continue;
            }

            var commandClass = CommandClass(resource.Access.Value);
            if (string.IsNullOrEmpty(commandClass) || !seen.Add(commandClass))
            {
                continue;
            }

            var rule = PermissionRule.Parse(
                $"{request.ToolId}({commandClass}:*)", PermissionOutcome.Allow, PermissionLayer.Session);
            store.AddSessionRule(rule);
        }
    }

    /// <summary>
    /// Conservative allowlist of executables that dispatch on a plain first subcommand token and whose
    /// subcommand-level granularity is safe to broaden to (e.g. approving <c>git status</c> installs a
    /// <c>git status:*</c> session rule, not an all-of-<c>git</c> rule).
    ///
    /// Rationale for an allowlist (issue #168): the previous heuristic fell back to an executable-wide class
    /// whenever the first argument was a flag, so <c>python -c ...</c>, <c>sh -c ...</c>, or <c>rm -i ...</c>
    /// could silently authorize ANY later <c>python</c>/<c>sh</c>/<c>rm</c> invocation for the session. We
    /// therefore refuse to broaden unless (1) the executable is one of these well-known, subcommand-oriented
    /// developer tools and (2) the token after it is a plain subcommand. Interpreters, shells, and
    /// destructive utilities (python, sh, bash, zsh, node, ruby, perl, rm, dd, ...) are deliberately absent,
    /// so they always stay at the engine's exact full-command approval. Membership is by the exact
    /// (path-free) executable token; a path-qualified executable such as <c>/usr/bin/git</c> or
    /// <c>./git</c> is never matched, so a same-named binary elsewhere cannot inherit the approval.
    /// </summary>
    private static readonly HashSet<string> BroadenableSubcommandTools = new(StringComparer.Ordinal)
    {
        "git", "dotnet", "npm", "pnpm", "yarn", "cargo", "go", "gh", "kubectl",
        "docker", "terraform", "gradle", "mvn", "bundle", "gem", "helm",
    };

    /// <summary>
    /// Reduces a command string to its command class: an allowlisted executable plus its first plain
    /// subcommand token (e.g. <c>git status</c>, <c>npm run</c>). Returns an empty string - meaning "do NOT
    /// broaden; keep only the exact approval" - for every ambiguous or unsafe form, including:
    /// a first token that begins with <c>-</c> (a flag/option, e.g. <c>python -c</c>, <c>rm -i</c>,
    /// <c>git -C /x status</c>), an executable not on <see cref="BroadenableSubcommandTools"/>,
    /// a path-qualified executable (<c>./script.sh</c>, <c>/usr/bin/git</c>), a redirect/quote/metacharacter,
    /// an environment assignment where the executable would be, or a missing subcommand. Leading
    /// <c>VAR=value</c> assignments before the executable are skipped.
    /// </summary>
    internal static string CommandClass(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        var tokens = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int i = 0;

        // Skip leading environment assignments (FOO=bar) that precede the executable.
        while (i < tokens.Length && IsAssignment(tokens[i]))
        {
            i++;
        }

        // The executable must be a plain, path-free word. Reject flags, redirects, quotes, assignments and
        // path-qualified binaries so we never broaden on something we cannot safely reason about.
        if (i >= tokens.Length || !IsPlainWord(tokens[i]))
        {
            return string.Empty;
        }

        var exe = tokens[i];
        i++;

        // Only well-known subcommand-dispatch tools broaden, and only to "executable + subcommand".
        // Everything else keeps the engine's exact full-command approval (no broadening).
        if (!BroadenableSubcommandTools.Contains(exe))
        {
            return string.Empty;
        }

        // Require a plain subcommand token immediately after the executable (no flag, path, assignment,
        // redirect or quoting). A flag-first form (e.g. `git -C /x status`) is intentionally NOT broadened.
        if (i >= tokens.Length || !IsPlainWord(tokens[i]))
        {
            return string.Empty;
        }

        return $"{exe} {tokens[i]}";
    }

    private static bool IsAssignment(string token)
    {
        int eq = token.IndexOf('=');
        return eq > 0 && IsBareWord(token[..eq]);
    }

    private static bool IsBareWord(string token)
    {
        if (token.Length == 0)
        {
            return false;
        }

        foreach (var c in token)
        {
            // Reject shell metacharacters / quoting so we never widen on something we cannot reason about.
            if (c == '|' || c == '&' || c == ';' || c == '<' || c == '>' || c == '`' ||
                c == '$' || c == '(' || c == ')' || c == '"' || c == '\'')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A plain word safe to use as an executable or subcommand token: a non-empty bare word that is not a
    /// flag/option (no leading <c>-</c>) and contains no assignment (<c>=</c>), path separator (<c>/</c> or
    /// <c>\</c>), redirect, quote or other shell metacharacter.
    /// </summary>
    private static bool IsPlainWord(string token)
    {
        if (token.Length == 0 || token[0] == '-')
        {
            return false;
        }

        foreach (var c in token)
        {
            if (c == '=' || c == '/' || c == '\\' ||
                c == '|' || c == '&' || c == ';' || c == '<' || c == '>' || c == '`' ||
                c == '$' || c == '(' || c == ')' || c == '"' || c == '\'')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>DI helpers for wiring the permission system into andy-cli's service collections.</summary>
public static class CliPermissionServiceExtensions
{
    /// <summary>
    /// Wires the permission engine (authorizer + layered store + decorating executor) into the service
    /// collection. Call after <c>IToolExecutor</c> is registered. When <paramref name="interactiveBroker"/>
    /// is provided, the TUI's interactive prompt is used; otherwise the default non-interactive prompt
    /// (fail-closed, or bypass via <c>ANDY_PERMISSION_MODE</c>) handles Ask in headless/ACP contexts.
    /// </summary>
    public static IServiceCollection AddAndyCliPermissions(
        this IServiceCollection services,
        PermissionRequestBroker? interactiveBroker,
        Action<PermissionStoreOptions>? configureStore = null)
    {
        if (interactiveBroker is not null)
        {
            services.AddSingleton(interactiveBroker);
            // Resolve the prompt lazily so it can capture the IPermissionStore that AddAndyPermissions
            // registers below. The store lets a session "Allow" install a broadened command-class rule that
            // prevents re-prompting for similar invocations.
            services.AddSingleton<IPermissionPrompt>(sp =>
                new CliPermissionPrompt(interactiveBroker, sp.GetService<IPermissionStore>()));
        }

        services.AddAndyPermissions(options =>
        {
            options.WithProjectDirectory(Directory.GetCurrentDirectory());
            // Safe-by-default: mutating file tools prompt unless a higher layer allows them (reads stay
            // auto-allowed; execute_command already asks via its capability). User/project/injected rules
            // and "Allow (session)" override these.
            options.Builtin = AppendAskDefaults(options.Builtin);
            // Optional caller hook (used by tests to isolate file-backed layers for determinism).
            configureStore?.Invoke(options);
        });
        return services;
    }

    private static IReadOnlyList<PermissionRule> AppendAskDefaults(IReadOnlyList<PermissionRule> builtin)
    {
        string[] mutating = { "write_file", "delete_file", "move_file", "copy_file", "file_editor", "replace_text", "create_directory" };
        var list = new List<PermissionRule>(builtin);
        foreach (var tool in mutating)
        {
            list.Add(PermissionRule.Parse($"{tool}(*)", PermissionOutcome.Ask, PermissionLayer.Builtin));
        }

        return list;
    }

    /// <summary>
    /// AX.4 (rivoli-ai/conductor#2091): inject a per-run permission allow-list into an already-built
    /// provider's <see cref="IPermissionStore"/>, relaxing EXACTLY the listed tools to
    /// <see cref="PermissionOutcome.Allow"/>. The rules are installed via
    /// <see cref="IPermissionStore.SetInjectedRules"/>, which retags every rule to
    /// <see cref="PermissionLayer.Injected"/> (precedence 5) — strictly above the
    /// <see cref="PermissionLayer.Builtin"/> (precedence 0) "Ask" defaults that
    /// <see cref="AppendAskDefaults"/> installs, so a normally-Ask tool such as <c>write_file</c>
    /// becomes executable. A tool NOT in the list is never granted, so it stays fail-closed/denied.
    ///
    /// This is an allow-LIST, not a blanket bypass: each listed tool gets its own
    /// <c>{tool}(*)</c> Allow rule and nothing else is touched. An empty or null list is a no-op
    /// (current fail-closed defaults stand).
    /// </summary>
    /// <returns>The set of tool ids that were actually injected as Allow rules (de-duplicated).</returns>
    public static IReadOnlyList<string> ApplyInjectedAllowList(
        IServiceProvider provider,
        IReadOnlyList<string>? allowedTools)
    {
        if (allowedTools is null || allowedTools.Count == 0)
        {
            return Array.Empty<string>();
        }

        var store = provider.GetService(typeof(IPermissionStore)) as IPermissionStore;
        if (store is null)
        {
            // No store wired (e.g. permissions not registered) — nothing to relax.
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rules = new List<PermissionRule>();
        foreach (var tool in allowedTools)
        {
            if (string.IsNullOrWhiteSpace(tool) || !seen.Add(tool))
            {
                continue;
            }

            // {tool}(*) at the Injected layer: a tool-scoped Allow that overrides the Builtin Ask
            // default for this tool only. SetInjectedRules retags to PermissionLayer.Injected.
            rules.Add(PermissionRule.Parse($"{tool}(*)", PermissionOutcome.Allow, PermissionLayer.Injected));
        }

        store.SetInjectedRules(rules);
        return seen.ToList();
    }
}
