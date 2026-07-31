using System;
using System.Collections.Generic;
using Andy.Tools.Core;

namespace Andy.Cli.Lsp;

/// <summary>
/// Folds a changed-file diagnostics report into the tool result that the model will read.
///
/// The model only ever sees <c>ToolExecutionResult.Data</c> (ToolAdapter serializes it and drops
/// Message whenever Data is present), so diagnostics have to land there to be part of the agent's
/// context rather than just a line in the terminal.
///
/// The attachment is non-destructive by construction: it never mutates the object the tool
/// returned. The feed and the execution tracker have already captured that reference by the time
/// this runs, and quietly changing an object they hold would make the same call render differently
/// depending on timing. A dictionary result is COPIED with the extra key; anything else is nested
/// under "result" in a fresh envelope.
/// </summary>
public static class LspResultAttachment
{
    /// <summary>Key carrying the structured report in the tool result and in metadata.</summary>
    public const string PayloadKey = "lsp_diagnostics";

    /// <summary>Key holding the original payload when it had to be nested.</summary>
    public const string OriginalResultKey = "result";

    /// <summary>
    /// Returns a payload equivalent to <paramref name="data"/> with the report attached.
    /// </summary>
    public static object Attach(object? data, LspDiagnosticsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var payload = report.ToStructuredPayload();

        if (data is IDictionary<string, object?> dictionary)
        {
            var copy = new Dictionary<string, object?>(dictionary, StringComparer.Ordinal)
            {
                [PayloadKey] = payload,
            };
            return copy;
        }

        var envelope = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [PayloadKey] = payload,
        };
        if (data is not null)
        {
            envelope[OriginalResultKey] = data;
        }
        return envelope;
    }

    /// <summary>
    /// Attaches the report to a live result. Only the <see cref="ToolExecutionResult.Data"/>
    /// reference is swapped, so previously captured snapshots are untouched.
    /// </summary>
    public static void AttachTo(ToolExecutionResult result, LspDiagnosticsReport report)
    {
        ArgumentNullException.ThrowIfNull(result);
        result.Data = Attach(result.Data, report);
    }
}
