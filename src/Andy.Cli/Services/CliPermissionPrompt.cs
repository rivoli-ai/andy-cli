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
    /// Grant granularity for a session "Allow": tool id + command class, where the command class is the
    /// executable leaf name plus its first subcommand token (e.g. <c>gh pr</c>, <c>git status</c>,
    /// <c>npm run</c>; a command with no subcommand stays at the executable, e.g. <c>ls</c>). The rule is
    /// session-scoped only (in-memory, lost on exit) and applies only to the same tool.
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
    /// Reduces a command string to its command class: the executable leaf name plus the first
    /// subcommand-like token. Leading <c>VAR=value</c> assignments are skipped; the first token that looks
    /// like a flag/option (starts with <c>-</c>) or is not a bare word terminates the class. Returns an empty
    /// string when no usable executable token is found.
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

        if (i >= tokens.Length || !IsBareWord(tokens[i]) || tokens[i].StartsWith("-", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var exe = LeafName(tokens[i]);
        i++;

        // Append the first subcommand token if it is a bare word (not a flag/option/path/redirection).
        if (i < tokens.Length && IsBareWord(tokens[i]) && !tokens[i].StartsWith("-", StringComparison.Ordinal))
        {
            return $"{exe} {tokens[i]}";
        }

        return exe;
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

    private static string LeafName(string path)
    {
        int slash = path.LastIndexOfAny(new[] { '/', '\\' });
        return slash >= 0 ? path[(slash + 1)..] : path;
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
