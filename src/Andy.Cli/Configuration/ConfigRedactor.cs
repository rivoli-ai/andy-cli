using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Andy.Cli.Configuration;

/// <summary>
/// Decides what <c>config show</c> is allowed to print.
///
/// Three independent reasons to redact, because any one of them alone leaks:
/// 1. The key path names a credential field (<c>llm.providers.*.apiKey</c>).
/// 2. The key sits under a header map, where the value is an Authorization
///    header more often than not.
/// 3. The value is one that <c>{env:NAME}</c> substitution resolved. That covers
///    a secret smuggled into an otherwise innocent field, such as a URL with an
///    embedded token.
/// </summary>
public static partial class ConfigRedactor
{
    /// <summary>What a redacted value is replaced with. Fixed length, so it leaks no entropy.</summary>
    public const string Placeholder = "<redacted>";

    [GeneratedRegex(@"(?i)(api[-_]?key|apikey|secret|password|passwd|credential|token|authorization|auth|cookie|session[-_]?key|private[-_]?key)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveNamePattern();

    /// <summary>True when the value at this dotted key path must not be printed.</summary>
    public static bool IsSensitivePath(string keyPath)
    {
        if (string.IsNullOrEmpty(keyPath))
        {
            return false;
        }

        var segments = keyPath.Split('.');

        // Every value of a headers map is treated as a credential.
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("headers", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return SensitiveNamePattern().IsMatch(segments[^1]);
    }

    /// <summary>
    /// The printable form of a value: the placeholder when the path is sensitive or
    /// the value contains a resolved secret, otherwise the value itself.
    /// </summary>
    public static string Redact(string keyPath, string? value, IReadOnlySet<string> secretValues)
    {
        if (value is null)
        {
            return "null";
        }

        if (IsSensitivePath(keyPath))
        {
            return value.Length == 0 ? "\"\"" : Placeholder;
        }

        return ContainsSecret(value, secretValues) ? Placeholder : value;
    }

    /// <summary>True when any resolved secret appears anywhere inside the text.</summary>
    public static bool ContainsSecret(string? text, IReadOnlySet<string> secretValues)
    {
        if (string.IsNullOrEmpty(text) || secretValues.Count == 0)
        {
            return false;
        }

        // Very short values (a one-character env var, "1", "true") would match
        // half the document and turn the report into noise without protecting
        // anything worth protecting.
        return secretValues.Any(secret =>
            secret.Length >= 4 && text.Contains(secret, StringComparison.Ordinal));
    }

    /// <summary>Removes every resolved secret from arbitrary text, e.g. a diagnostic message.</summary>
    public static string Scrub(string text, IReadOnlySet<string> secretValues)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        foreach (var secret in secretValues.Where(s => s.Length >= 4).OrderByDescending(s => s.Length))
        {
            text = text.Replace(secret, Placeholder, StringComparison.Ordinal);
        }
        return text;
    }
}
