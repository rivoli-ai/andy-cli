// AX.4 (rivoli-ai/conductor#2091): a per-run permission allow-list injected into the
// headless run relaxes EXACTLY the permitted tools; every other (mutating) tool stays
// fail-closed/denied; and a tool-usage audit records which tools actually ran.
//
// These tests exercise the REAL production wiring (BuildServiceProvider, which now calls
// ApplyInjectedAllowList; ToolUsageAuditor against the live IToolPermissionAuthorizer) so
// they fail against the pre-AX.4 code (no allow-list → write_file always Ask/deny; no
// audit) and pass now.

using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Headless;
using Andy.Cli.HeadlessConfig;
using Andy.Cli.Services;
using Andy.Permissions.Authorization;
using Andy.Permissions.Model;
using Andy.Tools.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Cli.Tests.Headless;

public class HeadlessPermissionAllowListTests
{
    private static HeadlessRunConfig Config(IReadOnlyList<string>? allowedTools)
        => new()
        {
            SchemaVersion = 1,
            RunId = Guid.NewGuid(),
            Agent = new HeadlessAgent { Slug = "ax4-agent", Instructions = "stub" },
            Model = new HeadlessModel { Provider = "stub", Id = "stub-1" },
            Tools = Array.Empty<HeadlessTool>(),
            Workspace = new HeadlessWorkspace { Root = System.IO.Path.GetTempPath() },
            Output = new HeadlessOutput
            {
                File = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ax4-out.txt"),
                Stream = "stdout",
            },
            Permissions = allowedTools is null ? null : new HeadlessPermissions { AllowedTools = allowedTools },
            Limits = new HeadlessLimits { MaxIterations = 4, TimeoutSeconds = 30 },
        };

    // Evaluate a tool's effective permission outcome through the SAME authorizer that
    // gates execution. Only Allow is executable in headless (no broker for Ask).
    private static PermissionOutcome Evaluate(ServiceProvider services, string toolName)
    {
        var authorizer = services.GetRequiredService<IToolPermissionAuthorizer>();
        var registry = services.GetRequiredService<IToolRegistry>();
        var metadata = registry.GetTool(toolName)?.Metadata;
        var context = new ToolAuthorizationContext(
            toolName,
            new Dictionary<string, object?>(),
            WorkingDirectory: null,
            Metadata: metadata);
        return authorizer.Evaluate(context).Outcome;
    }

    private static ServiceProvider Build(IReadOnlyList<string>? allowedTools)
    {
        var services = HeadlessAgentRunner.BuildServiceProvider(Config(allowedTools), NullLoggerFactory.Instance);
        HeadlessAgentRunner.RegisterBuiltInTools(services, NullLoggerFactory.Instance);
        return services;
    }

    [Fact]
    public void AllowListedMutatingTool_IsAllowed()
    {
        // (a) A tool in the allow-list executes (not denied). write_file is normally Ask.
        using var services = Build(new[] { "write_file" });

        Assert.Equal(PermissionOutcome.Allow, Evaluate(services, "write_file"));
    }

    [Fact]
    public void MutatingTool_NotInAllowList_StaysDenied()
    {
        // (b) A mutating tool NOT in the allow-list is denied (stays Ask → deny in headless).
        // Allow-list only relaxes write_file; delete_file must remain non-Allow.
        using var services = Build(new[] { "write_file" });

        Assert.NotEqual(PermissionOutcome.Allow, Evaluate(services, "delete_file"));
    }

    [Fact]
    public void AbsentAllowList_KeepsMutatingToolsDenied()
    {
        // (c) Absent allow-list => unchanged fail-closed behavior: write_file is NOT Allow.
        using var services = Build(allowedTools: null);

        Assert.NotEqual(PermissionOutcome.Allow, Evaluate(services, "write_file"));
    }

    [Fact]
    public void EmptyAllowList_KeepsMutatingToolsDenied()
    {
        // An explicit empty list grants nothing beyond auto-allowed read-only built-ins.
        using var services = Build(Array.Empty<string>());

        Assert.NotEqual(PermissionOutcome.Allow, Evaluate(services, "write_file"));
    }

    [Fact]
    public void AllowList_DoesNotBypass_ReadOnlyToolsStillAllowed()
    {
        // Read-only built-ins are auto-allowed regardless of the allow-list (no regression).
        using var services = Build(new[] { "write_file" });

        Assert.Equal(PermissionOutcome.Allow, Evaluate(services, "read_file"));
    }

    [Fact]
    public void ApplyInjectedAllowList_IsAllowListNotBlanketBypass()
    {
        // Injecting one tool must not relax a different unlisted mutating tool.
        using var services = Build(new[] { "write_file" });

        Assert.Equal(PermissionOutcome.Allow, Evaluate(services, "write_file"));
        Assert.NotEqual(PermissionOutcome.Allow, Evaluate(services, "move_file"));
        Assert.NotEqual(PermissionOutcome.Allow, Evaluate(services, "create_directory"));
    }

    [Fact]
    public void Audit_RecordsInvokedTools_WithPermittedStatus()
    {
        // (d) The audit records invoked tools, and "permitted" reflects the live engine.
        using var services = Build(new[] { "write_file" });
        var registry = services.GetRequiredService<IToolRegistry>();
        var authorizer = services.GetRequiredService<IToolPermissionAuthorizer>();

        var auditor = new ToolUsageAuditor();
        auditor.RecordInvocation("read_file");      // auto-allowed read-only
        auditor.RecordInvocation("write_file");     // allow-listed
        auditor.RecordInvocation("write_file");     // again (count == 2)
        auditor.RecordInvocation("delete_file");    // not in allow-list → denied

        var entries = auditor.BuildEntries(authorizer, registry);

        Assert.Equal(3, entries.Count);

        var read = entries.Single(e => e.ToolName == "read_file");
        Assert.Equal(1, read.Invocations);
        Assert.True(read.Permitted);

        var write = entries.Single(e => e.ToolName == "write_file");
        Assert.Equal(2, write.Invocations);
        Assert.True(write.Permitted);

        var delete = entries.Single(e => e.ToolName == "delete_file");
        Assert.Equal(1, delete.Invocations);
        Assert.False(delete.Permitted);
    }

    [Fact]
    public void ApplyInjectedAllowList_ReturnsDedupedInjectedToolIds()
    {
        using var services = Build(allowedTools: null);

        var injected = CliPermissionServiceExtensions.ApplyInjectedAllowList(
            services, new[] { "write_file", "write_file", "execute_command" });

        Assert.Equal(new[] { "write_file", "execute_command" }, injected);
    }
}
