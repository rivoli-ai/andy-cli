using Andy.Cli.Services;
using Andy.Permissions.Model;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class ApprovalRiskAssessorTests
{
    private const string Root = "/home/user/project";

    private static PermissionRequest Command(string toolId, string command, PermissionOutcome outcome = PermissionOutcome.Ask) =>
        new(toolId, toolId, command,
            new PermissionEvaluation(outcome, new[]
            {
                new EvaluatedResource(new ResourceAccess(ResourceKind.Command, command), outcome, null!, true)
            }));

    private static PermissionRequest Path(string toolId, string path, PermissionOutcome outcome = PermissionOutcome.Ask) =>
        new(toolId, toolId, path,
            new PermissionEvaluation(outcome, new[]
            {
                new EvaluatedResource(new ResourceAccess(ResourceKind.Path, path), outcome, null!, true)
            }));

    private static PermissionRequest Empty(string toolId = "read_file") =>
        new(toolId, toolId, "", new PermissionEvaluation(PermissionOutcome.Ask, System.Array.Empty<EvaluatedResource>()));

    // --- Low-risk: auto mode may allow ---

    [Fact]
    public void Read_command_is_normal() =>
        Assert.Equal(ApprovalRisk.Normal, ApprovalRiskAssessor.Assess(Command("execute_command", "git status"), Root));

    [Fact]
    public void Build_command_is_normal() =>
        Assert.Equal(ApprovalRisk.Normal, ApprovalRiskAssessor.Assess(Command("execute_command", "dotnet build ./src"), Root));

    [Fact]
    public void In_project_delete_is_normal() =>
        Assert.Equal(ApprovalRisk.Normal, ApprovalRiskAssessor.Assess(Command("execute_command", "rm -rf ./bin ./obj"), Root));

    [Fact]
    public void Empty_resources_are_normal() =>
        Assert.Equal(ApprovalRisk.Normal, ApprovalRiskAssessor.Assess(Empty(), Root));

    [Fact]
    public void In_project_write_path_is_normal() =>
        Assert.Equal(ApprovalRisk.Normal, ApprovalRiskAssessor.Assess(Path("write_file", "src/New.cs"), Root));

    // --- Deletes outside the project root: always High ---

    [Fact]
    public void Absolute_delete_outside_root_is_high() =>
        Assert.Equal(ApprovalRisk.High, ApprovalRiskAssessor.Assess(Command("execute_command", "rm -rf /var/data"), Root));

    [Fact]
    public void Home_delete_is_high() =>
        Assert.Equal(ApprovalRisk.High, ApprovalRiskAssessor.Assess(Command("execute_command", "rm -rf ~/Documents"), Root));

    [Fact]
    public void Parent_escape_delete_is_high() =>
        Assert.Equal(ApprovalRisk.High, ApprovalRiskAssessor.Assess(Command("execute_command", "rm -rf ../other-repo"), Root));

    [Fact]
    public void Delete_with_no_target_is_high() =>
        Assert.Equal(ApprovalRisk.High, ApprovalRiskAssessor.Assess(Command("execute_command", "rm -rf"), Root));

    // --- Git repo destruction: always High ---

    [Fact]
    public void Delete_dot_git_is_high() =>
        Assert.Equal(ApprovalRisk.High, ApprovalRiskAssessor.Assess(Command("execute_command", "rm -rf .git"), Root));

    [Fact]
    public void Delete_nested_dot_git_is_high() =>
        Assert.Equal(ApprovalRisk.High, ApprovalRiskAssessor.Assess(Command("execute_command", "rm -rf ./sub/.git/objects"), Root));

    [Fact]
    public void Path_to_dot_git_is_high() =>
        Assert.Equal(ApprovalRisk.High, ApprovalRiskAssessor.Assess(Path("delete_file", ".git/config"), Root));

    // --- Database destruction: always High ---

    [Theory]
    [InlineData("psql -c \"DROP DATABASE production\"")]
    [InlineData("mysql -e \"drop table users\"")]
    [InlineData("mongosh --eval \"db.dropDatabase()\"")]
    [InlineData("redis-cli FLUSHALL")]
    [InlineData("terraform destroy -auto-approve")]
    public void Database_destruction_is_high(string command) =>
        Assert.Equal(ApprovalRisk.High, ApprovalRiskAssessor.Assess(Command("execute_command", command), Root));

    // --- Destructive file tools outside the project: High ---

    [Fact]
    public void Delete_file_outside_root_is_high() =>
        Assert.Equal(ApprovalRisk.High, ApprovalRiskAssessor.Assess(Path("delete_file", "/etc/hosts"), Root));

    [Fact]
    public void Sensitive_ssh_path_is_high() =>
        Assert.Equal(ApprovalRisk.High, ApprovalRiskAssessor.Assess(Path("write_file", "/home/user/.ssh/config"), Root));

    // --- AutoApprovalMode gating ---

    [Fact]
    public void Auto_mode_disabled_never_auto_approves()
    {
        var mode = new AutoApprovalMode(Root);
        Assert.False(mode.CanAutoApprove(Command("execute_command", "git status")));
    }

    [Fact]
    public void Auto_mode_enabled_approves_low_risk_only()
    {
        var mode = new AutoApprovalMode(Root);
        mode.Enable();
        Assert.True(mode.CanAutoApprove(Command("execute_command", "git status")));
        Assert.False(mode.CanAutoApprove(Command("execute_command", "rm -rf /var/data")));
        Assert.False(mode.CanAutoApprove(Command("execute_command", "psql -c \"DROP DATABASE x\"")));
    }
}
