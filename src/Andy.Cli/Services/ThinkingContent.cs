using System.Text.Json;
using System.Text.RegularExpressions;

namespace Andy.Cli.Services;

/// <summary>Recognizes explicit provider reasoning without treating ordinary narration as thinking.</summary>
internal static class ThinkingContent
{
    private static readonly Regex Blocks = new("<think>(.*?)</think>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    public static IEnumerable<string> Extract(string? text) =>
        Blocks.Matches(text ?? "").Cast<Match>().Select(m => m.Groups[1].Value);

    public static string WithoutBlocks(string text) => Blocks.Replace(text, "");

    public static string? FromMetadata(IReadOnlyDictionary<string, object>? metadata)
    {
        if (metadata == null) return null;
        foreach (var key in new[] { "thinking", "reasoning_content" })
            if (metadata.TryGetValue(key, out var value))
            {
                if (value is string s) return s;
                if (value is JsonElement { ValueKind: JsonValueKind.String } element) return element.GetString();
            }
        return null;
    }
}
