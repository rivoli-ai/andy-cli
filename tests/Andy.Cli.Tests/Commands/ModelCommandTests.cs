using System;
using System.Linq;
using System.Threading.Tasks;
using Andy.Cli.Commands;
using Andy.Llm;
using Andy.Llm.Configuration;
using Andy.Llm.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Andy.Cli.Tests.Commands;

public class ModelCommandTests
{
    private readonly IServiceProvider _serviceProvider;

    public ModelCommandTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Configure LLM services with environment variables
        services.ConfigureLlmFromEnvironment();
        services.Configure<LlmOptions>(options =>
        {
            options.DefaultProvider = "cerebras";
        });
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public void Constructor_SetsDefaultProvider()
    {
        // Arrange & Act
        var command = new ModelCommand(_serviceProvider);

        // Assert
        Assert.Equal("cerebras", command.GetCurrentProvider());
    }

    [Theory]
    [InlineData("openai gpt-4o", "openai", "gpt-4o")] // Explicit provider and model
    [InlineData("openai gpt-4o-mini", "openai", "gpt-4o-mini")] // Explicit provider and model
    // Skip anthropic test since provider is not yet supported
    // [InlineData("anthropic claude-3-sonnet-20240229", "anthropic", "claude-3-sonnet-20240229")] // Switch to anthropic
    [InlineData("llama-3.3-70b", "cerebras", "llama-3.3-70b")] // Auto-detect Cerebras model
    public async Task SwitchModelAsync_ParsesArgumentsCorrectly(string args, string expectedProvider, string expectedModel)
    {
        // Arrange
        var command = new ModelCommand(_serviceProvider);
        var arguments = args.Split(' ');
        
        // Set up environment variables for API keys to avoid validation errors
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "test-key");
        Environment.SetEnvironmentVariable("CEREBRAS_API_KEY", "test-key");

        try
        {
            // Act
            var result = await command.ExecuteAsync(new[] { "switch" }.Concat(arguments).ToArray());

            // Assert
            Assert.True(result.Success, $"Command failed: {result.Message}");
            Assert.Equal(expectedProvider, command.GetCurrentProvider());
            Assert.Equal(expectedModel, command.GetCurrentModel());
            Assert.Contains("Success:", result.Message ?? "");
        }
        finally
        {
            // Clean up environment variables
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Environment.SetEnvironmentVariable("CEREBRAS_API_KEY", null);
        }
    }

    [Fact]
    public async Task SwitchModelAsync_RequiresApiKey()
    {
        // Arrange
        var command = new ModelCommand(_serviceProvider);
        
        // Ensure no API key is set
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);

        // Act
        var result = await command.ExecuteAsync(new[] { "switch", "openai", "gpt-4" });

        // Assert
        Assert.False(result.Success);
        Assert.Contains("No API key found", result.Message ?? "");
        Assert.Contains("OPENAI_API_KEY", result.Message ?? "");
    }

    [Fact]
    public async Task SwitchModelAsync_HandlesInvalidArguments()
    {
        // Arrange
        var command = new ModelCommand(_serviceProvider);

        // Act
        var result = await command.ExecuteAsync(new[] { "switch" });

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Usage:", result.Message ?? "");
    }

    [Theory]
    [InlineData("cerebras")]
    [InlineData("openai")]
    // Skip providers that are not yet supported in the current LlmProviderFactory
    // [InlineData("anthropic")]
    // [InlineData("gemini")]
    [InlineData("ollama")]
    public async Task SwitchProviderAsync_SwitchesToKnownProviders(string providerName)
    {
        // Arrange
        var command = new ModelCommand(_serviceProvider);
        
        // Set up API keys (except for ollama which doesn't need one)
        if (providerName != "ollama")
        {
            Environment.SetEnvironmentVariable($"{providerName.ToUpper()}_API_KEY", "test-key");
            if (providerName == "gemini")
            {
                Environment.SetEnvironmentVariable("GOOGLE_API_KEY", "test-key");
            }
        }

        try
        {
            // Act
            var result = await command.ExecuteAsync(new[] { "provider", providerName });

            // Assert
            Assert.True(result.Success, $"Failed to switch to {providerName}: {result.Message}");
            Assert.Equal(providerName, command.GetCurrentProvider());
            Assert.Contains("Success:", result.Message ?? "");
        }
        finally
        {
            // Clean up
            if (providerName != "ollama")
            {
                Environment.SetEnvironmentVariable($"{providerName.ToUpper()}_API_KEY", null);
                if (providerName == "gemini")
                {
                    Environment.SetEnvironmentVariable("GOOGLE_API_KEY", null);
                }
            }
        }
    }

    [Fact]
    public async Task SwitchProviderAsync_RejectsUnknownProvider()
    {
        // Arrange
        var command = new ModelCommand(_serviceProvider);

        // Act
        var result = await command.ExecuteAsync(new[] { "provider", "unknown-provider" });

        // Assert
        Assert.False(result.Success);
        Assert.Contains("not a recognized provider", result.Message ?? "");
    }

    [Fact]
    public async Task ListModelsAsync_ShowsCurrentProvider()
    {
        // Arrange
        var command = new ModelCommand(_serviceProvider);

        // Act
        var result = await command.ExecuteAsync(new[] { "list" });

        // Assert
        Assert.True(result.Success);
        Assert.Contains("CEREBRAS", result.Message ?? "");
        Assert.Contains("(current)", result.Message ?? "");
    }

    [Fact]
    public async Task ShowModelInfoAsync_DisplaysCurrentInfo()
    {
        // Arrange
        var command = new ModelCommand(_serviceProvider);

        // Act
        var result = await command.ExecuteAsync(new[] { "info" });

        // Assert
        Assert.True(result.Success);
        Assert.Contains("Provider: cerebras", result.Message ?? "");
        Assert.Contains("Model:", result.Message ?? "");
    }

    [Theory]
    [InlineData("refresh")]
    [InlineData("current")]
    public async Task ExecuteAsync_HandlesAllSubcommands(string subcommand)
    {
        // Arrange
        var command = new ModelCommand(_serviceProvider);

        // Act
        var result = await command.ExecuteAsync(new[] { subcommand });

        // Assert
        Assert.True(result.Success, $"Subcommand '{subcommand}' failed: {result.Message}");
    }

    [Fact]
    public async Task ExecuteAsync_RejectsUnknownSubcommand()
    {
        // Arrange
        var command = new ModelCommand(_serviceProvider);

        // Act
        var result = await command.ExecuteAsync(new[] { "unknown-subcommand" });

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Unknown subcommand", result.Message ?? "");
    }
}