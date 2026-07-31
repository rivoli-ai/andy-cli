using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Andy.Cli.Configuration;

/// <summary>
/// Merges the ordered layers into one document while recording, for every leaf,
/// which layer and which line produced the winning value.
///
/// Merge semantics are deliberate and per field, not "last document wins" and not
/// "concatenate everything":
///
/// * OBJECTS merge recursively. A higher layer that sets
///   <c>llm.providers.openai.model</c> keeps the lower layer's
///   <c>llm.providers.openai.apiBase</c>. This is what makes the keyed maps
///   (<c>llm.providers</c>, <c>mcp.servers</c>, and the <c>env</c>/<c>headers</c>
///   maps inside a server) merge entry by entry.
/// * ARRAYS replace. Concatenating would make it impossible to shorten a list from
///   a higher layer, and silently doubles arguments when a project file repeats
///   what the user file already said. <c>mcp.servers.*.args</c> is the field this
///   rule exists for.
/// * SCALARS replace, highest layer wins.
/// * An explicit JSON <c>null</c> replaces, and means "unset": the key is removed,
///   so the schema default applies again.
/// </summary>
public static class ConfigMerge
{
    /// <summary>
    /// Folds <paramref name="layers"/> (lowest precedence first) into a new object.
    /// <paramref name="provenance"/> is filled with one entry per winning leaf path.
    /// </summary>
    public static JsonObject Merge(
        IReadOnlyList<ConfigLayer> layers,
        IDictionary<string, ConfigOrigin> provenance)
    {
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(provenance);

        var result = new JsonObject();
        foreach (var layer in layers)
        {
            MergeObject(result, layer.Root, string.Empty, layer, provenance);
        }
        return result;
    }

    private static void MergeObject(
        JsonObject target,
        JsonObject source,
        string path,
        ConfigLayer layer,
        IDictionary<string, ConfigOrigin> provenance)
    {
        foreach (var pair in source.ToList())
        {
            var childPath = JsoncDocument.Join(path, pair.Key);
            var value = pair.Value;

            if (value is null)
            {
                // Explicit null unsets the key rather than storing a null leaf.
                target.Remove(pair.Key);
                ForgetSubtree(provenance, childPath);
                continue;
            }

            if (value is JsonObject sourceObject)
            {
                if (target[pair.Key] is JsonObject targetObject)
                {
                    MergeObject(targetObject, sourceObject, childPath, layer, provenance);
                }
                else
                {
                    var replacement = new JsonObject();
                    target[pair.Key] = replacement;
                    ForgetSubtree(provenance, childPath);
                    MergeObject(replacement, sourceObject, childPath, layer, provenance);
                }
                continue;
            }

            // Arrays and scalars replace outright.
            target[pair.Key] = value.DeepClone();
            ForgetSubtree(provenance, childPath);
            provenance[childPath] = layer.OriginOf(childPath);
        }
    }

    /// <summary>
    /// Drops provenance for a path and everything under it. Needed when a higher
    /// layer replaces a whole subtree: the old leaves are gone, so attributing them
    /// to their original file would be a lie.
    /// </summary>
    private static void ForgetSubtree(IDictionary<string, ConfigOrigin> provenance, string path)
    {
        var prefix = path + ".";
        var stale = provenance.Keys
            .Where(key => key == path
                || key.StartsWith(prefix, StringComparison.Ordinal)
                || key.StartsWith(path + "[", StringComparison.Ordinal))
            .ToList();
        foreach (var key in stale)
        {
            provenance.Remove(key);
        }
    }

    /// <summary>
    /// Enumerates every leaf of a merged document as (dotted path, value), with
    /// arrays treated as single leaves. Used by <c>config show</c>.
    /// </summary>
    public static IEnumerable<(string Path, JsonNode? Value)> Leaves(JsonNode? node, string path = "")
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.Count == 0 && path.Length > 0)
                {
                    yield return (path, obj);
                    yield break;
                }
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    foreach (var leaf in Leaves(pair.Value, JsoncDocument.Join(path, pair.Key)))
                    {
                        yield return leaf;
                    }
                }
                break;

            default:
                yield return (path, node);
                break;
        }
    }
}
