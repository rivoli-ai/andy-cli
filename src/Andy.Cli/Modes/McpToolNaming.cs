using System;
using System.Text;

namespace Andy.Cli.Modes;

/// <summary>
/// The single source of truth for how an MCP server's remote tool becomes an Andy tool id.
///
/// This lives here rather than in <c>InteractiveMcpToolHost</c> because Plan-mode server-wide
/// grants are expressed as a tool-id PREFIX: granting the server "docs" allows every tool whose id
/// starts with <c>mcp_docs_</c>, including tools that server exposes for the first time later. If
/// the host and the grant policy ever normalized names differently, a server-wide grant would
/// silently stop matching, so both call these helpers.
/// </summary>
public static class McpToolNaming
{
    /// <summary>
    /// Collapses an arbitrary server or tool name into the lowercase
    /// <c>[a-z0-9_]</c> form used inside tool ids.
    /// </summary>
    public static string NormalizeIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var result = new StringBuilder(value.Length);
        var previousUnderscore = false;
        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                result.Append(char.ToLowerInvariant(character));
                previousUnderscore = false;
            }
            else if (!previousUnderscore)
            {
                result.Append('_');
                previousUnderscore = true;
            }
        }

        return result.ToString().Trim('_');
    }

    /// <summary>The normalized server segment, with the same empty-name fallback the host uses.</summary>
    public static string NormalizeServerId(string serverName)
    {
        var normalized = NormalizeIdentifier(serverName);
        return normalized.Length == 0 ? "server" : normalized;
    }

    /// <summary>
    /// The tool-id prefix every tool from <paramref name="serverName"/> carries
    /// (e.g. <c>"docs"</c> to <c>"mcp_docs_"</c>).
    /// </summary>
    public static string ServerToolPrefix(string serverName) => $"mcp_{NormalizeServerId(serverName)}_";

    /// <summary>The full tool id for one remote tool, matching the host's registration.</summary>
    public static string BuildToolId(string serverName, string remoteToolName)
    {
        var toolId = NormalizeIdentifier(remoteToolName);
        return ServerToolPrefix(serverName) + (toolId.Length == 0 ? "tool" : toolId);
    }

    /// <summary>
    /// True when <paramref name="toolId"/> belongs to <paramref name="serverName"/>. Uniqueness
    /// suffixes the host appends on a collision (<c>_2</c>, <c>_3</c>) keep the prefix, so they
    /// match too.
    /// </summary>
    public static bool BelongsToServer(string? toolId, string serverName) =>
        !string.IsNullOrEmpty(toolId)
        && toolId!.StartsWith(ServerToolPrefix(serverName), StringComparison.OrdinalIgnoreCase);
}
