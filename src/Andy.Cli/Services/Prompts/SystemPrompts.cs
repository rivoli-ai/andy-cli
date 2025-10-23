using System.Runtime.InteropServices;

namespace Andy.Cli.Services.Prompts;

/// <summary>
/// Provides pre-configured system prompts for different scenarios.
/// </summary>
public static class SystemPrompts
{
    /// <summary>
    /// Gets the default CLI prompt with environment context.
    /// </summary>
    public static string GetDefaultCliPrompt()
    {
        return new SystemPromptBuilder()
            .WithCoreMandates()
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
