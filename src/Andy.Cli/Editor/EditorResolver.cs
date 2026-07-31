using System;
using System.Collections.Generic;

namespace Andy.Cli.Editor;

/// <summary>The outcome of resolving the user's external editor.</summary>
public sealed class EditorResolution
{
    private EditorResolution(bool success, string? variable, string fileName, IReadOnlyList<string> arguments, string? message)
    {
        Success = success;
        Variable = variable;
        FileName = fileName;
        Arguments = arguments;
        Message = message;
    }

    /// <summary>True when an editor command line was found and parsed.</summary>
    public bool Success { get; }

    /// <summary>Which environment variable supplied the value ("VISUAL" or "EDITOR"), when known.</summary>
    public string? Variable { get; }

    /// <summary>The program to launch. Empty when <see cref="Success"/> is false.</summary>
    public string FileName { get; }

    /// <summary>Configured arguments, before the temp file path is appended.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Actionable, user-facing guidance when <see cref="Success"/> is false.</summary>
    public string? Message { get; }

    internal static EditorResolution Resolved(string variable, string fileName, IReadOnlyList<string> arguments)
        => new(true, variable, fileName, arguments, null);

    internal static EditorResolution Failed(string? variable, string message)
        => new(false, variable, string.Empty, Array.Empty<string>(), message);
}

/// <summary>
/// Resolves the external editor from the environment, preferring <c>VISUAL</c> over
/// <c>EDITOR</c> (the POSIX convention: VISUAL is the full-screen editor, EDITOR may be
/// a line editor). Blank or whitespace-only values are skipped so that
/// <c>VISUAL=""</c> falls through to <c>EDITOR</c>. A variable that IS set but cannot be
/// parsed reports an error naming that variable rather than silently falling through,
/// so a typo does not launch an unexpected program.
/// </summary>
public sealed class EditorResolver
{
    /// <summary>Variables consulted, in precedence order.</summary>
    public static readonly IReadOnlyList<string> VariableOrder = new[] { "VISUAL", "EDITOR" };

    private readonly Func<string, string?> _readEnvironment;

    /// <summary>Create a resolver reading the process environment.</summary>
    public EditorResolver() : this(null) { }

    /// <summary>Create a resolver over an injected environment reader (used by tests).</summary>
    public EditorResolver(Func<string, string?>? readEnvironment)
        => _readEnvironment = readEnvironment ?? Environment.GetEnvironmentVariable;

    /// <summary>Resolve the editor command line.</summary>
    public EditorResolution Resolve()
    {
        foreach (var variable in VariableOrder)
        {
            string? value;
            try { value = _readEnvironment(variable); }
            catch { value = null; }

            if (string.IsNullOrWhiteSpace(value)) continue;

            if (EditorCommandLine.TryParse(value, out var fileName, out var arguments, out var error))
                return EditorResolution.Resolved(variable, fileName, arguments);

            return EditorResolution.Failed(
                variable,
                $"{variable} is set to \"{value}\" but could not be parsed: {error}.\n\n" +
                EditorSetupGuidance.QuotingHelp());
        }

        return EditorResolution.Failed(null, EditorSetupGuidance.NotConfiguredMessage());
    }
}
