using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Cli.Services;

namespace Andy.Cli.Auth;

/// <summary>
/// Builds the <see cref="ProviderAuthDescriptor"/> set from <see cref="ProviderRegistry"/>,
/// plus an optional per-user overlay file.
///
/// Two properties matter here. First, every provider in the registry automatically gains an
/// API-key login, so adding a provider never requires touching the CLI verb or the TUI.
/// Second, OAuth endpoints come from data (built-in table or the overlay file), so a provider
/// can gain an OAuth login without a code change - which is what issue #284 means by "auth
/// hooks without hard-coding every provider into the TUI".
///
/// CONFIG SEAM (#280): the overlay is read from ~/.andy/provider-auth.json here. Once layered
/// configuration lands, this loader should read the merged config layer instead; the descriptor
/// shape and every consumer stay unchanged.
/// </summary>
public static class ProviderAuthCatalog
{
    /// <summary>Overlay file, relative to the user's home directory.</summary>
    public const string OverlayFileName = "provider-auth.json";

    /// <summary>
    /// Minimum plausible length of a provider API key. Kept as a length rule rather than a
    /// vendor prefix so this repository contains no secret-shaped literals.
    /// </summary>
    private const int MinimumApiKeyLength = 16;

    /// <summary>Printable, non-whitespace ASCII - what every supported provider's keys use.</summary>
    private const string PrintableAsciiPattern = @"^[\x21-\x7E]+$";

    /// <summary>Descriptors for every known provider, ordered like the provider registry.</summary>
    public static IReadOnlyList<ProviderAuthDescriptor> All() => All(DefaultOverlayPath());

    /// <summary>Test seam: builds the catalog with an explicit overlay path (which may not exist).</summary>
    public static IReadOnlyList<ProviderAuthDescriptor> All(string? overlayPath)
    {
        var overlay = LoadOverlay(overlayPath);

        return ProviderRegistry.All
            .Select(p => Build(p, overlay))
            .ToList();
    }

    /// <summary>Finds the descriptor for a provider id or alias, or null when unknown.</summary>
    public static ProviderAuthDescriptor? Find(string? idOrAlias) => Find(idOrAlias, DefaultOverlayPath());

    /// <summary>Test seam: see <see cref="All(string?)"/>.</summary>
    public static ProviderAuthDescriptor? Find(string? idOrAlias, string? overlayPath)
    {
        var descriptor = ProviderRegistry.Find(idOrAlias);
        if (descriptor == null)
        {
            return null;
        }

        return Build(descriptor, LoadOverlay(overlayPath));
    }

    private static ProviderAuthDescriptor Build(
        ProviderDescriptor provider,
        IReadOnlyDictionary<string, OverlayEntry> overlay)
    {
        var fields = new List<AuthFieldSpec>();

        if (provider.RequiresApiKey)
        {
            fields.Add(new AuthFieldSpec
            {
                Name = "api_key",
                Label = $"{provider.DisplayName} API key",
                IsSecret = true,
                Required = true,
                MinLength = MinimumApiKeyLength,
                Pattern = PrintableAsciiPattern,
                Hint = $"Also settable without login by exporting {provider.PrimaryApiKeyEnvVar}."
            });

            fields.Add(new AuthFieldSpec
            {
                Name = "account_label",
                Label = "Account label (shown in status)",
                IsSecret = false,
                Required = false,
                MinLength = 0,
                MaxLength = 96,
                Hint = "A non-secret nickname, for example the workspace or billing account this key belongs to."
            });
        }

        OAuthEndpointConfig? oauth = null;
        if (overlay.TryGetValue(provider.Id, out var entry) && entry.OAuth is { IsUsable: true })
        {
            oauth = entry.OAuth;
        }

        return new ProviderAuthDescriptor
        {
            ProviderId = provider.Id,
            DisplayName = provider.DisplayName,
            EnvironmentVariables = provider.ApiKeyEnvVars,
            RequiresCredential = provider.RequiresApiKey,
            ApiKeyFields = fields,
            OAuth = oauth
        };
    }

    private static string? DefaultOverlayPath()
    {
        try
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".andy",
                OverlayFileName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, OverlayEntry> LoadOverlay(string? path)
    {
        var empty = new Dictionary<string, OverlayEntry>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return empty;
        }

        try
        {
            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<OverlayFile>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (parsed?.Providers == null)
            {
                return empty;
            }

            return new Dictionary<string, OverlayEntry>(parsed.Providers, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A malformed overlay must never block a login that would otherwise work with an
            // API key; the built-in descriptors are used instead.
            return empty;
        }
    }

    private sealed class OverlayFile
    {
        [JsonPropertyName("providers")]
        public Dictionary<string, OverlayEntry>? Providers { get; set; }
    }

    private sealed class OverlayEntry
    {
        [JsonPropertyName("oauth")]
        public OAuthEndpointConfig? OAuth { get; set; }
    }
}
