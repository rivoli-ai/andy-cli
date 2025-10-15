using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Andy.Cli.Commands;
using Andy.Llm;
using Andy.Llm.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Cli.Tests;

[Collection("EnvironmentVariableTests")] // Put tests in a collection to prevent parallel execution
public class ModelCommandProviderSwitchTests
{
    [Fact]
    public async Task Switching_From_Cerebras_To_OpenAI_Works_With_ApiKey()
    {
        // Arrange
        var prevOpenAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var prevCerebrasKey = Environment.GetEnvironmentVariable("CEREBRAS_API_KEY");
        var prevOpenAiModel = Environment.GetEnvironmentVariable("OPENAI_MODEL");
        
        // Temporarily clear any persisted model memory
        var configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".andy");
        var memoryPath = Path.Combine(configDir, "model-memory.json");
        var backupPath = memoryPath + ".backup";
        bool hadMemoryFile = File.Exists(memoryPath);
        
        try
        {
            // Backup and clear model memory
            if (hadMemoryFile)
            {
                File.Move(memoryPath, backupPath, true);
            }
            
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-openai-key");
            Environment.SetEnvironmentVariable("CEREBRAS_API_KEY", "test-cerebras-key");
            // Ensure we test default model fallback for OpenAI
            Environment.SetEnvironmentVariable("OPENAI_MODEL", null);

            var services = new ServiceCollection();
            services.AddLogging();
            services.ConfigureLlmFromEnvironment();
            services.AddLlmServices(options =>
            {
                options.DefaultProvider = "cerebras";
            });

            var serviceProvider = services.BuildServiceProvider();
            var cmd = new ModelCommand(serviceProvider);

            // Sanity: starts on cerebras
            Assert.Equal("cerebras", cmd.GetCurrentProvider());

            // Act
            var result = await cmd.ExecuteAsync(new[] { "provider", "openai" });

            // Assert
            Assert.True(result.Success, result.Message);
            Assert.Contains("Switched to provider: openai", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("openai", cmd.GetCurrentProvider());
            Assert.Equal("gpt-4o", cmd.GetCurrentModel()); // default OpenAI model when none remembered
        }
        finally
        {
            // Restore env
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", prevOpenAiKey);
            Environment.SetEnvironmentVariable("CEREBRAS_API_KEY", prevCerebrasKey);
            Environment.SetEnvironmentVariable("OPENAI_MODEL", prevOpenAiModel);
            
            // Restore model memory
            if (hadMemoryFile && File.Exists(backupPath))
            {
                File.Move(backupPath, memoryPath, true);
            }
        }
    }

    [Fact]
    public async Task Switching_To_OpenAI_Fails_Without_ApiKey()
    {
        // Arrange - Save original values first
        var prevOpenAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var prevCerebrasKey = Environment.GetEnvironmentVariable("CEREBRAS_API_KEY");

        try
        {
            // Aggressively ensure OPENAI_API_KEY is not set
            // Set to empty string instead of null, as HasApiKey checks IsNullOrEmpty
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", string.Empty);
            Environment.SetEnvironmentVariable("CEREBRAS_API_KEY", "test-cerebras-key");

            // Double-check it's really cleared
            var checkKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            Assert.True(string.IsNullOrEmpty(checkKey), $"OPENAI_API_KEY should be empty but was: '{checkKey}'");

            var services = new ServiceCollection();
            services.AddLogging();
            services.ConfigureLlmFromEnvironment();
            services.AddLlmServices(options =>
            {
                options.DefaultProvider = "cerebras";
            });

            var serviceProvider = services.BuildServiceProvider();
            var cmd = new ModelCommand(serviceProvider);

            // Act
            var result = await cmd.ExecuteAsync(new[] { "provider", "openai" });

            // Assert
            Assert.False(result.Success);
            Assert.Contains("No API key", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("cerebras", cmd.GetCurrentProvider()); // still unchanged
        }
        finally
        {
            // Restore env
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", prevOpenAiKey);
            Environment.SetEnvironmentVariable("CEREBRAS_API_KEY", prevCerebrasKey);
        }
    }
}
