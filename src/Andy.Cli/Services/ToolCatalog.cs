using System.Diagnostics.CodeAnalysis;
using Andy.Tools;
using Andy.Tools.Core;
using Andy.Tools.Framework;
using Andy.Tools.Library;
using Andy.Tools.Library.FileSystem;
using Andy.Tools.Library.Git;
using Andy.Tools.Library.System;
using Andy.Tools.Library.Text;
using Andy.Tools.Library.Utilities;
using Andy.Tools.Library.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Andy.Cli.Services;

/// <summary>
/// Centralized catalog for explicit tool registration.
/// This approach is trim-safe and provides clear visibility of all registered tools.
/// </summary>
public static class ToolCatalog
{
    /// <summary>
    /// Registers all built-in and custom tools with the service collection.
    /// This replaces the dynamic AddBuiltInTools() method with explicit registration.
    /// </summary>
    public static void RegisterAllTools(IServiceCollection services)
    {
        // FileSystem tools
        RegisterTool<CopyFileTool>(services);
        RegisterTool<DeleteFileTool>(services);
        RegisterTool<ListDirectoryTool>(services);
        RegisterTool<MoveFileTool>(services);
        RegisterTool<ReadFileTool>(services);
        RegisterTool<WriteFileTool>(services);

        // Git tools
        RegisterTool<GitDiffTool>(services);

        // System tools
        RegisterTool<ProcessInfoTool>(services);
        RegisterTool<SystemInfoTool>(services);
        RegisterTool<ExecuteCommandTool>(services);

        // Text processing tools
        RegisterTool<FormatTextTool>(services);
        RegisterTool<ReplaceTextTool>(services);
        RegisterTool<SearchTextTool>(services);

        // Utility tools
        RegisterTool<DateTimeTool>(services);
        RegisterTool<EncodingTool>(services);

        // Web tools
        RegisterTool<HttpRequestTool>(services);
        RegisterTool<JsonProcessorTool>(services);

        // Todo management
        RegisterTool<TodoManagementTool>(services);

        // Custom CLI tools
        RegisterTool<Andy.Cli.Tools.CreateDirectoryTool>(services);
        // BashCommandTool retired: it was display-only (never executed). Real shell execution is now
        // provided by Andy.Tools' ExecuteCommandTool (id "execute_command"), gated by the permission layer.
        RegisterTool<Andy.Cli.Tools.CodeIndexTool>(services);

        // Dataframe tools (Andy.Tools.Data) over an embedded in-memory DuckDB engine: load
        // (CSV/JSON/Parquet/Delta), inspect, transform, aggregate, join, reshape, and export tabular
        // data with no SQL or code execution. AddAndyDataFrameTools registers IDuckDbBackend +
        // IDatasetCatalog (singletons) and each dataframe_* tool as a ToolRegistrationInfo — the same
        // mechanism RegisterTool uses above — so they drain into the IToolRegistry alongside the rest.
        // Andy.Tools.Data is a TrimmerRootAssembly (see Andy.Cli.csproj) so the tool constructors
        // survive trimming/AOT. Path-bearing tools resolve an optional IPathPolicy from DI if present.
        Andy.Tools.Data.ServiceCollectionExtensions.AddAndyDataFrameTools(services);
    }

    /// <summary>
    /// Registers a single tool with empty configuration.
    /// The DynamicallyAccessedMembers attribute ensures the trimmer preserves
    /// the constructor metadata needed for dependency injection.
    /// </summary>
    private static void RegisterTool<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        TTool>(IServiceCollection services)
        where TTool : class
    {
        services.AddSingleton(new ToolRegistrationInfo
        {
            ToolType = typeof(TTool),
            Configuration = new Dictionary<string, object?>()
        });
    }
}
