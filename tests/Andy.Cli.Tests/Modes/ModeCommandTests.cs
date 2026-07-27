using System;
using System.IO;
using Andy.Cli.Commands;
using Andy.Cli.Modes;
using Xunit;

namespace Andy.Cli.Tests.Modes;

/// <summary>
/// <c>/mode</c> behaviour (issue #278), including the fail-closed rejection of unknown modes.
/// </summary>
public class ModeCommandTests
{
    [Fact]
    public void NoArguments_ReportsTheCurrentModeAndTheAvailableOnes()
    {
        var command = new ModeCommand(new AgentModeState(AgentMode.Plan));

        var result = command.Execute(Array.Empty<string>());

        Assert.True(result.Success);
        Assert.Contains("Current mode: Plan", result.Message);
        Assert.Contains("/mode build", result.Message);
        Assert.Contains("/mode plan", result.Message);
    }

    [Fact]
    public void SwitchingToPlan_Succeeds()
    {
        var state = new AgentModeState();
        var command = new ModeCommand(state);

        var result = command.Execute(new[] { "plan" });

        Assert.True(result.Success);
        Assert.Equal(AgentMode.Plan, state.Current);
        Assert.Contains("Plan", result.Message);
    }

    [Fact]
    public void SwitchingBackToBuild_Succeeds_BecauseTheCommandIsAnExplicitUserAction()
    {
        var state = new AgentModeState(AgentMode.Plan);
        var command = new ModeCommand(state);

        var result = command.Execute(new[] { "build" });

        Assert.True(result.Success);
        Assert.Equal(AgentMode.Build, state.Current);
    }

    [Theory]
    [InlineData("planning")]
    [InlineData("readonly")]
    [InlineData("bui1d")]
    public void UnknownMode_IsRejectedAndLeavesTheModeAlone(string requested)
    {
        var state = new AgentModeState(AgentMode.Plan);
        var command = new ModeCommand(state);

        var result = command.Execute(new[] { requested });

        Assert.False(result.Success);
        Assert.Contains("Unknown mode", result.Message);
        Assert.Equal(AgentMode.Plan, state.Current);
    }

    [Fact]
    public void RepeatingTheCurrentMode_IsAcknowledgedNotTreatedAsAChange()
    {
        var state = new AgentModeState(AgentMode.Plan);
        var command = new ModeCommand(state);

        var result = command.Execute(new[] { "plan" });

        Assert.True(result.Success);
        Assert.Contains("Already in Plan mode", result.Message);
    }

    [Fact]
    public void CommandMetadata_MatchesTheSlashCatalogEntry()
    {
        var command = new ModeCommand(new AgentModeState());

        Assert.Equal("mode", command.Name);
        Assert.Empty(command.Aliases);
    }

    /// <summary>
    /// The non-interactive path to the Plan-mode opt-ins, used from <c>/mode</c> in the TUI and from
    /// <c>andy-cli mode ...</c> by automation. It writes the same store the connection-time offer
    /// does, and is bound by the same rules.
    /// </summary>
    public sealed class GrantVerbs : IDisposable
    {
        private readonly string _root;
        private readonly string _project;
        private readonly string _user;

        public GrantVerbs()
        {
            _root = Path.Combine(Path.GetTempPath(), "andy-mode-cmd-" + Guid.NewGuid().ToString("N")[..8]);
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

        private (ModeCommand Command, PlanModeGrantStore Store) Build()
        {
            var store = Store();
            return (new ModeCommand(new AgentModeState(AgentMode.Plan), store), store);
        }

        [Fact]
        public void Grants_ReportsNothingWhenNoOptInsExist()
        {
            var (command, _) = Build();

            var result = command.Execute(new[] { "grants" });

            Assert.True(result.Success);
            Assert.Contains("(none)", result.Message);
        }

        [Fact]
        public void Allow_GrantsSpecificTools()
        {
            var (command, store) = Build();

            var result = command.Execute(new[] { "allow", "mcp_docs_search" });

            Assert.True(result.Success, result.Message);
            Assert.True(store.CurrentPolicy.Evaluate("mcp_docs_search", null).Allowed);
        }

        [Fact]
        public void AllowServer_GrantsEveryToolIncludingFutureOnes()
        {
            var (command, store) = Build();

            var result = command.Execute(new[] { "allow-server", "docs" });

            Assert.True(result.Success, result.Message);
            Assert.True(store.CurrentPolicy.Evaluate("mcp_docs_anything", null).Allowed);
        }

        [Theory]
        [InlineData("write_file")]
        [InlineData("execute_command")]
        public void Allow_RefusesAMutatingTool(string toolId)
        {
            var (command, store) = Build();

            var result = command.Execute(new[] { "allow", toolId });

            Assert.False(result.Success);
            Assert.Contains("mutating tool", result.Message);
            Assert.False(store.CurrentPolicy.Evaluate(toolId, null).Allowed);
        }

        [Fact]
        public void Revoke_RemovesAGrant()
        {
            var (command, store) = Build();
            command.Execute(new[] { "allow-server", "docs" });

            var result = command.Execute(new[] { "revoke", "docs" });

            Assert.True(result.Success, result.Message);
            Assert.False(store.CurrentPolicy.Evaluate("mcp_docs_anything", null).Allowed);
        }

        [Fact]
        public void Grants_ListsWhatIsInForce()
        {
            var (command, _) = Build();
            command.Execute(new[] { "allow-server", "docs" });
            command.Execute(new[] { "allow", "mcp_jira_get_issue" });

            var result = command.Execute(new[] { "grants" });

            Assert.True(result.Success);
            Assert.Contains("docs", result.Message);
            Assert.Contains("mcp_jira_get_issue", result.Message);
            Assert.Contains("revoke", result.Message);
        }

        [Theory]
        [InlineData("allow")]
        [InlineData("allow-server")]
        [InlineData("revoke")]
        public void GrantVerbsWithoutArguments_ExplainTheUsage(string verb)
        {
            var (command, _) = Build();

            var result = command.Execute(new[] { verb });

            Assert.False(result.Success);
            Assert.Contains("Usage:", result.Message);
        }

        [Fact]
        public void GrantVerbsAreUnavailableWithoutAStore()
        {
            // The one-shot and interactive paths always supply a store; this guards the fallback so a
            // context without one reports it instead of silently doing nothing.
            var command = new ModeCommand(new AgentModeState());

            var result = command.Execute(new[] { "grants" });

            Assert.False(result.Success);
            Assert.Contains("not available", result.Message);
        }

        [Fact]
        public void ModeSwitchingStillWorksAlongsideTheGrantVerbs()
        {
            var (command, _) = Build();

            Assert.True(command.Execute(new[] { "build" }).Success);
            Assert.False(command.Execute(new[] { "nonsense" }).Success);
        }

        [Fact]
        public void Status_AdvertisesTheGrantVerbsWhenAStoreIsPresent()
        {
            var (command, _) = Build();

            Assert.Contains("/mode grants", command.Status());
            Assert.Contains("/mode allow-server", command.Status());
        }
    }
}
