using System;
using System.IO;
using System.Linq;
using Andy.Cli.Services;
using Andy.Permissions.Model;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class PermissionRuleFileTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "andy-rulefile-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var f = PermissionRuleFile.Load(TempPath());
        Assert.Empty(f.Allow);
        Assert.Empty(f.Ask);
        Assert.Empty(f.Deny);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var path = TempPath();
        try
        {
            var f = new PermissionRuleFile();
            f.Set("execute_command(*)", PermissionOutcome.Allow);
            f.Set("write_file(*)", PermissionOutcome.Deny);
            f.Save(path);

            var reloaded = PermissionRuleFile.Load(path);
            Assert.Contains("execute_command(*)", reloaded.Allow);
            Assert.Contains("write_file(*)", reloaded.Deny);
            // Matches the store's on-disk shape: lowercase outcome keys.
            Assert.Contains("\"allow\"", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Remove_ExistingRule_ReturnsTrueAndDrops()
    {
        var f = new PermissionRuleFile();
        f.Set("read_file(*)", PermissionOutcome.Allow);

        Assert.True(f.Remove("read_file(*)"));
        Assert.False(f.Contains("read_file(*)"));
        Assert.False(f.Remove("read_file(*)")); // already gone
    }

    [Fact]
    public void Set_MovesRuleBetweenBuckets_NoDuplicates()
    {
        var f = new PermissionRuleFile();
        f.Set("execute_command(*)", PermissionOutcome.Allow);
        f.Set("execute_command(*)", PermissionOutcome.Deny); // re-target

        Assert.DoesNotContain("execute_command(*)", f.Allow);
        Assert.Contains("execute_command(*)", f.Deny);

        f.Set("execute_command(*)", PermissionOutcome.Deny); // idempotent
        Assert.Single(f.Deny, r => r == "execute_command(*)");
    }

    [Fact]
    public void Entries_EnumeratesAllBucketsWithOutcomes()
    {
        var f = new PermissionRuleFile();
        f.Set("a(*)", PermissionOutcome.Allow);
        f.Set("b(*)", PermissionOutcome.Ask);
        f.Set("c(*)", PermissionOutcome.Deny);

        var entries = f.Entries().ToList();
        Assert.Equal(3, entries.Count);
        Assert.Contains((PermissionOutcome.Allow, "a(*)"), entries);
        Assert.Contains((PermissionOutcome.Ask, "b(*)"), entries);
        Assert.Contains((PermissionOutcome.Deny, "c(*)"), entries);
    }

    [Theory]
    [InlineData(PersistScope.User, "permissions.json")]
    [InlineData(PersistScope.Project, "permissions.json")]
    [InlineData(PersistScope.Local, "permissions.local.json")]
    public void PathForScope_ResolvesExpectedFiles(PersistScope scope, string expectedSuffix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "proj-" + Guid.NewGuid().ToString("N"));
        var path = PermissionRuleFile.PathForScope(scope, dir);
        Assert.EndsWith(expectedSuffix, path);
        if (scope != PersistScope.User)
            Assert.Contains(".andy", path);
    }
}
