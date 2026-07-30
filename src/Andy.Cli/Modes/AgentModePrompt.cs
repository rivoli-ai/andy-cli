using System.Text;

namespace Andy.Cli.Modes;

/// <summary>
/// Renders the active mode into the model's context.
///
/// This is prompt text, and prompt text alone is NOT the safety boundary - the boundary is the
/// permission overlay in <see cref="ModeToolGate"/>. Telling the model about the mode simply stops
/// it from wasting turns on calls that are going to be refused, and lets it explain the constraint
/// to the user.
/// </summary>
public static class AgentModePrompt
{
    /// <summary>
    /// The system-prompt section describing the mode system and which mode is active at the time
    /// the agent is constructed.
    /// </summary>
    public static string SystemPromptSection(AgentModeDefinition mode)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Operating Mode");
        sb.AppendLine();
        sb.AppendLine($"The session is currently in **{mode.DisplayName} mode**. {mode.Summary}");
        sb.AppendLine();

        if (!mode.AllowsMutation)
        {
            AppendPlanConstraints(sb);
        }
        else
        {
            sb.AppendLine(
                "The user can switch to Plan mode at any time with `/mode plan`, which makes the session "
                + "read-only until they explicitly switch back.");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The per-turn reminder prepended to the user's message. The system prompt is fixed when the
    /// agent is built, so this is what keeps the model correct after a mid-session `/mode` switch.
    /// Returns null for modes that impose no constraint, leaving Build-mode turns untouched.
    /// </summary>
    public static string? TurnDirective(AgentModeDefinition mode)
    {
        if (mode.AllowsMutation)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"[Active mode: {mode.DisplayName} - read-only, enforced by tool permissions]");
        AppendPlanConstraints(sb);
        return sb.ToString().TrimEnd();
    }

    private static void AppendPlanConstraints(StringBuilder sb)
    {
        sb.AppendLine("Constraints, enforced by the tool permission layer and not merely requested:");
        sb.AppendLine();
        sb.AppendLine("- You MAY read files, list directories, search text, query the code index, inspect git history, and read documents.");
        sb.AppendLine("- You MUST NOT write, create, move, copy, or delete files.");
        sb.AppendLine("- You MUST NOT run shell commands. Every `execute_command` call is denied.");
        sb.AppendLine("- Tools that cannot be verified read-only (including MCP tools) are denied as well.");
        sb.AppendLine("- Attempting a denied tool wastes a turn: the call is refused before it runs, whatever the permission rules say.");
        sb.AppendLine();
        sb.AppendLine(
            "Produce a plan: describe the change you would make, the files involved, and the order of work. "
            + "Present it in your reply. Do not try to create a plan file. When the user is satisfied, they "
            + "will switch to Build mode themselves with `/mode build`; you cannot switch modes.");
        sb.AppendLine();
    }
}
