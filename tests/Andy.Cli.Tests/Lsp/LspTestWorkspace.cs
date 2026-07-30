using System;
using System.Collections.Generic;
using System.IO;
using Andy.Cli.Lsp;
using Andy.Cli.Lsp.Protocol;

namespace Andy.Cli.Tests.Lsp;

/// <summary>
/// A throwaway workspace directory plus the plumbing to point a manager at a deterministic fake
/// language server. Keeps each test to the behaviour it is actually about.
/// </summary>
public sealed class LspTestWorkspace : IDisposable
{
    private readonly List<FakeLspTransport> _transports = new();

    public LspTestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "andy-lsp-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
        // Resolve now so comparisons in the containment guard are apples-to-apples on platforms
        // where the temp directory is itself a symlink (macOS /var -> /private/var).
        Root = new DirectoryInfo(Root).FullName;
    }

    public string Root { get; }

    /// <summary>Every fake transport the manager created, in creation order.</summary>
    public IReadOnlyList<FakeLspTransport> Transports => _transports;

    public static LspServerDefinition Definition(
        string id = "fake",
        int diagnosticsTimeoutMs = 4000,
        int startTimeoutMs = 4000,
        IReadOnlyList<string>? rootMarkers = null) => new()
        {
            Id = id,
            Command = "fake-language-server",
            Extensions = new[] { ".fake" },
            RootMarkers = rootMarkers ?? Array.Empty<string>(),
            DiagnosticsTimeoutMs = diagnosticsTimeoutMs,
            StartTimeoutMs = startTimeoutMs,
        };

    public string WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public LspServerManager Manager(
        LspServerDefinition definition,
        FakeServerBehavior behavior = FakeServerBehavior.Normal,
        TimeSpan? publishDelay = null,
        bool allowOutsideWorkspace = false)
    {
        var configuration = new LspConfigurationLoadResult(
            new[] { definition },
            Array.Empty<string>(),
            new[] { "test" },
            allowOutsideWorkspace);

        return new LspServerManager(configuration, Root, (_, _) =>
        {
            var transport = new FakeLspTransport(behavior, publishDelay);
            lock (_transports)
            {
                _transports.Add(transport);
            }
            return transport;
        });
    }

    public static LspServerManager ManagerWithTransport(
        LspConfigurationLoadResult configuration,
        string root,
        Func<LspServerDefinition, string, ILspTransport> factory) =>
        new(configuration, root, factory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of a temp directory.
        }
    }
}
