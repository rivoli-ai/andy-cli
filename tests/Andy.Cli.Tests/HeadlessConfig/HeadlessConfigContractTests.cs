using System.Text.Json;
using Andy.Cli.Headless.Contract;
using Xunit;

namespace Andy.Cli.Tests.HeadlessConfig;

/// <summary>
/// Verifies the public contract-validation surface used by external generators.
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
        const string json = """
        {
          "version": 1,
          "agent": { "name": "planner" },
          "provider": { "name": "cohere" },
          "task": { "prompt": "hello" }
        }
        """;

        var result = HeadlessConfigContract.ValidateConfig(json);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void ValidateConfig_MissingRequiredField_Fails()
    {
        // Missing the required "task" object.
        const string json = """
        {
          "version": 1,
          "agent": { "name": "planner" },
          "provider": { "name": "openai" }
        }
        """;

        var result = HeadlessConfigContract.ValidateConfig(json);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void ValidateConfig_WrongVersionConstant_Fails()
    {
        const string json = """
        {
          "version": 2,
          "agent": { "name": "planner" },
          "provider": { "name": "openai" },
          "task": { "prompt": "hello" }
        }
        """;

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
        const string json = """
        {
          "type": "run.started",
          "seq": 0,
          "ts": "2026-05-29T12:00:00Z"
        }
        """;

        var result = HeadlessConfigContract.ValidateEvent(json);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void ValidateEvent_UnknownType_Fails()
    {
        const string json = """
        {
          "type": "run.exploded",
          "seq": 0,
          "ts": "2026-05-29T12:00:00Z"
        }
        """;

        var result = HeadlessConfigContract.ValidateEvent(json);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void GetConfigSchemaText_ReturnsEmbeddedSchema()
    {
        var text = HeadlessConfigContract.GetConfigSchemaText();

        Assert.Contains("headless-config.v1.json", text);
        Assert.Contains("\"version\"", text);
    }

    [Fact]
    public void GetEventSchemaText_ReturnsEmbeddedSchema()
    {
        var text = HeadlessConfigContract.GetEventSchemaText();

        Assert.Contains("headless-events.v1.json", text);
    }
}
