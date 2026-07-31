using System;
using System.Collections.Generic;
using Andy.Cli.Services.Formatting;
using Andy.Cli.Services.Sessions;
using Xunit;

namespace Andy.Cli.Tests.Services.Formatting;

public class FormatterDiagnosticsTests
{
    private static readonly SessionRedactor Redactor = new(new[] { "hunter2secret" });

    [Fact]
    public void Summarize_PrefersStderr()
    {
        var summary = FormatterDiagnostics.Summarize("the reason", "noise", Redactor);
        Assert.Equal("the reason", summary);
    }

    [Fact]
    public void Summarize_FallsBackToStdoutWhenStderrIsEmpty()
    {
        Assert.Equal("stdout reason", FormatterDiagnostics.Summarize("   ", "stdout reason", Redactor));
    }

    [Fact]
    public void Summarize_RedactsLiteralSecretValues()
    {
        var summary = FormatterDiagnostics.Summarize("config had hunter2secret in it", null, Redactor);

        Assert.DoesNotContain("hunter2secret", summary);
        Assert.Contains(SessionRedactor.Replacement, summary);
    }

    [Fact]
    public void Summarize_RedactsBearerTokensAndApiKeyShapes()
    {
        var summary = FormatterDiagnostics.Summarize(
            "Authorization: Bearer abc123def456\nkey sk-abcdefgh12345", null, Redactor);

        Assert.DoesNotContain("abc123def456", summary);
        Assert.DoesNotContain("sk-abcdefgh12345", summary);
    }

    [Fact]
    public void Summarize_BoundsHugeOutput()
    {
        var summary = FormatterDiagnostics.Summarize(new string('x', 100_000), null, Redactor);

        Assert.True(summary.Length <= FormatterDiagnostics.MaxDiagnosticChars + 80);
        Assert.Contains("truncated", summary);
    }

    [Fact]
    public void Bound_LeavesShortTextAlone()
    {
        Assert.Equal("short", FormatterDiagnostics.Bound("short", 100));
    }

    [Fact]
    public void BuildAgentReport_IsNullWhenEveryFormatterSucceeded()
    {
        var results = new[]
        {
            new FormatterRunResult("a", FormatterOutcome.NoChange, 0, string.Empty, TimeSpan.Zero),
            new FormatterRunResult("b", FormatterOutcome.Changed, 0, string.Empty, TimeSpan.Zero),
        };

        Assert.Null(FormatterDiagnostics.BuildAgentReport("src/a.cs", results));
    }

    [Fact]
    public void BuildAgentReport_NamesEveryFailingFormatterAndRefusesToClaimSuccess()
    {
        var results = new[]
        {
            new FormatterRunResult("ok", FormatterOutcome.Changed, 0, string.Empty, TimeSpan.Zero),
            new FormatterRunResult("bad", FormatterOutcome.NonZeroExit, 9, "stderr text", TimeSpan.Zero),
            new FormatterRunResult("slow", FormatterOutcome.TimedOut, null, "exceeded 5s", TimeSpan.Zero),
        };

        var report = FormatterDiagnostics.BuildAgentReport("src/a.cs", results);

        Assert.NotNull(report);
        Assert.Contains("src/a.cs", report);
        Assert.Contains("NOT formatter-clean", report);
        Assert.Contains("bad", report);
        Assert.Contains("exited with code 9", report);
        Assert.Contains("stderr text", report);
        Assert.Contains("slow", report);
        Assert.DoesNotContain("- ok", report);
    }

    [Fact]
    public void BuildAgentReport_IsBounded()
    {
        var results = new List<FormatterRunResult>();
        for (int i = 0; i < 200; i++)
        {
            results.Add(new FormatterRunResult($"f{i}", FormatterOutcome.NonZeroExit, 1,
                new string('y', 500), TimeSpan.Zero));
        }

        var report = FormatterDiagnostics.BuildAgentReport("src/a.cs", results);

        Assert.NotNull(report);
        Assert.True(report!.Length <= FormatterDiagnostics.MaxReportChars + 80);
    }

    [Theory]
    [InlineData(FormatterOutcome.NoChange, false)]
    [InlineData(FormatterOutcome.Changed, false)]
    [InlineData(FormatterOutcome.PermissionDenied, true)]
    [InlineData(FormatterOutcome.CommandNotFound, true)]
    [InlineData(FormatterOutcome.NonZeroExit, true)]
    [InlineData(FormatterOutcome.TimedOut, true)]
    [InlineData(FormatterOutcome.Cancelled, true)]
    [InlineData(FormatterOutcome.TargetMissing, true)]
    [InlineData(FormatterOutcome.TargetEscaped, true)]
    public void IsFailure_ClassifiesEveryOutcome(FormatterOutcome outcome, bool expected)
    {
        var result = new FormatterRunResult("f", outcome, null, string.Empty, TimeSpan.Zero);
        Assert.Equal(expected, result.IsFailure);
    }
}
