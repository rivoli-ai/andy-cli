using System;
using System.IO;
using System.Linq;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Permissions.Model;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;
using Xunit;

namespace Andy.Cli.Tests.Widgets;

public class PermissionsManagerTests
{
    // Use a temp project dir and unique tool names so we never touch (or collide with) the real
    // user-layer file the manager also reads.
    private static (string dir, string projFile) NewProject()
    {
        var dir = Path.Combine(Path.GetTempPath(), "andy-mgr-" + Guid.NewGuid().ToString("N"));
        return (dir, PermissionRuleFile.PathForScope(PersistScope.Project, dir));
    }

    private static void SeedProject(string projFile, Action<PermissionRuleFile> seed)
    {
        var f = new PermissionRuleFile();
        seed(f);
        f.Save(projFile);
    }

    // Move selection to the entry with the given rule; returns false if not present.
    private static bool Select(PermissionsManager m, string rule)
    {
        for (int i = 0; i < m.Count; i++)
        {
            if (m.Selected?.Rule == rule) return true;
            m.MoveSelection(1);
        }
        return m.Selected?.Rule == rule;
    }

    [Fact]
    public void Reload_LoadsProjectRules()
    {
        var (dir, proj) = NewProject();
        try
        {
            SeedProject(proj, f => f.Set("zzmgr_read(*)", PermissionOutcome.Allow));
            var m = new PermissionsManager(dir);
            m.Open();
            Assert.Contains(m.Entries, e => e.Rule == "zzmgr_read(*)" && e.Scope == PersistScope.Project);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CycleSelectedOutcome_PersistsToFile()
    {
        var (dir, proj) = NewProject();
        try
        {
            SeedProject(proj, f => f.Set("zzmgr_exec(*)", PermissionOutcome.Allow));
            var m = new PermissionsManager(dir);
            m.Open();
            Assert.True(Select(m, "zzmgr_exec(*)"));

            m.CycleSelectedOutcome(); // Allow -> Ask

            var onDisk = PermissionRuleFile.Load(proj);
            Assert.Contains("zzmgr_exec(*)", onDisk.Ask);
            Assert.DoesNotContain("zzmgr_exec(*)", onDisk.Allow);
            Assert.Equal(PermissionOutcome.Ask, m.Selected!.Outcome);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void DeleteSelected_RemovesFromFile()
    {
        var (dir, proj) = NewProject();
        try
        {
            SeedProject(proj, f =>
            {
                f.Set("zzmgr_a(*)", PermissionOutcome.Allow);
                f.Set("zzmgr_b(*)", PermissionOutcome.Deny);
            });
            var m = new PermissionsManager(dir);
            m.Open();
            Assert.True(Select(m, "zzmgr_b(*)"));

            m.DeleteSelected();

            var onDisk = PermissionRuleFile.Load(proj);
            Assert.False(onDisk.Contains("zzmgr_b(*)"));
            Assert.True(onDisk.Contains("zzmgr_a(*)"));
            Assert.DoesNotContain(m.Entries, e => e.Rule == "zzmgr_b(*)");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void OpenClose_TogglesState()
    {
        var (dir, _) = NewProject();
        var m = new PermissionsManager(dir);
        Assert.False(m.IsOpen);
        m.Open();
        Assert.True(m.IsOpen);
        m.Close();
        Assert.False(m.IsOpen);
    }

    [Fact]
    public void Render_DrawsTitleAndRules_WithoutThrowing()
    {
        var (dir, proj) = NewProject();
        try
        {
            SeedProject(proj, f => f.Set("zzmgr_render(*)", PermissionOutcome.Allow));
            var m = new PermissionsManager(dir);
            m.Open();

            var baseDl = new DL.DisplayListBuilder().Build();
            var b = new DL.DisplayListBuilder();
            m.Render(new L.Rect(0, 0, 100, 30), baseDl, b);

            var text = string.Concat(b.Build().Ops.OfType<DL.TextRun>().Select(r => r.Content));
            Assert.Contains("Permissions Manager", text);
            Assert.Contains("zzmgr_render", text);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void MoveSelection_ClampsWithinBounds()
    {
        var (dir, proj) = NewProject();
        try
        {
            SeedProject(proj, f =>
            {
                f.Set("zzmgr_1(*)", PermissionOutcome.Allow);
                f.Set("zzmgr_2(*)", PermissionOutcome.Allow);
            });
            var m = new PermissionsManager(dir);
            m.Open();
            m.MoveSelection(-100);
            Assert.Equal(0, m.SelectedIndex);
            m.MoveSelection(1000);
            Assert.Equal(m.Count - 1, m.SelectedIndex);
        }
        finally { Directory.Delete(dir, true); }
    }
}
