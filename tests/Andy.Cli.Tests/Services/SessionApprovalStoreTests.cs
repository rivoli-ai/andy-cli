using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services;
using Andy.Permissions.Model;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class SessionApprovalStoreTests : IDisposable
{
    private readonly string _dir;
    private const string Sid = "20260724T120000_ab12cd34";

    public SessionApprovalStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "andy-approvals-test-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SessionApprovalStore Store() => new(_dir);

    private static SessionApproval Allow(string tool, string spec, PersistScope scope = PersistScope.Session, string source = "user") =>
        new()
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Tool = tool,
            Specifier = spec,
            Outcome = PermissionOutcome.Allow,
            Scope = scope,
            Risk = ApprovalRisk.Normal,
            Source = source,
        };

    [Fact]
    public void Record_then_load_roundtrips()
    {
        var store = Store();
        store.Record(Sid, Allow("execute_command", "git status:*"));
        store.Record(Sid, Allow("write_file", ""));

        var loaded = store.Load(Sid);
        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, a => a.Tool == "execute_command" && a.Specifier == "git status:*");
        Assert.Contains(loaded, a => a.Tool == "write_file");
    }

    [Fact]
    public void LoadGrantableRules_returns_only_session_allows()
    {
        var store = Store();
        store.Record(Sid, Allow("execute_command", "git status:*", PersistScope.Session));
        store.Record(Sid, Allow("execute_command", "git fetch:*", PersistScope.Once));   // excluded
        store.Record(Sid, new SessionApproval
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Tool = "execute_command",
            Specifier = "rm:*",
            Outcome = PermissionOutcome.Deny,
            Scope = PersistScope.Session,
            Risk = ApprovalRisk.High,
            Source = "user",
        }); // deny excluded

        var rules = store.LoadGrantableRules(Sid);
        Assert.Single(rules);
        Assert.Equal("execute_command", rules[0].Tool);
        Assert.Equal("git status:*", rules[0].Specifier);
        Assert.Equal(PermissionOutcome.Allow, rules[0].Outcome);
        Assert.Equal(PermissionLayer.Session, rules[0].Layer);
    }

    [Fact]
    public void Load_missing_session_returns_empty()
    {
        Assert.Empty(Store().Load("20990101T000000_ffffffff"));
        Assert.Empty(Store().LoadGrantableRules("20990101T000000_ffffffff"));
    }

    [Fact]
    public void Corrupt_file_returns_empty_not_throw()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, Sid + ".approvals.json"), "{ not json !!!");
        Assert.Empty(Store().Load(Sid));
    }

    [Fact]
    public void Invalid_session_id_is_ignored()
    {
        var store = Store();
        store.Record("../../etc/evil", Allow("execute_command", "x:*"));
        Assert.Empty(store.Load("../../etc/evil"));
    }

    // --- Prompt integration: auto + record ---

    private static PermissionRequest Cmd(string command) =>
        new("execute_command", "Execute Command", command,
            new PermissionEvaluation(PermissionOutcome.Ask, new[]
            {
                new EvaluatedResource(new ResourceAccess(ResourceKind.Command, command), PermissionOutcome.Ask, null!, true)
            }));

    [Fact]
    public async Task Auto_mode_allows_low_risk_without_prompt_and_records()
    {
        var broker = new PermissionRequestBroker();
        var store = Store();
        var auto = new AutoApprovalMode("/home/user/project");
        auto.Enable();
        var prompt = new CliPermissionPrompt(broker, store: null, autoMode: auto, approvalStore: store) { SessionId = Sid };

        var decision = await prompt.RequestAsync(Cmd("git status"), CancellationToken.None);

        Assert.True(decision.Allowed);
        Assert.Equal(PersistScope.Session, decision.Persist);
        Assert.False(broker.HasPending); // never surfaced to the UI

        var recorded = store.Load(Sid);
        Assert.Single(recorded);
        Assert.Equal("auto", recorded[0].Source);
        Assert.Equal(PersistScope.Session, recorded[0].Scope);
    }

    [Fact]
    public async Task Auto_mode_still_prompts_for_high_risk()
    {
        var broker = new PermissionRequestBroker();
        var store = Store();
        var auto = new AutoApprovalMode("/home/user/project");
        auto.Enable();
        var prompt = new CliPermissionPrompt(broker, store: null, autoMode: auto, approvalStore: store) { SessionId = Sid };

        var task = prompt.RequestAsync(Cmd("rm -rf /var/data"), CancellationToken.None);

        // High-risk: must surface to the UI, not auto-allow.
        Assert.True(broker.TryDequeue(out var pending));
        Assert.False(task.IsCompleted);
        pending!.Completion.TrySetResult(new PermissionDecision(false, PersistScope.Once));
        var decision = await task;
        Assert.False(decision.Allowed);

        // Deny is recorded for audit but is not re-grantable.
        var recorded = store.Load(Sid);
        Assert.Single(recorded);
        Assert.Equal(PermissionOutcome.Deny, recorded[0].Outcome);
        Assert.Equal(ApprovalRisk.High, recorded[0].Risk);
    }

    [Fact]
    public async Task User_decision_is_recorded_with_user_source()
    {
        var broker = new PermissionRequestBroker();
        var store = Store();
        var prompt = new CliPermissionPrompt(broker, store: null, autoMode: null, approvalStore: store) { SessionId = Sid };

        var task = prompt.RequestAsync(Cmd("git fetch origin"), CancellationToken.None);
        Assert.True(broker.TryDequeue(out var pending));
        pending!.Completion.TrySetResult(new PermissionDecision(true, PersistScope.Session));
        await task;

        var recorded = store.Load(Sid);
        Assert.Single(recorded);
        Assert.Equal("user", recorded[0].Source);
        Assert.Equal(PermissionOutcome.Allow, recorded[0].Outcome);
    }
}
