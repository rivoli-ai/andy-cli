using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Andy.Llm;
using Andy.Llm.Models;
using Andy.Llm.Extensions;
using Andy.Llm.Services;
using Andy.Llm.Configuration;
using Andy.Llm.Abstractions;
using Andy.Llm.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Andy.Cli.Widgets;
using Andy.Cli.Services;

namespace Andy.Cli.Commands;

public class ModelCommand : ICommand
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ModelMemoryService _modelMemory;
    private readonly Dictionary<string, List<ModelInfo>> _modelCache = new();
    private readonly Dictionary<string, DateTime> _cacheTimestamps = new();
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, string> _lastProviderErrors = new();

    private LlmClient? _currentClient;
    private string _currentProvider = "cerebras";
    private string _currentModel = "";
    private IServiceProvider? _currentServiceProvider;

    public string Name => "model";
    public string Description => "Manage AI models and providers";
    public string[] Aliases => new[] { "m", "/model", "/m" };

    public ModelCommand(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _currentClient = serviceProvider.GetService<LlmClient>();
        _modelMemory = new ModelMemoryService();

        // Try to get the configured default provider from options
        var options = serviceProvider.GetService<IOptions<LlmOptions>>();
        var hasConfiguredProvider = options?.Value != null && !string.IsNullOrEmpty(options.Value.DefaultProvider);
        if (hasConfiguredProvider)
        {
            _currentProvider = options.Value.DefaultProvider!;
        }

        // Load last used provider and model
        var current = _modelMemory.GetCurrent();
        if (current.HasValue && !hasConfiguredProvider)
        {
            // Only use the saved provider if it has a valid API key and no provider was explicitly configured
            if (HasApiKey(current.Value.Provider))
            {
                _currentProvider = current.Value.Provider;
                _currentModel = current.Value.Model;
            }
            else
            {
                // Use default model for the current provider
                _currentModel = GetDefaultModel(_currentProvider);
            }
        }
        else if (current.HasValue && _currentProvider == current.Value.Provider)
        {
            // If saved provider matches current provider (from options), use the saved model
            _currentModel = current.Value.Model;
        }
        else
        {
            _currentModel = GetDefaultModel(_currentProvider);
        }
    }

    public async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0)
        {
            return await ListModelsAsync(cancellationToken);
        }

        var subcommand = args[0].ToLowerInvariant();
        var subArgs = args.Skip(1).ToArray();

        return subcommand switch
        {
            "list" or "ls" => await ListModelsAsync(cancellationToken),
            "switch" or "sw" => await SwitchModelAsync(subArgs, cancellationToken),
            "provider" or "p" => await SwitchProviderAsync(subArgs, cancellationToken),
            "info" or "current" => await ShowModelInfoAsync(cancellationToken),
            "test" => await TestModelAsync(subArgs, cancellationToken),
            "refresh" => await RefreshModelsAsync(cancellationToken),
            _ => CommandResult.Failure($"Unknown subcommand: {subcommand}")
        };
    }

    private async Task<CommandResult> ListModelsAsync(CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        result.AppendLine("Available Models and Providers");
        result.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        result.AppendLine();

        var providers = new[] { "cerebras", "openai", "anthropic", "gemini", "ollama" };
        var rememberedModels = _modelMemory.GetAllModels();

        foreach (var provider in providers)
        {
            var hasApiKey = HasApiKey(provider);
            var isCurrentProvider = provider == _currentProvider;
            var url = GetProviderUrl(provider);
            var rememberedModel = rememberedModels.TryGetValue(provider, out var m) ? m : null;
            var currentModel = isCurrentProvider ? _currentModel : rememberedModel;

            // Provider header with URL
            if (isCurrentProvider)
            {
                result.AppendLine($"▶ {provider.ToUpper()} [{url}] (current)");
            }
            else
            {
                result.AppendLine($"  {provider.ToUpper()} [{url}]");
            }

            // API key status and model listing
            if (!hasApiKey && provider != "ollama")
            {
                result.AppendLine($"  ⚠ No API key configured");
                // Show remembered model if any
                if (!string.IsNullOrEmpty(rememberedModel))
                {
                    result.AppendLine($"  Last used: {rememberedModel}");
                }
            }
            else
            {
                // Try to get models for any provider with API key (or ollama)
                var models = await GetProviderModelsAsync(provider, cancellationToken);

                if (models.Any())
                {
                    result.AppendLine($"  Models ({models.Count}):");

                    // Show first 15 models and indicate if there are more
                    var displayModels = models.Take(15).ToList();
                    foreach (var model in displayModels)
                    {
                        var isCurrentModel = isCurrentProvider && model.Id == currentModel;
                        var isRemembered = model.Id == rememberedModel;

                        if (isCurrentModel)
                        {
                            result.AppendLine($"    ▸ {model.Id} ← active");
                        }
                        else if (isRemembered)
                        {
                            result.AppendLine($"    • {model.Id} ← last used");
                        }
                        else
                        {
                            result.AppendLine($"    • {model.Id}");
                        }

                        if (!string.IsNullOrEmpty(model.Description))
                        {
                            result.AppendLine($"      {model.Description}");
                        }
                    }

                    if (models.Count > 15)
                    {
                        result.AppendLine($"    ... and {models.Count - 15} more models");
                        result.AppendLine($"    Use /model refresh to update the list");
                    }
                }
                else
                {
                    // Show more specific error message
                    if (hasApiKey)
                    {
                        result.AppendLine($"  Models: Unable to fetch");
                        // Show detailed error if available
                        if (_lastProviderErrors.TryGetValue(provider, out var error))
                        {
                            result.AppendLine($"    Error: {error}");
                        }
                        else
                        {
                            result.AppendLine($"    (check API key or connectivity)");
                        }
                    }
                    else if (provider == "ollama")
                    {
                        result.AppendLine($"  Models: Ollama service not running or unreachable");
                    }
                    else
                    {
                        result.AppendLine($"  Models: No API key configured");
                    }
                    
                    // Show remembered model if any
                    if (!string.IsNullOrEmpty(rememberedModel))
                    {
                        result.AppendLine($"    • {rememberedModel} ← last used");
                    }
                }
            }

            result.AppendLine();
        }

        result.AppendLine("Commands:");
        result.AppendLine("  /model switch <model>     - Switch to a different model (same provider)");
        result.AppendLine("  /model provider <name>    - Switch to a different provider");
        result.AppendLine("  /model refresh            - Refresh model lists from API");
        result.AppendLine("  /model test [prompt]      - Test the current model");
        result.AppendLine();
        result.AppendLine("Example: /model switch gpt-4o-mini");
        result.AppendLine("Example: /model provider anthropic");

        return CommandResult.CreateSuccess(result.ToString());
    }

    private async Task<List<ModelInfo>> GetProviderModelsAsync(string providerName, CancellationToken cancellationToken)
    {
        // Check cache first
        if (_modelCache.TryGetValue(providerName, out var cachedModels) &&
            _cacheTimestamps.TryGetValue(providerName, out var timestamp) &&
            DateTime.UtcNow - timestamp < _cacheExpiry)
        {
            return cachedModels;
        }

        try
        {
            // Clear any previous error for this provider
            _lastProviderErrors.Remove(providerName);
            
            // Try to use the existing service provider first
            var factory = _serviceProvider.GetService<ILlmProviderFactory>();
            
            if (factory == null)
            {
                // Fallback: Create a temporary service provider for the specific provider
                var services = new ServiceCollection();
                services.AddLogging();
                services.ConfigureLlmFromEnvironment();
                services.AddLlmServices(options =>
                {
                    options.DefaultProvider = providerName;
                });

                var tempProvider = services.BuildServiceProvider();
                factory = tempProvider.GetService<ILlmProviderFactory>();
            }

            if (factory != null)
            {
                var provider = factory.CreateProvider(providerName);
                if (provider != null)
                {
                    var models = await provider.ListModelsAsync(cancellationToken);
                    var modelList = models?.ToList() ?? new List<ModelInfo>();

                    // Update cache
                    _modelCache[providerName] = modelList;
                    _cacheTimestamps[providerName] = DateTime.UtcNow;

                    return modelList;
                }
                else
                {
                    // More specific error messages for common issues
                    if (!HasApiKey(providerName) && providerName != "ollama")
                    {
                        _lastProviderErrors[providerName] = $"No API key configured (set {GetApiKeyName(providerName)})";
                    }
                    else
                    {
                        _lastProviderErrors[providerName] = $"Could not create provider instance";
                    }
                }
            }
            else
            {
                _lastProviderErrors[providerName] = "LLM provider factory not available";
            }
        }
        catch (HttpRequestException httpEx)
        {
            // Network or API errors
            var errorMsg = httpEx.StatusCode?.ToString() ?? "Network error";
            if (httpEx.Message.Contains("401") || httpEx.Message.Contains("Unauthorized"))
            {
                _lastProviderErrors[providerName] = $"Invalid API key (401 Unauthorized)";
            }
            else if (httpEx.Message.Contains("403") || httpEx.Message.Contains("Forbidden"))
            {
                _lastProviderErrors[providerName] = $"Access denied (403 Forbidden) - Check API permissions";
            }
            else if (httpEx.Message.Contains("404"))
            {
                _lastProviderErrors[providerName] = $"API endpoint not found (404) - Check provider URL";
            }
            else
            {
                _lastProviderErrors[providerName] = $"{errorMsg}: {httpEx.Message}";
            }
        }
        catch (TaskCanceledException)
        {
            _lastProviderErrors[providerName] = "Request timeout - API may be unavailable";
        }
        catch (Exception ex)
        {
            // Log error but don't fail completely
            var errorMsg = ex.Message;
            if (ex.InnerException != null)
            {
                errorMsg = ex.InnerException.Message;
            }
            
            // Store error for display - keep it concise
            if (errorMsg.Length > 100)
            {
                errorMsg = errorMsg.Substring(0, 100) + "...";
            }
            _lastProviderErrors[providerName] = errorMsg;
        }

        // Return empty list if we can't fetch models
        return new List<ModelInfo>();
    }

    private async Task<CommandResult> RefreshModelsAsync(CancellationToken cancellationToken)
    {
        // Clear cache to force refresh
        _modelCache.Clear();
        _cacheTimestamps.Clear();

        var result = new StringBuilder();
        result.AppendLine("Refreshing model lists from API...");
        result.AppendLine();

        var providers = new[] { "cerebras", "openai", "anthropic", "gemini", "ollama" };

        foreach (var provider in providers)
        {
            if (HasApiKey(provider) || provider == "ollama")
            {
                var models = await GetProviderModelsAsync(provider, cancellationToken);
                result.AppendLine($"{provider}: Found {models.Count} models");
            }
            else
            {
                result.AppendLine($"{provider}: Skipped (no API key)");
            }
        }

        return CommandResult.CreateSuccess(result.ToString());
    }

    private async Task<CommandResult> SwitchModelAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length < 1)
        {
            return CommandResult.Failure("Usage: /model switch <model-name> OR /model switch <provider> <model-name>");
        }

        string providerName = _currentProvider;
        string modelName;

        // Check if first arg is a known provider
        if (args.Length >= 2 && IsKnownProvider(args[0].ToLowerInvariant()))
        {
            // Format: /model switch openai gpt-4
            providerName = args[0].ToLowerInvariant();
            modelName = string.Join(" ", args.Skip(1)).Trim();
        }
        else
        {
            // Format: /model switch gpt-4 (use current provider)
            modelName = string.Join(" ", args).Trim();
            
            // First check if the model exists in the current provider
            var currentProviderModels = await GetProviderModelsAsync(_currentProvider, cancellationToken);
            if (!currentProviderModels.Any(m => m.Id.Equals(modelName, StringComparison.OrdinalIgnoreCase)))
            {
                // Model not in current provider, search other providers
                var detectedProvider = await DetectProviderFromModelNameAsync(modelName, cancellationToken);
                if (detectedProvider != null && detectedProvider != _currentProvider)
                {
                    // Model belongs to a different provider, auto-switch
                    providerName = detectedProvider;
                }
            }
        }

        // Check if we need to switch providers
        bool providerChanged = providerName != _currentProvider;

        // Validate provider has API key
        if (!HasApiKey(providerName) && providerName != "ollama")
        {
            return CommandResult.Failure(ConsoleColors.ErrorPrefix($"No API key found for {providerName}. Set {GetApiKeyName(providerName)} environment variable."));
        }

        try
        {
            // Update current provider and model
            _currentProvider = providerName;
            _currentModel = modelName;
            _modelMemory.SetCurrent(providerName, modelName);

            // Recreate services with new provider and model
            var services = new ServiceCollection();
            services.AddLogging();
            services.ConfigureLlmFromEnvironment();

            // Override the model in environment
            Environment.SetEnvironmentVariable($"{providerName.ToUpper()}_MODEL", modelName);

            services.AddLlmServices(options =>
            {
                options.DefaultProvider = providerName;
                if (options.Providers.TryGetValue(providerName, out var config))
                {
                    config.Model = modelName;
                }
            });

            var newProvider = services.BuildServiceProvider();
            _currentServiceProvider = newProvider;
            _currentClient = newProvider.GetService<LlmClient>();

            var message = new StringBuilder();
            if (providerChanged)
            {
                // Check if this was an auto-switch (model not explicitly paired with provider)
                if (args.Length == 1 || (args.Length > 1 && !IsKnownProvider(args[0].ToLowerInvariant())))
                {
                    // Auto-switched to the correct provider for this model
                    message.AppendLine(ConsoleColors.SuccessPrefix($"Auto-switched to {providerName} provider for model: {modelName}"));
                    message.AppendLine($"Provider URL: {GetProviderUrl(providerName)}");
                }
                else
                {
                    message.AppendLine(ConsoleColors.SuccessPrefix($"Switched to provider: {providerName}"));
                    message.AppendLine($"Model: {modelName}");
                    message.AppendLine($"Provider URL: {GetProviderUrl(providerName)}");
                }
                message.AppendLine();
                message.AppendLine(ConsoleColors.NotePrefix("Conversation context reset for new provider"));
            }
            else
            {
                message.AppendLine(ConsoleColors.SuccessPrefix($"Switched to model: {modelName}"));
                message.AppendLine($"Provider: {providerName} [{GetProviderUrl(providerName)}]");
            }

            return CommandResult.CreateSuccess(message.ToString());
        }
        catch (Exception ex)
        {
            return CommandResult.Failure(ConsoleColors.ErrorPrefix($"Failed to switch model: {ex.Message}"));
        }
    }

    private Task<CommandResult> SwitchProviderAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length < 1)
        {
            return Task.FromResult(CommandResult.Failure("Usage: /model provider <provider-name>"));
        }

        var provider = args[0].ToLowerInvariant();

        // Check if this is a known provider
        if (!IsKnownProvider(provider))
        {
            var message = new StringBuilder();
            message.AppendLine(ConsoleColors.ErrorPrefix($"'{provider}' is not a recognized provider."));
            message.AppendLine();
            message.AppendLine("Supported providers:");
            message.AppendLine("  • cerebras  - Fast inference with Llama models [https://api.cerebras.ai]");
            message.AppendLine("  • openai    - GPT models [https://api.openai.com or custom]");
            message.AppendLine("  • anthropic - Claude models [https://api.anthropic.com]");
            message.AppendLine("  • gemini    - Google Gemini models [https://generativelanguage.googleapis.com]");
            message.AppendLine("  • ollama    - Local models via Ollama [http://localhost:11434]");
            message.AppendLine();
            message.AppendLine("For custom endpoints, set environment variables:");
            message.AppendLine("  OPENAI_API_BASE=<your-endpoint>");
            message.AppendLine("  OLLAMA_API_BASE=<your-endpoint>");
            return Task.FromResult(CommandResult.Failure(message.ToString()));
        }

        if (!HasApiKey(provider))
        {
            return Task.FromResult(CommandResult.Failure(ConsoleColors.ErrorPrefix($"No API key found for {provider}. Set {GetApiKeyName(provider)} environment variable.")));
        }

        try
        {
            // Get the last used model for this provider or default
            var modelName = _modelMemory.GetLastModel(provider) ?? GetDefaultModel(provider);

            // Update current provider and model
            _currentProvider = provider;
            _currentModel = modelName;
            _modelMemory.SetCurrent(provider, modelName);

            // Recreate services with new provider
            var services = new ServiceCollection();
            services.AddLogging();
            services.ConfigureLlmFromEnvironment();
            services.AddLlmServices(options =>
            {
                options.DefaultProvider = provider;
            });

            var newProvider = services.BuildServiceProvider();
            _currentServiceProvider = newProvider;
            _currentClient = newProvider.GetService<LlmClient>();

            var message = new StringBuilder();
            message.AppendLine(ConsoleColors.SuccessPrefix($"Switched to provider: {provider}"));
            message.AppendLine($"URL: {GetProviderUrl(provider)}");
            message.AppendLine($"Model: {modelName}");

            return Task.FromResult(CommandResult.CreateSuccess(message.ToString()));
        }
        catch (Exception ex)
        {
            return Task.FromResult(CommandResult.Failure(ConsoleColors.ErrorPrefix($"Failed to switch provider: {ex.Message}")));
        }
    }

    private async Task<CommandResult> ShowModelInfoAsync(CancellationToken cancellationToken)
    {
        var info = new StringBuilder();
        info.AppendLine("Current Model Information");
        info.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        info.AppendLine();

        info.AppendLine($"Provider: {_currentProvider}");
        info.AppendLine($"URL: {GetProviderUrl(_currentProvider)}");
        info.AppendLine($"Model: {_currentModel}");
        info.AppendLine($"Status: {(_currentClient != null ? "Connected" : "Not connected")}");
        info.AppendLine();

        // Try to get current model info
        var models = await GetProviderModelsAsync(_currentProvider, cancellationToken);
        var currentModelInfo = models.FirstOrDefault(m => m.Id == _currentModel);
        if (currentModelInfo != null && !string.IsNullOrEmpty(currentModelInfo.Description))
        {
            info.AppendLine("Model Details:");
            info.AppendLine($"  {currentModelInfo.Description}");
            if (currentModelInfo.MaxTokens.HasValue && currentModelInfo.MaxTokens.Value > 0)
            {
                info.AppendLine($"  Context: {currentModelInfo.MaxTokens.Value:N0} tokens");
            }
            info.AppendLine();
        }

        // Show remembered models
        var remembered = _modelMemory.GetAllModels();
        if (remembered.Any())
        {
            info.AppendLine("Remembered Models:");
            foreach (var (provider, model) in remembered)
            {
                if (provider != "_current")
                {
                    info.AppendLine($"  {provider}: {model}");
                }
            }
        }

        return CommandResult.CreateSuccess(info.ToString());
    }

    private async Task<CommandResult> TestModelAsync(string[] args, CancellationToken cancellationToken)
    {
        if (_currentClient == null)
        {
            return CommandResult.Failure(ConsoleColors.ErrorPrefix("No model client available. Please switch to a valid model first."));
        }

        var prompt = args.Length > 0
            ? string.Join(" ", args)
            : "Hello! Please respond with a brief greeting.";

        try
        {
            var startTime = DateTime.UtcNow;
            // Create a simple conversation context for testing
            var testConversation = new ConversationContext
            {
                SystemInstruction = "You are a helpful assistant. Respond briefly.",
                MaxContextMessages = 1
            };
            testConversation.AddUserMessage(prompt);
            var request = testConversation.CreateRequest();
            request.MaxTokens = 100;

            var response = await _currentClient.CompleteAsync(request, cancellationToken);
            var elapsed = DateTime.UtcNow - startTime;

            var result = new StringBuilder();
            result.AppendLine("Model Test Results:");
            result.AppendLine();
            result.AppendLine($"Provider: {_currentProvider} [{GetProviderUrl(_currentProvider)}]");
            result.AppendLine($"Model: {_currentModel}");
            result.AppendLine($"Prompt: {prompt}");
            result.AppendLine($"Response: {response.Content}");
            result.AppendLine($"Response Time: {elapsed.TotalMilliseconds:F0}ms");

            if (response.TokensUsed.HasValue)
            {
                result.AppendLine($"Tokens Used: {response.TokensUsed.Value}");
            }

            return CommandResult.CreateSuccess(result.ToString());
        }
        catch (Exception ex)
        {
            return CommandResult.Failure(ConsoleColors.ErrorPrefix($"Model test failed: {ex.Message}"));
        }
    }

    private bool IsKnownProvider(string provider)
    {
        return provider switch
        {
            "cerebras" or "openai" or "anthropic" or "gemini" or "ollama" => true,
            _ => false
        };
    }

    private bool HasApiKey(string provider)
    {
        return provider switch
        {
            "cerebras" => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CEREBRAS_API_KEY")),
            "openai" => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY")),
            "anthropic" => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")),
            "gemini" => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GOOGLE_API_KEY")),
            "ollama" => true, // Ollama doesn't require an API key
            _ => false
        };
    }

    private string GetApiKeyName(string provider)
    {
        return provider switch
        {
            "cerebras" => "CEREBRAS_API_KEY",
            "openai" => "OPENAI_API_KEY",
            "anthropic" => "ANTHROPIC_API_KEY",
            "gemini" => "GOOGLE_API_KEY",
            _ => $"{provider.ToUpper()}_API_KEY"
        };
    }

    private string GetProviderUrl(string provider)
    {
        return provider switch
        {
            "cerebras" => "https://api.cerebras.ai",
            "openai" => Environment.GetEnvironmentVariable("OPENAI_API_BASE") ?? "https://api.openai.com",
            "anthropic" => "https://api.anthropic.com",
            "gemini" => "https://generativelanguage.googleapis.com",
            "ollama" => Environment.GetEnvironmentVariable("OLLAMA_API_BASE") ?? "http://localhost:11434",
            _ => "unknown"
        };
    }

    private string GetDefaultModel(string provider)
    {
        // Check environment variables first
        var envModel = Environment.GetEnvironmentVariable($"{provider.ToUpper()}_MODEL");
        if (!string.IsNullOrEmpty(envModel))
        {
            return envModel;
        }

        return provider switch
        {
            "cerebras" => "llama-3.3-70b",
            "openai" => "gpt-4o",
            "anthropic" => "claude-3-sonnet-20240229",
            "gemini" => "gemini-2.0-flash-exp",
            "ollama" => "llama2",
            _ => "unknown"
        };
    }

    private async Task<string?> DetectProviderFromModelNameAsync(string modelName, CancellationToken cancellationToken)
    {
        // Check all providers to see which one has this model
        var providers = new[] { "cerebras", "openai", "anthropic", "gemini", "ollama" };
        
        foreach (var provider in providers)
        {
            // Skip providers without API keys (except ollama)
            if (!HasApiKey(provider) && provider != "ollama")
                continue;
            
            try
            {
                // Get models for this provider (from cache or API)
                var models = await GetProviderModelsAsync(provider, cancellationToken);
                
                // Check if this provider has the requested model
                if (models.Any(m => m.Id.Equals(modelName, StringComparison.OrdinalIgnoreCase)))
                {
                    return provider;
                }
            }
            catch
            {
                // Continue checking other providers if this one fails
                continue;
            }
        }
        
        // No provider found with this model
        return null;
    }

    public LlmClient? GetCurrentClient() => _currentClient;
    public string GetCurrentProvider() => _currentProvider;
    public string GetCurrentModel() => _currentModel;

    public async Task<ModelListItem> CreateModelListItemAsync(CancellationToken cancellationToken = default)
    {
        var item = new ModelListItem($"Models - {_currentProvider}: {_currentModel}");

        // Get all available providers
        var providerNames = new[] { "cerebras", "openai", "anthropic", "gemini", "ollama" };

        foreach (var provider in providerNames)
        {
            var hasApiKey = HasApiKey(provider);
            var isCurrentProvider = provider == _currentProvider;
            var url = GetProviderUrl(provider);
            var status = !hasApiKey && provider != "ollama" ? " (no key)" : isCurrentProvider ? " ← current" : "";

            // Add provider with URL in description
            item.AddProvider($"{provider} [{url}]{status}");

            // Show models for current provider only (to keep visual display compact)
            if (isCurrentProvider)
            {
                // Get actual models from API
                var models = await GetProviderModelsAsync(provider, cancellationToken);

                if (models.Any())
                {
                    // Show first 5 models in visual display
                    foreach (var model in models.Take(5))
                    {
                        var isCurrentModel = model.Id == _currentModel;
                        var description = isCurrentModel ? "active" : "";
                        item.AddModel(model.Id, description, true, isCurrentModel);
                    }

                    if (models.Count > 5)
                    {
                        item.AddModel($"... and {models.Count - 5} more", "Use /model list to see all", false, false);
                    }
                }
                else
                {
                    // Fallback if API is unavailable
                    item.AddModel(_currentModel, "active", true, true);
                }
            }
        }

        return item;
    }
}