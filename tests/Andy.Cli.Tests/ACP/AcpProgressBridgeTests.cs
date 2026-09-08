using Andy.Acp.Core.Agent;
using Andy.Cli.ACP;
using Andy.Llm.Configuration;
using Andy.Model.Llm;
using Andy.Model.Model;
using Andy.Tools.Core;
using Moq;

namespace Andy.Cli.Tests.ACP;

public class AcpProgressBridgeTests
{
    [Fact]
    public void ModelConfiguration_UsesConfiguredModel()
    {
        var options = new LlmOptions
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["openrouter"] = new()
                {
                    Provider = "openrouter",
                    Model = "moonshotai/kimi-k3",
                },
            },
        };

        var model = AcpModelConfiguration.ResolveAndApply(options, "openrouter");

        Assert.Equal("moonshotai/kimi-k3", model);
        Assert.Equal("moonshotai/kimi-k3", options.Providers["openrouter"].Model);
    }

    [Fact]
    public void ModelConfiguration_AppliesRegistryFallbackToProvider()
    {
        var options = new LlmOptions
        {
            DefaultModel = null,
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["openrouter"] = new()
                {
                    Provider = "openrouter",
                    Model = null,
                },
            },
        };

        var model = AcpModelConfiguration.ResolveAndApply(options, "openrouter");

        Assert.Equal("moonshotai/kimi-k3", model);
        Assert.Equal(model, options.Providers["openrouter"].Model);
    }

    [Fact]
    public void ModelConfiguration_CreatesFallbackProviderWithoutCredentials()
    {
        var options = new LlmOptions();

        var config = AcpModelConfiguration.EnsureProviderConfig(options, "cerebras");

        Assert.Same(config, options.Providers["cerebras"]);
        Assert.Equal("cerebras", config.Provider);
        Assert.Equal("llama-3.3-70b", config.Model);
        Assert.Equal("https://api.cerebras.ai", config.ApiBase);
    }

    [Fact]
    public void ModelConfiguration_UpdatesExistingProviderModel()
    {
        var existing = new ProviderConfig { Provider = "openai", Model = "old" };
        var options = new LlmOptions
        {
            Providers = new Dictionary<string, ProviderConfig> { ["openai"] = existing }
        };

        var config = AcpModelConfiguration.EnsureProviderConfig(options, "openai", "gpt-4.1");

        Assert.Same(existing, config);
        Assert.Equal("gpt-4.1", existing.Model);
    }

    [Fact]
    public async Task UnavailableProvider_DefersActionableCredentialErrorUntilCompletion()
    {
        var provider = new UnavailableLlmProvider("cerebras");

        Assert.False(await provider.IsAvailableAsync());
        Assert.Empty(await provider.ListModelsAsync());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CompleteAsync(new LlmRequest { Messages = Array.Empty<Andy.Model.Model.Message>() }));
        Assert.Contains("CEREBRAS_API_KEY", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgressProvider_DoesNotDuplicateNarrationAsThinking()
    {
        var inner = new Mock<ILlmProvider>();
        inner.Setup(provider => provider.CompleteAsync(
                It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                AssistantMessage = new Message
                {
                    Role = Role.Assistant,
                    Content = "I will inspect the project first.",
                    ToolCalls =
                    [
                        new Andy.Model.Model.ToolCall
                        {
                            Id = "call-1",
                            Name = "list_directory",
                            ArgumentsJson = "{\"path\":\".\"}",
                        },
                    ],
                },
            });

        var thoughts = new List<string>();
        var streamer = CreateStreamer(thoughts: thoughts);
        var sink = new AcpSessionUpdateSink();
        sink.Attach(streamer.Object);
        var provider = new AcpProgressLlmProvider(inner.Object, sink);

        await provider.CompleteAsync(
            new LlmRequest { Messages = [] }, CancellationToken.None);

        Assert.Empty(thoughts);
    }

    [Fact]
    public async Task ObservingExecutor_SendsRealToolStartAndCompletion()
    {
        var inner = new Mock<IToolExecutor>();
        inner.Setup(executor => executor.ExecuteAsync(
                "read_file",
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<ToolExecutionContext?>()))
            .ReturnsAsync(new ToolExecutionResult
            {
                IsSuccessful = true,
                Data = "file contents",
            });

        var calls = new List<Andy.Acp.Core.Agent.ToolCall>();
        var results = new List<Andy.Acp.Core.Agent.ToolResult>();
        var streamer = CreateStreamer(calls: calls, results: results);
        var sink = new AcpSessionUpdateSink();
        sink.Attach(streamer.Object);
        var executor = new AcpObservingToolExecutor(inner.Object, sink);

        var actual = await executor.ExecuteAsync(
            "read_file",
            new Dictionary<string, object?> { ["path"] = "README.md" },
            new ToolExecutionContext());

        Assert.True(actual.IsSuccessful);
        var call = Assert.Single(calls);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("read", call.Kind);
        Assert.Equal("in_progress", call.Status);
        var result = Assert.Single(results);
        Assert.Equal(call.Id, result.CallId);
        Assert.False(result.IsError);
        Assert.Equal("file contents", result.Content);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task ToolOutcomesHaveCorrelatedTerminalUpdates(bool cancelled, bool limited, bool denied)
    {
        var inner = new Mock<IToolExecutor>();
        inner.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<ToolExecutionContext?>()))
            .ReturnsAsync(new ToolExecutionResult
            {
                IsSuccessful = !denied,
                WasCancelled = cancelled,
                HitResourceLimits = limited,
                ErrorMessage = denied ? "Permission denied" : null
            });
        var calls = new List<Andy.Acp.Core.Agent.ToolCall>();
        var results = new List<Andy.Acp.Core.Agent.ToolResult>();
        var sink = new AcpSessionUpdateSink();
        sink.Attach(CreateStreamer(calls: calls, results: results).Object);
        await new AcpObservingToolExecutor(inner.Object, sink).ExecuteAsync("execute_command", []);
        Assert.Equal(Assert.Single(calls).Id, Assert.Single(results).CallId);
        Assert.Equal(cancelled || limited || denied, results[0].IsError);
        Assert.Equal("content", Assert.Single(results[0].ContentItems!).Type);
    }

    [Fact]
    public async Task ThrownCancellationTerminatesStartedToolWithFreshToken()
    {
        using var cts = new CancellationTokenSource();
        var inner = new Mock<IToolExecutor>();
        inner.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<ToolExecutionContext?>()))
            .Returns(() => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });
        var calls = new List<Andy.Acp.Core.Agent.ToolCall>();
        var results = new List<Andy.Acp.Core.Agent.ToolResult>();
        var sink = new AcpSessionUpdateSink();
        sink.Attach(CreateStreamer(calls: calls, results: results).Object);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AcpObservingToolExecutor(inner.Object, sink)
            .ExecuteAsync("execute_command", [], new ToolExecutionContext { CancellationToken = cts.Token }));
        Assert.Equal(Assert.Single(calls).Id, Assert.Single(results).CallId);
        Assert.True(results[0].IsError);
        Assert.Contains("cancelled", results[0].Content);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public async Task OnlySuccessfulFullWritesProduceNativeReplacementDiffs(bool append, bool success, bool diff)
    {
        var inner = new Mock<IToolExecutor>();
        inner.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<ToolExecutionContext?>()))
            .ReturnsAsync(new ToolExecutionResult { IsSuccessful = success });
        var results = new List<Andy.Acp.Core.Agent.ToolResult>();
        var sink = new AcpSessionUpdateSink();
        sink.Attach(CreateStreamer(results: results).Object);
        var path = Path.Combine(Path.GetTempPath(), "sample.txt");
        await new AcpObservingToolExecutor(inner.Object, sink).ExecuteAsync("write_file",
            new() { ["file_path"] = path, ["content"] = "new contents", ["append"] = append });
        var result = Assert.Single(results);
        Assert.Equal(path, Assert.Single(result.Locations!).Path);
        Assert.Equal(diff, result.ContentItems!.Any(c => c.Type == "diff"));
        if (diff) Assert.Equal("new contents", result.ContentItems!.Single(c => c.Type == "diff").NewText);
    }

    private static Mock<IResponseStreamer> CreateStreamer(
        List<string>? thoughts = null,
        List<Andy.Acp.Core.Agent.ToolCall>? calls = null,
        List<Andy.Acp.Core.Agent.ToolResult>? results = null)
    {
        var streamer = new Mock<IResponseStreamer>();
        streamer.Setup(value => value.SendThinkingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string text, CancellationToken _) =>
            {
                thoughts?.Add(text);
                return Task.CompletedTask;
            });
        streamer.Setup(value => value.SendToolCallAsync(
                It.IsAny<Andy.Acp.Core.Agent.ToolCall>(), It.IsAny<CancellationToken>()))
            .Returns((Andy.Acp.Core.Agent.ToolCall call, CancellationToken _) =>
            {
                calls?.Add(call);
                return Task.CompletedTask;
            });
        streamer.Setup(value => value.SendToolResultAsync(
                It.IsAny<Andy.Acp.Core.Agent.ToolResult>(), It.IsAny<CancellationToken>()))
            .Returns((Andy.Acp.Core.Agent.ToolResult result, CancellationToken _) =>
            {
                results?.Add(result);
                return Task.CompletedTask;
            });
        return streamer;
    }
}
