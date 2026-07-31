using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Lsp;

/// <summary>
/// The process-wide language-server session for the interactive CLI.
///
/// Why an ambient holder rather than constructor injection: the tool executor that needs this sits
/// several layers below where the session is built (Program -> SimpleAssistantService ->
/// UiUpdatingToolExecutor), and that chain is rebuilt on every /restart and model switch. The CLI
/// already uses this shape for the two other pieces of session state the tool layer needs
/// (<see cref="Services.ToolExecutionTracker"/>, <see cref="Services.WorkingDirectoryTracker"/>),
/// so this follows the established pattern instead of threading a new parameter through hot files.
///
/// The holder is inert until <see cref="Start"/> is called, which is what makes the whole feature
/// no-op-by-default: with no configured servers there is no manager, no reporter, and no processes.
/// </summary>
public sealed class LspSession : IAsyncDisposable
{
    private static LspSession _instance = new();

    private LspServerManager? _manager;
    private LspDiagnosticsService? _reporter;

    /// <summary>The active session. Never null; simply carries nothing until started.</summary>
    public static LspSession Instance => Volatile.Read(ref _instance);

    /// <summary>Diagnostics reporter for the tool layer, or null when no server is configured.</summary>
    public IFileMutationDiagnosticsReporter? Reporter => _reporter;

    /// <summary>The manager backing this session, or null when nothing is configured.</summary>
    public LspServerManager? Manager => _manager;

    /// <summary>Configuration problems worth showing in <c>/lsp status</c>.</summary>
    public LspConfigurationLoadResult Configuration { get; private set; } = LspConfigurationLoadResult.Empty;

    /// <summary>
    /// Builds a session for <paramref name="workspaceRoot"/> and installs it as
    /// <see cref="Instance"/>. Safe to call when nothing is configured: the result is a session
    /// that reports "no servers configured" and never launches anything.
    /// </summary>
    public static LspSession Start(
        IConfiguration? configuration,
        string workspaceRoot,
        ILoggerFactory? loggerFactory = null)
    {
        LspConfigurationLoadResult loaded;
        try
        {
            loaded = LspConfigurationLoader.Load(configuration, workspaceRoot);
        }
        catch (Exception ex)
        {
            loggerFactory?.CreateLogger<LspSession>()
                .LogWarning(ex, "[LSP] Could not load language server configuration");
            loaded = LspConfigurationLoadResult.Empty;
        }

        var session = new LspSession { Configuration = loaded };

        if (loaded.Servers.Count > 0)
        {
            session._manager = new LspServerManager(loaded, workspaceRoot, transportFactory: null, loggerFactory);
            session._reporter = new LspDiagnosticsService(
                session._manager,
                loggerFactory?.CreateLogger<LspDiagnosticsService>());
        }

        Volatile.Write(ref _instance, session);
        return session;
    }

    /// <summary>Installs an already-built session (used by tests and by embedded hosts).</summary>
    public static LspSession Install(LspServerManager manager, LspConfigurationLoadResult configuration)
    {
        var session = new LspSession
        {
            Configuration = configuration,
            _manager = manager,
            _reporter = new LspDiagnosticsService(manager),
        };
        Volatile.Write(ref _instance, session);
        return session;
    }

    /// <summary>Restores the inert session, disposing whatever was installed.</summary>
    public static async Task ResetAsync()
    {
        var previous = Interlocked.Exchange(ref _instance, new LspSession());
        await previous.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        var manager = _manager;
        _manager = null;
        _reporter = null;
        if (manager is not null)
        {
            await manager.DisposeAsync().ConfigureAwait(false);
        }
    }
}
