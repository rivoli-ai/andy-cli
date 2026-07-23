using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andy.Skills;
using Andy.Skills.Tools;

namespace Andy.Cli.Services;

/// <summary>
/// CLI-side persistence for which skills the user has disabled. Andy.Skills itself has no
/// per-skill enabled/disabled state (a skill exists on disk or it does not), so the CLI keeps
/// a small JSON file of disabled skill names next to the other workspace config:
/// <c>&lt;workspace&gt;/.andy/skills.disabled.json</c>, shaped <c>{ "disabled": ["name", ...] }</c>.
/// </summary>
public sealed class SkillsDisableList
{
    /// <summary>Resolve the on-disk disable-list file for a workspace.</summary>
    public static string PathFor(string workspaceDirectory)
        => Path.Combine(workspaceDirectory, ".andy", "skills.disabled.json");

    private sealed class Document
    {
        public List<string> Disabled { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    /// <summary>Load the disabled skill names, or an empty set if the file is missing/unreadable.</summary>
    public static HashSet<string> Load(string path)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return set;
        try
        {
            var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), JsonOptions);
            if (doc?.Disabled != null)
                foreach (var name in doc.Disabled)
                    if (!string.IsNullOrWhiteSpace(name))
                        set.Add(name.Trim());
        }
        catch
        {
            // A corrupt disable list must never take the whole catalog down; treat as empty.
        }
        return set;
    }

    /// <summary>Persist the disabled skill names (creating the directory as needed).</summary>
    public static void Save(string path, IEnumerable<string> disabled)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var doc = new Document { Disabled = disabled.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList() };
        File.WriteAllText(path, JsonSerializer.Serialize(doc, JsonOptions));
    }
}

/// <summary>
/// Decorates the Andy.Skills.Tools <see cref="ISkillCatalog"/> with the CLI's disable list.
/// The <c>skill</c> and <c>skill_file</c> tools resolve skills through this catalog, so a
/// disabled skill genuinely cannot be loaded by the agent (this is a real gate, not a display
/// filter). The disable list is re-read on each call, so /skills enable|disable takes effect
/// immediately for subsequent tool calls.
/// </summary>
public sealed class FilteredSkillCatalog : ISkillCatalog
{
    private readonly ISkillCatalog _inner;
    private readonly string _disableListPath;

    public FilteredSkillCatalog(ISkillCatalog inner, string disableListPath)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _disableListPath = disableListPath ?? throw new ArgumentNullException(nameof(disableListPath));
    }

    /// <summary>The disable-list file this catalog consults.</summary>
    public string DisableListPath => _disableListPath;

    public async Task<IReadOnlyList<Skill>> GetSkillsAsync(CancellationToken cancellationToken = default)
    {
        var all = await _inner.GetSkillsAsync(cancellationToken);
        var disabled = SkillsDisableList.Load(_disableListPath);
        if (disabled.Count == 0)
            return all;
        return all.Where(s => !disabled.Contains(s.Name)).ToList();
    }

    /// <summary>All discovered skills, including disabled ones (for management UIs).</summary>
    public Task<IReadOnlyList<Skill>> GetAllSkillsAsync(CancellationToken cancellationToken = default)
        => _inner.GetSkillsAsync(cancellationToken);

    public async Task<Skill?> FindAsync(string name, CancellationToken cancellationToken = default)
    {
        if (IsDisabled(name))
            return null;
        return await _inner.FindAsync(name, cancellationToken);
    }

    public Task<IReadOnlyList<SkillDiagnostic>> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
        => _inner.GetDiagnosticsAsync(cancellationToken);

    public void Invalidate() => _inner.Invalidate();

    /// <summary>True when the skill name is on the disable list.</summary>
    public bool IsDisabled(string name) => SkillsDisableList.Load(_disableListPath).Contains(name);

    /// <summary>
    /// Add or remove a skill name on the persisted disable list. Returns true when the list
    /// changed (false when it was already in the requested state).
    /// </summary>
    public bool SetDisabled(string name, bool disabled)
    {
        var set = SkillsDisableList.Load(_disableListPath);
        var changed = disabled ? set.Add(name) : set.Remove(name);
        if (changed)
            SkillsDisableList.Save(_disableListPath, set);
        return changed;
    }
}
