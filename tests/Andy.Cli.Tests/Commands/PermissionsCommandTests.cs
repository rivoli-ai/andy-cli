using System;
using System.IO;
using System.Threading.Tasks;
using Andy.Cli.Commands;
using Andy.Permissions.DependencyInjection;
using Andy.Permissions.Model;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Cli.Tests.Commands;

public class PermissionsCommandTests
{
    // Build a command backed by a real permission store whose user layer is an isolated temp file,
    // so add/list round-trips touch nothing on the machine.
    private static (PermissionsCommand cmd, string userFile, ServiceProvider sp) Build()
    {
        var userFile = Path.Combine(Path.GetTempPath(), "andy-perm-" + Guid.NewGuid().ToString("N") + ".json");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAndyPermissions(o =>
        {
            o.UserFilePath = userFile;
            o.ProjectFilePath = null;
            o.LocalFilePath = null;
            o.ManagedFilePath = null;
        });
        var sp = services.BuildServiceProvider();
        return (new PermissionsCommand(sp), userFile, sp);
    }

    [Fact]
    public async Task List_ShowsBuiltinLayer()
    {
        var (cmd, _, sp) = Build();
        using (sp)
        {
            var r = await cmd.ExecuteAsync(new[] { "list" });
            Assert.True(r.Success, r.Message);
            Assert.Contains("[Builtin]", r.Message);
        }
    }

    [Fact]
    public async Task Allow_PersistsRule_ToUserFile_AndListShowsIt()
    {
        var (cmd, userFile, sp) = Build();
        using (sp)
        {
            try
            {
                var write = await cmd.ExecuteAsync(new[] { "allow", "execute_command(*)", "--scope", "user" });
                Assert.True(write.Success, write.Message);

                Assert.True(File.Exists(userFile), "user rule file should have been created");

                var list = await cmd.ExecuteAsync(new[] { "list" });
                Assert.Contains("execute_command", list.Message);
                Assert.Contains("[User]", list.Message);
            }
            finally
            {
                if (File.Exists(userFile)) File.Delete(userFile);
            }
        }
    }

    [Fact]
    public async Task Path_ListsUserProjectAndLocal()
    {
        var (cmd, _, sp) = Build();
        using (sp)
        {
            var r = await cmd.ExecuteAsync(new[] { "path" });
            Assert.True(r.Success);
            Assert.Contains("permissions.json", r.Message);
            Assert.Contains("user", r.Message);
            Assert.Contains("project", r.Message);
            Assert.Contains("local", r.Message);
        }
    }

    [Fact]
    public async Task UnknownSubcommand_Fails()
    {
        var (cmd, _, sp) = Build();
        using (sp)
            Assert.False((await cmd.ExecuteAsync(new[] { "frobnicate" })).Success);
    }

    [Fact]
    public async Task Allow_WithoutTool_Fails()
    {
        var (cmd, _, sp) = Build();
        using (sp)
            Assert.False((await cmd.ExecuteAsync(new[] { "allow" })).Success);
    }

    [Fact]
    public async Task Allow_WithUnknownScope_Fails()
    {
        var (cmd, _, sp) = Build();
        using (sp)
            Assert.False((await cmd.ExecuteAsync(new[] { "allow", "write_file", "--scope", "galaxy" })).Success);
    }

    [Theory]
    [InlineData("execute_command(*)", "execute_command", "*")]
    [InlineData("write_file", "write_file", "*")]
    [InlineData("execute_command(git*)", "execute_command", "git*")]
    public void ParseSpec_SplitsToolAndSpecifier(string raw, string tool, string spec)
    {
        var (t, s) = PermissionsCommand.ParseSpec(raw);
        Assert.Equal(tool, t);
        Assert.Equal(spec, s);
    }

    [Fact]
    public void TryParseScope_DefaultsToUser_AndParsesValues()
    {
        Assert.True(PermissionsCommand.TryParseScope(new[] { "allow", "x" }, out var s1, out _));
        Assert.Equal(PersistScope.User, s1);

        Assert.True(PermissionsCommand.TryParseScope(new[] { "allow", "x", "--scope", "project" }, out var s2, out _));
        Assert.Equal(PersistScope.Project, s2);

        Assert.True(PermissionsCommand.TryParseScope(new[] { "allow", "x", "-s", "local" }, out var s3, out _));
        Assert.Equal(PersistScope.Local, s3);

        Assert.False(PermissionsCommand.TryParseScope(new[] { "allow", "x", "--scope", "nope" }, out _, out var err));
        Assert.NotEmpty(err);
    }
}
