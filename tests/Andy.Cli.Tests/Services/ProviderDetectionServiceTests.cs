using System;
using System.Collections.Generic;
using Xunit;
using Andy.Cli.Services;

namespace Andy.Cli.Tests.Services;

[Collection("EnvironmentVariableTests")] // Put tests in a collection to prevent parallel execution
public class ProviderDetectionServiceTests
{
    [Fact]
    public void DetectDefaultProvider_WithOllamaEnvVar_ReturnsOllama()
    {
        // Arrange
        var originalOllama = Environment.GetEnvironmentVariable("OLLAMA_API_BASE");
        var originalSkipOllama = Environment.GetEnvironmentVariable("ANDY_SKIP_OLLAMA");
        var originalOpenAI = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var originalAnthropic = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        var originalCerebras = Environment.GetEnvironmentVariable("CEREBRAS_API_KEY");
        var originalGemini = Environment.GetEnvironmentVariable("GOOGLE_API_KEY");

        try
        {
            // Clear higher priority providers
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Environment.SetEnvironmentVariable("CEREBRAS_API_KEY", null);
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", null);
            Environment.SetEnvironmentVariable("ANDY_SKIP_OLLAMA", null); // Ensure Ollama detection is enabled

            // Set Ollama
            Environment.SetEnvironmentVariable("OLLAMA_API_BASE", "http://localhost:11434");
            var service = new ProviderDetectionService();

            // Act
            var result = service.DetectDefaultProvider();

            // Assert
            Assert.Equal("ollama", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OLLAMA_API_BASE", originalOllama);
            Environment.SetEnvironmentVariable("ANDY_SKIP_OLLAMA", originalSkipOllama);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", originalOpenAI);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", originalAnthropic);
            Environment.SetEnvironmentVariable("CEREBRAS_API_KEY", originalCerebras);
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", originalGemini);
        }
    }

    [Fact]
    public void DetectDefaultProvider_WithAzureCredentials_ReturnsAzure()
    {
        // Arrange
        var originalKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var originalEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var originalOllama = Environment.GetEnvironmentVariable("OLLAMA_API_BASE");
        var originalSkipOllama = Environment.GetEnvironmentVariable("ANDY_SKIP_OLLAMA");
        var originalOpenAI = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var originalAnthropic = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        var originalCerebras = Environment.GetEnvironmentVariable("CEREBRAS_API_KEY");
        var originalGemini = Environment.GetEnvironmentVariable("GOOGLE_API_KEY");

        try
        {
            // Clear all higher priority providers
            Environment.SetEnvironmentVariable("OLLAMA_API_BASE", null);
            Environment.SetEnvironmentVariable("ANDY_SKIP_OLLAMA", "1"); // Skip Ollama detection
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Environment.SetEnvironmentVariable("CEREBRAS_API_KEY", null);
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", null);

            // Set Azure
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", "test-key");
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://test.openai.azure.com");
            var service = new ProviderDetectionService();

            // Act
            var result = service.DetectDefaultProvider();

            // Assert
            Assert.Equal("azure", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", originalKey);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", originalEndpoint);
            Environment.SetEnvironmentVariable("OLLAMA_API_BASE", originalOllama);
            Environment.SetEnvironmentVariable("ANDY_SKIP_OLLAMA", originalSkipOllama);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", originalOpenAI);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", originalAnthropic);
            Environment.SetEnvironmentVariable("CEREBRAS_API_KEY", originalCerebras);
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", originalGemini);
        }
    }

    [Fact]
    public void DetectDefaultProvider_WithOnlyOpenAI_ReturnsOpenAI()
    {
        // Arrange
        var originalOpenAI = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var originalAzureKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var originalAzureEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var originalOllama = Environment.GetEnvironmentVariable("OLLAMA_API_BASE");

        try
        {
            // Clear higher priority providers
            Environment.SetEnvironmentVariable("OLLAMA_API_BASE", null);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", null);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", null);

            // Set OpenAI
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-openai-key");

            var service = new ProviderDetectionService();

            // Act
            var result = service.DetectDefaultProvider();

            // Assert
            Assert.Equal("openai", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", originalOpenAI);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", originalAzureKey);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", originalAzureEndpoint);
            Environment.SetEnvironmentVariable("OLLAMA_API_BASE", originalOllama);
        }
    }

    [Fact]
    public void DetectDefaultProvider_WithOnlyCerebras_ReturnsCerebras()
    {
        // Arrange
        var originalCerebras = Environment.GetEnvironmentVariable("CEREBRAS_API_KEY");
        var originalOpenAI = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var originalAzureKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var originalAzureEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var originalOllama = Environment.GetEnvironmentVariable("OLLAMA_API_BASE");

        try
        {
            // Clear higher priority providers
            Environment.SetEnvironmentVariable("OLLAMA_API_BASE", null);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", null);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", null);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);

            // Set Cerebras
            Environment.SetEnvironmentVariable("CEREBRAS_API_KEY", "test-cerebras-key");

            var service = new ProviderDetectionService();

            // Act
            var result = service.DetectDefaultProvider();

            // Assert
            Assert.Equal("cerebras", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CEREBRAS_API_KEY", originalCerebras);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", originalOpenAI);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", originalAzureKey);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", originalAzureEndpoint);
            Environment.SetEnvironmentVariable("OLLAMA_API_BASE", originalOllama);
        }
    }

    [Fact(Skip = "Test is environment-dependent and may fail if API keys are set in the environment")]
    public void DetectDefaultProvider_WithNoCredentials_ReturnsNull()
    {
        // Arrange
        var originalValues = new Dictionary<string, string?>
        {
            ["OLLAMA_API_BASE"] = Environment.GetEnvironmentVariable("OLLAMA_API_BASE"),
            ["AZURE_OPENAI_API_KEY"] = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"),
            ["AZURE_OPENAI_ENDPOINT"] = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT"),
            ["OPENAI_API_KEY"] = Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            ["CEREBRAS_API_KEY"] = Environment.GetEnvironmentVariable("CEREBRAS_API_KEY"),
            ["ANTHROPIC_API_KEY"] = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
            ["GOOGLE_API_KEY"] = Environment.GetEnvironmentVariable("GOOGLE_API_KEY"),
            ["ANDY_SKIP_OLLAMA"] = Environment.GetEnvironmentVariable("ANDY_SKIP_OLLAMA")
        };

        try
        {
            // Clear all provider environment variables
            foreach (var key in originalValues.Keys)
            {
                Environment.SetEnvironmentVariable(key, null);
            }
            // Also ensure Ollama detection is skipped
            Environment.SetEnvironmentVariable("ANDY_SKIP_OLLAMA", "1");

            var service = new ProviderDetectionService();

            // Act
            var result = service.DetectDefaultProvider();

            // Assert
            Assert.Null(result);
        }
        finally
        {
            // Restore original values
            foreach (var kvp in originalValues)
            {
                Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);
            }
        }
    }

    [Fact]
    public void GetAvailableProviders_WithMultipleProviders_ReturnsInPriorityOrder()
    {
        // Arrange
        var originalValues = new Dictionary<string, string?>
        {
            ["CEREBRAS_API_KEY"] = Environment.GetEnvironmentVariable("CEREBRAS_API_KEY"),
            ["OPENAI_API_KEY"] = Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            ["ANTHROPIC_API_KEY"] = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
        };

        try
        {
            // Set up multiple providers (not including Ollama/Azure which have higher priority)
            Environment.SetEnvironmentVariable("CEREBRAS_API_KEY", "test-cerebras");
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-openai");
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "test-anthropic");

            var service = new ProviderDetectionService();

            // Act
            var result = service.GetAvailableProviders();

            // Assert
            // OpenAI has priority 3, Cerebras has priority 4, Anthropic has priority 5
            Assert.Contains("openai", result);
            Assert.Contains("cerebras", result);
            Assert.Contains("anthropic", result);

            // Check order - OpenAI should come before Cerebras
            var openaiIndex = result.IndexOf("openai");
            var cerebrasIndex = result.IndexOf("cerebras");
            Assert.True(openaiIndex < cerebrasIndex);
        }
        finally
        {
            foreach (var kvp in originalValues)
            {
                Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);
            }
        }
    }

    [Fact]
    public void IsProviderAvailable_WithValidProvider_ReturnsCorrectResult()
    {
        // Arrange
        var originalValue = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
            var service = new ProviderDetectionService();

            // Act & Assert
            Assert.True(service.IsProviderAvailable("openai"));
            Assert.False(service.IsProviderAvailable("unknown-provider"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", originalValue);
        }
    }
}