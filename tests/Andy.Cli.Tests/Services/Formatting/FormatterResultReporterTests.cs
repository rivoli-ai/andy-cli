using System.Collections.Generic;
using Andy.Cli.Services.Formatting;
using Andy.Tools.Core;
using Xunit;

namespace Andy.Cli.Tests.Services.Formatting;

public class FormatterResultReporterTests
{
    [Fact]
    public void Attach_PutsTheReportInMetadata()
    {
        var result = new ToolExecutionResult { IsSuccessful = true };

        FormatterResultReporter.Attach(result, "formatter failed");

        Assert.Equal("formatter failed", result.Metadata![FormatterResultReporter.ReportKey]);
    }

    [Fact]
    public void Attach_AlsoAddsTheReportToDictionaryData_SoTheModelSeesIt()
    {
        var data = new Dictionary<string, object?> { ["path"] = "a.cs" };
        var result = new ToolExecutionResult { IsSuccessful = true, Data = data };

        FormatterResultReporter.Attach(result, "formatter failed");

        Assert.Equal("formatter failed", data[FormatterResultReporter.ReportKey]);
        Assert.Equal("a.cs", data["path"]);
    }

    [Fact]
    public void Attach_AppendsToAnExistingMessageRatherThanReplacingIt()
    {
        var result = new ToolExecutionResult { IsSuccessful = true, Message = "Wrote 3 lines" };

        FormatterResultReporter.Attach(result, "formatter failed");

        Assert.Contains("Wrote 3 lines", result.Message);
        Assert.Contains("formatter failed", result.Message);
    }

    [Fact]
    public void Attach_IsANoOpWhenThereIsNothingToReport()
    {
        var result = new ToolExecutionResult { IsSuccessful = true, Message = "Wrote 3 lines" };

        FormatterResultReporter.Attach(result, null);
        FormatterResultReporter.Attach(result, "   ");

        Assert.Equal("Wrote 3 lines", result.Message);
        Assert.True(result.Metadata is null || !result.Metadata.ContainsKey(FormatterResultReporter.ReportKey));
    }

    [Fact]
    public void Attach_LeavesNonDictionaryDataUntouched()
    {
        var result = new ToolExecutionResult { IsSuccessful = true, Data = "plain string result" };

        FormatterResultReporter.Attach(result, "formatter failed");

        Assert.Equal("plain string result", result.Data);
        Assert.Equal("formatter failed", result.Metadata![FormatterResultReporter.ReportKey]);
    }
}
