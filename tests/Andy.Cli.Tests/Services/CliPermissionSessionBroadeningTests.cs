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
    [InlineData("git status > out.txt", "git status")]           // trailing redirect ignored
    [InlineData("git log --oneline", "git log")]                 // read-only history broadens
    [InlineData("dotnet build", "dotnet build")]
    [InlineData("dotnet restore", "dotnet restore")]
    [InlineData("cargo check --all", "cargo check")]             // read-only compile check broadens
    [InlineData("docker ps -a", "docker ps")]                    // read-only listing broadens
    [InlineData("kubectl get pods", "kubectl get")]              // read-only get broadens
    [InlineData("terraform plan", "terraform plan")]             // dry-run broadens
    [InlineData("FOO=bar gh pr list", "gh pr")]                  // leading env assignment is skipped
    public void CommandClass_broadens_allowlisted_executable_plus_subcommand(string command, string expected)
    {
        Assert.Equal(expected, CliPermissionPrompt.CommandClass(command));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("| rm")]                 // begins with a metacharacter => not a bare executable
    [InlineData("$(whoami)")]            // command substitution => refuse to widen
    // Interpreters / shells / destructive utilities: never auto-broaden (issue #168).
    [InlineData("python -c \"print(1)\"")]  // was previously broadened to executable-wide "python"
    [InlineData("python3 -c import os")]
    [InlineData("sh -c \"rm -rf /\"")]
    [InlineData("bash -lc echo")]
    [InlineData("node -e 1")]
    [InlineData("rm -i file")]              // was previously broadened to executable-wide "rm"
    [InlineData("rm -rf /tmp/x")]
    [InlineData("dd if=/dev/zero of=/dev/sda")]
    // Allowlisted executable but a CODE-EXECUTING subcommand => keep exact approval only (issue #168).
    // Broadening these to `exe subcommand:*` would auto-authorize arbitrary later arguments.
    [InlineData("docker run hello-world")]  // would else authorize `docker run -v /:/host ... rm -rf`
    [InlineData("dotnet run")]
    [InlineData("npm run build")]
    [InlineData("cargo run")]
    [InlineData("go run main.go")]
    [InlineData("kubectl exec pod -- sh")]
    [InlineData("terraform apply")]
    [InlineData("docker build .")]           // Dockerfile RUN steps execute code
    [InlineData("git config user.email x")]  // can wire alias.*/core.pager to a shell command
    [InlineData("git commit -m msg")]        // runs pre-commit / commit-msg hooks (code execution)
    [InlineData("git push")]                 // not on the read-only safe list
    [InlineData("gh api /repos")]            // arbitrary GitHub requests
    // Allowlisted executable but first token is a flag => keep exact approval only.
    [InlineData("dotnet --version")]
    [InlineData("git -C /x status")]
    // Not on the allowlist => keep exact approval only (no executable-wide rule).
    [InlineData("ls -la")]
    [InlineData("ls")]
    [InlineData("cat file.txt")]
    // Path-qualified executables must never broaden by leaf name.
    [InlineData("./script.sh")]
    [InlineData("./script.sh run")]
    [InlineData("/usr/bin/git status")]
    // Environment assignment where the executable would be => not broadened.
    [InlineData("FOO=bar")]
    // A non-identifier LHS is NOT an env assignment, so the token stays the (unsafe) executable and is
    // rejected as path/flag-qualified => not broadened (assignment-LHS tightening).
    [InlineData("x/y=z git status")]
    [InlineData("-x=y git status")]
    // Allowlisted executable with no subcommand => nothing to broaden to.
    [InlineData("git")]
    // Redirect / quoted first argument after an allowlisted exe => not a plain subcommand.
    [InlineData("git > out.txt")]
    [InlineData("git \"space arg\"")]
    public void CommandClass_returns_empty_for_ambiguous_or_unsafe_commands(string command)
    {
        Assert.Equal(string.Empty, CliPermissionPrompt.CommandClass(command));
    }

    [Fact]
    public void Ambiguous_command_installs_no_broadened_session_rule()
    {
        var store = new FakeStore();

        // A narrowly-intended `python -c ...` approval must not become an executable-wide python rule.
        CliPermissionPrompt.GrantBroadenedSessionRules(AskRequest("python -c \"print(1)\""), store);

        Assert.Empty(store.SessionRules);
    }

    [Fact]
    public void Code_executing_subcommand_installs_no_broadened_session_rule()
    {
        var store = new FakeStore();

        // Approving a benign `docker run hello-world` must NOT install a `docker run:*` rule that would then
        // auto-authorize `docker run -v /:/host --rm alpine sh -c 'rm -rf /host'` (issue #168).
        CliPermissionPrompt.GrantBroadenedSessionRules(AskRequest("docker run hello-world"), store);

        Assert.Empty(store.SessionRules);
    }

    [Fact]
    public void Safe_readonly_subcommand_installs_a_broadened_pair_rule()
    {
        var store = new FakeStore();

        CliPermissionPrompt.GrantBroadenedSessionRules(AskRequest("kubectl get pods"), store);

        var rule = Assert.Single(store.SessionRules);
        Assert.Equal("kubectl get:*", rule.Specifier);
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
