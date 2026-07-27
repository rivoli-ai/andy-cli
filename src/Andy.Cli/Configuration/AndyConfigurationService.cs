using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Andy.Permissions.Store;

namespace Andy.Cli.Configuration;

/// <summary>
/// What to load and from where. Every path is injectable so the loader can be
/// tested against synthetic home and workspace directories on any platform.
/// </summary>
public sealed class ConfigLoadRequest
{
    /// <summary>Directory the project layer is discovered from. Defaults to the process CWD.</summary>
    public string WorkspaceDirectory { get; init; } = Directory.GetCurrentDirectory();

    /// <summary>Home directory holding .andy/andy.jsonc. Defaults to the user profile.</summary>
    public string? UserHomeDirectory { get; init; }

    /// <summary>
    /// Packaged appsettings.json folded into the defaults layer. Defaults to the one
    /// next to the executable. Pass an empty string to load none.
    /// </summary>
    public string? AppSettingsPath { get; init; }

    /// <summary>Arguments the CLI layer is derived from.</summary>
    public IReadOnlyList<string> CommandLineArguments { get; init; } = Array.Empty<string>();

    /// <summary>
    /// When false, the user and project andy.jsonc files are not read at all.
    /// Set by <c>andy-cli run --headless --isolated</c> so a containerised run is
    /// reproducible from its own config file plus the environment, and cannot be
    /// altered by whatever happens to be checked into the workspace.
    /// </summary>
    public bool IncludeUserAndProjectLayers { get; init; } = true;

    /// <summary>
    /// An already-built layer inserted ABOVE the environment and BELOW the command
    /// line. Used for the headless run config, which is a different file format
    /// (headless-config.v1) translated into this schema by
    /// <c>HeadlessConfigLayer.Build</c>; keeping it as an opaque layer means the
    /// configuration service does not have to know that contract exists.
    /// </summary>
    public ConfigLayer? OverrideLayer { get; init; }

    /// <summary>
    /// Environment used both for the environment layer and for <c>{env:NAME}</c>
    /// substitution. Null means the real process environment.
    /// </summary>
    public IReadOnlyDictionary<string, string>? EnvironmentOverride { get; init; }

    internal string ResolvedWorkspace => Path.GetFullPath(
        string.IsNullOrWhiteSpace(WorkspaceDirectory)
            ? Directory.GetCurrentDirectory()
            : WorkspaceDirectory);

    internal string ResolvedHome => string.IsNullOrWhiteSpace(UserHomeDirectory)
        ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        : Path.GetFullPath(UserHomeDirectory);

    internal string ResolvedAppSettings => AppSettingsPath
        ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    internal Func<string, string?> Lookup => EnvironmentOverride is null
        ? Environment.GetEnvironmentVariable
        : name => EnvironmentOverride.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// The single entry point for Andy configuration.
///
/// Loads packaged defaults, the user file, the project files, the environment and
/// the command line; substitutes <c>{env:NAME}</c>; validates each layer against the
/// versioned schema; merges with per-field semantics while recording provenance;
/// resolves relative paths against their declaring file; and binds the result to
/// typed options. Interactive, headless and ACP mode all read from the same
/// <see cref="Shared"/> instance, so no mode can drift from another.
/// </summary>
public sealed class AndyConfigurationService
{
    private static readonly JsonSerializerOptions s_bindOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static EffectiveConfiguration? s_shared;
    private static readonly object s_sharedGate = new();

    /// <summary>
    /// The process-wide configuration, loaded on first use. Call
    /// <see cref="InitializeShared"/> from the entry point to control the workspace
    /// and arguments it is built from.
    /// </summary>
    public static EffectiveConfiguration Shared
    {
        get
        {
            if (s_shared is not null)
            {
                return s_shared;
            }
            lock (s_sharedGate)
            {
                return s_shared ??= new AndyConfigurationService().Load(new ConfigLoadRequest());
            }
        }
    }

    /// <summary>
    /// Loads the process-wide configuration once. Later calls return the first
    /// result unless <paramref name="force"/> is set, so a mode that starts up in
    /// several stages cannot end up with two different views.
    /// </summary>
    public static EffectiveConfiguration InitializeShared(ConfigLoadRequest request, bool force = false)
    {
        lock (s_sharedGate)
        {
            if (s_shared is null || force)
            {
                s_shared = new AndyConfigurationService().Load(request);
            }
            return s_shared;
        }
    }

    /// <summary>Drops the cached configuration. Test-only.</summary>
    internal static void ResetShared()
    {
        lock (s_sharedGate)
        {
            s_shared = null;
        }
    }

    public EffectiveConfiguration Load(ConfigLoadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var workspace = request.ResolvedWorkspace;
        var diagnostics = new List<ConfigDiagnostic>();
        var secretValues = new HashSet<string>(StringComparer.Ordinal);
        var layers = new List<ConfigLayer>();
        var sources = new List<ConfigSource>();

        var defaults = ConfigLayerBuilder.BuildPackagedDefaults(
            request.ResolvedAppSettings, workspace, diagnostics);
        layers.Add(defaults);
        sources.Add(defaults.Source);

        if (request.IncludeUserAndProjectLayers)
        {
            foreach (var (kind, path) in ConfigLayerBuilder.DiscoverFiles(request.ResolvedHome, workspace))
            {
                var layer = TryLoadFile(kind, path, diagnostics);
                if (layer is null)
                {
                    continue;
                }
                layers.Add(layer);
                sources.Add(layer.Source);
            }
        }

        var environment = ConfigLayerBuilder.BuildEnvironment(request.Lookup, workspace, secretValues);
        layers.Add(environment);
        sources.Add(environment.Source);

        if (request.OverrideLayer is { } overrideLayer)
        {
            layers.Add(overrideLayer);
            sources.Add(overrideLayer.Source);
        }

        var commandLine = ConfigLayerBuilder.BuildCommandLine(
            request.CommandLineArguments.ToArray(), workspace);
        layers.Add(commandLine);
        sources.Add(commandLine.Source);

        foreach (var layer in layers)
        {
            ConfigSubstitution.Apply(layer, request.Lookup, diagnostics, secretValues);
            ConfigSchemaValidator.Validate(layer, diagnostics);
        }

        var provenance = new Dictionary<string, ConfigOrigin>(StringComparer.Ordinal);
        var merged = ConfigMerge.Merge(layers, provenance);

        ConfigPathResolver.Resolve(merged, provenance, workspace, diagnostics);

        var config = Bind(merged, defaults.Source, diagnostics);
        ApplyComputedDefaults(config, request);

        return new EffectiveConfiguration
        {
            Config = config,
            Merged = merged,
            Provenance = provenance,
            Sources = sources,
            Diagnostics = diagnostics,
            SecretValues = secretValues,
            ResolvedPaths = BuildResolvedPaths(config, request, workspace),
            WorkspaceDirectory = workspace,
        };
    }

    private static ConfigLayer? TryLoadFile(
        ConfigSourceKind kind,
        string path,
        ICollection<ConfigDiagnostic> diagnostics)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var source = ConfigSource.File(kind, path);

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(ConfigDiagnostic.Error(
                ConfigDiagnosticCodes.UnreadableFile, source, $"could not be read: {ex.Message}"));
            return null;
        }

        try
        {
            var document = JsoncDocument.Parse(text);
            return new ConfigLayer { Source = source, Root = document.Root, Document = document };
        }
        catch (JsoncParseException ex)
        {
            diagnostics.Add(ConfigDiagnostic.Error(
                ConfigDiagnosticCodes.InvalidJson,
                source,
                $"is not valid JSONC: {ex.Message}",
                keyPath: string.Empty,
                line: ex.Line,
                column: ex.Column));
            return null;
        }
    }

    private static AndyConfiguration Bind(
        JsonObject merged,
        ConfigSource fallbackSource,
        ICollection<ConfigDiagnostic> diagnostics)
    {
        try
        {
            return merged.Deserialize<AndyConfiguration>(s_bindOptions) ?? new AndyConfiguration();
        }
        catch (JsonException ex)
        {
            // The schema pass should have caught anything structural; reaching here
            // means a type the schema allows but the binder cannot coerce.
            diagnostics.Add(ConfigDiagnostic.Error(
                ConfigDiagnosticCodes.SemanticError,
                fallbackSource,
                $"the merged configuration could not be bound to typed options: {ex.Message}"));
            return new AndyConfiguration();
        }
    }

    private static void ApplyComputedDefaults(AndyConfiguration config, ConfigLoadRequest request)
    {
        config.Session.Directory ??= Path.Combine(request.ResolvedHome, ".andy", "sessions");

        foreach (var server in config.Mcp.Servers.Values)
        {
            server.Transport = server.Transport?.Trim().ToLowerInvariant();
        }
    }

    /// <summary>
    /// Locations Andy computes rather than reads. The permission rule files stay in
    /// their own security format on purpose (rivoli-ai/andy-cli#280 keeps that
    /// boundary), but their whereabouts must not be a mystery, so they are reported
    /// here and printed by <c>config show --effective</c>.
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildResolvedPaths(
        AndyConfiguration config,
        ConfigLoadRequest request,
        string workspace)
    {
        var options = new PermissionStoreOptions().WithProjectDirectory(workspace);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workspace"] = workspace,
            ["config.user"] = Path.Combine(request.ResolvedHome, ".andy", ConfigSchema.FileName),
            ["config.project"] = Path.Combine(workspace, ConfigSchema.FileName),
            ["config.projectDotAndy"] = Path.Combine(workspace, ".andy", ConfigSchema.FileName),
            ["session.directory"] = config.Session.Directory
                ?? Path.Combine(request.ResolvedHome, ".andy", "sessions"),
            ["permissions.user"] = options.UserFilePath ?? PermissionStoreOptions.DefaultUserFilePath(),
            ["permissions.project"] = options.ProjectFilePath
                ?? Path.Combine(workspace, ".andy", "permissions.json"),
            ["permissions.local"] = options.LocalFilePath
                ?? Path.Combine(workspace, ".andy", "permissions.local.json"),
            ["mcp.projectFile"] = Path.Combine(workspace, ".andy", "mcp-servers.json"),
        };
        return paths;
    }
}
