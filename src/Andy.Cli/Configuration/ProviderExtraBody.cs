using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Andy.Cli.Configuration;

/// <summary>
/// Resolves a provider's <c>ExtraBody</c> config block — e.g. OpenRouter <c>provider</c> routing or
/// a <c>models</c> fallback array — into a typed dictionary the engine forwards to the LLM provider
/// as <c>LlmRequest.ExtraBody</c>.
///
/// <see cref="IConfiguration"/> flattens every value to a string and represents arrays as children
/// keyed by contiguous indices, so we reconstruct the JSON shape explicitly: scalar types are
/// inferred (bool/long/double/string), index-keyed sections become lists, everything else an object.
/// </summary>
public static class ProviderExtraBody
{
    /// <summary>
    /// The typed ExtraBody for <paramref name="provider"/> under
    /// <c>Llm:Providers:&lt;provider&gt;:ExtraBody</c>, or null when none is configured.
    /// </summary>
    public static IReadOnlyDictionary<string, object?>? Resolve(IConfiguration configuration, string provider)
        => Convert(configuration.GetSection($"Llm:Providers:{provider}:ExtraBody")) as IReadOnlyDictionary<string, object?>;

    internal static object? Convert(IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
        {
            return ParseScalar(section.Value);
        }
        // An array surfaces as children keyed by contiguous indices "0","1",… (in order).
        var isArray = children.Select((c, i) => c.Key == i.ToString(CultureInfo.InvariantCulture)).All(x => x);
        if (isArray)
        {
            return children.Select(Convert).ToList();
        }
        var dict = new Dictionary<string, object?>();
        foreach (var child in children)
        {
            dict[child.Key] = Convert(child);
        }
        return dict;
    }

    internal static object? ParseScalar(string? value)
    {
        if (value is null) return null;
        if (bool.TryParse(value, out var b)) return b;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return value;
    }
}
