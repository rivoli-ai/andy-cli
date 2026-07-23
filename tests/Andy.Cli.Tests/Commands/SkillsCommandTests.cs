using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Andy.Cli.Commands;
using Andy.Cli.Services;
using Andy.Skills.Tools;
using Andy.Tools.Framework;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Cli.Tests.Commands;

public class SkillsCommandTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _skillsRoot;

    public SkillsCommandTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "andy-skills-ws-" + Guid.NewGuid().ToString("N"));
        _skillsRoot = Path.Combine(_workspace, ".andy", "skills");
        Directory.CreateDirectory(_skillsRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    private void WriteSkill(string name, string description, string body = "Do the thing.")
    {
        var dir = Path.Combine(_skillsRoot, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"),
            $"---\nname: {name}\ndescription: {description}\n---\n{body}\n");
    }

    // Mirrors the production wiring in ToolCatalog: AddAndySkills + the FilteredSkillCatalog
    // decorator, but rooted in an isolated temp workspace.
    private (SkillsCommand cmd, ServiceProvider sp) Build()
    {
        var services = new ServiceCollection();
        Andy.Skills.Tools.ServiceCollectionExtensions.AddAndySkills(services, new[] { _skillsRoot });
        services.AddSingleton<ISkillCatalog>(sp => new FilteredSkillCatalog(
            new SkillCatalog(sp.GetRequiredService<SkillCatalogOptions>()),
            SkillsDisableList.PathFor(_workspace)));
        var sp = services.BuildServiceProvider();
        return (new SkillsCommand(sp, _workspace), sp);
    }

    [Fact]
    public async Task List_WithNoSkills_ReportsRootsHonestly()
    {
        var (cmd, sp) = Build();
        using (sp)
        {
            var r = await cmd.ExecuteAsync(Array.Empty<string>());
            Assert.True(r.Success, r.Message);
            Assert.Contains("No skills found", r.Message);
            Assert.Contains("SKILL.md", r.Message);
        }
    }

    [Fact]
    public async Task List_ShowsDiscoveredSkills()
    {
        WriteSkill("demo-skill", "A demo skill for tests");
        WriteSkill("other-skill", "Another skill");
        var (cmd, sp) = Build();
        using (sp)
        {
            var r = await cmd.ExecuteAsync(new[] { "list" });
            Assert.True(r.Success, r.Message);
            Assert.Contains("demo-skill", r.Message);
            Assert.Contains("A demo skill for tests", r.Message);
            Assert.Contains("other-skill", r.Message);
        }
    }

    [Fact]
    public async Task Info_ShowsSkillDetails()
    {
        WriteSkill("demo-skill", "A demo skill for tests");
        var (cmd, sp) = Build();
        using (sp)
        {
            var r = await cmd.ExecuteAsync(new[] { "info", "demo-skill" });
            Assert.True(r.Success, r.Message);
            Assert.Contains("demo-skill", r.Message);
            Assert.Contains("A demo skill for tests", r.Message);
            Assert.Contains("SKILL.md", r.Message);
            Assert.Contains("enabled", r.Message);
        }
    }

    [Fact]
    public async Task Info_UnknownSkill_Fails()
    {
        var (cmd, sp) = Build();
        using (sp)
            Assert.False((await cmd.ExecuteAsync(new[] { "info", "nope" })).Success);
    }

    [Fact]
    public async Task Disable_HidesSkillFromAgentCatalog_AndEnableRestoresIt()
    {
        WriteSkill("demo-skill", "A demo skill for tests");
        var (cmd, sp) = Build();
        using (sp)
        {
            var catalog = sp.GetRequiredService<ISkillCatalog>();

            // Enabled by default: visible to the agent-facing catalog.
            Assert.NotNull(await catalog.FindAsync("demo-skill"));

            var disable = await cmd.ExecuteAsync(new[] { "disable", "demo-skill" });
            Assert.True(disable.Success, disable.Message);

            // The agent-facing catalog must genuinely refuse the skill now.
            Assert.Null(await catalog.FindAsync("demo-skill"));
            Assert.DoesNotContain(await catalog.GetSkillsAsync(), s => s.Name == "demo-skill");

            // But the management listing still shows it, marked disabled.
            var list = await cmd.ExecuteAsync(new[] { "list" });
            Assert.Contains("demo-skill", list.Message);
            Assert.Contains("[disabled]", list.Message);

            var enable = await cmd.ExecuteAsync(new[] { "enable", "demo-skill" });
            Assert.True(enable.Success, enable.Message);
            Assert.NotNull(await catalog.FindAsync("demo-skill"));
        }
    }

    [Fact]
    public async Task Disable_PersistsToDisableListFile()
    {
        WriteSkill("demo-skill", "A demo skill for tests");
        var (cmd, sp) = Build();
        using (sp)
        {
            await cmd.ExecuteAsync(new[] { "disable", "demo-skill" });
            var path = SkillsDisableList.PathFor(_workspace);
            Assert.True(File.Exists(path), "disable list file should have been written");
            Assert.Contains("demo-skill", File.ReadAllText(path));

            var disabled = SkillsDisableList.Load(path);
            Assert.Contains("demo-skill", disabled);
        }
    }

    [Fact]
    public async Task Disable_UnknownSkill_Fails()
    {
        var (cmd, sp) = Build();
        using (sp)
            Assert.False((await cmd.ExecuteAsync(new[] { "disable", "ghost-skill" })).Success);
    }

    [Fact]
    public async Task Diagnostics_ReportsMalformedManifest()
    {
        var dir = Path.Combine(_skillsRoot, "broken-skill");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), "no frontmatter here");
        var (cmd, sp) = Build();
        using (sp)
        {
            var r = await cmd.ExecuteAsync(new[] { "diagnostics" });
            Assert.True(r.Success, r.Message);
            Assert.Contains("broken-skill", r.Message);
        }
    }

    [Fact]
    public async Task Reload_Succeeds_AndReportsCount()
    {
        WriteSkill("demo-skill", "A demo skill for tests");
        var (cmd, sp) = Build();
        using (sp)
        {
            var first = await cmd.ExecuteAsync(new[] { "list" });
            Assert.Contains("demo-skill", first.Message);

            WriteSkill("late-skill", "Added after the first scan");
            var reload = await cmd.ExecuteAsync(new[] { "reload" });
            Assert.True(reload.Success, reload.Message);
            Assert.Contains("2", reload.Message);
        }
    }

    [Fact]
    public async Task Help_IsHonestAboutWhatEnableDisableIs()
    {
        var (cmd, sp) = Build();
        using (sp)
        {
            var r = await cmd.ExecuteAsync(new[] { "help" });
            Assert.True(r.Success);
            Assert.Contains("skills enable", r.Message);
            Assert.Contains("skills disable", r.Message);
            // The disable list is a CLI-side mechanism, not an Andy.Skills feature; say so.
            Assert.Contains("CLI-side", r.Message);
            Assert.Contains("skills.disabled.json", r.Message);
        }
    }

    [Fact]
    public async Task Command_WithoutCatalog_FailsGracefully()
    {
        var cmd = new SkillsCommand(serviceProvider: null, workspaceDirectory: _workspace);
        var r = await cmd.ExecuteAsync(Array.Empty<string>());
        Assert.False(r.Success);
        Assert.Contains("not available", r.Message);
    }

    [Fact]
    public void CompositionRoot_RegistersSkillTools_AndFilteredCatalog()
    {
        // Full production wiring: AddCoreToolServices + InitializeToolRegistry. The skill
        // tools need constructor injection, so they must come through the registry's factory
        // overload; this test fails if they ever regress to the Activator-based path
        // (which rejects tools without a parameterless constructor).
        var services = new ServiceCollection();
        services.AddLogging();
        Andy.Cli.Hosting.AppCompositionRoot.AddCoreToolServices(services, permissionBroker: null);
        using var sp = services.BuildServiceProvider();

        var registry = Andy.Cli.Hosting.AppCompositionRoot.InitializeToolRegistry(sp);

        Assert.NotNull(registry.GetTool("skill"));
        Assert.NotNull(registry.GetTool("skill_file"));

        // And the catalog the tools resolve must be the disable-list-aware decorator.
        Assert.IsType<FilteredSkillCatalog>(sp.GetRequiredService<ISkillCatalog>());
    }
}
