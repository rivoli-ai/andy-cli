using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Andy.Cli.Services.ToolResults;

/// <summary>
/// Typed reads over the structured payload a tool returned.
///
/// This is the ONLY place tool results are interpreted. Andy.Tools returns
/// <c>Dictionary&lt;string, object?&gt;</c> payloads with documented keys (for example
/// execute_command returns exit_code / stdout / stderr / duration_ms / timed_out /
/// working_directory), so reading them here is reading the upstream structure directly - not
/// re-parsing text the UI rendered a layer earlier.
///
/// Every accessor is total: a missing key, a null, or a value of an unexpected type yields the
/// caller's default rather than throwing, because a renderer must never be able to crash the feed.
/// </summary>
public static class ToolData
{
    /// <summary>
    /// Look a key up in whatever shape <paramref name="source"/> happens to be: a generic or
    /// non-generic dictionary, a JSON object, or a plain object with matching properties. Key
    /// matching is case-insensitive and also tolerates snake_case/camelCase/PascalCase spelling
    /// differences, so a "line_count" lookup finds a "LineCount" property.
    /// </summary>
    public static bool TryGet(object? source, string key, out object? value)
    {
        value = null;
        if (source is null || string.IsNullOrEmpty(key)) return false;

        switch (source)
        {
            case IReadOnlyDictionary<string, object?> ro:
                foreach (var kv in ro)
                {
                    if (KeyMatches(kv.Key, key)) { value = kv.Value; return true; }
                }
                return false;

            case IDictionary<string, object?> gd:
                foreach (var kv in gd)
                {
                    if (KeyMatches(kv.Key, key)) { value = kv.Value; return true; }
                }
                return false;

            case IDictionary raw:
                foreach (DictionaryEntry entry in raw)
                {
                    if (entry.Key?.ToString() is { } k && KeyMatches(k, key)) { value = entry.Value; return true; }
                }
                return false;

            case JsonElement json when json.ValueKind == JsonValueKind.Object:
                foreach (var prop in json.EnumerateObject())
                {
                    if (KeyMatches(prop.Name, key)) { value = Unwrap(prop.Value); return true; }
                }
                return false;

            case string:
                // A bare string payload has no addressable members; treating it as an object and
                // reflecting over String's properties (Length, Chars) would return nonsense.
                return false;

            default:
                {
                    var type = source.GetType();
                    foreach (var prop in type.GetProperties())
                    {
                        if (prop.GetIndexParameters().Length > 0) continue;
                        if (!KeyMatches(prop.Name, key)) continue;
                        try { value = prop.GetValue(source); return true; }
                        catch { return false; }
                    }
                    return false;
                }
        }
    }

    /// <summary>Look up the first of <paramref name="keys"/> that is present and non-null.</summary>
    public static bool TryGetAny(object? source, out object? value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (TryGet(source, key, out value) && value is not null) return true;
        }
        value = null;
        return false;
    }

    /// <summary>Read a string, trimmed. Returns null when absent, null, or blank.</summary>
    public static string? GetString(object? source, params string[] keys)
    {
        if (!TryGetAny(source, out var value, keys)) return null;
        var s = value switch
        {
            string str => str,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            _ => value?.ToString()
        };
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>Read an integer, tolerating boxed numerics and numeric strings.</summary>
    public static int? GetInt(object? source, params string[] keys)
        => GetLong(source, keys) is { } l && l is >= int.MinValue and <= int.MaxValue ? (int)l : null;

    /// <summary>Read a long, tolerating boxed numerics and numeric strings.</summary>
    public static long? GetLong(object? source, params string[] keys)
    {
        if (!TryGetAny(source, out var value, keys)) return null;
        return value switch
        {
            long l => l,
            int i => i,
            short s => s,
            byte b => b,
            uint ui => ui,
            ulong ul when ul <= long.MaxValue => (long)ul,
            double d when !double.IsNaN(d) && !double.IsInfinity(d) => (long)d,
            float f when !float.IsNaN(f) && !float.IsInfinity(f) => (long)f,
            decimal m => (long)m,
            JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetInt64(out var jl) => jl,
            string str when long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pl) => pl,
            _ => null
        };
    }

    /// <summary>Read a double, tolerating boxed numerics and numeric strings.</summary>
    public static double? GetDouble(object? source, params string[] keys)
    {
        if (!TryGetAny(source, out var value, keys)) return null;
        return value switch
        {
            double d => d,
            float f => f,
            decimal m => (double)m,
            long l => l,
            int i => i,
            JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetDouble(out var jd) => jd,
            string str when double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var pd) => pd,
            _ => null
        };
    }

    /// <summary>Read a boolean, tolerating "true"/"false" strings and 0/1 numerics.</summary>
    public static bool? GetBool(object? source, params string[] keys)
    {
        if (!TryGetAny(source, out var value, keys)) return null;
        return value switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            string str when bool.TryParse(str, out var pb) => pb,
            long l => l != 0,
            int i => i != 0,
            short s => s != 0,
            byte b8 => b8 != 0,
            _ => null
        };
    }

    /// <summary>
    /// Read a list of elements. Strings are deliberately NOT treated as enumerable, since a
    /// character sequence is never what a caller asking for a list wants.
    /// </summary>
    public static IReadOnlyList<object?> GetList(object? source, params string[] keys)
    {
        if (!TryGetAny(source, out var value, keys)) return Array.Empty<object?>();
        return AsList(value);
    }

    /// <summary>Coerce a value that is already in hand into a list, with the same string rule as <see cref="GetList"/>.</summary>
    public static IReadOnlyList<object?> AsList(object? value)
    {
        switch (value)
        {
            case null:
            case string:
                return Array.Empty<object?>();
            case JsonElement { ValueKind: JsonValueKind.Array } je:
                return je.EnumerateArray().Select(Unwrap).ToList();
            case IEnumerable enumerable:
                {
                    var items = new List<object?>();
                    foreach (var item in enumerable) items.Add(item);
                    return items;
                }
            default:
                return Array.Empty<object?>();
        }
    }

    /// <summary>
    /// Read a duration expressed either as a millisecond number (duration_ms) or as anything
    /// TimeSpan-shaped (a TimeSpan, or a string TimeSpan such as "00:00:01.2340000").
    /// </summary>
    public static TimeSpan? GetDuration(object? source, params string[] keys)
    {
        if (!TryGetAny(source, out var value, keys)) return null;
        switch (value)
        {
            case TimeSpan ts:
                return ts;
            case string s when TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var parsed):
                return parsed;
        }

        // Fall through to the numeric reading. Keys ending in _ms are milliseconds; a bare
        // numeric duration from these tools is milliseconds too (execute_command's duration_ms).
        var ms = GetDouble(source, keys);
        return ms.HasValue && ms.Value >= 0 ? TimeSpan.FromMilliseconds(ms.Value) : null;
    }

    /// <summary>
    /// Split a text payload into lines with the newline convention normalized. Blank lines are
    /// PRESERVED: dropping them corrupts any output whose blank lines carry meaning (diffs,
    /// formatted reports) - see issue #257.
    /// </summary>
    public static IReadOnlyList<string> SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    // JsonElement values are unwrapped into ordinary CLR values so downstream accessors do not
    // each need to special-case them again.
    private static object? Unwrap(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element
    };

    // "line_count" == "lineCount" == "LineCount" == "linecount". Comparing with separators and
    // case removed lets one lookup serve dictionary payloads and reflected POCOs alike.
    private static bool KeyMatches(string candidate, string key)
    {
        if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase)) return true;

        int i = 0, j = 0;
        while (true)
        {
            while (i < candidate.Length && (candidate[i] == '_' || candidate[i] == '-')) i++;
            while (j < key.Length && (key[j] == '_' || key[j] == '-')) j++;
            if (i >= candidate.Length || j >= key.Length) return i >= candidate.Length && j >= key.Length;
            if (char.ToLowerInvariant(candidate[i]) != char.ToLowerInvariant(key[j])) return false;
            i++;
            j++;
        }
    }
}
