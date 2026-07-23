using System.Text.Json;
using Andy.Cli.Mcp;
using Microsoft.Extensions.Configuration;

namespace Andy.Cli.Tests.Mcp;

public sealed class McpConfigurationLoaderTests
{
    [Fact]
    public void Load_ReadsAppsettingsServers()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mcp:Servers:local:Transport"] = "stdio",
                ["Mcp:Servers:local:Command"] = "npx",
                ["Mcp:Servers:local:Args:0"] = "-y",
                ["Mcp:Servers:local:Args:1"] = "@example/mcp",
            })
            .Build();

        using var project = TemporaryProject.Create();
        var result = McpConfigurationLoader.Load(configuration, project.Path);

        var server = Assert.Single(result.Servers);
        Assert.Equal("local", server.Name);
        Assert.Equal("stdio", server.Transport);
        Assert.Equal("npx", server.Command);
        Assert.Equal(new[] { "-y", "@example/mcp" }, server.Args);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Load_ProjectFileOverridesAppsettingsByServerName()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mcp:Servers:shared:Transport"] = "stdio",
                ["Mcp:Servers:shared:Command"] = "old-command",
            })
            .Build();

        using var project = TemporaryProject.Create();
        project.WriteConfiguration(new
        {
            servers = new
            {
                shared = new
                {
                    transport = "http",
                    url = "https://mcp.example.test/rpc",
                },
            },
        });

        var result = McpConfigurationLoader.Load(configuration, project.Path);

        var server = Assert.Single(result.Servers);
        Assert.Equal("http", server.Transport);
        Assert.Equal("https://mcp.example.test/rpc", server.Url);
        Assert.Null(server.Command);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Load_InterpolatesEnvironmentVariablesWithoutPersistingSecretsInErrors()
    {
        var variableName = $"ANDY_MCP_TEST_{Guid.NewGuid():N}";
        const string secretValue = "test-secret-value";
        Environment.SetEnvironmentVariable(variableName, secretValue);

        try
        {
            using var project = TemporaryProject.Create();
            project.WriteConfiguration(new
            {
                servers = new Dictionary<string, object>
                {
                    ["remote"] = new
                    {
                        transport = "http",
                        url = "https://mcp.example.test/rpc",
                        headers = new Dictionary<string, string>
                        {
                            ["Authorization"] = $"Bearer ${{{variableName}}}",
                        },
                    },
                },
            });

            var result = McpConfigurationLoader.Load(null, project.Path);

            var server = Assert.Single(result.Servers);
            Assert.Equal($"Bearer {secretValue}", server.Headers["Authorization"]);
            Assert.DoesNotContain(result.Errors, error => error.Contains(secretValue, StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public void Load_RejectsUnsetEnvironmentVariables()
    {
        var variableName = $"ANDY_MCP_MISSING_{Guid.NewGuid():N}";
        using var project = TemporaryProject.Create();
        project.WriteConfiguration(new
        {
            servers = new Dictionary<string, object>
            {
                ["remote"] = new
                {
                    transport = "http",
                    url = "https://mcp.example.test/rpc",
                    headers = new Dictionary<string, string>
                    {
                        ["Authorization"] = $"Bearer ${{{variableName}}}",
                    },
                },
            },
        });

        var result = McpConfigurationLoader.Load(null, project.Path);

        Assert.Empty(result.Servers);
        var error = Assert.Single(result.Errors);
        Assert.Contains(variableName, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ReturnsAnErrorForMalformedProjectJson()
    {
        using var project = TemporaryProject.Create();
        project.WriteRaw("{ invalid");

        var result = McpConfigurationLoader.Load(null, project.Path);

        Assert.Empty(result.Servers);
        Assert.Single(result.Errors);
        Assert.Contains("invalid JSON", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TemporaryProject : IDisposable
    {
        private TemporaryProject(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryProject Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"andy-mcp-config-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryProject(path);
        }

        public void WriteConfiguration(object value) =>
            WriteRaw(JsonSerializer.Serialize(value));

        public void WriteRaw(string json)
        {
            var directory = System.IO.Path.Combine(Path, ".andy");
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                System.IO.Path.Combine(directory, McpConfigurationLoader.ProjectFileName),
                json);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
