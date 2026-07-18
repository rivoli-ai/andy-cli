using System.Reflection;

namespace Andy.Cli.ACP;

/// <summary>
/// Derives ACP server identity (name and version) from the running assembly's
/// metadata instead of hard-coded strings, so the advertised version cannot
/// drift from the packaged CLI build.
/// </summary>
public static class AcpServerMetadata
{
    /// <summary>Server display name, from the assembly name.</summary>
    public static string GetName(Assembly? assembly = null)
    {
        var asm = assembly ?? typeof(AcpServerMetadata).Assembly;
        return asm.GetName().Name ?? "Andy.CLI";
    }

    /// <summary>
    /// Server version. Prefers the informational version (preserving any
    /// pre-release suffix, with build metadata trimmed) and falls back to the
    /// assembly version. Reuses the same resolution as the interactive UI.
    /// </summary>
    public static string GetVersion(Assembly? assembly = null)
    {
        var asm = assembly ?? typeof(AcpServerMetadata).Assembly;
        var informational = asm
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var resolved = VersionInfo.ResolveDisplayVersion(informational, asm.GetName().Version);
        return string.IsNullOrWhiteSpace(resolved) ? "0.0.0" : resolved;
    }
}
