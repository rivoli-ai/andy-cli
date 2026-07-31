using System;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Commands;
using Andy.Cli.Lsp;
using Xunit;

namespace Andy.Cli.Tests.Lsp;

/// <summary>
/// Phase 2 of issue #282: /lsp status and /lsp restart, and the requirement that a startup or
/// crash failure is ACTIONABLE - the command line that was tried and the server's own words.
/// </summary>
public sealed class LspCommandTests
{
    private static LspCommand CommandFor(LspSession session) => new(() => session);

    [Fact]
    public async Task StatusWithNothingConfiguredExplainsHowToConfigure()
    {
        var command = new LspCommand(() => new LspSession());

        var result = await command.ExecuteAsync(new[] { "status" });

        Assert.True(result.Success);
        Assert.Contains("No language servers configured", result.Message);
        Assert.Contains(".andy/lsp-servers.json", result.Message);
        Assert.Contains("docs/lsp-diagnostics.md", result.Message);
    }

    [Fact]
    public async Task StatusListsConfiguredServersBeforeTheyStart()
    {
        using var workspace = new LspTestWorkspace();
        await using var manager = workspace.Manager(LspTestWorkspace.Definition());
        var session = LspSession.Install(manager, manager.Configuration);

        var result = await CommandFor(session).ExecuteAsync(Array.Empty<string>());

        Assert.Contains("[idle] fake", result.Message);
        Assert.Contains("extensions: .fake", result.Message);
        Assert.Contains("start on the first matching file change", result.Message);

        await LspSession.ResetAsync();
    }

    [Fact]
    public async Task StatusShowsARunningServerAndItsRoot()
    {
        using var workspace = new LspTestWorkspace();
        await using var manager = workspace.Manager(LspTestWorkspace.Definition());
        var service = new LspDiagnosticsService(manager);
        await service.ReportAsync(workspace.WriteFile("a.fake", "an ERROR here\n"), CancellationToken.None);

        var session = LspSession.Install(manager, manager.Configuration);
        var result = await CommandFor(session).ExecuteAsync(new[] { "status" });

        Assert.Contains("[running] fake", result.Message);
        Assert.Contains(workspace.Root, result.Message);

        await LspSession.ResetAsync();
    }

    [Fact]
    public async Task StatusExplainsAStartupFailureInTermsTheUserCanActOn()
    {
        using var workspace = new LspTestWorkspace();
        var definition = new LspServerDefinition
        {
            Id = "missing",
            Command = "andy-no-such-language-server",
            Extensions = new[] { ".fake" },
            StartTimeoutMs = 2000,
            DiagnosticsTimeoutMs = 500,
        };
        var configuration = new LspConfigurationLoadResult(
            new[] { definition }, new[] { "example error" }, Array.Empty<string>());
        await using var manager = new LspServerManager(configuration, workspace.Root);
        var service = new LspDiagnosticsService(manager);
        await service.ReportAsync(workspace.WriteFile("a.fake", "x"), CancellationToken.None);

        var session = LspSession.Install(manager, configuration);
        var result = await CommandFor(session).ExecuteAsync(new[] { "status" });

        Assert.Contains("[failed] missing", result.Message);
        Assert.Contains("andy-no-such-language-server", result.Message);
        Assert.Contains("Configuration errors:", result.Message);
        Assert.Contains("example error", result.Message);

        await LspSession.ResetAsync();
    }

    [Fact]
    public async Task RestartStopsRunningServersAndClearsRememberedFailures()
    {
        using var workspace = new LspTestWorkspace();
        await using var manager = workspace.Manager(LspTestWorkspace.Definition());
        var service = new LspDiagnosticsService(manager);
        var path = workspace.WriteFile("a.fake", "an ERROR here\n");
        await service.ReportAsync(path, CancellationToken.None);

        Assert.Single(workspace.Transports);
        var session = LspSession.Install(manager, manager.Configuration);

        var result = await CommandFor(session).ExecuteAsync(new[] { "restart" });

        Assert.True(result.Success);
        Assert.Contains("stopped 1", result.Message);
        Assert.True(workspace.Transports[0].HasExited);

        // The next mutation brings up a fresh server.
        var report = await service.ReportAsync(path, CancellationToken.None);
        Assert.Equal(LspDiagnosticsStatus.Received, report!.Status);
        Assert.Equal(2, workspace.Transports.Count);

        await LspSession.ResetAsync();
    }

    [Fact]
    public async Task RestartWithNoServersIsHarmless()
    {
        var command = new LspCommand(() => new LspSession());
        var result = await command.ExecuteAsync(new[] { "restart" });

        Assert.True(result.Success);
        Assert.Contains("nothing to restart", result.Message);
    }

    [Fact]
    public async Task UnknownSubcommandFails()
    {
        var command = new LspCommand(() => new LspSession());
        var result = await command.ExecuteAsync(new[] { "frobnicate" });

        Assert.False(result.Success);
        Assert.Contains("Unknown lsp subcommand", result.Message);
    }

    [Fact]
    public async Task HelpDocumentsTheSubcommandsAndTheNoDownloadPolicy()
    {
        var command = new LspCommand(() => new LspSession());
        var result = await command.ExecuteAsync(new[] { "help" });

        Assert.Contains("/lsp status", result.Message);
        Assert.Contains("/lsp restart", result.Message);
        Assert.Contains("never downloads", result.Message);
    }
}
