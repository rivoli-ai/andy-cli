using System.Text.Json;
using Andy.Cli.Headless.Contract;
using Xunit;

namespace Andy.Cli.Tests.HeadlessConfig;

/// <summary>
/// Verifies the public contract-validation surface used by external generators
/// (andy-containers, conductor) to check their produced headless config against
/// the canonical schema. Field names mirror schemas/headless-config.v1.json.
/// </summary>
public class HeadlessConfigContractTests
{
    private static readonly string RepoRoot = GetRepoRoot();

    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Andy.Cli.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    // A configuration that satisfies headless-config.v1.json in full.
    private const string ValidConfigJson = """
    {
      "schema_version": 1,
      "run_id": "00000000-0000-0000-0000-0000000000ff",
      "agent": {
        "slug": "triage-agent",
        "instructions": "Classify issues."
      },
      "model": {
        "provider": "anthropic",
        "id": "claude-sonnet-4-6"
      },
      "tools": [],
      "workspace": { "root": "/workspace" },
      "output": { "file": "/tmp/out.json", "stream": "stdout" },
      "limits": { "max_iterations": 50, "timeout_seconds": 300 }
    }
    """;

    [Fact]
    public void SchemaVersion_Is_One()
    {
        Assert.Equal(1, HeadlessConfigContract.SchemaVersion);
    }

    [Fact]
    public void ValidateConfig_KnownGoodSample_Passes()
    {
        var samplePath = Path.Combine(RepoRoot, "schemas", "samples", "planning-headless.json");
        var json = File.ReadAllText(samplePath);

        var result = HeadlessConfigContract.ValidateConfig(json);

        Assert.True(result.IsValid, "Expected the known-good sample to validate. Errors: " + string.Join("; ", result.Errors));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidateConfig_InlineValidConfig_Passes()
    {
        var result = HeadlessConfigContract.ValidateConfig(ValidConfigJson);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidateConfig_JsonElementOverload_Passes()
    {
        var samplePath = Path.Combine(RepoRoot, "schemas", "samples", "planning-headless.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(samplePath));

        var result = HeadlessConfigContract.ValidateConfig(doc.RootElement);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void ValidateConfig_UnknownProvider_Fails()
    {
        // model.provider enum is closed; "cohere" is not a member.
        var json = ValidConfigJson.Replace("\"provider\": \"anthropic\"", "\"provider\": \"cohere\"");

        var result = HeadlessConfigContract.ValidateConfig(json);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void ValidateConfig_MissingRequiredField_Fails()
    {
        // Drop the required top-level "limits" object.
        const string json = """
        {
          "schema_version": 1,
          "run_id": "00000000-0000-0000-0000-0000000000ff",
          "agent": { "slug": "triage-agent", "instructions": "Classify issues." },
          "model": { "provider": "anthropic", "id": "claude-sonnet-4-6" },
          "tools": [],
          "workspace": { "root": "/workspace" },
          "output": { "file": "/tmp/out.json", "stream": "stdout" }
        }
        """;

        var result = HeadlessConfigContract.ValidateConfig(json);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void ValidateConfig_WrongVersionConstant_Fails()
    {
        // schema_version is a const: 1.
        var json = ValidConfigJson.Replace("\"schema_version\": 1", "\"schema_version\": 2");

        var result = HeadlessConfigContract.ValidateConfig(json);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void ValidateConfig_MalformedJson_FailsWithError()
    {
        var result = HeadlessConfigContract.ValidateConfig("{ not json");

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void ValidateConfig_NullJson_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => HeadlessConfigContract.ValidateConfig((string)null!));
    }

    [Fact]
    public void ValidateEvent_ValidRecord_Passes()
    {
        // Envelope per headless-events.v1.json: schema_version, ts, kind, data.
        const string json = """
        {
          "schema_version": 1,
          "ts": "2026-05-29T12:00:00+00:00",
          "kind": "started",
          "data": {
            "run_id": "00000000-0000-0000-0000-0000000000ff",
            "agent_slug": "triage-agent",
            "model_provider": "anthropic",
            "model_id": "claude-sonnet-4-6",
            "tool_count": 0
          }
        }
        """;

        var result = HeadlessConfigContract.ValidateEvent(json);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void ValidateEvent_UnknownKind_Fails()
    {
        // kind enum is closed; "exploded" is not a member.
        const string json = """
        {
          "schema_version": 1,
          "ts": "2026-05-29T12:00:00+00:00",
          "kind": "exploded",
          "data": {}
        }
        """;

        var result = HeadlessConfigContract.ValidateEvent(json);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void ValidateEvent_RequiredActionVerification_Passes()
    {
        const string json = """
        {
          "schema_version": 1,
          "ts": "2026-07-22T12:00:00+00:00",
          "kind": "required_action_verification",
          "data": {
            "satisfied": false,
            "requirements": [{
              "index": 0,
              "tool_name": "execute_command",
              "command_digest": "sha256:abc",
              "at_least": 1,
              "observed_matches": 1,
              "successful_matches": 0,
              "satisfied": false,
              "calls": [{"call_id": "call-1", "outcome": "denied"}]
            }]
          }
        }
        """;

        var result = HeadlessConfigContract.ValidateEvent(json);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void GetConfigSchemaText_ReturnsEmbeddedSchema()
    {
        var text = HeadlessConfigContract.GetConfigSchemaText();

        Assert.Contains("headless-config.v1.json", text);
        Assert.Contains("schema_version", text);
    }

    [Fact]
    public void GetEventSchemaText_ReturnsEmbeddedSchema()
    {
        var text = HeadlessConfigContract.GetEventSchemaText();

        Assert.Contains("headless-events.v1.json", text);
    }
}
