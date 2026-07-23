// rivoli-ai/andy-cli#180: the v1 headless contract must not carry silent no-op
// fields. Every field is either applied/verified by the runtime or the config is
// rejected. These tests pin the rejection/enforcement behavior at the config
// boundary: schema (contract library), the loader, and the semantic validator.

using System.IO;
using Andy.Cli.Headless.Contract;
using Andy.Cli.HeadlessConfig;
using Xunit;

namespace Andy.Cli.Tests.HeadlessConfig;

public class HeadlessV1ContractTests
{
    // A config that satisfies headless-config.v1 in full. Tests mutate a copy.
    private const string BaseConfig = """
    {
      "schema_version": 1,
      "run_id": "00000000-0000-0000-0000-0000000000ff",
      "agent": { "slug": "triage-agent", "instructions": "Classify issues." },
      "model": { "provider": "anthropic", "id": "claude-sonnet-4-6" },
      "tools": [],
      "workspace": { "root": "/workspace" },
      "output": { "file": "/tmp/out.json", "stream": "stdout" },
      "limits": { "max_iterations": 50, "timeout_seconds": 300 }
    }
    """;

    // ---- policy_id / boundaries: removed, must be rejected --------------------

    [Fact]
    public void Contract_PolicyId_Rejected()
    {
        var json = BaseConfig.Replace(
            "\"tools\": [],",
            "\"tools\": [], \"policy_id\": \"11111111-2222-3333-4444-555555555555\",");

        var result = HeadlessConfigContract.ValidateConfig(json);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Contract_Boundaries_Rejected()
    {
        var json = BaseConfig.Replace(
            "\"tools\": [],",
            "\"tools\": [], \"boundaries\": [\"read-only\"],");

        var result = HeadlessConfigContract.ValidateConfig(json);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task Loader_PolicyId_RejectedWithActionableMessage()
    {
        var json = BaseConfig.Replace(
            "\"tools\": [],",
            "\"tools\": [], \"policy_id\": \"11111111-2222-3333-4444-555555555555\",");

        var result = await LoadJsonAsync(json);

        Assert.False(result.IsSuccess);
        Assert.Contains("policy_id", result.Error);
        Assert.Contains("false security assurance", result.Error);
    }

    [Fact]
    public async Task Loader_Boundaries_RejectedWithActionableMessage()
    {
        var json = BaseConfig.Replace(
            "\"tools\": [],",
            "\"tools\": [], \"boundaries\": [\"read-only\"],");

        var result = await LoadJsonAsync(json);

        Assert.False(result.IsSuccess);
        Assert.Contains("boundaries", result.Error);
    }

    // ---- unknown/unsupported fields fail validation --------------------------

    [Fact]
    public async Task Loader_UnknownTopLevelField_Rejected()
    {
        var json = BaseConfig.Replace("\"tools\": [],", "\"tools\": [], \"totally_unknown\": 1,");

        var result = await LoadJsonAsync(json);

        Assert.False(result.IsSuccess);
    }

    // ---- api_key_ref: only env:NAME, no secret leak --------------------------

    [Fact]
    public async Task Loader_ApiKeyRef_SecretStoreScheme_Rejected()
    {
        var json = BaseConfig.Replace(
            "\"id\": \"claude-sonnet-4-6\"",
            "\"id\": \"claude-sonnet-4-6\", \"api_key_ref\": \"secret-store:prod-key\"");

        var result = await LoadJsonAsync(json);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Loader_ApiKeyRef_BareValue_RejectedWithoutLeakingIt()
    {
        const string secret = "sk-super-secret-value-1234";
        var json = BaseConfig.Replace(
            "\"id\": \"claude-sonnet-4-6\"",
            $"\"id\": \"claude-sonnet-4-6\", \"api_key_ref\": \"{secret}\"");

        var result = await LoadJsonAsync(json);

        Assert.False(result.IsSuccess);
        // The rejection message must never echo the offending secret value.
        Assert.DoesNotContain(secret, result.Error);
    }

    [Fact]
    public async Task Loader_ApiKeyRef_EnvForm_Accepted()
    {
        var json = BaseConfig.Replace(
            "\"id\": \"claude-sonnet-4-6\"",
            "\"id\": \"claude-sonnet-4-6\", \"api_key_ref\": \"env:ANDY_MODEL_KEY\"");

        var result = await LoadJsonAsync(json);

        Assert.True(result.IsSuccess, result.Error);
    }

    // ---- env_vars reserved-name protection -----------------------------------

    [Theory]
    [InlineData("ANDY_TOKEN")]
    [InlineData("ANDY_MCP_URL")]
    [InlineData("ANDY_PROXY_URL")]
    // Permission-engine controls: a config must not be able to weaken or disable
    // the fail-closed permission gate from inside its own env_vars (env_vars are
    // applied before the permission engine is built).
    [InlineData("ANDY_PERMISSION_MODE")]
    [InlineData("ANDY_PERMISSIONS_FILE")]
    [InlineData("ANDY_PERMISSIONS_JSON")]
    public async Task Loader_EnvVars_ShadowingReserved_Rejected(string reserved)
    {
        var json = BaseConfig.Replace(
            "\"tools\": [],",
            $"\"tools\": [], \"env_vars\": {{ \"{reserved}\": \"attacker-controlled\" }},");

        var result = await LoadJsonAsync(json);

        Assert.False(result.IsSuccess);
        Assert.Contains(reserved, result.Error);
    }

    [Fact]
    public async Task Loader_EnvVars_NonReserved_Accepted()
    {
        var json = BaseConfig.Replace(
            "\"tools\": [],",
            "\"tools\": [], \"env_vars\": { \"RIVOLI_TENANT_ID\": \"tenant-acme\" },");

        var result = await LoadJsonAsync(json);

        Assert.True(result.IsSuccess, result.Error);
    }

    // ---- FIFO requires event_sink.path (cross-field) -------------------------

    [Fact]
    public async Task Loader_FifoStream_WithoutSinkPath_Rejected()
    {
        var json = BaseConfig.Replace("\"stream\": \"stdout\"", "\"stream\": \"fifo\"");

        var result = await LoadJsonAsync(json);

        // Rejected by the schema's allOf/if-then cross-field rule (the loader's
        // semantic validator is a defense-in-depth backstop for the same rule).
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Validator_FifoStream_WithoutSinkPath_ReturnsClearError()
    {
        // Direct semantic-validator coverage: the friendly, path-naming message
        // the loader would surface if a config reached it without a sink path.
        var config = new HeadlessRunConfig
        {
            Output = new HeadlessOutput { File = "/tmp/out", Stream = "fifo" },
        };

        var error = HeadlessConfigValidator.Validate(config);

        Assert.NotNull(error);
        Assert.Contains("event_sink.path", error);
    }

    [Fact]
    public void Contract_FifoStream_WithoutSinkPath_RejectedBySchema()
    {
        var json = BaseConfig.Replace("\"stream\": \"stdout\"", "\"stream\": \"fifo\"");

        var result = HeadlessConfigContract.ValidateConfig(json);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Loader_FifoStream_WithSinkPath_Accepted()
    {
        var json = BaseConfig
            .Replace("\"stream\": \"stdout\"", "\"stream\": \"fifo\"")
            .Replace("\"tools\": [],", "\"tools\": [], \"event_sink\": { \"path\": \"/run/andy/events.fifo\" },");

        var result = await LoadJsonAsync(json);

        Assert.True(result.IsSuccess, result.Error);
    }

    // ---- required actions use exact, non-pattern command matching ------------

    [Fact]
    public async Task Loader_RequiredExecuteCommand_Accepted()
    {
        var json = BaseConfig.Replace(
            "\"tools\": [],",
            "\"tools\": [], \"required_actions\": [{ \"tool_name\": \"execute_command\", "
                + "\"command_equals\": \"dotnet test\", \"at_least\": 1 }],");

        var result = await LoadJsonAsync(json);

        Assert.True(result.IsSuccess, result.Error);
        var requirement = Assert.Single(result.Config!.RequiredActions);
        Assert.Equal("dotnet test", requirement.CommandEquals);
        Assert.Equal(1, requirement.AtLeast);
    }

    [Fact]
    public void Validator_CommandConstraintOnNonCommandTool_Rejected()
    {
        var config = new HeadlessRunConfig
        {
            RequiredActions =
            [
                new HeadlessRequiredAction
                {
                    ToolName = "read_file",
                    CommandEquals = "dotnet test"
                }
            ]
        };

        var error = HeadlessConfigValidator.Validate(config);

        Assert.Contains("only valid", error);
    }

    [Theory]
    [InlineData(" dotnet test")]
    [InlineData("dotnet test ")]
    [InlineData("dotnet test\n")]
    [InlineData("dotnet test *")]
    [InlineData("dotnet test ?")]
    public void RequiredCommandMatcher_RejectsAmbiguousOrUnsafeForms(string command)
    {
        var ok = RequiredCommandMatcher.TryNormalize(command, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    // ---- validator unit surface ----------------------------------------------

    [Theory]
    [InlineData("env:ANDY_MODEL_KEY", true)]
    [InlineData("env:_X", true)]
    [InlineData("env:", false)]
    [InlineData("secret-store:foo", false)]
    [InlineData("sk-1234", false)]
    [InlineData("env:has space", false)]
    public void TryParseEnvRef_Classifies(string reference, bool expectedOk)
    {
        var ok = HeadlessConfigValidator.TryParseEnvRef(reference, out var name, out var error);

        Assert.Equal(expectedOk, ok);
        if (ok)
        {
            Assert.NotEmpty(name);
            Assert.Null(error);
        }
        else
        {
            Assert.NotNull(error);
        }
    }

    private static async Task<HeadlessConfigLoadResult> LoadJsonAsync(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"h180-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        try
        {
            return await HeadlessConfigLoader.TryLoadAsync(path);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }
}
