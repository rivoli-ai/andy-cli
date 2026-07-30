using System;
using System.Collections.Generic;
using System.Linq;
using Andy.Tools.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services.Formatting;

/// <summary>
/// Builds the shared post-mutation pipeline and publishes it as the session's ambient default.
///
/// The pipeline is normally passed explicitly into <see cref="UiUpdatingToolExecutor"/>. The
/// ambient slot exists because the executor is constructed deep inside
/// <see cref="SimpleAssistantService"/>, which is built in five places and takes no service
/// provider; threading a new parameter through all of them would churn the hottest file in the
/// repo for no behavioural gain. This follows the convention already used by
/// <c>ToolExecutionTracker.Instance</c> and <c>WorkingDirectoryTracker.Instance</c>.
/// </summary>
public static class PostMutationPipelineFactory
{
    /// <summary>
    /// The session's pipeline, or null before <see cref="ConfigureAmbient"/> runs (headless, tests,
    /// and any host that never configures one). A null ambient means the executor falls back to
    /// <see cref="PostMutationPipeline.DiffOnly"/> - diff computed from disk, no formatters.
    /// </summary>
    public static PostMutationPipeline? Ambient { get; private set; }

    /// <summary>The catalog behind <see cref="Ambient"/>, for <c>/formatters status</c>.</summary>
    public static FormatterCatalog? AmbientCatalog { get; private set; }

    /// <summary>
    /// Build the pipeline for a project and install it as the ambient default. Safe to call more
    /// than once; the last call wins.
    /// </summary>
    public static PostMutationPipeline ConfigureAmbient(
        IServiceProvider? services,
        string projectRoot,
        ILoggerFactory? loggerFactory = null)
    {
        var catalog = FormatterCatalog.ForProject(projectRoot);
        var pipeline = Create(services, catalog, loggerFactory);
        AmbientCatalog = catalog;
        Ambient = pipeline;
        return pipeline;
    }

    /// <summary>Reset the ambient slot (used by tests so one test cannot leak into another).</summary>
    public static void ResetAmbient()
    {
        Ambient = null;
        AmbientCatalog = null;
    }

    /// <summary>
    /// Compose a pipeline from a catalog and the host's services.
    ///
    /// Any <see cref="IPostMutationStep"/> registered in DI is picked up and ordered by
    /// <see cref="IPostMutationStep.Order"/> - that is how #276 (snapshot finalization) and #282
    /// (LSP notification) attach themselves without touching this class.
    /// </summary>
    public static PostMutationPipeline Create(
        IServiceProvider? services,
        FormatterCatalog catalog,
        ILoggerFactory? loggerFactory = null)
    {
        var logger = loggerFactory?.CreateLogger("Andy.Cli.Formatting");
        var runner = new FormatterRunner(
            catalog,
            new FormatterProcessRunner(),
            ResolvePermissionGate(services),
            logger);

        var steps = services?.GetService<IEnumerable<IPostMutationStep>>()?.ToArray()
            ?? Array.Empty<IPostMutationStep>();

        return new PostMutationPipeline(runner, steps, logger);
    }

    /// <summary>
    /// Route formatter execution through the host's command-permission gate when one is registered.
    /// Falls back to ungated only when the host runs without the permission system at all, which
    /// matches Andy.Tools' own "no gate registered means no gating" contract.
    /// </summary>
    private static IFormatterPermissionGate ResolvePermissionGate(IServiceProvider? services)
    {
        var gate = services?.GetService<IToolPermissionGate>();
        return gate is null
            ? UngatedFormatterPermission.Instance
            : new ToolGateFormatterPermission(gate, services?.GetService<IToolRegistry>());
    }
}
