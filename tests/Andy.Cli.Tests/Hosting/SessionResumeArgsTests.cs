using Andy.Cli.Hosting;
using Xunit;

namespace Andy.Cli.Tests.Hosting;

/// <summary>
/// Startup flag parsing for session resume (issue #231): --resume &lt;id&gt; and
/// --continue, including the malformed-flag paths.
/// </summary>
public class SessionResumeArgsTests
{
    [Fact]
    public void Parse_NoFlags_DoesNotRequestResume()
    {
        var parsed = SessionResumeArgs.Parse(new[] { "--instrumentation" });
        Assert.False(parsed.RequestsResume);
        Assert.Null(parsed.Error);
    }

    [Fact]
    public void Parse_ResumeWithId()
    {
        var parsed = SessionResumeArgs.Parse(new[] { "--resume", "20260723-120000-ab12" });
        Assert.True(parsed.RequestsResume);
        Assert.Equal("20260723-120000-ab12", parsed.SessionId);
        Assert.False(parsed.ContinueLatest);
    }

    [Fact]
    public void Parse_ShortFlags()
    {
        Assert.Equal("abc", SessionResumeArgs.Parse(new[] { "-r", "abc" }).SessionId);
        Assert.True(SessionResumeArgs.Parse(new[] { "-c" }).ContinueLatest);
    }

    [Fact]
    public void Parse_Continue()
    {
        var parsed = SessionResumeArgs.Parse(new[] { "--continue" });
        Assert.True(parsed.RequestsResume);
        Assert.True(parsed.ContinueLatest);
        Assert.Null(parsed.SessionId);
    }

    [Fact]
    public void Parse_ResumeWithoutId_ReportsError()
    {
        var parsed = SessionResumeArgs.Parse(new[] { "--resume" });
        Assert.NotNull(parsed.Error);
        Assert.False(parsed.RequestsResume);
    }

    [Fact]
    public void Parse_ResumeFollowedByFlag_ReportsError()
    {
        var parsed = SessionResumeArgs.Parse(new[] { "--resume", "--instrumentation" });
        Assert.NotNull(parsed.Error);
        Assert.False(parsed.RequestsResume);
    }

    [Fact]
    public void Parse_ResumeAndContinueTogether_ReportsError()
    {
        var parsed = SessionResumeArgs.Parse(new[] { "--resume", "abc", "--continue" });
        Assert.NotNull(parsed.Error);
        Assert.False(parsed.RequestsResume);
    }
}
