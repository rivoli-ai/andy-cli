using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Andy.Llm;
using Andy.Llm.Models;
using Andy.Llm.Extensions;
using Andy.Llm.Services;
using Andy.Llm.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Andy.Cli.Widgets;

namespace Andy.Cli.Commands;

public class ModelCommand : ICommand
{
    private readonly IServiceProvider _serviceProvider;
    private LlmClient? _currentClient;
    private string _currentProvider = "cerebras";
    private IServiceProvider? _currentServiceProvider;
    
    public string Name => "model";
    public string Description => "Manage AI models and providers";
    public string[] Aliases => new[] { "m", "/model", "/m" };
    
    public ModelCommand(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _currentClient = serviceProvider.GetService<LlmClient>();
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
            "info" or "current" => await ShowModelInfoAsync(cancellationToken),
            "test" => await TestModelAsync(subArgs, cancellationToken),
            _ => CommandResult.Failure($"Unknown subcommand: {subcommand}. Use 'model list' to see available models.")
        };
    }
    
    private async Task<CommandResult> ListModelsAsync(CancellationToken cancellationToken)
    {
        // This method is still needed for non-UI list commands, but now uses the dynamic model listing
        var modelListItem = await CreateModelListItemAsync(cancellationToken);
        
        // Convert ModelListItem to text format for CommandResult
        var result = new StringBuilder();
        result.AppendLine("Available Models:");
        result.AppendLine();
        result.AppendLine("Models are now dynamically loaded from each provider's API.");
        result.AppendLine("Use the UI to see colored status indicators.");
        
        return CommandResult.CreateSuccess(result.ToString());
    }
    
    private Task<CommandResult> SwitchModelAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length < 1)
        {
            return Task.FromResult(CommandResult.Failure("Usage: model switch <provider> [model]"));
        }
        
        var provider = args[0].ToLowerInvariant();
        var modelName = args.Length > 1 ? args[1] : GetDefaultModel(provider);
        
        if (!HasApiKey(provider))
        {
            return Task.FromResult(CommandResult.Failure(ConsoleColors.ErrorPrefix($"No API key found for {provider}. Set {GetApiKeyName(provider)} environment variable.")));
        }
        
        try
        {
            // Recreate services with new provider
            var services = new ServiceCollection();
            services.AddLogging();
            services.ConfigureLlmFromEnvironment();
            services.AddLlmServices(options =>
            {
                options.DefaultProvider = provider;
                // Model-specific configuration would go here if supported
            });
            
            var newProvider = services.BuildServiceProvider();
            _currentServiceProvider = newProvider;
            _currentClient = newProvider.GetService<LlmClient>();
            _currentProvider = provider;
            
            return Task.FromResult(CommandResult.CreateSuccess(ConsoleColors.SuccessPrefix($"Switched to {provider} - {modelName ?? GetDefaultModel(provider)}")));
        }
        catch (Exception ex)
        {
            return Task.FromResult(CommandResult.Failure(ConsoleColors.ErrorPrefix($"Failed to switch model: {ex.Message}")));
        }
    }
    
    private Task<CommandResult> ShowModelInfoAsync(CancellationToken cancellationToken)
    {
        var info = new StringBuilder();
        info.AppendLine("Current Model Information");
        info.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        info.AppendLine();
        
        info.AppendLine($"Provider: {_currentProvider}");
        info.AppendLine($"Model: {GetDefaultModel(_currentProvider)}");
        info.AppendLine($"Status: {(_currentClient != null ? "Connected" : "Not connected")}");
        info.AppendLine($"API Key: {(HasApiKey(_currentProvider) ? "Set" : "Not set")}");
        
        // Model-specific information
        var modelInfo = GetModelInfo(_currentProvider);
        if (!string.IsNullOrEmpty(modelInfo))
        {
            info.AppendLine();
            info.AppendLine("Specifications:");
            info.AppendLine(modelInfo);
        }
        
        return Task.FromResult(CommandResult.CreateSuccess(info.ToString()));
    }
    
    private async Task<CommandResult> TestModelAsync(string[] args, CancellationToken cancellationToken)
    {
        if (_currentClient == null)
        {
            return CommandResult.Failure(ConsoleColors.ErrorPrefix("No model connected. Use 'model switch' to select a provider."));
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
    
    private bool HasApiKey(string provider)
    {
        return provider switch
        {
            "cerebras" => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CEREBRAS_API_KEY")),
            "openai" => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY")),
            "anthropic" => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")),
            "gemini" => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GOOGLE_API_KEY")),
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
    
    private string GetDefaultModel(string provider)
    {
        return provider switch
        {
            "cerebras" => "llama-3.3-70b",
            "openai" => "gpt-4o",
            "anthropic" => "claude-3-sonnet",
            "gemini" => "gemini-2.0-flash-exp",
            _ => "unknown"
        };
    }
    
    private string GetModelInfo(string provider)
    {
        return provider switch
        {
            "cerebras" => "  Context: 8,192 tokens\n  Speed: Ultra-fast inference\n  Cost: $0.15/M input, $0.15/M output",
            "openai" => "  Context: 128,000 tokens\n  Features: Multimodal, function calling\n  Cost: $5/M input, $15/M output",
            "anthropic" => "  Context: 200,000 tokens\n  Features: Constitutional AI\n  Cost: $3/M input, $15/M output",
            "gemini" => "  Context: 1M+ tokens\n  Features: Multimodal, code execution\n  Cost: Free tier available",
            _ => ""
        };
    }
    
    public LlmClient? GetCurrentClient() => _currentClient;
    public string GetCurrentProvider() => _currentProvider;
    
    public async Task<ModelListItem> CreateModelListItemAsync(CancellationToken cancellationToken = default)
    {
        var item = new ModelListItem("Available Models:");
        
        // Get all available providers
        var providerNames = new[] { "cerebras", "openai", "anthropic", "gemini", "ollama" };
        string currentModel = GetDefaultModel(_currentProvider);
        
        // Create a service provider with factory
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        
        // Configure providers with API keys
        services.AddLlmServices(options =>
        {
            options.DefaultProvider = _currentProvider;
            
            // Configure each provider
            options.Providers["cerebras"] = new ProviderConfig
            {
                ApiKey = Environment.GetEnvironmentVariable("CEREBRAS_API_KEY"),
                Model = "llama-3.3-70b",
                Enabled = HasApiKey("cerebras")
            };
            
            options.Providers["openai"] = new ProviderConfig
            {
                ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
                Model = "gpt-4o",
                Enabled = HasApiKey("openai")
            };
            
            options.Providers["anthropic"] = new ProviderConfig
            {
                ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
                Model = "claude-3-opus",
                Enabled = HasApiKey("anthropic")
            };
            
            options.Providers["gemini"] = new ProviderConfig
            {
                ApiKey = Environment.GetEnvironmentVariable("GOOGLE_API_KEY"),
                Model = "gemini-1.5-pro",
                Enabled = HasApiKey("gemini")
            };
            
            options.Providers["ollama"] = new ProviderConfig
            {
                ApiBase = Environment.GetEnvironmentVariable("OLLAMA_API_BASE") ?? "http://localhost:11434",
                Model = "llama2",
                Enabled = true
            };
        });
        
        var serviceProvider = services.BuildServiceProvider();
        var factory = serviceProvider.GetService<ILlmProviderFactory>();
        
        if (factory == null)
        {
            item.AddApiKeyStatus("Error: Could not create provider factory");
            return item;
        }
        
        foreach (var providerName in providerNames.OrderBy(p => p))
        {
            var hasApiKey = HasApiKey(providerName);
            
            // Skip providers without API keys (except ollama which doesn't need one)
            if (!hasApiKey && providerName != "ollama") 
            {
                continue;
            }
            
            item.AddProvider(char.ToUpper(providerName[0]) + providerName.Substring(1));
            
            try
            {
                var provider = factory.CreateProvider(providerName);
                
                // Check if provider is available
                if (!await provider.IsAvailableAsync(cancellationToken))
                {
                    item.AddModel("(unavailable)", "Provider is not available", false, false);
                    continue;
                }
                
                var models = await provider.ListModelsAsync(cancellationToken);
                
                if (models != null && models.Any())
                {
                    foreach (var model in models)
                    {
                        var isCurrent = _currentProvider == providerName && model.Id == currentModel;
                        
                        // Build description from model characteristics
                        var descParts = new List<string>();
                        
                        if (!string.IsNullOrEmpty(model.Description))
                        {
                            descParts.Add(model.Description);
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(model.Family))
                                descParts.Add(model.Family);
                            if (!string.IsNullOrEmpty(model.ParameterSize))
                                descParts.Add(model.ParameterSize);
                        }
                        
                        // Add capabilities
                        var capabilities = new List<string>();
                        if (model.SupportsFunctions == true)
                            capabilities.Add("Functions");
                        if (model.SupportsVision == true)
                            capabilities.Add("Vision");
                        if (model.MaxTokens > 0)
                            capabilities.Add($"{model.MaxTokens:N0} tokens");
                        
                        if (capabilities.Any())
                            descParts.Add($"[{string.Join(", ", capabilities)}]");
                        
                        var description = string.Join(" - ", descParts);
                        if (string.IsNullOrWhiteSpace(description))
                            description = model.Name ?? model.Id;
                        
                        item.AddModel(model.Id, description, true, isCurrent);
                    }
                }
                else
                {
                    // No models returned from API
                    item.AddModel("(no models available)", "Unable to list models from API", false, false);
                }
            }
            catch (Exception ex)
            {
                // Show error for this provider
                item.AddModel("(error)", $"Failed: {ex.Message}", false, false);
            }
        }
        
        // Add current model info
        if (_currentClient != null)
        {
            item.AddApiKeyStatus($"Current: {_currentProvider} - {currentModel}");
        }
        
        // Add API key status
        var apiKeyStatus = new List<string>();
        if (HasApiKey("cerebras")) apiKeyStatus.Add("CEREBRAS_API_KEY [SET]");
        if (HasApiKey("openai")) apiKeyStatus.Add("OPENAI_API_KEY [SET]");
        if (HasApiKey("anthropic")) apiKeyStatus.Add("ANTHROPIC_API_KEY [SET]");
        if (HasApiKey("gemini")) apiKeyStatus.Add("GOOGLE_API_KEY [SET]");
        
        if (apiKeyStatus.Any())
        {
            item.AddApiKeyStatus($"API Keys Set: {string.Join(", ", apiKeyStatus)}");
        }
        else
        {
            item.AddApiKeyStatus("Warning: No API keys found. Set environment variables:");
            item.AddApiKeyStatus("   CEREBRAS_API_KEY, OPENAI_API_KEY, ANTHROPIC_API_KEY, or GOOGLE_API_KEY");
        }
        
        return item;
    }
    
    public IServiceProvider? GetCurrentServiceProvider() => _currentServiceProvider;
}