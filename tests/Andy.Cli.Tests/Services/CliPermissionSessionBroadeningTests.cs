using System.Collections.Generic;
using System.Linq;
using Andy.Cli.Services;
using Andy.Permissions.Model;
using Andy.Permissions.Store;
using Xunit;

namespace Andy.Cli.Tests.Services;

/// <summary>
/// Unit tests for the session-grant broadening that <see cref="CliPermissionPrompt"/> applies when a user
/// picks "Allow (session)". The engine's own remembered rule pins the exact command line, which re-prompts
/// for argument-only variations; the broadened rule is keyed to the command class (executable + first
/// subcommand) so similar invocations are not re-prompted, while genuinely different commands still are.
/// </summary>
public class CliPermissionSessionBroadeningTests
{
    [Theory]
    [InlineData("gh pr list --limit 10", "gh pr")]
    [InlineData("git status", "git status")]
    [InlineData("npm run build", "npm run")]
    [InlineData("ls -la", "ls")]
    [InlineData("/usr/bin/gh pr view 5", "gh pr")]   // executable leaf name only
    [InlineData("FOO=bar gh pr list", "gh pr")]      // leading env assignment is skipped
    [InlineData("dotnet --version", "dotnet")]       // first non-exe token is a flag => executable only
    public void CommandClass_groups_by_executable_and_first_subcommand(string command, string expected)
    {
        Assert.Equal(expected, CliPermissionPrompt.CommandClass(command));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("| rm")]            // begins with a metacharacter => not a bare executable
    [InlineData("$(whoami)")]       // command substitution => refuse to widen
    public void CommandClass_returns_empty_when_no_safe_executable(string command)
    {
        Assert.Equal(string.Empty, CliPermissionPrompt.CommandClass(command));
    }

    [Fact]
    public void Session_allow_installs_a_broadened_command_class_rule()
    {
        var store = new FakeStore();
        var request = AskRequest("gh pr list --limit 10");

        CliPermissionPrompt.GrantBroadenedSessionRules(request, store);

        var rule = Assert.Single(store.SessionRules);
        Assert.Equal("execute_command", rule.Tool);
        Assert.Equal("gh pr:*", rule.Specifier);
        Assert.Equal(PermissionOutcome.Allow, rule.Outcome);
        Assert.Equal(PermissionLayer.Session, rule.Layer);
    }

    [Fact]
    public void Broadened_rule_matches_similar_commands_but_not_unrelated_ones()
    {
        var store = new FakeStore();
        CliPermissionPrompt.GrantBroadenedSessionRules(AskRequest("gh pr list --limit 10"), store);
        var rule = Assert.Single(store.SessionRules);

        // Same command class with different arguments: covered (no re-prompt).
        Assert.True(MatchesCommand(rule.Specifier, "gh pr list --limit 20"));
        Assert.True(MatchesCommand(rule.Specifier, "gh pr view 42"));

        // A different subcommand or a different executable: NOT covered (still prompts).
        Assert.False(MatchesCommand(rule.Specifier, "gh repo delete acme/widgets"));
        Assert.False(MatchesCommand(rule.Specifier, "rm -rf /tmp/x"));
    }

    [Fact]
    public void Only_command_resources_flagged_for_ask_are_broadened()
    {
        var store = new FakeStore();
        var allowed = new EvaluatedResource(
            new ResourceAccess(ResourceKind.Command, "git status"), PermissionOutcome.Allow, null, true);
        var asked = new EvaluatedResource(
            new ResourceAccess(ResourceKind.Command, "gh pr list"), PermissionOutcome.Ask, null, true);
        var request = new PermissionRequest("execute_command", "Execute Command", "run",
            new PermissionEvaluation(PermissionOutcome.Ask, new[] { allowed, asked }));

        CliPermissionPrompt.GrantBroadenedSessionRules(request, store);

        var rule = Assert.Single(store.SessionRules);
        Assert.Equal("gh pr:*", rule.Specifier);   // only the Ask resource was broadened
    }

    [Fact]
    public void No_store_is_a_safe_no_op()
    {
        // Should not throw when the store is unavailable (e.g. non-interactive wiring).
        CliPermissionPrompt.GrantBroadenedSessionRules(AskRequest("gh pr list"), store: null);
    }

    private static bool MatchesCommand(string specifier, string command)
        => Andy.Permissions.Matching.SpecifierMatcher.MatchCommand(specifier, command);

    private static PermissionRequest AskRequest(string command)
    {
        var resource = new EvaluatedResource(
            new ResourceAccess(ResourceKind.Command, command), PermissionOutcome.Ask, null, true);
        return new PermissionRequest("execute_command", "Execute Command", command,
            new PermissionEvaluation(PermissionOutcome.Ask, new[] { resource }));
    }

    /// <summary>Minimal <see cref="IPermissionStore"/> that records the session rules added to it.</summary>
    private sealed class FakeStore : IPermissionStore
    {
        public List<PermissionRule> SessionRules { get; } = new();

        public void AddSessionRule(PermissionRule rule) => SessionRules.Add(rule);

        public IReadOnlyList<PermissionRule> GetRules() => SessionRules.ToList();

        public System.Threading.Tasks.Task AppendRuleAsync(
            string toolId, string specifier, PermissionOutcome outcome, PersistScope scope,
            System.Threading.CancellationToken ct = default)
            => System.Threading.Tasks.Task.CompletedTask;

        public void SetInjectedRules(IEnumerable<PermissionRule> rules) { }
    }
}
