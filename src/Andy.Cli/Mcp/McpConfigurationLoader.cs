using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Andy.Cli.Mcp;

/// <summary>
/// Loads interactive MCP servers from appsettings and the project-local
/// .andy/mcp-servers.json file. Project configuration overrides an appsettings
/// server with the same name.
/// </summary>
public static partial class McpConfigurationLoader
{
    public const string ProjectFileName = "mcp-servers.json";

    public static McpConfigurationLoadResult Load(
        IConfiguration? applicationConfiguration,
        string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);

        var servers = new Dictionary<string, McpServerConfiguration>(
            StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var sources = new List<string>();

        LoadApplicationConfiguration(applicationConfiguration, servers, sources);

        var projectPath = Path.Combine(projectDirectory, ".andy", ProjectFileName);
        if (File.Exists(projectPath))
        {
            sources.Add(projectPath);
            try
            {
                var json = File.ReadAllText(projectPath);
                var file = JsonSerializer.Deserialize<McpServerConfigurationFile>(
                    json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        AllowTrailingCommas = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                    });

                if (file?.Servers is null)
                {
                    errors.Add($"{projectPath}: expected a top-level 'servers' object.");
                }
                else
                {
                    foreach (var (name, server) in file.Servers)
                    {
                        server.Name = name;
                        servers[name] = server;
                    }
                }
            }
            catch (JsonException ex)
            {
                errors.Add($"{projectPath}: invalid JSON ({ex.Message}).");
            }
            catch (IOException ex)
            {
                errors.Add($"{projectPath}: could not be read ({ex.Message}).");
            }
            catch (UnauthorizedAccessException)
            {
                errors.Add($"{projectPath}: access denied.");
            }
        }

        var validServers = new List<McpServerConfiguration>();
        foreach (var server in servers.Values.OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase))
        {
            ExpandEnvironmentVariables(server, errors);
            if (Validate(server, errors))
            {
                validServers.Add(server);
            }
        }

        return new McpConfigurationLoadResult(validServers, errors, sources);
    }

    private static void LoadApplicationConfiguration(
        IConfiguration? configuration,
        IDictionary<string, McpServerConfiguration> servers,
        ICollection<string> sources)
    {
        if (configuration is null)
        {
            return;
        }

        var section = configuration.GetSection("Mcp:Servers");
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
        {
            return;
        }

        sources.Add("appsettings.json (Mcp:Servers)");
        foreach (var child in children)
        {
            var server = new McpServerConfiguration
            {
                Name = child.Key,
                Transport = child["Transport"] ?? string.Empty,
                Enabled = !bool.TryParse(child["Enabled"], out var enabled) || enabled,
                Url = child["Url"],
                Command = child["Command"],
                WorkingDirectory = child["WorkingDirectory"],
                Args = child.GetSection("Args").GetChildren()
                    .Select(value => value.Value ?? string.Empty)
                    .ToList(),
                Environment = ReadStringMap(child.GetSection("Env"), StringComparer.Ordinal),
                Headers = ReadStringMap(
                    child.GetSection("Headers"),
                    StringComparer.OrdinalIgnoreCase),
            };
            servers[child.Key] = server;
        }
    }

    private static Dictionary<string, string> ReadStringMap(
        IConfigurationSection section,
        IEqualityComparer<string> comparer)
    {
        var result = new Dictionary<string, string>(comparer);
        foreach (var value in section.GetChildren())
        {
            result[value.Key] = value.Value ?? string.Empty;
        }
        return result;
    }

    private static void ExpandEnvironmentVariables(
        McpServerConfiguration server,
        ICollection<string> errors)
    {
        server.Url = Expand(server.Url, server.Name, "url", errors);
        server.Command = Expand(server.Command, server.Name, "command", errors);
        server.WorkingDirectory = Expand(
            server.WorkingDirectory,
            server.Name,
            "workingDirectory",
            errors);

        for (var index = 0; index < server.Args.Count; index++)
        {
            server.Args[index] =
                Expand(server.Args[index], server.Name, $"args[{index}]", errors) ?? string.Empty;
        }

        foreach (var key in server.Environment.Keys.ToList())
        {
            server.Environment[key] =
                Expand(server.Environment[key], server.Name, $"env.{key}", errors) ?? string.Empty;
        }

        foreach (var key in server.Headers.Keys.ToList())
        {
            server.Headers[key] =
                Expand(server.Headers[key], server.Name, $"headers.{key}", errors) ?? string.Empty;
        }
    }

    private static string? Expand(
        string? value,
        string serverName,
        string field,
        ICollection<string> errors)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return EnvironmentVariablePattern().Replace(value, match =>
        {
            var variable = match.Groups[1].Value;
            var expanded = Environment.GetEnvironmentVariable(variable);
            if (expanded is null)
            {
                errors.Add(
                    $"MCP server '{serverName}' field '{field}' references unset environment variable '{variable}'.");
                return match.Value;
            }
            return expanded;
        });
    }

    private static bool Validate(
        McpServerConfiguration server,
        ICollection<string> errors)
    {
        var valid = true;
        if (string.IsNullOrWhiteSpace(server.Name))
        {
            errors.Add("MCP server names must not be blank.");
            valid = false;
        }

        var transport = server.Transport.Trim().ToLowerInvariant();
        switch (transport)
        {
            case "stdio":
                if (string.IsNullOrWhiteSpace(server.Command))
                {
                    errors.Add($"MCP server '{server.Name}' requires 'command' for stdio transport.");
                    valid = false;
                }
                break;
            case "http":
            case "sse":
            case "streamable-http":
                if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var endpoint) ||
                    (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
                {
                    errors.Add(
                        $"MCP server '{server.Name}' requires an absolute HTTP(S) 'url'.");
                    valid = false;
                }
                break;
            default:
                errors.Add(
                    $"MCP server '{server.Name}' has unsupported transport '{server.Transport}'. "
                    + "Use 'stdio' or 'http'.");
                valid = false;
                break;
        }

        if (ContainsUnexpandedVariable(server))
        {
            valid = false;
        }

        return valid;
    }

    private static bool ContainsUnexpandedVariable(McpServerConfiguration server)
    {
        static bool Contains(string? value) =>
            value is not null && EnvironmentVariablePattern().IsMatch(value);

        return Contains(server.Url) ||
               Contains(server.Command) ||
               Contains(server.WorkingDirectory) ||
               server.Args.Any(Contains) ||
               server.Environment.Values.Any(Contains) ||
               server.Headers.Values.Any(Contains);
    }

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentVariablePattern();
}
