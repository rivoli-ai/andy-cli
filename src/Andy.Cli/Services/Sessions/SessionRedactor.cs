using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Andy.Cli.Services.Sessions;

/// <summary>
/// Redacts secrets from persisted session transcripts. Mirrors the redaction
/// conventions established by <see cref="Andy.Cli.Headless.HeadlessTranscriptSession"/>:
/// sensitive JSON property names are replaced wholesale, and string values are
/// scrubbed for bearer tokens, key/value-style secrets, provider API key shapes,
/// and the literal values of secret-looking environment variables. Redaction
/// operates on individual JSON string values (never on the raw JSON text), so
/// the document structure survives and the transcript stays restorable.
/// </summary>
public sealed class SessionRedactor
{
    public const string Replacement = "[REDACTED]";

    private static readonly Regex s_bearerPattern = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.Compiled);
    private static readonly Regex s_keyValuePattern = new(
        @"(?i)\b(api[_-]?key|access[_-]?token|token|secret|password)\b[""']?"
            + @"(\s*[:=]\s*)[""']?([^\s,""'}]+)",
        RegexOptions.Compiled);
    private static readonly Regex s_apiKeyPattern = new(
        @"\b(sk-(?:or-)?[A-Za-z0-9_-]{8,})\b",
        RegexOptions.Compiled);
    private static readonly Regex s_secretEnvNamePattern = new(
        @"(?i)(API[_-]?KEY|ACCESS[_-]?TOKEN|SECRET|PASSWORD|_TOKEN)$",
        RegexOptions.Compiled);

    private readonly IReadOnlyList<string> _secretValues;

    /// <param name="secretValues">
    /// Literal secret values to strip from persisted text. When null, the values of
    /// secret-looking environment variables (names ending in API_KEY, TOKEN, SECRET,
    /// PASSWORD) are used.
    /// </param>
    public SessionRedactor(IEnumerable<string>? secretValues = null)
    {
        var values = secretValues ?? ResolveSecretValuesFromEnvironment();
        // Longest first so overlapping secrets are removed greedily.
        _secretValues = values
            .Where(v => !string.IsNullOrEmpty(v) && v.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(v => v.Length)
            .ToArray();
    }

    /// <summary>
    /// Redacts a JSON document by walking its nodes: sensitive property names are
    /// replaced wholesale and every string value is scrubbed. Returns the input
    /// unchanged when it is not valid JSON.
    /// </summary>
    public string RedactJson(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return RedactText(json);
        }

        if (root is null)
        {
            return json;
        }

        RedactNode(root);
        return root.ToJsonString();
    }

    /// <summary>Scrubs one plain-text value.</summary>
    public string RedactText(string text)
    {
        var redacted = text;
        foreach (var secret in _secretValues)
        {
            redacted = redacted.Replace(secret, Replacement, StringComparison.Ordinal);
        }

        redacted = s_bearerPattern.Replace(redacted, "Bearer " + Replacement);
        redacted = s_keyValuePattern.Replace(redacted, "$1$2" + Replacement);
        redacted = s_apiKeyPattern.Replace(redacted, Replacement);
        return redacted;
    }

    private void RedactNode(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (IsSensitiveProperty(property.Key))
                {
                    obj[property.Key] = Replacement;
                }
                else if (property.Value is not null)
                {
                    RedactNode(property.Value);
                }
            }
            return;
        }

        if (node is JsonArray array)
        {
            // Index-based: ReplaceWith on a string element mutates the array and
            // would invalidate a foreach enumerator.
            for (var i = 0; i < array.Count; i++)
            {
                var child = array[i];
                if (child is not null)
                {
                    RedactNode(child);
                }
            }
            return;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            value.ReplaceWith(JsonValue.Create(RedactText(text)));
        }
    }

    internal static bool IsSensitiveProperty(string propertyName)
    {
        var normalized = propertyName.Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
        return normalized is "api_key" or "apikey" or "authorization" or "access_token"
            or "token" or "secret" or "password";
    }

    /// <summary>
    /// Collects the values of environment variables whose names look secret-bearing
    /// (…API_KEY, …TOKEN, …SECRET, …PASSWORD), so a transcript that happened to echo
    /// one of them never reaches disk verbatim.
    /// </summary>
    public static IReadOnlyList<string> ResolveSecretValuesFromEnvironment()
    {
        var values = new List<string>();
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value
                && value.Length >= 4
                && s_secretEnvNamePattern.IsMatch(name))
            {
                values.Add(value);
            }
        }
        return values;
    }
}
