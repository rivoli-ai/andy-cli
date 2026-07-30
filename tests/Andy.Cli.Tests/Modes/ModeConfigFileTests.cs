using System;
using System.IO;
using Andy.Cli.Modes;
using Xunit;

namespace Andy.Cli.Tests.Modes;

/// <summary>
/// The read-only opt-in file (issue #278). It exists so Plan mode stays usable with MCP tools; it
/// must never be able to weaken the built-in classification or break start-up when malformed.
/// </summary>
public sealed class ModeConfigFileTests : IDisposable
{
    private readonly string _project;
    private readonly string _user;

    public ModeConfigFileTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "andy-mode-config-" + Guid.NewGuid().ToString("N")[..8]);
        _project = Path.Combine(root, "project");
        _user = Path.Combine(root, "home");
        Directory.CreateDirectory(Path.Combine(_project, ".andy"));
        Directory.CreateDirectory(Path.Combine(_user, ".andy"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_project)!, recursive: true);
        }
        catch (IOException)
        {
            // Test hygiene only.
        }
    }

    private void WriteProject(string json) => File.WriteAllText(ModeConfigFile.PathFor(_project), json);

    private void WriteUser(string json) => File.WriteAllText(ModeConfigFile.PathFor(_user), json);

    [Fact]
    public void MissingFiles_YieldTheDefaultPolicy()
    {
        var policy = ModeConfigFile.LoadPolicy(_project, _user);

        Assert.Empty(policy.AdditionalReadOnlyTools);
        Assert.False(policy.Evaluate("mcp__docs__search", null).Allowed);
    }

    [Fact]
    public void OnlyTheUserListIsHonoured()
    {
        // Grants are per developer. A project file cannot contribute, because it is committed and
        // would grant access to teammates who never saw the opt-in prompt.
        WriteProject("{ \"planReadOnlyTools\": [\"mcp__docs__search\"] }");
        WriteUser("{ \"planReadOnlyTools\": [\"mcp__jira__get_issue\"] }");

        var policy = ModeConfigFile.LoadPolicy(_project, _user);

        Assert.False(policy.Evaluate("mcp__docs__search", null).Allowed);
        Assert.True(policy.Evaluate("mcp__jira__get_issue", null).Allowed);
        Assert.False(policy.Evaluate("mcp__jira__create_issue", null).Allowed);
    }

    [Fact]
    public void MalformedJson_LeavesPlanModeAtItsFailClosedDefault()
    {
        WriteProject("{ this is not json");

        var policy = ModeConfigFile.LoadPolicy(_project, _user);

        Assert.Empty(policy.AdditionalReadOnlyTools);
        Assert.False(policy.Evaluate("mcp__docs__search", null).Allowed);
    }

    [Fact]
    public void OptInCannotOverrideTheBuiltInMutatingClassification()
    {
        WriteUser("{ \"planReadOnlyTools\": [\"write_file\", \"execute_command\"] }");

        var policy = ModeConfigFile.LoadPolicy(_project, _user);

        Assert.False(policy.Evaluate("write_file", null).Allowed);
        Assert.False(policy.Evaluate("execute_command", null).Allowed);
    }

    [Fact]
    public void MalformedUserJson_LeavesPlanModeAtItsFailClosedDefault()
    {
        WriteUser("{ this is not json");

        var policy = ModeConfigFile.LoadPolicy(_project, _user);

        Assert.Empty(policy.AdditionalReadOnlyTools);
        Assert.False(policy.Evaluate("mcp__docs__search", null).Allowed);
    }

    [Fact]
    public void ProjectScopeDiagnostics_AreEmptyForAFileWithNoGrantKeys()
    {
        WriteProject("{ }");

        Assert.Empty(ModeConfigFile.ProjectScopeDiagnostics(_project, _user));
    }

    [Fact]
    public void ProjectScopeDiagnostics_ReportGrantKeysAndWhereTheyBelong()
    {
        WriteProject("{ \"planReadOnlyMcpServers\": [\"docs\"] }");

        var message = Assert.Single(ModeConfigFile.ProjectScopeDiagnostics(_project, _user));

        Assert.Contains("per developer", message);
        Assert.Contains("DENIED", message);
        Assert.Contains(ModeConfigFile.PathFor(_user), message);
    }

    [Fact]
    public void HasGrantKeys_DetectsEachPerDeveloperKey()
    {
        Assert.False(new ModeConfigFile().HasGrantKeys);
        Assert.True(new ModeConfigFile { PlanReadOnlyTools = { "a" } }.HasGrantKeys);
        Assert.True(new ModeConfigFile { PlanReadOnlyMcpServers = { "a" } }.HasGrantKeys);
        Assert.True(new ModeConfigFile { McpPlanOptInAsked = { ["a"] = new() } }.HasGrantKeys);
    }

    [Fact]
    public void PathFor_UsesTheConventionalLocation()
    {
        Assert.Equal(
            Path.Combine("/somewhere", ".andy", "modes.json"),
            ModeConfigFile.PathFor("/somewhere"));
    }
}
