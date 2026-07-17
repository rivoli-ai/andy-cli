using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Andy.Cli.Widgets;
using Andy.Cli.Services;
using Andy.Llm;
using Andy.Llm.Configuration;
using Andy.Llm.Extensions;
using Andy.Llm.Providers;
using Andy.Model.Llm;

namespace Andy.Cli.Commands;

public class ModelCommand : ICommand
{
    private IServiceProvider _serviceProvider;
    private readonly ModelMemoryService _modelMemory;
    private readonly Dictionary<string, List<ModelInfo>> _modelCache = new();
    private readonly Dictionary<string, DateTime> _cacheTimestamps = new();
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, string> _lastProviderErrors = new();

    private ILlmProvider? _currentProviderInstance;
    private string _currentProviderName = "cerebras";
    private string _currentModel = "";
    private IServiceProvider? _currentServiceProvider;

    public string Name => "model";
    public string Description => "Manage AI models and providers";
    public string[] Aliases => new[] { "m", "/model", "/m" };

    public ModelCommand(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _modelMemory = new ModelMemoryService();

        // First priority: Check for configured default provider from options
        var options = serviceProvider.GetService<IOptions<LlmOptions>>();
        var hasConfiguredProvider = options?.Value != null && !string.IsNullOrEmpty(options.Value.DefaultProvider);
        if (hasConfiguredProvider)
        {
            // Resolve any alias (e.g. "gemini") to its canonical id so downstream lookups match
            _currentProviderName = ProviderRegistry.Resolve(options!.Value.DefaultProvider) ?? options.Value.DefaultProvider!;
        }
        else
        {
            // Second priority: Auto-detect based on environment variables
            var detectionService = new ProviderDetectionService();
            var detectedProvider = detectionService.DetectDefaultProvider();

            if (!string.IsNullOrEmpty(detectedProvider))
            {
                // Detection already returns a canonical id, but resolve defensively so every
                // startup path stores the canonical provider id.
                _currentProviderName = ProviderRegistry.Resolve(detectedProvider) ?? detectedProvider;
            }
            else
            {
                // Third priority: Load last used provider
                var current = _modelMemory.GetCurrent();
                if (current.HasValue)
                {
                    // Resolve any stored alias (e.g. "gemini") to its canonical id before use so the
                    // provider factory (which only knows canonical ids) can create the provider.
                    var storedProvider = ProviderRegistry.Resolve(current.Value.Provider) ?? current.Value.Provider;
                    if (HasApiKey(storedProvider))
                    {
                        _currentProviderName = storedProvider;
                        _currentModel = current.Value.Model;
                    }
                }
            }
        }

        // Load last used model for the current provider. Resolve the stored provider id so an
        // aliased entry (e.g. "gemini") still matches the canonical current provider ("google").
        var savedConfig = _modelMemory.GetCurrent();
        var savedProvider = savedConfig.HasValue
            ? ProviderRegistry.Resolve(savedConfig.Value.Provider) ?? savedConfig.Value.Provider
            : null;
        if (savedConfig.HasValue && _currentProviderName == savedProvider)
        {
            // If saved provider matches current provider, use the saved model
            _currentModel = savedConfig.Value.Model;
        }
        else
        {
            // Otherwise use default model for the provider
            _currentModel = GetDefaultModel(_currentProviderName);
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
            "detect" or "diagnostics" => ShowProviderDiagnostics(),
            _ => CommandResult.Failure($"Unknown subcommand: {subcommand}")
        };
    }

    private async Task<CommandResult> ListModelsAsync(CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        result.AppendLine("Available Models and Providers");
        result.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        result.AppendLine();

        var providers = ProviderRegistry.Ids;
        var rememberedModels = _modelMemory.GetAllModels();

        foreach (var provider in providers)
        {
            var hasApiKey = HasApiKey(provider);
            var isCurrentProvider = provider == _currentProviderName;
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
            else if (!ProviderRegistry.SupportsModelListing(provider))
            {
                // Provider does not expose a model-listing API; skip the query.
                result.AppendLine($"  Models: Listing not supported by this provider");
                if (!string.IsNullOrEmpty(rememberedModel))
                {
                    result.AppendLine($"    • {rememberedModel} ← last used");
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

    private CommandResult ShowProviderDiagnostics()
    {
        var detectionService = new ProviderDetectionService();
        var diagnostics = detectionService.GetDiagnosticInfo();

        var result = new StringBuilder();
        result.AppendLine();
        result.AppendLine(diagnostics);
        result.AppendLine();
        result.AppendLine("Current Settings:");
        result.AppendLine($"  Active Provider: {_currentProviderName}");
        result.AppendLine($"  Active Model: {_currentModel}");

        return CommandResult.CreateSuccess(result.ToString());
    }

    private async Task<CommandResult> RefreshModelsAsync(CancellationToken cancellationToken)
    {
        // Clear cache to force refresh
        _modelCache.Clear();
        _cacheTimestamps.Clear();

        var result = new StringBuilder();
        result.AppendLine("Refreshing model lists from API...");
        result.AppendLine();

        var providers = ProviderRegistry.Ids;

        foreach (var provider in providers)
        {
            if (!ProviderRegistry.SupportsModelListing(provider))
            {
                result.AppendLine($"{provider}: Skipped (model listing not supported)");
            }
            else if (HasApiKey(provider) || provider == "ollama")
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

        string providerName = _currentProviderName;
        string modelName;

        // Check if first arg is a known provider (id or alias)
        if (args.Length >= 2 && IsKnownProvider(args[0].ToLowerInvariant()))
        {
            // Format: /model switch openai gpt-4 (resolve any alias to its canonical id)
            providerName = ProviderRegistry.Resolve(args[0].ToLowerInvariant())!;
            modelName = string.Join(" ", args.Skip(1)).Trim();
        }
        else
        {
            // Format: /model switch gpt-4 (use current provider)
            modelName = string.Join(" ", args).Trim();

            // First check if the model exists in the current provider
            var currentProviderModels = await GetProviderModelsAsync(_currentProviderName, cancellationToken);
            if (!currentProviderModels.Any(m => m.Id.Equals(modelName, StringComparison.OrdinalIgnoreCase)))
            {
                // Model not in current provider, search other providers
                var detectedProvider = await DetectProviderFromModelNameAsync(modelName, cancellationToken);
                if (detectedProvider != null && detectedProvider != _currentProviderName)
                {
                    // Model belongs to a different provider, auto-switch
                    providerName = detectedProvider;
                }
            }
        }

        // Check if we need to switch providers
        bool providerChanged = providerName != _currentProviderName;

        // Validate provider has API key
        if (!HasApiKey(providerName) && providerName != "ollama")
        {
            return CommandResult.Failure(ConsoleColors.ErrorPrefix($"No API key found for {providerName}. Set {GetApiKeyName(providerName)} environment variable."));
        }

        try
        {
            // Update current provider and model
            _currentProviderName = providerName;
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
            SwapServiceProvider(newProvider); // Dispose the previous service provider/clients
            GetOrCreateProviderInstance(); // Try to create new instance

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

                // Show tool limitations for providers that impose a small tool-count limit
                if (ProviderRegistry.Find(providerName)?.LimitsToolCount == true)
                {
                    message.AppendLine();
                    message.AppendLine(ConsoleColors.NotePrefix($"{providerName} provider limited to 4 essential tools to prevent API errors"));
                    message.AppendLine("  Available tools: list_directory, read_file, execute_command, search_files");
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

        var requested = args[0].ToLowerInvariant();

        // Check if this is a known provider (id or alias) and resolve to its canonical id
        var provider = ProviderRegistry.Resolve(requested);
        if (provider == null)
        {
            return Task.FromResult(CommandResult.Failure(BuildUnknownProviderMessage(requested)));
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
            _currentProviderName = provider;
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
            SwapServiceProvider(newProvider); // Dispose the previous service provider/clients
            GetOrCreateProviderInstance(); // Try to create new instance

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

        info.AppendLine($"Provider: {_currentProviderName}");
        info.AppendLine($"URL: {GetProviderUrl(_currentProviderName)}");
        info.AppendLine($"Model: {_currentModel}");
        info.AppendLine($"Status: {(GetOrCreateProviderInstance() != null ? "Connected" : "Not connected")}");
        info.AppendLine();

        // Try to get current model info
        var models = await GetProviderModelsAsync(_currentProviderName, cancellationToken);
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
        var provider = GetOrCreateProviderInstance();
        if (provider == null)
        {
            return CommandResult.Failure(ConsoleColors.ErrorPrefix("No model client available. Please switch to a valid model first."));
        }

        var prompt = args.Length > 0
            ? string.Join(" ", args)
            : "Hello! Please respond with a brief greeting.";

        try
        {
            var startTime = DateTime.UtcNow;
            // Create a simple test request
            var request = new Andy.Model.Llm.LlmRequest
            {
                Messages = new[]
                {
                    new Andy.Model.Model.Message
                    {
                        Role = Andy.Model.Model.Role.System,
                        Content = "You are a helpful assistant. Respond briefly."
                    },
                    new Andy.Model.Model.Message
                    {
                        Role = Andy.Model.Model.Role.User,
                        Content = prompt
                    }
                },
                Config = new Andy.Model.Llm.LlmClientConfig
                {
                    MaxTokens = 100,
                    Model = _currentModel
                }
            };

            var response = await provider.CompleteAsync(request, cancellationToken);
            var elapsed = DateTime.UtcNow - startTime;

            var result = new StringBuilder();
            result.AppendLine("Model Test Results:");
            result.AppendLine();
            result.AppendLine($"Provider: {_currentProviderName} [{GetProviderUrl(_currentProviderName)}]");
            result.AppendLine($"Model: {_currentModel}");
            result.AppendLine($"Prompt: {prompt}");
            result.AppendLine($"Response: {response.AssistantMessage?.Content ?? "No response"}");
            result.AppendLine($"Response Time: {elapsed.TotalMilliseconds:F0}ms");

            if (response.Usage != null)
            {
                result.AppendLine($"Tokens Used: {response.Usage.TotalTokens}");
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
        return ProviderRegistry.IsKnown(provider);
    }

    private bool HasApiKey(string provider)
    {
        return ProviderRegistry.HasCredentials(provider);
    }

    private string GetApiKeyName(string provider)
    {
        return ProviderRegistry.GetApiKeyEnvVar(provider);
    }

    private string GetProviderUrl(string provider)
    {
        return ProviderRegistry.GetEndpoint(provider);
    }

    private string GetDefaultModel(string provider)
    {
        // Check environment variables first
        var envModel = Environment.GetEnvironmentVariable($"{provider.ToUpper()}_MODEL");
        if (!string.IsNullOrEmpty(envModel))
        {
            return envModel;
        }

        // Check configured default from LlmOptions
        var options = _serviceProvider.GetService<IOptions<Andy.Llm.Configuration.LlmOptions>>();
        if (options?.Value?.Providers != null &&
            options.Value.Providers.TryGetValue(provider.ToLowerInvariant(), out var providerConfig) &&
            !string.IsNullOrEmpty(providerConfig.Model))
        {
            return providerConfig.Model;
        }

        // Fall back to the registry default
        return ProviderRegistry.Find(provider)?.DefaultModel ?? "unknown";
    }

    private async Task<string?> DetectProviderFromModelNameAsync(string modelName, CancellationToken cancellationToken)
    {
        // Check all providers to see which one has this model
        var providers = ProviderRegistry.Ids;

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

    private ILlmProvider? GetOrCreateProviderInstance()
    {
        if (_currentProviderInstance == null)
        {
            var providerFactory = _serviceProvider.GetService<ILlmProviderFactory>();
            if (providerFactory != null && HasApiKey(_currentProviderName))
            {
                try
                {
                    _currentProviderInstance = providerFactory.CreateProvider(_currentProviderName);
                }
                catch (Exception ex)
                {
                    // Log but don't crash - provider creation failed
                    _lastProviderErrors[_currentProviderName] = ex.Message;
                }
            }
        }
        return _currentProviderInstance;
    }

    public ILlmProvider? GetCurrentProviderInstance() => GetOrCreateProviderInstance();
    public string GetCurrentProvider() => _currentProviderName;
    public string GetCurrentModel() => _currentModel;

    /// <summary>
    /// Adopts a freshly built service provider as the current one and disposes the previously
    /// created service provider (and any disposable clients it owned).
    /// </summary>
    private void SwapServiceProvider(ServiceProvider newProvider)
    {
        var previous = _currentServiceProvider;
        _currentServiceProvider = newProvider;
        _currentProviderInstance = null; // Clear old provider instance
        _serviceProvider = newProvider;  // Route subsequent lookups to the new provider

        if (previous != null && !ReferenceEquals(previous, newProvider))
        {
            try
            {
                (previous as IDisposable)?.Dispose();
            }
            catch
            {
                // Disposal is best-effort; never fail a switch because cleanup threw.
            }
        }
    }

    /// <summary>
    /// Builds the "unknown provider" help message from the registry so the advertised
    /// provider set always matches what detection and switching actually support.
    /// </summary>
    private static string BuildUnknownProviderMessage(string requested)
    {
        var message = new StringBuilder();
        message.AppendLine(ConsoleColors.ErrorPrefix($"'{requested}' is not a recognized provider."));
        message.AppendLine();
        message.AppendLine("Supported providers:");
        foreach (var descriptor in ProviderRegistry.All)
        {
            var aliasNote = descriptor.Aliases.Count > 0
                ? $" (alias: {string.Join(", ", descriptor.Aliases)})"
                : "";
            message.AppendLine($"  • {descriptor.Id}{aliasNote} - {descriptor.DisplayName} [{ProviderRegistry.GetEndpoint(descriptor.Id)}]");
        }
        message.AppendLine();
        message.AppendLine("For custom endpoints, set environment variables:");
        message.AppendLine("  OPENAI_API_BASE=<your-endpoint>");
        message.AppendLine("  OLLAMA_API_BASE=<your-endpoint>");
        return message.ToString();
    }

    public async Task<ModelListItem> CreateModelListItemAsync(CancellationToken cancellationToken = default)
    {
        var item = new ModelListItem($"Models - {_currentProviderName}: {_currentModel}");

        // Get all available providers
        var providerNames = ProviderRegistry.Ids;

        foreach (var provider in providerNames)
        {
            var hasApiKey = HasApiKey(provider);
            var isCurrentProvider = provider == _currentProviderName;
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