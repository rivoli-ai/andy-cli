using System;
using System.Collections.Generic;
using System.Linq;

namespace Andy.Cli.Services;

/// <summary>
/// Service for detecting available LLM providers based on environment variables.
/// The provider set, priorities, and required env vars come from
/// <see cref="ProviderRegistry"/> so detection stays in sync with the /model command.
/// </summary>
public class ProviderDetectionService
{
    /// <summary>
    /// Detects the default provider based on available environment variables
    /// </summary>
    /// <returns>The canonical id of the detected provider, or null if none found</returns>
    public string? DetectDefaultProvider()
    {
        return ProviderRegistry.All
            .Where(IsProviderAvailable)
            .OrderBy(p => p.DetectionPriority)
            .Select(p => p.Id)
            .FirstOrDefault();
    }

    /// <summary>
    /// Gets all available providers ordered by priority
    /// </summary>
    /// <returns>List of available provider ids</returns>
    public List<string> GetAvailableProviders()
    {
        return ProviderRegistry.All
            .Where(IsProviderAvailable)
            .OrderBy(p => p.DetectionPriority)
            .Select(p => p.Id)
            .ToList();
    }

    /// <summary>
    /// Checks if a specific provider (by id or alias) is available
    /// </summary>
    public bool IsProviderAvailable(string providerName)
    {
        var descriptor = ProviderRegistry.Find(providerName);
        if (descriptor == null)
            return false;

        return IsProviderAvailable(descriptor);
    }

    private bool IsProviderAvailable(ProviderDescriptor provider)
    {
        // Local providers (e.g. Ollama) are detected by probing rather than by env var
        if (provider.IsLocal)
        {
            return IsOllamaRunning();
        }

        return ProviderRegistry.HasCredentials(provider.Id);
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

        foreach (var provider in ProviderRegistry.All.OrderBy(p => p.DetectionPriority))
        {
            info.Append($"{provider.Id} (priority {provider.DetectionPriority}): ");

            if (provider.IsLocal)
            {
                info.AppendLine(IsOllamaRunning() ? "Available (running)" : "Not available");
            }
            else
            {
                var missingVars = provider.ApiKeyEnvVars
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
