using System;
using System.Collections.Generic;
using Andy.Tools.Core;

namespace Andy.Cli.Services.Formatting;

/// <summary>
/// Attaches a formatter failure report to the tool result that flows back to the model.
///
/// OpenCode logs formatter failures and moves on, which lets the agent believe a file was formatted
/// when it was not. Andy instead threads the formatter's exit code and bounded, redacted stderr
/// into the tool result, so the model reads it in the same place it reads everything else about the
/// call and can decide what to do.
/// </summary>
public static class FormatterResultReporter
{
    /// <summary>Key under which the report appears in the tool result's data and metadata.</summary>
    public const string ReportKey = "formatter_diagnostics";

    /// <summary>
    /// Add <paramref name="report"/> to <paramref name="result"/>. Additive by design: existing data
    /// is preserved so presenters that read a tool's own shape keep working.
    /// </summary>
    public static void Attach(ToolExecutionResult result, string? report)
    {
        if (result is null || string.IsNullOrWhiteSpace(report))
        {
            return;
        }

        // Metadata always carries it: this is the channel every consumer can read without caring
        // what shape the tool's own Data has.
        result.Metadata ??= new Dictionary<string, object?>();
        result.Metadata[ReportKey] = report;

        // When the tool returns a dictionary, add the report there too so it is serialized into the
        // payload the model actually receives.
        if (result.Data is Dictionary<string, object?> dictionary)
        {
            dictionary[ReportKey] = report;
        }

        // Keep the human-facing message honest as well.
        result.Message = string.IsNullOrWhiteSpace(result.Message)
            ? report
            : result.Message + "\n\n" + report;
    }
}
