using System;
using System.Collections.Generic;
using System.Linq;

namespace Andy.Cli.Services;

/// <summary>
/// Service for detecting available LLM providers based on environment variables
/// </summary>
public class ProviderDetectionService
{
    private readonly struct ProviderInfo
    {
        public string Name { get; init; }
        public string[] RequiredEnvVars { get; init; }
        public int Priority { get; init; }
    }

    private static readonly ProviderInfo[] Providers = new[]
    {
        new ProviderInfo
        {
            Name = "openai",
            RequiredEnvVars = new[] { "OPENAI_API_KEY" },
            Priority = 1 // Highest priority - most reliable
        },
        new ProviderInfo
        {
            Name = "anthropic",
            RequiredEnvVars = new[] { "ANTHROPIC_API_KEY" },
            Priority = 2
        },
        new ProviderInfo
        {
            Name = "cerebras",
            RequiredEnvVars = new[] { "CEREBRAS_API_KEY" },
            Priority = 3
        },
        new ProviderInfo
        {
            Name = "gemini",
            RequiredEnvVars = new[] { "GOOGLE_API_KEY" },
            Priority = 4
        },
        new ProviderInfo
        {
            Name = "ollama",
            RequiredEnvVars = Array.Empty<string>(), // Ollama doesn't require API key
            Priority = 5
        },
        new ProviderInfo
        {
            Name = "azure",
            RequiredEnvVars = new[] { "AZURE_OPENAI_API_KEY", "AZURE_OPENAI_ENDPOINT" },
            Priority = 6
        }
    };

    /// <summary>
    /// Detects the default provider based on available environment variables
    /// </summary>
    /// <returns>The name of the detected provider, or null if none found</returns>
    public string? DetectDefaultProvider()
    {
        var availableProviders = new List<(string name, int priority)>();

        foreach (var provider in Providers)
        {
            if (IsProviderAvailable(provider))
            {
                availableProviders.Add((provider.Name, provider.Priority));
            }
        }

        // Return the provider with the highest priority (lowest number)
        return availableProviders
            .OrderBy(p => p.priority)
            .Select(p => p.name)
            .FirstOrDefault();
    }

    /// <summary>
    /// Gets all available providers ordered by priority
    /// </summary>
    /// <returns>List of available provider names</returns>
    public List<string> GetAvailableProviders()
    {
        var availableProviders = new List<(string name, int priority)>();

        foreach (var provider in Providers)
        {
            if (IsProviderAvailable(provider))
            {
                availableProviders.Add((provider.Name, provider.Priority));
            }
        }

        return availableProviders
            .OrderBy(p => p.priority)
            .Select(p => p.name)
            .ToList();
    }

    /// <summary>
    /// Checks if a specific provider is available
    /// </summary>
    public bool IsProviderAvailable(string providerName)
    {
        var provider = Providers.FirstOrDefault(p =>
            p.Name.Equals(providerName, StringComparison.OrdinalIgnoreCase));

        if (provider.Name == null)
            return false;

        return IsProviderAvailable(provider);
    }

    private bool IsProviderAvailable(ProviderInfo provider)
    {
        // Special case for Ollama - check if it's running
        if (provider.Name == "ollama")
        {
            return IsOllamaRunning();
        }

        // For other providers, check if all required environment variables are set
        return provider.RequiredEnvVars.All(envVar =>
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar)));
    }

    private bool IsOllamaRunning()
    {
        // Check if Ollama auto-detection is disabled
        var ollamaAutoDetect = Environment.GetEnvironmentVariable("ANDY_OLLAMA_AUTO_DETECT");
        if (ollamaAutoDetect == "false" || ollamaAutoDetect == "0")
        {
            // Only consider Ollama available if OLLAMA_API_BASE is explicitly set
            return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OLLAMA_API_BASE"));
        }

        try
        {
            // Check if OLLAMA_API_BASE is set
            var ollamaBase = Environment.GetEnvironmentVariable("OLLAMA_API_BASE");
            if (!string.IsNullOrEmpty(ollamaBase))
                return true;

            // Check if Ollama should be skipped even if running
            var skipOllama = Environment.GetEnvironmentVariable("ANDY_SKIP_OLLAMA");
            if (skipOllama == "true" || skipOllama == "1")
                return false;

            // Check if default Ollama port is accessible
            // This is a simple check - actual connectivity test would require HTTP client
            using (var client = new System.Net.Http.HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(1);
                var task = client.GetAsync("http://localhost:11434/api/tags");
                task.Wait(1000);
                return task.IsCompletedSuccessfully && task.Result.IsSuccessStatusCode;
            }
        }
        catch
        {
            // If we can't connect, assume Ollama is not available
            return false;
        }
    }

    /// <summary>
    /// Gets diagnostic information about provider detection
    /// </summary>
    public string GetDiagnosticInfo()
    {
        var info = new System.Text.StringBuilder();
        info.AppendLine("Provider Detection Diagnostics:");
        info.AppendLine("-------------------------------");

        foreach (var provider in Providers.OrderBy(p => p.Priority))
        {
            info.Append($"{provider.Name} (priority {provider.Priority}): ");

            if (provider.Name == "ollama")
            {
                info.AppendLine(IsOllamaRunning() ? "Available (running)" : "Not available");
            }
            else
            {
                var missingVars = provider.RequiredEnvVars
                    .Where(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v)))
                    .ToList();

                if (missingVars.Any())
                {
                    info.AppendLine($"Missing: {string.Join(", ", missingVars)}");
                }
                else
                {
                    info.AppendLine("Available");
                }
            }
        }

        var detected = DetectDefaultProvider();
        info.AppendLine();
        info.AppendLine($"Detected Default Provider: {detected ?? "None"}");

        return info.ToString();
    }
}