using System;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Andy.Cli.Configuration;

/// <summary>
/// Settings for the interactive shell escape (issue #286): the composer mode entered by typing
/// <c>!</c> at prompt offset zero, which runs the rest of the line through the SAME
/// permission-gated <c>execute_command</c> path the model's shell tool uses.
///
/// The feature is a convenience over an already-gated capability, not a new privilege, but it is
/// still the shortest path from a keystroke to a child process, so it must be possible to turn it
/// off outright in a locked-down deployment. Two independent switches do that:
///
/// <list type="bullet">
/// <item>
/// <description>
/// <c>ShellEscape:Enabled=false</c> in appsettings.json (or any configured provider).
/// </description>
/// </item>
/// <item>
/// <description>
/// The <c>ANDY_SHELL_ESCAPE</c> environment variable set to a falsey value
/// (<c>0</c>, <c>off</c>, <c>false</c>, <c>no</c>, <c>disabled</c>).
/// </description>
/// </item>
/// </list>
///
/// The environment variable wins over configuration in BOTH directions is deliberately NOT the
/// rule: a disable can come from either source and neither can be overridden by the other, so an
/// operator who disables the feature in a managed config file cannot have that undone by an
/// environment variable in the user's shell profile. In other words the two switches are ANDed.
/// </summary>
public sealed record ShellEscapeOptions
{
    /// <summary>Configuration section the settings are read from.</summary>
    public const string SectionName = "ShellEscape";

    /// <summary>Environment variable that can disable shell escape independently of configuration.</summary>
    public const string EnvironmentVariable = "ANDY_SHELL_ESCAPE";

    /// <summary>Default wall-clock budget for one user-invoked command.</summary>
    public const int DefaultTimeoutSeconds = 120;

    /// <summary>
    /// Default cap on the characters kept from each of stdout and stderr. Generous compared with
    /// <see cref="Andy.Cli.Services.ToolOutputLimits"/> (which protects the MODEL's context window)
    /// because this output is never sent to the model automatically - it is only shown to the user
    /// and only reaches the model if they explicitly attach it.
    /// </summary>
    public const int DefaultMaxOutputCharacters = 40_000;

    /// <summary>Whether typing <c>!</c> at the start of an empty prompt enters shell mode.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Wall-clock budget for one command, in seconds. Values below 1 fall back to the default.</summary>
    public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;

    /// <summary>Characters retained from each stream before truncation. Values below 1 fall back to the default.</summary>
    public int MaxOutputCharacters { get; init; } = DefaultMaxOutputCharacters;

    /// <summary>Settings with every default applied.</summary>
    public static ShellEscapeOptions Default { get; } = new();

    /// <summary>Settings with the feature turned off.</summary>
    public static ShellEscapeOptions Disabled { get; } = new() { Enabled = false };

    /// <summary>Effective timeout, with out-of-range values normalized to the default.</summary>
    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds > 0 ? TimeoutSeconds : DefaultTimeoutSeconds);

    /// <summary>Effective per-stream output cap, with out-of-range values normalized to the default.</summary>
    public int EffectiveMaxOutputCharacters =>
        MaxOutputCharacters > 0 ? MaxOutputCharacters : DefaultMaxOutputCharacters;

    /// <summary>
    /// Reads the options from configuration and the environment. A missing section means "enabled
    /// with defaults"; either source can disable the feature and neither can re-enable it once the
    /// other has said no.
    /// </summary>
    public static ShellEscapeOptions Resolve(IConfiguration? configuration, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;

        var section = configuration?.GetSection(SectionName);
        var enabled = ParseBool(section?["Enabled"]) ?? true;
        var timeout = ParseInt(section?["TimeoutSeconds"]) ?? DefaultTimeoutSeconds;
        var maxOutput = ParseInt(section?["MaxOutputCharacters"]) ?? DefaultMaxOutputCharacters;

        // A falsey environment variable disables; anything else (including unset) leaves the
        // configured value alone, so ANDY_SHELL_ESCAPE=1 cannot re-enable a config-disabled feature.
        if (ParseBool(environment(EnvironmentVariable)) == false)
        {
            enabled = false;
        }

        return new ShellEscapeOptions
        {
            Enabled = enabled,
            TimeoutSeconds = timeout > 0 ? timeout : DefaultTimeoutSeconds,
            MaxOutputCharacters = maxOutput > 0 ? maxOutput : DefaultMaxOutputCharacters,
        };
    }

    /// <summary>
    /// Parses the shapes people actually write in config files and shell profiles. Returns null
    /// when the value is absent or unrecognizable, so the caller can keep its default.
    /// </summary>
    internal static bool? ParseBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" or "enabled" => true,
            "0" or "false" or "no" or "off" or "disabled" => false,
            _ => null
        };
    }

    private static int? ParseInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
