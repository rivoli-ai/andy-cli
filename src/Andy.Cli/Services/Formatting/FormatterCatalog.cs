using System;
using System.Collections.Generic;
using System.Linq;

namespace Andy.Cli.Services.Formatting;

/// <summary>
/// Why a formatter did or did not match a file. Ordered from "would run" downwards so the status
/// command can group the report.
/// </summary>
public enum FormatterMatchState
{
    /// <summary>Enabled, the extension matches, and the command resolves locally: it will run.</summary>
    Runnable,

    /// <summary>The extension matches but the command is not installed, so it is skipped.</summary>
    CommandNotFound,

    /// <summary>The extension matches but the definition is disabled.</summary>
    Disabled,

    /// <summary>The definition does not declare this file's extension.</summary>
    ExtensionMismatch,
}

/// <summary>
/// One formatter evaluated against one file, with the reason - this is what
/// <c>/formatters status &lt;file&gt;</c> prints.
/// </summary>
/// <param name="Definition">The formatter that was considered.</param>
/// <param name="State">Whether it would run, and if not, why not.</param>
/// <param name="ResolvedCommandPath">The executable backing the command, when it resolves.</param>
/// <param name="Reason">A human-readable explanation of <paramref name="State"/>.</param>
public sealed record FormatterMatch(
    FormatterDefinition Definition,
    FormatterMatchState State,
    string? ResolvedCommandPath,
    string Reason)
{
    /// <summary>True when this formatter would actually be invoked for the file.</summary>
    public bool WillRun => State == FormatterMatchState.Runnable;
}

/// <summary>
/// Selects the formatters that apply to a file, in a deterministic order, and explains the choice.
///
/// Ordering is by <see cref="FormatterDefinition.Order"/> ascending, then by name using ordinal
/// comparison. It never depends on dictionary or filesystem enumeration order, so the same config
/// always produces the same sequence of formatter runs - which matters because formatters are not
/// generally commutative.
/// </summary>
public sealed class FormatterCatalog
{
    private readonly IReadOnlyList<FormatterDefinition> _definitions;
    private readonly Func<string, string?> _resolveCommand;

    /// <param name="definitions">The merged definition set (see <see cref="FormatterConfigLoader"/>).</param>
    /// <param name="resolveCommand">
    /// Command resolver; defaults to <see cref="FormatterAvailability.Resolve(string?)"/>. Injected so
    /// tests can describe an arbitrary machine, and so a future config source can supply a pinned path.
    /// </param>
    public FormatterCatalog(
        IReadOnlyList<FormatterDefinition> definitions,
        Func<string, string?>? resolveCommand = null)
    {
        _definitions = Order(definitions ?? Array.Empty<FormatterDefinition>());
        _resolveCommand = resolveCommand ?? FormatterAvailability.Resolve;
    }

    /// <summary>An empty catalog: nothing configured, nothing runs.</summary>
    public static FormatterCatalog Empty { get; } = new(Array.Empty<FormatterDefinition>(), _ => null);

    /// <summary>Build a catalog from the project's config layers.</summary>
    public static FormatterCatalog ForProject(string projectRoot, bool includeDetectedDefaults = true)
        => new(FormatterConfigLoader.Load(projectRoot, includeDetectedDefaults));

    /// <summary>Every known definition, in the catalog's deterministic order.</summary>
    public IReadOnlyList<FormatterDefinition> Definitions => _definitions;

    /// <summary>
    /// Evaluate every definition against a file and explain the outcome. Ordered so the formatters
    /// that would run come first, in the exact order they would run.
    /// </summary>
    public IReadOnlyList<FormatterMatch> Explain(string filePath)
    {
        var matches = new List<FormatterMatch>(_definitions.Count);
        foreach (var definition in _definitions)
        {
            matches.Add(Evaluate(definition, filePath));
        }

        return matches
            .OrderBy(m => (int)m.State)
            .ThenBy(m => m.Definition.Order)
            .ThenBy(m => m.Definition.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The formatters that will run for a file, in execution order. Empty when nothing matches -
    /// the overwhelmingly common case, which must stay free of process launches.
    /// </summary>
    public IReadOnlyList<FormatterMatch> SelectFor(string filePath)
        => _definitions
            .Select(d => Evaluate(d, filePath))
            .Where(m => m.WillRun)
            .OrderBy(m => m.Definition.Order)
            .ThenBy(m => m.Definition.Name, StringComparer.Ordinal)
            .ToArray();

    private FormatterMatch Evaluate(FormatterDefinition definition, string filePath)
    {
        if (!definition.MatchesExtension(filePath))
        {
            var declared = definition.Extensions.Count == 0
                ? "no extensions declared"
                : "handles " + string.Join(", ", definition.Extensions);
            return new FormatterMatch(definition, FormatterMatchState.ExtensionMismatch, null,
                $"does not handle this file ({declared})");
        }

        if (!definition.Enabled)
        {
            return new FormatterMatch(definition, FormatterMatchState.Disabled, null,
                $"extension matches but the formatter is disabled in the {SourceLabel(definition.Source)} configuration");
        }

        var resolved = _resolveCommand(definition.Command);
        if (string.IsNullOrEmpty(resolved))
        {
            return new FormatterMatch(definition, FormatterMatchState.CommandNotFound, null,
                $"extension matches but '{definition.Command}' was not found on PATH (Andy never installs formatters)");
        }

        return new FormatterMatch(definition, FormatterMatchState.Runnable, resolved,
            $"matched on extension, defined by the {SourceLabel(definition.Source)} configuration, "
            + $"command resolves to {resolved}, order {definition.Order}");
    }

    internal static string SourceLabel(FormatterSource source) => source switch
    {
        FormatterSource.Project => "project",
        FormatterSource.User => "user",
        _ => "locally detected",
    };

    private static IReadOnlyList<FormatterDefinition> Order(IReadOnlyList<FormatterDefinition> definitions)
        => definitions
            .OrderBy(d => d.Order)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ToArray();
}
