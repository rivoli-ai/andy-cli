using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Andy.Permissions.Model;
using Andy.Permissions.Store;

namespace Andy.Cli.Services;

/// <summary>
/// Reads and writes a single permission-layer file in the same on-disk shape the permission store
/// uses — three arrays of <c>"tool(specifier)"</c> rule strings keyed by outcome:
/// <code>{ "allow": [...], "ask": [...], "deny": [...] }</code>
/// This is what lets the CLI/TUI <b>remove</b> and <b>re-target</b> rules (the store itself only
/// appends), persisting changes to wherever that layer lives on disk.
/// </summary>
public sealed class PermissionRuleFile
{
    public List<string> Allow { get; set; } = new();
    public List<string> Ask { get; set; } = new();
    public List<string> Deny { get; set; } = new();

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    /// <summary>Load the file, or an empty set if it does not exist or cannot be parsed.</summary>
    public static PermissionRuleFile Load(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return new PermissionRuleFile();
        try
        {
            return JsonSerializer.Deserialize<PermissionRuleFile>(File.ReadAllText(path), ReadOptions)
                   ?? new PermissionRuleFile();
        }
        catch
        {
            return new PermissionRuleFile();
        }
    }

    /// <summary>Persist to disk (creating the directory), normalizing each bucket.</summary>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, WriteOptions));
    }

    private List<string> Bucket(PermissionOutcome outcome) => outcome switch
    {
        PermissionOutcome.Allow => Allow,
        PermissionOutcome.Ask => Ask,
        _ => Deny,
    };

    /// <summary>All rules across buckets as (outcome, rule) pairs.</summary>
    public IEnumerable<(PermissionOutcome Outcome, string Rule)> Entries()
        => Allow.Select(r => (PermissionOutcome.Allow, r))
            .Concat(Ask.Select(r => (PermissionOutcome.Ask, r)))
            .Concat(Deny.Select(r => (PermissionOutcome.Deny, r)));

    /// <summary>True if any bucket contains the rule string.</summary>
    public bool Contains(string rule) => Entries().Any(e => e.Rule == rule);

    /// <summary>Remove the rule from every bucket. Returns true if anything was removed.</summary>
    public bool Remove(string rule)
    {
        bool removed = Allow.RemoveAll(r => r == rule) > 0;
        removed |= Ask.RemoveAll(r => r == rule) > 0;
        removed |= Deny.RemoveAll(r => r == rule) > 0;
        return removed;
    }

    /// <summary>Set a rule to a single outcome (removing it from any other bucket first).</summary>
    public void Set(string rule, PermissionOutcome outcome)
    {
        Remove(rule);
        var bucket = Bucket(outcome);
        if (!bucket.Contains(rule))
            bucket.Add(rule);
    }

    /// <summary>Resolve the on-disk file for a persist scope, mirroring the CLI's store wiring.</summary>
    public static string PathForScope(PersistScope scope, string projectDirectory)
    {
        var opts = new PermissionStoreOptions().WithProjectDirectory(projectDirectory);
        return scope switch
        {
            PersistScope.Project => opts.ProjectFilePath ?? Path.Combine(projectDirectory, ".andy", "permissions.json"),
            PersistScope.Local => opts.LocalFilePath ?? Path.Combine(projectDirectory, ".andy", "permissions.local.json"),
            _ => opts.UserFilePath ?? PermissionStoreOptions.DefaultUserFilePath(),
        };
    }
}
