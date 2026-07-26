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
    public void ProjectAndUserListsAreMerged()
    {
        WriteProject("{ \"planReadOnlyTools\": [\"mcp__docs__search\"] }");
        WriteUser("{ \"planReadOnlyTools\": [\"mcp__jira__get_issue\"] }");

        var policy = ModeConfigFile.LoadPolicy(_project, _user);

        Assert.True(policy.Evaluate("mcp__docs__search", null).Allowed);
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
        WriteProject("{ \"planReadOnlyTools\": [\"write_file\", \"execute_command\"] }");

        var policy = ModeConfigFile.LoadPolicy(_project, _user);

        Assert.False(policy.Evaluate("write_file", null).Allowed);
        Assert.False(policy.Evaluate("execute_command", null).Allowed);
    }

    [Fact]
    public void PathFor_UsesTheConventionalLocation()
    {
        Assert.Equal(
            Path.Combine("/somewhere", ".andy", "modes.json"),
            ModeConfigFile.PathFor("/somewhere"));
    }
}
