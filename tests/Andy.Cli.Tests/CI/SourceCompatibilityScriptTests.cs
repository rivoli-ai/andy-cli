using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Andy.Cli.Tests.CI;

public class SourceCompatibilityScriptTests
{
    [Fact]
    public async Task MissingRepositoryFailsBeforeBuild()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new Fixture();
        var missingEngine = Path.Combine(fixture.Root, "missing-engine");

        var result = await fixture.RunAsync(engineSource: missingEngine);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Engine source repo not found", result.Stderr);
        Assert.Empty(fixture.DotnetCommands);
    }

    [Fact]
    public async Task SourceBuildFailureStopsBeforeContractTestsAndCleansUp()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new Fixture();

        var result = await fixture.RunAsync(failCommand: "build");

        Assert.Equal(17, result.ExitCode);
        Assert.Equal(new[] { "msbuild", "msbuild", "restore", "build" }, fixture.DotnetCommands);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.TempParent));
        Assert.Equal("original", File.ReadAllText(fixture.MarkerPath));
    }

    [Fact]
    public async Task ContractTestFailureIsReturnedAndTemporaryWorkspaceIsRemoved()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new Fixture();

        var result = await fixture.RunAsync(failCommand: "test");

        Assert.Equal(17, result.ExitCode);
        Assert.Equal(new[] { "msbuild", "msbuild", "restore", "build", "test" }, fixture.DotnetCommands);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.TempParent));
    }

    [Fact]
    public async Task SuccessfulRunPrintsMachineReadableRevisionsAndCleansUp()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new Fixture();

        var result = await fixture.RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new[] { "msbuild", "msbuild", "restore", "build", "test" }, fixture.DotnetCommands);
        var summaryLine = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("SOURCE_COMPAT_SUMMARY=", StringComparison.Ordinal));
        using var summary = JsonDocument.Parse(summaryLine["SOURCE_COMPAT_SUMMARY=".Length..]);
        Assert.Equal("passed", summary.RootElement.GetProperty("status").GetString());
        Assert.Equal(Fixture.EngineSha,
            summary.RootElement.GetProperty("engine").GetProperty("revision").GetString());
        Assert.Equal(Fixture.TuiSha,
            summary.RootElement.GetProperty("tui").GetProperty("revision").GetString());
        Assert.Equal("9.9.9-source",
            summary.RootElement.GetProperty("engine").GetProperty("package_version").GetString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.TempParent));
        Assert.Equal("original", File.ReadAllText(fixture.MarkerPath));
    }

    private sealed class Fixture : IDisposable
    {
        public const string EngineSha = "1111111111111111111111111111111111111111";
        public const string TuiSha = "2222222222222222222222222222222222222222";

        private readonly string _scriptPath;
        private readonly string _dotnetLog;
        private readonly string _fakeDotnet;
        private readonly string _fakeGit;

        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"source-compat-test-{Guid.NewGuid():N}");
            RepoRoot = Path.Combine(Root, "andy-cli");
            EngineSource = Path.Combine(Root, "andy-engine");
            TuiSource = Path.Combine(Root, "andy-tui2");
            TempParent = Path.Combine(Root, "temp");
            Directory.CreateDirectory(RepoRoot);
            Directory.CreateDirectory(Path.Combine(EngineSource, "src", "Andy.Engine"));
            Directory.CreateDirectory(Path.Combine(TuiSource, "src", "Andy.Tui"));
            Directory.CreateDirectory(TempParent);

            File.WriteAllText(Path.Combine(RepoRoot, "Andy.Cli.sln"), "fake solution");
            MarkerPath = Path.Combine(RepoRoot, "marker.txt");
            File.WriteAllText(MarkerPath, "original");
            File.WriteAllText(Path.Combine(EngineSource, "src", "Andy.Engine", "Andy.Engine.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(TuiSource, "src", "Andy.Tui", "Andy.Tui.csproj"), "<Project />");

            var realRoot = FindRepoRoot();
            _scriptPath = Path.Combine(realRoot, "scripts", "check-source-compat.sh");
            _dotnetLog = Path.Combine(Root, "dotnet.log");
            _fakeDotnet = WriteExecutable("fake-dotnet.sh", """
                #!/usr/bin/env bash
                printf '%s\n' "$1" >> "$FAKE_DOTNET_LOG"
                if [ "$1" = "msbuild" ]; then
                  printf '%s\n' '9.9.9-source'
                  exit 0
                fi
                if [ "${FAKE_DOTNET_FAIL:-}" = "$1" ]; then
                  exit 17
                fi
                exit 0
                """);
            _fakeGit = WriteExecutable("fake-git.sh", """
                #!/usr/bin/env bash
                if [ "$3" = "rev-parse" ]; then
                  case "$2" in
                    *andy-engine) printf '%s\n' '1111111111111111111111111111111111111111' ;;
                    *andy-tui2) printf '%s\n' '2222222222222222222222222222222222222222' ;;
                    *) printf '%s\n' '3333333333333333333333333333333333333333' ;;
                  esac
                fi
                exit 0
                """);
        }

        public string Root { get; }
        public string RepoRoot { get; }
        public string EngineSource { get; }
        public string TuiSource { get; }
        public string TempParent { get; }
        public string MarkerPath { get; }

        public string[] DotnetCommands => File.Exists(_dotnetLog)
            ? File.ReadAllLines(_dotnetLog)
            : Array.Empty<string>();

        public async Task<Result> RunAsync(string? engineSource = null, string? failCommand = null)
        {
            var startInfo = new ProcessStartInfo("/bin/bash")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(_scriptPath);
            startInfo.ArgumentList.Add("--engine-src");
            startInfo.ArgumentList.Add(engineSource ?? EngineSource);
            startInfo.ArgumentList.Add("--tui-src");
            startInfo.ArgumentList.Add(TuiSource);
            startInfo.Environment["SOURCE_COMPAT_REPO_ROOT"] = RepoRoot;
            startInfo.Environment["SOURCE_COMPAT_TEMP_PARENT"] = TempParent;
            startInfo.Environment["SOURCE_COMPAT_DOTNET"] = _fakeDotnet;
            startInfo.Environment["SOURCE_COMPAT_GIT"] = _fakeGit;
            startInfo.Environment["FAKE_DOTNET_LOG"] = _dotnetLog;
            if (failCommand is not null)
            {
                startInfo.Environment["FAKE_DOTNET_FAIL"] = failCommand;
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start source compatibility script.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new Result(process.ExitCode, await stdout, await stderr);
        }

        private string WriteExecutable(string name, string content)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllText(path, content);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for test artifacts.
            }
        }

        private static string FindRepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Andy.Cli.sln")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName
                ?? throw new InvalidOperationException("Could not locate the repository root.");
        }
    }

    private sealed record Result(int ExitCode, string Stdout, string Stderr);
}
