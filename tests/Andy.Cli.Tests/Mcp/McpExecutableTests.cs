using System.Diagnostics;
using System.Text.Json;
using Andy.Cli.Tests.TestHelpers;

namespace Andy.Cli.Tests.Mcp;

public sealed class McpExecutableTests
{
    [Fact]
    public async Task Executable_InitializesAndDiscoversMcpTools()
    {
        await using var server = new McpRoutingTestServer("smoke_tool");
        await server.StartAsync();
        var workspace = Directory.CreateTempSubdirectory("andy-mcp-executable-");
        try
        {
            var config = Path.Combine(workspace.FullName, "run.json");
            await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
            {
                schema_version = 1,
                run_id = Guid.NewGuid(),
                agent = new { slug = "mcp-smoke", instructions = "Unused", output_format = "plain" },
                model = new { provider = "openrouter", id = "unused" },
                tools = new[] { new { name = "smoke_tool", transport = "mcp", endpoint = server.BaseEndpoint } },
                // This guard runs after tool discovery and before provider resolution,
                // so no model credentials or external model request are needed.
                workspace = new { root = workspace.FullName, branch = "deliberately-not-a-git-worktree" },
                output = new { file = Path.Combine(workspace.FullName, "output.txt"), stream = "stdout" },
                permissions = new { allowed_tools = Array.Empty<string>() },
                limits = new { max_iterations = 1, timeout_seconds = 15, max_output_tokens = 256 },
            }));
            var binary = Environment.GetEnvironmentVariable("ANDY_MCP_SMOKE_BINARY");
            var start = new ProcessStartInfo(binary ?? "dotnet")
            {
                WorkingDirectory = workspace.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            if (binary is null) start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "andy-cli.dll"));
            foreach (var argument in new[] { "run", "--headless", "--config", config }) start.ArgumentList.Add(argument);
            start.Environment.Remove("ANDY_TOKEN");
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await process.WaitForExitAsync(timeout.Token);
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            var output = await stdout;
            var errors = await stderr;
            Assert.True(output.Contains("workspace_branch_invalid", StringComparison.Ordinal), output + errors);
            Assert.Equal(1, server.InitializeRequestCount);
            Assert.Equal(1, server.ToolsListRequestCount);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }
}
