using System;
using System.IO;
using System.Linq;
using Andy.Cli.Modes;
using Xunit;

namespace Andy.Cli.Tests.Modes;

/// <summary>
/// The Plan-mode opt-in store (issue #278 follow-up): grants, revokes, persistence, and the
/// invariant that no grant shape can re-enable a mutating tool.
/// </summary>
public sealed class PlanModeGrantStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _project;
    private readonly string _user;

    public PlanModeGrantStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "andy-plan-grants-" + Guid.NewGuid().ToString("N")[..8]);
        _project = Path.Combine(_root, "project");
        _user = Path.Combine(_root, "home");
        Directory.CreateDirectory(_project);
        Directory.CreateDirectory(_user);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Test hygiene only.
        }
    }

    private PlanModeGrantStore Store() => new(_project, _user);

    private void WriteUserConfig(string json)
    {
        Directory.CreateDirectory(Path.Combine(_user, ".andy"));
        File.WriteAllText(ModeConfigFile.PathFor(_user), json);
    }

    private void WriteProjectConfig(string json)
    {
        Directory.CreateDirectory(Path.Combine(_project, ".andy"));
        File.WriteAllText(ModeConfigFile.PathFor(_project), json);
    }

    [Fact]
    public void McpToolsAreDeniedBeforeAnyOptIn()
    {
        var store = Store();

        Assert.False(store.CurrentPolicy.Evaluate("mcp_docs_search", null).Allowed);
        Assert.True(store.List().IsEmpty);
    }

    [Fact]
    public void PerToolGrantAllowsExactlyThatTool()
    {
        var store = Store();

        var result = store.GrantTools(new[] { "mcp_docs_search" });

        Assert.True(result.Success, result.Message);
        Assert.True(store.CurrentPolicy.Evaluate("mcp_docs_search", null).Allowed);
        Assert.False(store.CurrentPolicy.Evaluate("mcp_docs_fetch", null).Allowed);
    }

    [Fact]
    public void ServerWideGrantAllowsEveryToolFromThatServer()
    {
        var store = Store();

        var result = store.GrantServer("docs");

        Assert.True(result.Success, result.Message);
        Assert.True(store.CurrentPolicy.Evaluate("mcp_docs_search", null).Allowed);
        Assert.True(store.CurrentPolicy.Evaluate("mcp_docs_fetch", null).Allowed);
        // ...and nothing from a different server.
        Assert.False(store.CurrentPolicy.Evaluate("mcp_jira_get_issue", null).Allowed);
    }

    [Fact]
    public void ServerWideGrantCoversToolsDiscoveredLater()
    {
        // The defining difference between the two grant shapes: a server-wide grant is a prefix
        // rule, so a tool the server exposes for the first time tomorrow is already covered.
        var store = Store();
        store.GrantServer("docs");

        Assert.True(store.CurrentPolicy.Evaluate("mcp_docs_a_tool_that_did_not_exist_yet", null).Allowed);
    }

    [Fact]
    public void PerToolGrantDoesNotCoverToolsDiscoveredLater()
    {
        var store = Store();
        store.GrantTools(new[] { "mcp_docs_search" });

        Assert.False(store.CurrentPolicy.Evaluate("mcp_docs_brand_new", null).Allowed);
    }

    [Theory]
    [InlineData("write_file")]
    [InlineData("execute_command")]
    [InlineData("delete_file")]
    [InlineData("dataframe_export")]
    public void AMutatingToolCanNeverBeGranted(string toolId)
    {
        var store = Store();

        var result = store.GrantTools(new[] { toolId });

        Assert.False(result.Success);
        Assert.Contains("cannot be opened up for a mutating tool", result.Message);
        Assert.False(store.CurrentPolicy.Evaluate(toolId, null).Allowed);
        // Nothing was written, so the file did not silently gain a useless entry.
        Assert.True(store.List().IsEmpty);
    }

    [Fact]
    public void AMixedGrantIsRejectedWholesale()
    {
        // Partially applying a grant would quietly drop the entry the user cared about.
        var store = Store();

        var result = store.GrantTools(new[] { "mcp_docs_search", "write_file" });

        Assert.False(result.Success);
        Assert.False(store.CurrentPolicy.Evaluate("mcp_docs_search", null).Allowed);
    }

    [Fact]
    public void AHandWrittenGrantForAMutatingToolStillHasNoEffect()
    {
        // Defence in depth: even bypassing the store's validation by editing the user file, the
        // policy consults capability-based denials before any opt-in.
        WriteUserConfig(
            "{ \"planReadOnlyTools\": [\"write_file\"], \"planReadOnlyMcpServers\": [\"docs\"] }");

        var store = Store();

        Assert.False(store.CurrentPolicy.Evaluate("write_file", null).Allowed);
        Assert.False(store.CurrentPolicy.Evaluate("execute_command", null).Allowed);
        Assert.True(store.CurrentPolicy.Evaluate("mcp_docs_search", null).Allowed);
    }

    [Fact]
    public void ServerWideGrantCannotSmuggleInAMutatingBuiltIn()
    {
        var store = Store();
        store.GrantServer("docs");

        Assert.False(store.CurrentPolicy.Evaluate("write_file", null).Allowed);
        Assert.False(store.CurrentPolicy.Evaluate("execute_command", null).Allowed);
    }

    [Fact]
    public void ServerWideGrantStillHonoursParameterBasedDenials()
    {
        var store = Store();
        store.GrantServer("docs");

        // An output-file argument is a write no matter which server the tool came from.
        var verdict = store.CurrentPolicy.Evaluate(
            "mcp_docs_search",
            new System.Collections.Generic.Dictionary<string, object?> { ["output_file"] = "/tmp/x" });

        Assert.False(verdict.Allowed);
    }

    [Fact]
    public void RevokeRemovesAPerToolGrant()
    {
        var store = Store();
        store.GrantTools(new[] { "mcp_docs_search" });

        var result = store.Revoke(new[] { "mcp_docs_search" });

        Assert.True(result.Success, result.Message);
        Assert.False(store.CurrentPolicy.Evaluate("mcp_docs_search", null).Allowed);
    }

    [Fact]
    public void RevokeRemovesAServerWideGrant()
    {
        var store = Store();
        store.GrantServer("docs");

        var result = store.Revoke(new[] { "docs" });

        Assert.True(result.Success, result.Message);
        Assert.False(store.CurrentPolicy.Evaluate("mcp_docs_search", null).Allowed);
    }

    [Fact]
    public void RevokingSomethingThatWasNeverGrantedReportsIt()
    {
        var store = Store();

        var result = store.Revoke(new[] { "mcp_docs_search" });

        Assert.False(result.Success);
        Assert.Contains("No matching grant", result.Message);
    }

    [Fact]
    public void GrantsSurviveARestart()
    {
        Store().GrantServer("docs");
        Store().GrantTools(new[] { "mcp_jira_get_issue" });

        // A brand-new store instance is what the next process start looks like.
        var restarted = Store();

        Assert.True(restarted.CurrentPolicy.Evaluate("mcp_docs_search", null).Allowed);
        Assert.True(restarted.CurrentPolicy.Evaluate("mcp_jira_get_issue", null).Allowed);
        Assert.False(restarted.CurrentPolicy.Evaluate("mcp_other_thing", null).Allowed);
    }

    [Fact]
    public void GrantsAreWrittenToTheUserFileAndNotTheProjectFile()
    {
        // Grants are per developer: writing them into the project would commit one developer's
        // decision into the repository for everyone.
        var store = Store();
        store.GrantServer("docs");
        store.GrantTools(new[] { "mcp_jira_get_issue" });

        Assert.True(File.Exists(ModeConfigFile.PathFor(_user)));
        Assert.False(File.Exists(ModeConfigFile.PathFor(_project)));

        var listing = store.List();
        Assert.Contains("docs", listing.Servers);
        Assert.Contains("mcp_jira_get_issue", listing.Tools);
        Assert.Empty(listing.IgnoredProjectEntries);
        Assert.Equal(ModeConfigFile.PathFor(_user), store.GrantConfigPath);
    }

    [Fact]
    public void UserScopedGrantsAreRead()
    {
        WriteUserConfig("{ \"planReadOnlyTools\": [\"mcp_global_lookup\"] }");

        var store = Store();

        Assert.True(store.CurrentPolicy.Evaluate("mcp_global_lookup", null).Allowed);
        Assert.Contains("mcp_global_lookup", store.List().Tools);
    }

    [Fact]
    public void ProjectScopeGrantsAreIgnored()
    {
        // The core of the per-developer rule: a committed project file must not hand Plan-mode
        // access to a teammate who never saw the opt-in prompt.
        WriteProjectConfig(
            "{ \"planReadOnlyTools\": [\"mcp_docs_search\"], \"planReadOnlyMcpServers\": [\"docs\"] }");

        var store = Store();

        Assert.False(store.CurrentPolicy.Evaluate("mcp_docs_search", null).Allowed);
        Assert.False(store.CurrentPolicy.Evaluate("mcp_docs_anything", null).Allowed);
        Assert.True(store.List().IsEmpty);
    }

    [Fact]
    public void ProjectScopeGrantsProduceADiagnostic()
    {
        WriteProjectConfig(
            "{ \"planReadOnlyTools\": [\"mcp_docs_search\"], \"planReadOnlyMcpServers\": [\"docs\"] }");

        var diagnostics = Store().Diagnostics;

        var message = Assert.Single(diagnostics);
        Assert.Contains("Ignoring project-scope Plan-mode grants", message);
        Assert.Contains("per developer", message);
        Assert.Contains("mcp_docs_search", message);
        Assert.Contains("server:docs", message);
        // It must point at where the grant SHOULD go.
        Assert.Contains(ModeConfigFile.PathFor(_user), message);
    }

    [Fact]
    public void IgnoredProjectEntriesAreListedForReview()
    {
        WriteProjectConfig("{ \"planReadOnlyMcpServers\": [\"docs\"] }");

        var listing = Store().List();

        Assert.True(listing.IsEmpty);
        Assert.Contains("server:docs", listing.IgnoredProjectEntries);
    }

    [Fact]
    public void AskBookkeepingIsPerDeveloperSoATeammateIsStillOffered()
    {
        // Developer A answered the offer; their record lives in their own user file.
        var developerA = Store();
        developerA.RecordOffered("docs", new[] { "mcp_docs_search" });
        Assert.False(developerA.NeedsOffer("docs", new[] { "mcp_docs_search" }));

        // Developer B clones the repo (same project directory, different home) and must still be
        // asked - even if A's record had somehow been committed.
        WriteProjectConfig("{ \"mcpPlanOptInAsked\": { \"docs\": [\"mcp_docs_search\"] } }");
        var otherHome = Path.Combine(_root, "home-b");
        Directory.CreateDirectory(otherHome);
        var developerB = new PlanModeGrantStore(_project, otherHome);

        Assert.True(developerB.NeedsOffer("docs", new[] { "mcp_docs_search" }));
    }

    [Fact]
    public void ProjectScopeAskBookkeepingProducesItsOwnDiagnostic()
    {
        WriteProjectConfig("{ \"mcpPlanOptInAsked\": { \"docs\": [\"mcp_docs_search\"] } }");

        var message = Assert.Single(Store().Diagnostics);

        Assert.Contains("mcpPlanOptInAsked", message);
        Assert.Contains("per developer", message);
    }

    [Fact]
    public void ACleanProjectFileProducesNoDiagnostic()
    {
        Assert.Empty(Store().Diagnostics);

        WriteProjectConfig("{ }");
        Assert.Empty(Store().Diagnostics);
    }

    [Fact]
    public void RevokeOperatesOnTheUserFile()
    {
        WriteProjectConfig("{ \"planReadOnlyMcpServers\": [\"docs\"] }");
        var store = Store();
        store.GrantServer("docs");
        Assert.True(store.CurrentPolicy.Evaluate("mcp_docs_search", null).Allowed);

        Assert.True(store.Revoke(new[] { "docs" }).Success);

        Assert.False(store.CurrentPolicy.Evaluate("mcp_docs_search", null).Allowed);
        // The project file is never rewritten by a revoke.
        Assert.Contains("planReadOnlyMcpServers", File.ReadAllText(ModeConfigFile.PathFor(_project)));
    }

    [Fact]
    public void ChangedFiresOnEveryGrant()
    {
        var store = Store();
        var fired = 0;
        store.Changed += (_, _) => fired++;

        store.GrantServer("docs");
        store.GrantTools(new[] { "mcp_jira_get_issue" });

        Assert.Equal(2, fired);
    }

    [Fact]
    public void NeedsOfferIsTrueForAFreshServer()
    {
        var store = Store();

        Assert.True(store.NeedsOffer("docs", new[] { "mcp_docs_search" }));
    }

    [Fact]
    public void NeedsOfferIsFalseOnceTheOfferWasShown()
    {
        var store = Store();
        store.RecordOffered("docs", new[] { "mcp_docs_search" });

        Assert.False(store.NeedsOffer("docs", new[] { "mcp_docs_search" }));
    }

    [Fact]
    public void NeedsOfferBecomesTrueAgainForANewlyDiscoveredTool()
    {
        // Declining must not silence the server forever: a tool it never offered is surfaced.
        var store = Store();
        store.RecordOffered("docs", new[] { "mcp_docs_search" });

        Assert.True(store.NeedsOffer("docs", new[] { "mcp_docs_search", "mcp_docs_brand_new" }));
    }

    [Fact]
    public void NeedsOfferIsFalseWhenTheServerIsAlreadyGrantedWide()
    {
        var store = Store();
        store.GrantServer("docs");

        Assert.False(store.NeedsOffer("docs", new[] { "mcp_docs_search", "mcp_docs_anything" }));
    }

    [Fact]
    public void RecordOfferedGrantsNothing()
    {
        var store = Store();

        store.RecordOffered("docs", new[] { "mcp_docs_search" });

        Assert.False(store.CurrentPolicy.Evaluate("mcp_docs_search", null).Allowed);
        Assert.True(store.List().IsEmpty);
    }

    [Fact]
    public void UngrantedToolsReportsOnlyWhatPlanModeWouldDeny()
    {
        var store = Store();
        store.GrantTools(new[] { "mcp_docs_search" });

        var ungranted = store.UngrantedTools(new[] { "mcp_docs_search", "mcp_docs_fetch" });

        Assert.Equal(new[] { "mcp_docs_fetch" }, ungranted.ToArray());
    }

    [Fact]
    public void ServerNamesAreNormalizedTheSameWayToolIdsAre()
    {
        // The grant records the display name; matching goes through the shared id convention, so a
        // server called "My Docs" grants tools registered as mcp_my_docs_*.
        var store = Store();
        store.GrantServer("My Docs");

        Assert.True(store.CurrentPolicy.Evaluate("mcp_my_docs_search", null).Allowed);
    }
}
