using System.Diagnostics;
using System.IO;
using Xunit;

namespace Andy.Cli.Tests.CI;

public class AcpSmokeDriverTests
{
    [Fact]
    public async Task DriverAcceptsInitializeAndSessionNewHandshake()
    {
        if (OperatingSystem.IsWindows() || !HasPython())
        {
            return;
        }

        using var fixture = new Fixture(validInitialize: true);
        var result = await fixture.RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("acp-smoke: PASS", result.Stdout);
        Assert.Contains("protocolVersion=1", result.Stdout);
        Assert.Contains("sessionId=smoke-session", result.Stdout);
    }

    [Fact]
    public async Task DriverRejectsInitializeWithoutProtocolVersion()
    {
        if (OperatingSystem.IsWindows() || !HasPython())
        {
            return;
        }

        using var fixture = new Fixture(validInitialize: false);
        var result = await fixture.RunAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("did not contain integer protocolVersion", result.Stderr);
    }

    private static bool HasPython()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("python3", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            process?.WaitForExit(5000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly string _server;

        public Fixture(bool validInitialize)
        {
            _root = Path.Combine(Path.GetTempPath(), $"acp-smoke-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            _server = Path.Combine(_root, "fake-acp-server.py");
            var initializeResult = validInitialize
                ? "{'protocolVersion': 1}"
                : "{}";
            File.WriteAllText(_server, $$"""
                #!/usr/bin/env python3
                import json
                import sys

                for line in sys.stdin:
                    request = json.loads(line)
                    if request.get('method') == 'initialize':
                        result = {{initializeResult}}
                    elif request.get('method') == 'session/new':
                        result = {'sessionId': 'smoke-session'}
                    else:
                        result = {}
                    print(json.dumps({'jsonrpc': '2.0', 'id': request.get('id'), 'result': result}), flush=True)
                """);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_server,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        public async Task<Result> RunAsync()
        {
            var driver = Path.Combine(FindRepoRoot(), "scripts", "acp-smoke.py");
            var startInfo = new ProcessStartInfo("python3")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(driver);
            startInfo.ArgumentList.Add(_server);
            startInfo.ArgumentList.Add("--cwd");
            startInfo.ArgumentList.Add(_root);
            startInfo.ArgumentList.Add("--timeout");
            startInfo.ArgumentList.Add("2");

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start ACP smoke driver.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new Result(process.ExitCode, await stdout, await stderr);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
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
