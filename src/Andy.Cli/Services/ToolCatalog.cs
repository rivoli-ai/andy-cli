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

        // PDF document tools (Andy.Tools.Pdf) over the fully-managed Andy.Doc engine: read a PDF and
        // extract text, reading-order reflow, tables, the outline (bookmark) tree, document info, and
        // full-text search — read-only, no code execution. AddAndyPdfTools registers each pdf_* tool as
        // a ToolRegistrationInfo, draining into the IToolRegistry like the rest. Andy.Tools.Pdf is a
        // TrimmerRootAssembly (see Andy.Cli.csproj) so the tool constructors survive trimming/AOT.
        // Useful for understanding financial filings (10-K) and earnings-call transcripts.
        Andy.Tools.Pdf.ServiceCollectionExtensions.AddAndyPdfTools(services);

        // Agent Skills (Andy.Skills via Andy.Skills.Tools). Skills are discovered from the
        // conventional roots: <workspace>/.andy/skills, then ~/.andy/skills. The catalog is
        // decorated with the CLI's persisted disable list so `/skills disable <name>` is honored
        // by the skill tools themselves (a disabled skill cannot be loaded), not just hidden
        // from listings. NOTE: the upstream AddAndySkills extension is deliberately NOT used
        // here - it registers the `skill` / `skill_file` tools as ToolRegistrationInfo entries,
        // but those tools require constructor injection (ISkillCatalog), which this app's
        // Activator-based IToolRegistry.RegisterTool(Type) path rejects. The tools instead
        // register through the registry's factory overload in
        // AppCompositionRoot.InitializeToolRegistry.
        // Andy.Skills / Andy.Skills.Tools are TrimmerRootAssembly entries (see Andy.Cli.csproj).
        var workspace = System.IO.Directory.GetCurrentDirectory();
        var skillOptions = new Andy.Skills.Tools.SkillCatalogOptions();
        foreach (var root in Andy.Skills.SkillDiscovery.DefaultRoots(workspace))
        {
            skillOptions.Roots.Add(root);
        }
        services.AddSingleton(skillOptions);
        services.AddSingleton<Andy.Skills.Tools.ISkillCatalog>(sp => new FilteredSkillCatalog(
            new Andy.Skills.Tools.SkillCatalog(sp.GetRequiredService<Andy.Skills.Tools.SkillCatalogOptions>()),
            SkillsDisableList.PathFor(workspace)));
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
