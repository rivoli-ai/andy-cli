using System.Linq;
using Andy.Cli.Hosting;
using Andy.Tools.Core;
using Andy.Tools.Execution;
using Andy.Tools.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Andy.Cli.Tests.Hosting;

/// <summary>
/// Verifies the shared composition-root helper registers the core Andy.Tools
/// service graph and initialises the tool registry. This is the block that was
/// duplicated verbatim across the interactive, ACP and one-shot command paths.
/// </summary>
public class AppCompositionRootTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        AppCompositionRoot.AddCoreToolServices(services, permissionBroker: null);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddCoreToolServices_RegistersCoreToolServices()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetService<IToolRegistry>());
        Assert.NotNull(provider.GetService<IToolExecutor>());
        Assert.NotNull(provider.GetService<ISecurityManager>());
        Assert.NotNull(provider.GetService<IPermissionProfileService>());
    }

    [Fact]
    public void AddCoreToolServices_RegistersBuiltInTools()
    {
        using var provider = BuildProvider();

        var registrations = provider.GetServices<Andy.Tools.Framework.ToolRegistrationInfo>();
        Assert.NotEmpty(registrations);
    }

    [Fact]
    public void InitializeToolRegistry_RegistersToolsIntoRegistry()
    {
        using var provider = BuildProvider();

        var registry = AppCompositionRoot.InitializeToolRegistry(provider);

        Assert.NotNull(registry);
        Assert.Same(provider.GetRequiredService<IToolRegistry>(), registry);
        Assert.NotEmpty(registry.GetTools());
    }
}
