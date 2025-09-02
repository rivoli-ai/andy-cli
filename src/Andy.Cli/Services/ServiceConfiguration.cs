using System;
using Andy.Llm.Extensions;
using Andy.Tools.Core;
using Andy.Tools.Execution;
using Andy.Tools.Framework;
using Andy.Tools.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services;

/// <summary>
/// Handles service registration and dependency injection configuration
/// </summary>
public static class ServiceConfiguration
{
    /// <summary>
    /// Configures all services for the application
    /// </summary>
    public static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        
        // Add logging
        services.AddLogging();
        
        // Configure LLM services
        services.ConfigureLlmFromEnvironment();
        services.AddLlmServices(options =>
        {
            options.DefaultProvider = "cerebras";
        });
        
        // Configure Tool services
        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddSingleton<IToolExecutor, ToolExecutor>();
        services.AddSingleton<ISecurityManager, SecurityManager>();
        services.AddSingleton<IPermissionProfileService, PermissionProfileService>();
        
        // Register built-in tools
        services.AddBuiltInTools();
        
        var serviceProvider = services.BuildServiceProvider();
        
        // Initialize tool registry and register tools
        var toolRegistry = serviceProvider.GetRequiredService<IToolRegistry>();
        var toolRegistrations = serviceProvider.GetServices<ToolRegistrationInfo>();
        foreach (var registration in toolRegistrations)
        {
            toolRegistry.RegisterTool(registration.ToolType, registration.Configuration);
        }
        
        return serviceProvider;
    }
}