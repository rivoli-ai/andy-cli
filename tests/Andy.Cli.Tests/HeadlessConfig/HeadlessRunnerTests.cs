// AQ2 tests (rivoli-ai/andy-cli#47). Verify the argument-parsing + config-loading
// scaffolding for `andy-cli run --headless --config <path>`, including the
// structured exit-code contract (0/2 paths for AQ2; 1/3/4 are AQ3+).
//
// The schema lives in-assembly as an embedded resource; tests share the AQ1
// sample fixtures from schemas/samples/ to keep positive-path coverage aligned
// with the AQ1 schema tests.

using System.IO;
using System.Text;
using Andy.Cli.HeadlessConfig;
using Xunit;

namespace Andy.Cli.Tests.HeadlessConfig;

public class HeadlessRunnerTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string SamplesDir = Path.Combine(RepoRoot, "schemas", "samples");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Andy.Cli.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException(
                "Could not locate Andy.Cli.sln walking up from test output directory.");
        }
        return dir.FullName;
    }

    // AQ3 contract: even when the agent loop can't run (e.g. fixtures
    // reference MCP endpoints that don't exist on the test host), the run
    // MUST produce a structured event stream — at minimum a fatal error
    // event followed by a finished event with the matching exit code. The
    // AQ2 stub-test that asserted Success on these fixtures is gone with
    // the stub itself.
    [Theory]
    [InlineData("triage-headless.json")]
    [InlineData("planning-headless.json")]
    [InlineData("coding-headless.json")]
    public async Task Run_FixtureWithUnreachableTools_EmitsErrorAndFinished(string fixtureName)
    {
        var path = Path.Combine(SamplesDir, fixtureName);
        var (stdout, stderr) = NewIoCapture();

        var code = await HeadlessRunner.RunAsync(
            ["run", "--headless", "--config", path], stdout, stderr);

        Assert.Equal(HeadlessExitCode.AgentFailure, code);
        var lines = stdout.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, l => l.Contains("\"kind\":\"error\"") && l.Contains("\"fatal\":true"));
        Assert.Contains(lines, l => l.Contains("\"kind\":\"finished\"") && l.Contains("\"exit_code\":1"));
    }

    [Fact]
    public async Task Run_MissingHeadlessFlag_ReturnsConfigError()
    {
        var path = Path.Combine(SamplesDir, "triage-headless.json");
        var (stdout, stderr) = NewIoCapture();

        var code = await HeadlessRunner.RunAsync(
            ["run", "--config", path], stdout, stderr);

        Assert.Equal(HeadlessExitCode.ConfigError, code);
        Assert.Contains("--headless", stderr.ToString());
    }

    [Fact]
    public async Task Run_MissingConfigFlag_ReturnsConfigError()
    {
        var (stdout, stderr) = NewIoCapture();

        var code = await HeadlessRunner.RunAsync(
            ["run", "--headless"], stdout, stderr);

        Assert.Equal(HeadlessExitCode.ConfigError, code);
        Assert.Contains("--config", stderr.ToString());
    }

    [Fact]
    public async Task Run_ConfigFlagWithoutValue_ReturnsConfigError()
    {
        var (stdout, stderr) = NewIoCapture();

        var code = await HeadlessRunner.RunAsync(
            ["run", "--headless", "--config"], stdout, stderr);

        Assert.Equal(HeadlessExitCode.ConfigError, code);
        Assert.Contains("requires a path", stderr.ToString());
    }

    [Fact]
    public async Task Run_UnknownArgument_ReturnsConfigError()
    {
        var (stdout, stderr) = NewIoCapture();

        var code = await HeadlessRunner.RunAsync(
            ["run", "--headless", "--config", "x", "--weird"], stdout, stderr);

        Assert.Equal(HeadlessExitCode.ConfigError, code);
        Assert.Contains("Unknown argument", stderr.ToString());
    }

    [Fact]
    public async Task Run_NonexistentConfigPath_ReturnsConfigError()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.json");
        var (stdout, stderr) = NewIoCapture();

        var code = await HeadlessRunner.RunAsync(
            ["run", "--headless", "--config", missing], stdout, stderr);

        Assert.Equal(HeadlessExitCode.ConfigError, code);
        Assert.Contains("not found", stderr.ToString());
    }

    [Fact]
    public async Task Run_MalformedJson_ReturnsConfigError()
    {
        using var tmp = NewTempFile();
        File.WriteAllText(tmp.Path, "{ this is not JSON");
        var (stdout, stderr) = NewIoCapture();

        var code = await HeadlessRunner.RunAsync(
            ["run", "--headless", "--config", tmp.Path], stdout, stderr);

        Assert.Equal(HeadlessExitCode.ConfigError, code);
        Assert.Contains("not valid JSON", stderr.ToString());
    }

    [Fact]
    public async Task Run_SchemaInvalidConfig_ReturnsConfigError()
    {
        // Missing every required field. Schema must reject; deserializer never sees it.
        using var tmp = NewTempFile();
        File.WriteAllText(tmp.Path, "{\"schema_version\": 1}");
        var (stdout, stderr) = NewIoCapture();

        var code = await HeadlessRunner.RunAsync(
            ["run", "--headless", "--config", tmp.Path], stdout, stderr);

        Assert.Equal(HeadlessExitCode.ConfigError, code);
        Assert.Contains("does not match", stderr.ToString());
    }

    [Fact]
    public async Task Run_WrongSchemaVersion_ReturnsConfigError()
    {
        using var tmp = NewTempFile();
        // Valid shape but schema_version=2; AQ1 pins v1 as `const`.
        var validText = File.ReadAllText(Path.Combine(SamplesDir, "triage-headless.json"));
        var bumped = validText.Replace("\"schema_version\": 1", "\"schema_version\": 2");
        File.WriteAllText(tmp.Path, bumped);
        var (stdout, stderr) = NewIoCapture();

        var code = await HeadlessRunner.RunAsync(
            ["run", "--headless", "--config", tmp.Path], stdout, stderr);

        Assert.Equal(HeadlessExitCode.ConfigError, code);
    }

    // ---- helpers -----------------------------------------------------------

    private static (StringWriter Stdout, StringWriter Stderr) NewIoCapture()
        => (new StringWriter(new StringBuilder()), new StringWriter(new StringBuilder()));

    private static TempFile NewTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aq2-{Guid.NewGuid():N}.json");
        return new TempFile(path);
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; }
        public TempFile(string path) { Path = path; }
        public void Dispose()
        {
            try { if (File.Exists(Path)) File.Delete(Path); } catch { /* best-effort */ }
        }
    }
}
