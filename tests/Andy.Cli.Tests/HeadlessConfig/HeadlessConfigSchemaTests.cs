// AQ1 tests — validate the headless config JSON Schema v1 (rivoli-ai/andy-cli#46).
//
// Exercises the contract the andy-containers configurator (Epic AP4) will write
// and `andy-cli run --headless --config <path>` (AQ2) will consume. Whenever the
// JSON Schema changes, positive fixtures and negative cases both live here so a
// breaking change is loud at CI time, not at runtime in a container.

using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Xunit;

namespace Andy.Cli.Tests.HeadlessConfig;

public class HeadlessConfigSchemaTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string SchemaPath = Path.Combine(RepoRoot, "schemas", "headless-config.v1.json");
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
            throw new InvalidOperationException("Could not locate Andy.Cli.sln walking up from test output directory.");
        }
        return dir.FullName;
    }

    // JsonSchema.Net registers any schema carrying an "$id" into a process-global
    // SchemaRegistry and throws "Overwriting registered schemas is not permitted"
    // when another component (the HeadlessConfigLoader, the AQ8 contract library)
    // registers the same $id during the same test run. The schema has no internal
    // "$ref", so the $id is unused for resolution here; strip it before building so
    // this loader never touches the global registry. Cache the parsed schema so
    // every test shares one instance.
    private static readonly Lazy<JsonSchema> s_schema = new(() =>
    {
        var node = JsonNode.Parse(File.ReadAllText(SchemaPath))
            ?? throw new InvalidOperationException($"Schema at {SchemaPath} parsed to null.");
        if (node is JsonObject obj)
        {
            obj.Remove("$id");
        }
        return JsonSerializer.Deserialize<JsonSchema>(node)
            ?? throw new InvalidOperationException($"Schema at {SchemaPath} deserialized to null.");
    });

    private static JsonSchema LoadSchema() => s_schema.Value;

    private static JsonNode LoadJson(string path)
    {
        var text = File.ReadAllText(path);
        var node = JsonNode.Parse(text);
        if (node is null)
        {
            throw new InvalidOperationException($"JSON at {path} parsed to null.");
        }
        return node;
    }

    [Theory]
    [InlineData("triage-headless.json")]
    [InlineData("planning-headless.json")]
    [InlineData("coding-headless.json")]
    public void FixtureValidatesAgainstSchema(string fixtureName)
    {
        var schema = LoadSchema();
        var node = LoadJson(Path.Combine(SamplesDir, fixtureName));

        var result = schema.Evaluate(ToElement(node), new EvaluationOptions
        {
            OutputFormat = OutputFormat.List
        });

        Assert.True(result.IsValid,
            $"Fixture {fixtureName} failed schema validation: {FormatErrors(result)}");
    }

    [Fact]
    public void SchemaVersion_MustBeExactly1()
    {
        var schema = LoadSchema();
        var config = MinimalValidConfig();
        config["schema_version"] = 2;

        var result = schema.Evaluate(ToElement(config));
        Assert.False(result.IsValid, "schema_version != 1 must be rejected");
    }

    [Fact]
    public void AgentSlug_RejectsUppercase()
    {
        var schema = LoadSchema();
        var config = MinimalValidConfig();
        config["agent"]!["slug"] = "Triage-Agent";

        var result = schema.Evaluate(ToElement(config));
        Assert.False(result.IsValid, "agent.slug must be lowercase-only");
    }

    [Fact]
    public void AgentSlug_RejectsLeadingDigit()
    {
        var schema = LoadSchema();
        var config = MinimalValidConfig();
        config["agent"]!["slug"] = "1triage";

        var result = schema.Evaluate(ToElement(config));
        Assert.False(result.IsValid, "agent.slug must begin with a letter");
    }

    [Fact]
    public void MissingRequired_RunId_Rejected()
    {
        var schema = LoadSchema();
        var config = MinimalValidConfig();
        config.AsObject().Remove("run_id");

        var result = schema.Evaluate(ToElement(config));
        Assert.False(result.IsValid, "run_id is required");
    }

    [Fact]
    public void MissingRequired_Limits_Rejected()
    {
        var schema = LoadSchema();
        var config = MinimalValidConfig();
        config.AsObject().Remove("limits");

        var result = schema.Evaluate(ToElement(config));
        Assert.False(result.IsValid, "limits is required");
    }

    [Fact]
    public void McpToolBinding_Validates()
    {
        var schema = LoadSchema();
        var config = MinimalValidConfig();
        config["tools"] = new JsonArray(
            new JsonObject
            {
                ["name"] = "issues.get",
                ["transport"] = "mcp",
                ["endpoint"] = "https://mcp.internal/issues.get"
            });

        var result = schema.Evaluate(ToElement(config));
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void CliToolBinding_Validates()
    {
        var schema = LoadSchema();
        var config = MinimalValidConfig();
        config["tools"] = new JsonArray(
            new JsonObject
            {
                ["name"] = "tasks.list",
                ["transport"] = "cli",
                ["binary"] = "andy-tasks-cli",
                ["command"] = new JsonArray("andy-tasks-cli", "list")
            });

        var result = schema.Evaluate(ToElement(config));
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void ToolBinding_RejectsUnknownTransport()
    {
        var schema = LoadSchema();
        var config = MinimalValidConfig();
        config["tools"] = new JsonArray(
            new JsonObject
            {
                ["name"] = "weird.tool",
                ["transport"] = "websocket",
                ["endpoint"] = "ws://example/weird"
            });

        var result = schema.Evaluate(ToElement(config));
        Assert.False(result.IsValid, "transport must be 'mcp' or 'cli'");
    }

    [Fact]
    public void McpToolBinding_MissingEndpoint_PassesSchema()
    {
        // ADR 0002: endpoint is now optional in the MCP tool binding schema.
        // Semantic validation (MCP without endpoint and no mcp_gateway) is
        // handled by HeadlessConfigLoader, not the JSON Schema.
        var schema = LoadSchema();
        var config = MinimalValidConfig();
        config["tools"] = new JsonArray(
            new JsonObject
            {
                ["name"] = "issues.get",
                ["transport"] = "mcp"
                // endpoint absent — allowed by schema when mcp_gateway is present
            });

        var result = schema.Evaluate(ToElement(config));
        Assert.True(result.IsValid, "MCP binding without endpoint passes schema (endpoint is now optional)");
    }

    [Fact]
    public void Model_RejectsUnknownProvider()
    {
        var schema = LoadSchema();
        var config = MinimalValidConfig();
        config["model"]!["provider"] = "cohere";

        var result = schema.Evaluate(ToElement(config));
        Assert.False(result.IsValid, "model.provider enum is closed");
    }

    [Fact]
    public void Model_AcceptsOpenRouterProvider()
    {
        var schema = LoadSchema();
        var config = MinimalValidConfig();
        config["model"]!["provider"] = "openrouter";
        config["model"]!["id"] = "xiaomi/mimo-v2.5";

        var result = schema.Evaluate(ToElement(config));
        Assert.True(result.IsValid, "openrouter is a supported provider");
    }

    // AX.4 (rivoli-ai/conductor#2091): optional permission allow-list block.
    [Fact]
    public void Permissions_AllowedTools_Validates()
    {
        var schema = LoadSchema();
        var config = MinimalValidConfig();
        config["permissions"] = new JsonObject
        {
            ["allowed_tools"] = new JsonArray("write_file", "execute_command")
        };

        var result = schema.Evaluate(ToElement(config));
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void Permissions_Absent_StillValid()
    {
        // Backward compatible: omitting the block keeps the config valid.
        var schema = LoadSchema();
        var config = MinimalValidConfig();

        var result = schema.Evaluate(ToElement(config));
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void Permissions_RejectsUnknownProperty()
    {
        var schema = LoadSchema();
        var config = MinimalValidConfig();
        config["permissions"] = new JsonObject
        {
            ["allowed_tools"] = new JsonArray("write_file"),
            ["bogus"] = true
        };

        var result = schema.Evaluate(ToElement(config));
        Assert.False(result.IsValid, "permissions has additionalProperties:false");
    }

    [Fact]
    public void Permissions_RejectsUppercaseToolName()
    {
        var schema = LoadSchema();
        var config = MinimalValidConfig();
        config["permissions"] = new JsonObject
        {
            ["allowed_tools"] = new JsonArray("Write_File")
        };

        var result = schema.Evaluate(ToElement(config));
        Assert.False(result.IsValid, "allowed_tools entries must match the tool-name pattern");
    }

    [Fact]
    public void Limits_MaxIterationsZero_Rejected()
    {
        var schema = LoadSchema();
        var config = MinimalValidConfig();
        config["limits"]!["max_iterations"] = 0;

        var result = schema.Evaluate(ToElement(config));
        Assert.False(result.IsValid, "max_iterations must be >= 1");
    }

    [Fact]
    public void Limits_TimeoutExceedsDay_Rejected()
    {
        var schema = LoadSchema();
        var config = MinimalValidConfig();
        config["limits"]!["timeout_seconds"] = 86401;

        var result = schema.Evaluate(ToElement(config));
        Assert.False(result.IsValid, "timeout_seconds must be <= 86400 (one day)");
    }

    [Fact]
    public void AdditionalProperties_Rejected()
    {
        var schema = LoadSchema();
        var config = MinimalValidConfig();
        config["something_extra"] = "should be rejected";

        var result = schema.Evaluate(ToElement(config));
        Assert.False(result.IsValid, "top-level additionalProperties: false");
    }

    [Fact]
    public void EventSinkSubject_RejectsWrongNamespace()
    {
        var schema = LoadSchema();
        var config = MinimalValidConfig();
        config["event_sink"] = new JsonObject
        {
            ["nats_subject"] = "andy.tasks.events.goal.abc.planned"
        };

        var result = schema.Evaluate(ToElement(config));
        Assert.False(result.IsValid, "event_sink.nats_subject must live in andy.containers.events.run.*");
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Converts a <see cref="JsonNode"/> tree into a <see cref="JsonElement"/>
    /// so it can be passed to <see cref="JsonSchema.Evaluate(JsonElement,
    /// EvaluationOptions?)"/>. JsonSchema.Net 9.x's primary overload is
    /// element-based; doing one conversion here keeps tests readable.
    /// </summary>
    private static JsonElement ToElement(JsonNode node)
        => JsonDocument.Parse(node.ToJsonString()).RootElement;

    private static JsonObject MinimalValidConfig() => new()
    {
        ["schema_version"] = 1,
        ["run_id"] = "00000000-0000-0000-0000-0000000000ff",
        ["agent"] = new JsonObject
        {
            ["slug"] = "triage-agent",
            ["instructions"] = "Classify issues."
        },
        ["model"] = new JsonObject
        {
            ["provider"] = "anthropic",
            ["id"] = "claude-sonnet-4-6"
        },
        ["tools"] = new JsonArray(),
        ["workspace"] = new JsonObject
        {
            ["root"] = "/workspace"
        },
        ["output"] = new JsonObject
        {
            ["file"] = "/tmp/out.json",
            ["stream"] = "stdout"
        },
        ["limits"] = new JsonObject
        {
            ["max_iterations"] = 50,
            ["timeout_seconds"] = 300
        }
    };

    private static string FormatErrors(EvaluationResults results)
    {
        var writer = new StringWriter();
        if (results.Details is null)
        {
            return results.IsValid ? "(valid)" : "(invalid, no details available)";
        }
        foreach (var detail in results.Details)
        {
            if (detail.IsValid) continue;
            var errors = detail.Errors is null
                ? "(no errors)"
                : string.Join("; ", detail.Errors.Select(kv => $"{kv.Key}={kv.Value}"));
            writer.WriteLine($"  {detail.EvaluationPath}: {errors}");
        }
        return writer.ToString();
    }
}
