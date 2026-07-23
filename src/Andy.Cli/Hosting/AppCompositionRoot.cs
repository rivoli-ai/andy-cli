using System.Collections.Generic;
using Andy.Cli.Services;
using Andy.Tools.Core;
using Andy.Tools.Execution;
using Andy.Tools.Framework;
using Andy.Tools.Registry;
using Microsoft.Extensions.DependencyInjection;

namespace Andy.Cli.Hosting;

/// <summary>
/// Single composition-root helper for the CLI's tool service graph.
///
/// The interactive TUI path, the ACP server path and the one-shot command path
/// all registered the same block of Andy.Tools services and then ran the same
/// tool-registry initialisation loop. That block was duplicated verbatim in three
/// places in <c>Program.cs</c> (differing only by the permission broker argument).
/// This type centralises the two operations so the three entry points share one
/// definition. It is a thin, behaviour-preserving wrapper: the registrations,
/// their lifetimes, and the initialisation loop are identical to the originals.
/// </summary>
public static class AppCompositionRoot
{
    /// <summary>
    /// Registers the core Andy.Tools service graph used by every CLI mode:
    /// validator, registry, discovery, security manager, resource monitor,
    /// output limiter, executor, permission profile service and lifecycle
    /// manager; wires tool execution through the permission engine; sets the
    /// framework options; and registers all built-in tools via the trim-safe
    /// <see cref="ToolCatalog"/>.
    /// </summary>
    /// <param name="services">The service collection to populate.</param>
    /// <param name="permissionBroker">
    /// The broker used to surface interactive permission prompts (interactive TUI),
    /// or <c>null</c> for non-interactive modes (ACP / one-shot command) which
    /// fail closed / bypass via environment configuration.
    /// </param>
    public static IServiceCollection AddCoreToolServices(
        IServiceCollection services,
        PermissionRequestBroker? permissionBroker)
    {
        // Core services from AddAndyTools, registered manually to avoid the
        // HostedService requirement.
        services.AddSingleton<Andy.Tools.Validation.IToolValidator, Andy.Tools.Validation.ToolValidator>();
        services.AddSingleton<IToolRegistry, Andy.Tools.Registry.ToolRegistry>();
        services.AddSingleton<Andy.Tools.Discovery.IToolDiscovery, Andy.Tools.Discovery.ToolDiscoveryService>();
        services.AddSingleton<Andy.Tools.Execution.ISecurityManager, Andy.Tools.Execution.SecurityManager>();
        services.AddSingleton<Andy.Tools.Execution.IResourceMonitor, Andy.Tools.Execution.ResourceMonitor>();
        services.AddSingleton<Andy.Tools.Core.OutputLimiting.IToolOutputLimiter, Andy.Tools.Core.OutputLimiting.ToolOutputLimiter>();
        services.AddSingleton<IToolExecutor, ToolExecutor>();
        services.AddSingleton<Andy.Tools.Core.IPermissionProfileService, Andy.Tools.Core.PermissionProfileService>();
        services.AddSingleton<Andy.Tools.Framework.IToolLifecycleManager, Andy.Tools.Framework.ToolLifecycleManager>();

        // Gate tool execution through the permission engine. A non-null broker
        // drives the interactive prompt; null fails closed / bypasses via env.
        services.AddAndyCliPermissions(permissionBroker);

        // Framework options: built-in tools are registered separately below.
        services.AddSingleton(new Andy.Tools.Framework.ToolFrameworkOptions
        {
            RegisterBuiltInTools = false,
            EnableObservability = false,
            AutoDiscoverTools = false
        });

        // Register all tools using the trim-safe ToolCatalog.
        ToolCatalog.RegisterAllTools(services);

        return services;
    }

    /// <summary>
    /// Resolves the tool registry and registers every discovered
    /// <see cref="ToolRegistrationInfo"/> with it. Mirrors the initialisation
    /// loop that previously followed <c>BuildServiceProvider</c> in each mode.
    /// </summary>
    /// <returns>The initialised <see cref="IToolRegistry"/>.</returns>
    public static IToolRegistry InitializeToolRegistry(System.IServiceProvider serviceProvider)
    {
        var toolRegistry = serviceProvider.GetRequiredService<IToolRegistry>();
        var toolRegistrations = serviceProvider.GetServices<ToolRegistrationInfo>();
        foreach (var registration in toolRegistrations)
        {
            toolRegistry.RegisterTool(registration.ToolType, registration.Configuration);
        }

        RegisterSkillTools(serviceProvider, toolRegistry);

        return toolRegistry;
    }

    /// <summary>
    /// Registers the Agent Skills tools (`skill` and `skill_file`). Unlike the built-in tools
    /// above, these require constructor injection (the shared <c>ISkillCatalog</c>), which the
    /// Activator-based <see cref="IToolRegistry.RegisterTool(System.Type, Dictionary{string, object?})"/>
    /// path rejects (it demands a parameterless constructor), so they go through the registry's
    /// factory overload instead. No-op when no skill catalog is registered.
    /// </summary>
    private static void RegisterSkillTools(System.IServiceProvider serviceProvider, IToolRegistry toolRegistry)
    {
        var skillCatalog = serviceProvider.GetService<Andy.Skills.Tools.ISkillCatalog>();
        if (skillCatalog == null)
        {
            return;
        }

        toolRegistry.RegisterTool(
            new Andy.Skills.Tools.SkillTool(skillCatalog).Metadata,
            sp => new Andy.Skills.Tools.SkillTool(
                sp.GetRequiredService<Andy.Skills.Tools.ISkillCatalog>()),
            new Dictionary<string, object?>());

        toolRegistry.RegisterTool(
            new Andy.Skills.Tools.SkillResourceTool(skillCatalog).Metadata,
            sp => new Andy.Skills.Tools.SkillResourceTool(
                sp.GetRequiredService<Andy.Skills.Tools.ISkillCatalog>()),
            new Dictionary<string, object?>());
    }
}
