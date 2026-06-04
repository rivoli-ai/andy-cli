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
/// </summary>
public sealed class CliPermissionPrompt : IPermissionPrompt
{
    private readonly PermissionRequestBroker _broker;

    public CliPermissionPrompt(PermissionRequestBroker broker) => _broker = broker;

    public Task<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken cancellationToken = default)
    {
        var pending = new PendingPermissionRequest { Request = request };
        cancellationToken.Register(() => pending.Completion.TrySetResult(PermissionDecision.DenyOnce));
        _broker.Enqueue(pending);
        return pending.Completion.Task;
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
    public static IServiceCollection AddAndyCliPermissions(this IServiceCollection services, PermissionRequestBroker? interactiveBroker)
    {
        if (interactiveBroker is not null)
        {
            services.AddSingleton(interactiveBroker);
            services.AddSingleton<IPermissionPrompt>(new CliPermissionPrompt(interactiveBroker));
        }

        services.AddAndyPermissions(options =>
        {
            options.WithProjectDirectory(Directory.GetCurrentDirectory());
            // Safe-by-default: mutating file tools prompt unless a higher layer allows them (reads stay
            // auto-allowed; execute_command already asks via its capability). User/project/injected rules
            // and "Allow (session)" override these.
            options.Builtin = AppendAskDefaults(options.Builtin);
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
}
