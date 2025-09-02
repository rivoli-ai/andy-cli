using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Andy.Llm;
using Andy.Llm.Models;
using Andy.Llm.Extensions;
using Microsoft.Extensions.DependencyInjection;
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
        var result = new StringBuilder();
        result.AppendLine("Available Models:");
        result.AppendLine();
        
        // Define available models based on what each provider typically offers
        // Since Andy.Llm doesn't expose a model listing API, we maintain this list
        var providers = new Dictionary<string, List<(string name, string description, bool available)>>
        {
            ["cerebras"] = new List<(string, string, bool)>
            {
                ("llama-3.3-70b", "Llama 3.3 70B - Latest, fast inference", HasApiKey("cerebras")),
                ("llama-3.1-8b", "Llama 3.1 8B - Lightweight and efficient", HasApiKey("cerebras")),
                ("llama-3.1-70b", "Llama 3.1 70B - Previous generation", HasApiKey("cerebras"))
            },
            ["openai"] = new List<(string, string, bool)>
            {
                ("gpt-4o", "GPT-4 Omni - Most capable model", HasApiKey("openai")),
                ("gpt-4o-mini", "GPT-4 Omni Mini - Faster, more affordable", HasApiKey("openai")),
                ("gpt-3.5-turbo", "GPT-3.5 Turbo - Fast and efficient", HasApiKey("openai"))
            },
            ["anthropic"] = new List<(string, string, bool)>
            {
                ("claude-3-opus", "Claude 3 Opus - Most capable", HasApiKey("anthropic")),
                ("claude-3-sonnet", "Claude 3 Sonnet - Balanced performance", HasApiKey("anthropic")),
                ("claude-3-haiku", "Claude 3 Haiku - Fast and efficient", HasApiKey("anthropic"))
            },
            ["gemini"] = new List<(string, string, bool)>
            {
                ("gemini-2.0-flash-exp", "Gemini 2.0 Flash - Experimental, multimodal", HasApiKey("gemini")),
                ("gemini-1.5-pro", "Gemini 1.5 Pro - Advanced reasoning", HasApiKey("gemini")),
                ("gemini-1.5-flash", "Gemini 1.5 Flash - Fast and efficient", HasApiKey("gemini"))
            }
        };
        
        // Try to get current provider info from the active client
        string currentModel = GetDefaultModel(_currentProvider);
        
        foreach (var provider in providers.OrderBy(p => p.Key))
        {
            result.AppendLine($"{char.ToUpper(provider.Key[0]) + provider.Key.Substring(1)}:");
            
            foreach (var model in provider.Value)
            {
                var isCurrent = _currentProvider == provider.Key && model.name == currentModel;
                var indicator = isCurrent ? "→ " : "  ";
                var status = model.available ? "[OK]" : "[X]";
                var availability = model.available ? "" : " (API key required)";
                
                result.AppendLine($"{indicator}{status} {model.name}{availability}");
                if (!string.IsNullOrEmpty(model.description))
                {
                    result.AppendLine($"     {model.description}");
                }
            }
            result.AppendLine();
        }
        
        // Show current active model
        if (_currentClient != null)
        {
            result.AppendLine($"Current: {_currentProvider} - {currentModel}");
            result.AppendLine();
        }
        
        // Show which API keys are set
        var apiKeyStatus = new List<string>();
        if (HasApiKey("cerebras")) apiKeyStatus.Add("CEREBRAS_API_KEY [SET]");
        if (HasApiKey("openai")) apiKeyStatus.Add("OPENAI_API_KEY [SET]");
        if (HasApiKey("anthropic")) apiKeyStatus.Add("ANTHROPIC_API_KEY [SET]");
        if (HasApiKey("gemini")) apiKeyStatus.Add("GOOGLE_API_KEY [SET]");
        
        if (apiKeyStatus.Any())
        {
            result.AppendLine($"API Keys Set: {string.Join(", ", apiKeyStatus)}");
        }
        else
        {
            result.AppendLine("Warning: No API keys found. Set environment variables:");
            result.AppendLine("   CEREBRAS_API_KEY, OPENAI_API_KEY, ANTHROPIC_API_KEY, or GOOGLE_API_KEY");
        }
        
        return await Task.FromResult(CommandResult.CreateSuccess(result.ToString()));
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
        
        foreach (var providerName in providerNames.OrderBy(p => p))
        {
            var hasApiKey = HasApiKey(providerName);
            
            // Skip providers without API keys (except ollama which doesn't need one)
            if (!hasApiKey && providerName != "ollama") 
            {
                continue;
            }
            
            item.AddProvider(char.ToUpper(providerName[0]) + providerName.Substring(1));
            
            // Try to use dynamic model listing if we're on the current provider
            bool usedDynamicListing = false;
            if (providerName == _currentProvider && _currentServiceProvider != null)
            {
                try
                {
                    var provider = _currentServiceProvider.GetService<Andy.Llm.Abstractions.ILlmProvider>();
                    if (provider != null)
                    {
                        var models = await provider.ListModelsAsync(cancellationToken);
                        
                        if (models != null && models.Any())
                        {
                            foreach (var model in models)
                            {
                                var isCurrent = model.Id == currentModel;
                                var description = !string.IsNullOrEmpty(model.Description) ? model.Description : 
                                                 (!string.IsNullOrEmpty(model.ParameterSize) ? $"{model.Family} {model.ParameterSize}" : model.Name);
                                
                                item.AddModel(model.Id, description, true, isCurrent);
                            }
                            usedDynamicListing = true;
                        }
                    }
                }
                catch
                {
                    // Fall through to use fallback models
                }
            }
            
            // Use fallback models if dynamic listing wasn't used or failed
            if (!usedDynamicListing)
            {
                AddFallbackModels(item, providerName, currentModel);
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
    
    private void AddFallbackModels(ModelListItem item, string providerName, string currentModel)
    {
        var fallbackModels = providerName switch
        {
            "cerebras" => new[] 
            { 
                ("llama-3.3-70b", "Llama 3.3 70B - Latest, fast inference"),
                ("llama-3.1-8b", "Llama 3.1 8B - Lightweight and efficient"),
                ("llama-3.1-70b", "Llama 3.1 70B - Previous generation")
            },
            "openai" => new[]
            {
                ("gpt-4o", "GPT-4 Omni - Most capable model"),
                ("gpt-4o-mini", "GPT-4 Omni Mini - Faster, more affordable"),
                ("gpt-3.5-turbo", "GPT-3.5 Turbo - Fast and efficient")
            },
            "anthropic" => new[]
            {
                ("claude-3-opus", "Claude 3 Opus - Most capable"),
                ("claude-3-sonnet", "Claude 3 Sonnet - Balanced performance"),
                ("claude-3-haiku", "Claude 3 Haiku - Fast and efficient")
            },
            "gemini" => new[]
            {
                ("gemini-1.5-pro", "Gemini 1.5 Pro - Advanced reasoning"),
                ("gemini-1.5-flash", "Gemini 1.5 Flash - Fast multimodal")
            },
            _ => Array.Empty<(string, string)>()
        };
        
        foreach (var (modelId, description) in fallbackModels)
        {
            var isCurrent = _currentProvider == providerName && modelId == currentModel;
            item.AddModel(modelId, description, true, isCurrent);
        }
    }
    public IServiceProvider? GetCurrentServiceProvider() => _currentServiceProvider;
}