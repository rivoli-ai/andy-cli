using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace Andy.Cli.Configuration;

/// <summary>
/// Rewrites object keys to the exact casing the schema declares.
///
/// Used for ONE thing: folding the packaged PascalCase appsettings.json
/// (<c>Llm:Providers:openai:ApiBase</c>) into the camelCase schema without editing
/// that file. It is deliberately NOT applied to user or project andy.jsonc files —
/// there, a key that differs only by case is a typo and must be reported as an
/// unknown key rather than silently accepted.
/// </summary>
public static class SchemaKeyNormalizer
{
    public static void Normalize(JsonNode? instance, JsonObject? schema)
    {
        if (schema is null)
        {
            return;
        }

        switch (instance)
        {
            case JsonObject obj:
                {
                    var properties = schema["properties"] as JsonObject;
                    var additionalSchema = schema["additionalProperties"] as JsonObject;

                    foreach (var pair in obj.ToList())
                    {
                        var canonical = properties?
                            .FirstOrDefault(p => string.Equals(p.Key, pair.Key, StringComparison.OrdinalIgnoreCase))
                            .Key;

                        var child = pair.Value;
                        if (canonical is not null && canonical != pair.Key)
                        {
                            obj.Remove(pair.Key);
                            child = child?.DeepClone();
                            obj[canonical] = child;
                        }

                        var childSchema = canonical is not null
                            ? properties?[canonical] as JsonObject
                            : additionalSchema;
                        Normalize(child, childSchema);
                    }
                    break;
                }

            case JsonArray array:
                {
                    if (schema["items"] is JsonObject itemSchema)
                    {
                        foreach (var item in array)
                        {
                            Normalize(item, itemSchema);
                        }
                    }
                    break;
                }
        }
    }
}
