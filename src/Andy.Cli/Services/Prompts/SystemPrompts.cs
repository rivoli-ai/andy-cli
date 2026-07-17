using System.Runtime.InteropServices;

namespace Andy.Cli.Services.Prompts;

/// <summary>
/// Provides pre-configured system prompts for different scenarios.
/// </summary>
/// <remarks>
/// System-prompt composition status (issue #174):
/// All non-headless modes compose their prompt through this helper (the shared
/// <see cref="SystemPromptBuilder"/> pipeline), but they still enter it via different methods:
/// <list type="bullet">
///   <item>Interactive TUI (Program.BuildSystemPrompt) uses <see cref="GetPromptWithTools"/>,
///   injecting the available tools plus the current model/provider.</item>
///   <item>The ACP server (AndyAgentProvider) and <c>SimpleAssistantService</c> use
///   <see cref="GetDefaultCliPrompt"/> (no tool list / model context).</item>
///   <item>The headless runner intentionally uses the caller-supplied
///   <c>config.Agent.Instructions</c> as the system prompt (the run's objective).</item>
/// </list>
/// The provider registry has been centralized (<see cref="Andy.Cli.Services.ProviderRegistry"/>),
/// but fully unifying the prompt bodies across modes (e.g. making the ACP path tool-aware) is
/// deferred: the modes have deliberately different context needs and headless is caller-driven.
/// Tracked as remaining work for #174.
/// </remarks>
public static class SystemPrompts
{
    /// <summary>
    /// Gets the default CLI prompt with environment context.
    /// </summary>
    public static string GetDefaultCliPrompt()
    {
        return new SystemPromptBuilder()
            .WithCoreMandates()
            .WithResponseFormatting()
            .WithWorkflowGuidelines()
            .WithEnvironment(
                platform: GetPlatformName(),
                workingDirectory: Directory.GetCurrentDirectory(),
                currentDate: DateTime.Now,
                timeZone: TimeZoneInfo.Local)
            .Build();
    }

    /// <summary>
    /// Gets a custom prompt with tools and environment context.
    /// </summary>
    public static string GetPromptWithTools(
        IEnumerable<ToolInfo> tools,
        string? customInstructions = null)
    {
        return new SystemPromptBuilder()
            .WithCoreMandates()
            .WithResponseFormatting()
            .WithWorkflowGuidelines()
            .WithEnvironment(
                platform: GetPlatformName(),
                workingDirectory: Directory.GetCurrentDirectory(),
                currentDate: DateTime.Now,
                timeZone: TimeZoneInfo.Local)
            .WithAvailableTools(tools)
            .WithCustomInstructions(customInstructions)
            .Build();
    }

    /// <summary>
    /// Gets the platform name in a human-readable format.
    /// </summary>
    private static string GetPlatformName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "Windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "Linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "macOS";

        return RuntimeInformation.OSDescription;
    }
}
