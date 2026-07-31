using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Lsp;

/// <summary>
/// Asks a language server what it thinks of a file that a tool just changed.
///
/// This is the seam the tool layer talks to. Everything about it is defensive: it is called from
/// inside a tool execution, so no configuration mistake, missing binary, crashed server, malformed
/// message or slow analyzer may throw, block, or otherwise reach the agent loop. Every failure
/// mode becomes a bounded report with a status.
/// </summary>
public interface IFileMutationDiagnosticsReporter
{
    /// <summary>
    /// Reports diagnostics for the file at <paramref name="absolutePath"/> as it now exists on
    /// disk. Returns null when there is nothing to say at all (no server claims this file type).
    ///
    /// ORDERING (rivoli-ai/andy-cli#283): callers MUST run any post-mutation formatter before this
    /// call. The file is read here, so whatever the formatter wrote is what the server sees and
    /// what the diagnostics describe.
    /// </summary>
    Task<LspDiagnosticsReport?> ReportAsync(string absolutePath, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IFileMutationDiagnosticsReporter"/>: resolves a configured server for the
/// file, starts it lazily, synchronizes the on-disk content, and waits a bounded time for
/// diagnostics.
/// </summary>
public sealed class LspDiagnosticsService : IFileMutationDiagnosticsReporter
{
    private readonly LspServerManager _manager;
    private readonly ILogger<LspDiagnosticsService>? _logger;

    public LspDiagnosticsService(LspServerManager manager, ILogger<LspDiagnosticsService>? logger = null)
    {
        _manager = manager;
        _logger = logger;
    }

    public async Task<LspDiagnosticsReport?> ReportAsync(string absolutePath, CancellationToken cancellationToken)
    {
        try
        {
            return await ReportCoreAsync(absolutePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Belt and braces. Diagnostics are an enhancement to a tool call that already succeeded;
            // they must never be the reason it fails.
            _logger?.LogWarning(ex, "[LSP] Diagnostics for {Path} failed", absolutePath);
            return null;
        }
    }

    private async Task<LspDiagnosticsReport?> ReportCoreAsync(string absolutePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return null;

        var definition = _manager.ResolveDefinition(absolutePath);
        if (definition is null) return null;

        // Containment check first: a server must never be pointed at a path outside the workspace
        // unless the workspace configuration explicitly allows it.
        if (!_manager.Configuration.AllowOutsideWorkspace &&
            !LspWorkspaceGuard.IsWithinWorkspace(_manager.WorkspaceRoot, absolutePath))
        {
            return LspDiagnosticsReport.Unavailable(
                definition.Id,
                absolutePath,
                LspDiagnosticsStatus.OutsideWorkspace,
                "file is outside the active workspace; set allowOutsideWorkspace in .andy/lsp-servers.json to permit it");
        }

        string text;
        try
        {
            var info = new FileInfo(absolutePath);
            if (!info.Exists)
            {
                return LspDiagnosticsReport.Unavailable(
                    definition.Id, absolutePath, LspDiagnosticsStatus.Skipped, "file no longer exists");
            }

            if (info.Length > LspLimits.MaxSyncedFileBytes)
            {
                return LspDiagnosticsReport.Unavailable(
                    definition.Id,
                    absolutePath,
                    LspDiagnosticsStatus.Skipped,
                    $"file is larger than {LspLimits.MaxSyncedFileBytes / 1024} KB");
            }

            text = await File.ReadAllTextAsync(absolutePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LspDiagnosticsReport.Unavailable(
                definition.Id, absolutePath, LspDiagnosticsStatus.Skipped, $"file could not be read ({ex.Message})");
        }

        var root = _manager.Configuration.AllowOutsideWorkspace
            ? LspWorkspaceGuard.FindProjectRoot(_manager.WorkspaceRoot, absolutePath, definition.RootMarkers)
                ?? Path.GetDirectoryName(Path.GetFullPath(absolutePath))
                ?? _manager.WorkspaceRoot
            : LspWorkspaceGuard.FindProjectRoot(_manager.WorkspaceRoot, absolutePath, definition.RootMarkers)
                ?? _manager.WorkspaceRoot;

        var client = await _manager.GetOrStartAsync(definition, root, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            var detail = DescribeStartFailure(definition.Id, root);
            return LspDiagnosticsReport.Unavailable(
                definition.Id, absolutePath, LspDiagnosticsStatus.ServerUnavailable, detail);
        }

        var (status, diagnostics, waitDetail) = await client.SyncAndWaitForDiagnosticsAsync(
            absolutePath,
            text,
            TimeSpan.FromMilliseconds(definition.DiagnosticsTimeoutMs),
            cancellationToken).ConfigureAwait(false);

        return status == LspDiagnosticsStatus.Received
            ? LspDiagnosticsReport.Bounded(definition.Id, absolutePath, status, diagnostics)
            : LspDiagnosticsReport.Unavailable(definition.Id, absolutePath, status, waitDetail ?? status.ToString());
    }

    private string DescribeStartFailure(string serverId, string root)
    {
        foreach (var status in _manager.GetStatuses())
        {
            if (string.Equals(status.ServerId, serverId, StringComparison.OrdinalIgnoreCase) &&
                status.Detail is not null)
            {
                return status.Detail;
            }
        }

        return $"language server '{serverId}' could not be started for {root}";
    }
}
