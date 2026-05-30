using System;
using System.Reflection;

namespace Andy.Cli;

/// <summary>
/// Resolves the human-readable application version for display in the UI.
/// </summary>
public static class VersionInfo
{
    /// <summary>
    /// Resolves the display version for the running application.
    /// Prefers the informational version (which preserves the full string
    /// including any pre-release suffix) and falls back to the assembly
    /// version. Build-metadata hashes (anything after a '+') are trimmed so
    /// the header stays readable.
    /// </summary>
    public static string ResolveDisplayVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return ResolveDisplayVersion(informational, assembly.GetName().Version);
    }

    /// <summary>
    /// Resolves the display version from the supplied informational version
    /// string and assembly version. Exposed for testing.
    /// </summary>
    public static string ResolveDisplayVersion(string? informationalVersion, Version? assemblyVersion)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            // Strip build metadata (e.g. "+abc1234") which is not human-readable.
            var plus = informationalVersion.IndexOf('+');
            var trimmed = (plus >= 0 ? informationalVersion.Substring(0, plus) : informationalVersion).Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        if (assemblyVersion != null)
        {
            return $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
        }

        return string.Empty;
    }
}
