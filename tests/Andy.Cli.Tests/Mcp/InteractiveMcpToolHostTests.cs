using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Andy.Cli.Commands;
using Andy.Cli.Headless.Tools;
using Andy.MCP.Client;
using Andy.Cli.Mcp;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;
using Andy.Tools.Core;
using Moq;

namespace Andy.Cli.Tests.Mcp;

public sealed class InteractiveMcpToolHostTests
{
    [Theory]
    [InlineData("filesystem", "read-file", "mcp_filesystem_read_file")]
    [InlineData("My Server", "Run Tool", "mcp_my_server_run_tool")]
    public void BuildToolId_ProducesProviderSafeNames(
        string server,
        string tool,
        string expected)
    {
        Assert.Equal(expected, InteractiveMcpToolHost.BuildToolId(server, tool));
    }

    [Fact]
    public void BuildArguments_QuotesWhitespaceAndEmbeddedQuotes()
    {
        var result = InteractiveMcpToolHost.BuildArguments(
            new[] { "--flag", "two words", "say \"hello\"" });

        Assert.Equal("--flag \"two words\" \"say \\\"hello\\\"\"", result);
    }

    [Fact]
    public async Task DisabledServer_IsReportedWithoutOpeningAConnection()
    {
        var configuration = new McpConfigurationLoadResult(
            new[]
            {
                new McpServerConfiguration
                {
                    Name = "disabled-local",
                    Transport = "stdio",
                    Command = "not-started",
                    Enabled = false,
                },
            },
            Array.Empty<string>(),
            new[] { "test" });
        var registry = new Mock<IToolRegistry>(MockBehavior.Strict);

        await using var host = await InteractiveMcpToolHost.BuildAsync(
            configuration,
            registry.Object);

        var status = Assert.Single(host.Statuses);
        Assert.Equal(McpServerConnectionState.Disabled, status.State);
        Assert.Empty(status.ToolIds);

        var command = new McpCommand(host);
        var result = await command.ExecuteAsync(new[] { "status" });
        Assert.True(result.Success);
        Assert.Contains("[disabled] disabled-local (stdio)", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectedServer_RegistersDiscoveredToolsWithSourceMetadata(bool structured)
    {
        var configuration = new McpConfigurationLoadResult(
            new[]
            {
                new McpServerConfiguration
                {
                    Name = "local-files",
                    Transport = "stdio",
                    Command = "fake",
                },
            },
            Array.Empty<string>(),
            new[] { "test" });
        var registered = new List<ToolMetadata>();
        Func<IServiceProvider, ITool>? toolFactory = null;
        var registry = new Mock<IToolRegistry>();
        registry
            .Setup(value => value.GetTool(It.IsAny<string>()))
            .Returns((ToolRegistration?)null);
        registry
            .Setup(value => value.RegisterTool(
                It.IsAny<ToolMetadata>(),
                It.IsAny<Func<IServiceProvider, ITool>>(),
                It.IsAny<Dictionary<string, object?>>()))
            .Callback<ToolMetadata, Func<IServiceProvider, ITool>, Dictionary<string, object?>>(
                (metadata, factory, _) =>
                {
                    registered.Add(metadata);
                    toolFactory = factory;
                });
        var transport = new FakeMcpTransport { Structured = structured };

        await using var host = await InteractiveMcpToolHost.BuildWithTransportFactoryAsync(
            configuration,
            registry.Object,
            _ => transport);

        var status = Assert.Single(host.Statuses);
        Assert.Equal(McpServerConnectionState.Connected, status.State);
        Assert.Equal(new[] { "mcp_local_files_read_note" }, status.ToolIds);

        var metadata = Assert.Single(registered);
        Assert.Equal("mcp_local_files_read_note", metadata.Id);
        Assert.Contains("[MCP: local-files]", metadata.Description, StringComparison.Ordinal);
        var path = Assert.Single(metadata.Parameters);
        Assert.Equal("path", path.Name);
        Assert.Equal("string", path.Type);
        Assert.True(path.Required);

        var tool = Assert.IsAssignableFrom<ITool>(toolFactory!(Mock.Of<IServiceProvider>()));
        var execution = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["path"] = "notes/today.md" },
            new ToolExecutionContext());

        Assert.True(execution.IsSuccessful, execution.ErrorMessage);
        Assert.Equal("read-note", transport.LastToolCall?.Name);
        Assert.Equal(
            "notes/today.md",
            transport.LastToolCall?.Arguments?.GetProperty("path").GetString());
        if (structured)
        {
            Assert.Equal("note contents", Assert.IsType<JsonElement>(execution.Data).GetProperty("note").GetString());
        }
        else
        {
            Assert.Equal("note contents", execution.Data);
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        transport.LastToolCall = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(
            new Dictionary<string, object?> { ["path"] = "notes/today.md" },
            new ToolExecutionContext { CancellationToken = cancellation.Token }));
        Assert.Null(transport.LastToolCall);
    }

    [Fact]
    public async Task RunningCall_CancellationPropagatesToMcpPeer()
    {
        var transport = new FakeMcpTransport { DeferToolResult = true };
        await using var client = await McpClient.ConnectAsync(transport);
        var remote = Assert.Single(await client.ListToolsAsync());
        var tool = new McpRemoteTool("mcp_test_read_note", "test", client, remote);
        using var cancellation = new CancellationTokenSource();
        var execution = tool.ExecuteAsync(new() { ["path"] = "note.md" },
            new ToolExecutionContext { CancellationToken = cancellation.Token });
        await transport.ToolCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));
        await transport.CallCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class FakeMcpTransport : IClientTransport
    {
        private readonly Channel<JsonRpcMessage> _messages =
            Channel.CreateUnbounded<JsonRpcMessage>();

        public bool IsConnected { get; private set; }

        public event EventHandler<TransportDisconnectedEventArgs>? Disconnected;

        public IAsyncEnumerable<JsonRpcMessage> Messages => ReadMessagesAsync();

        public CallToolRequest? LastToolCall { get; set; }

        public bool Structured { get; init; }

        public bool DeferToolResult { get; init; }
        public TaskCompletionSource ToolCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CallCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task SendAsync(
            JsonRpcMessage message,
            CancellationToken cancellationToken = default)
        {
            if (message is JsonRpcNotification { Method: "notifications/cancelled" })
            {
                CallCancelled.TrySetResult();
            }
            if (message is not JsonRpcRequest request)
            {
                return Task.CompletedTask;
            }

            if (request.Method == "tools/call" && DeferToolResult)
            {
                ToolCalled.TrySetResult();
                return Task.CompletedTask;
            }

            var result = request.Method switch
            {
                "initialize" => JsonSerializer.SerializeToElement(new
                {
                    protocolVersion = request.GetParams<InitializeParams>()!.ProtocolVersion,
                    capabilities = new
                    {
                        tools = new { listChanged = false },
                    },
                    serverInfo = new
                    {
                        name = "fake-mcp",
                        version = "1.0.0",
                    },
                }),
                "tools/list" => JsonSerializer.SerializeToElement(new
                {
                    tools = new[]
                    {
                        new
                        {
                            name = "read-note",
                            description = "Reads a note.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    path = new
                                    {
                                        type = "string",
                                        description = "Note path.",
                                    },
                                },
                                required = new[] { "path" },
                            },
                        },
                    },
                }),
                "tools/call" => BuildToolResult(request),
                _ => JsonSerializer.SerializeToElement(new { }),
            };

            _messages.Writer.TryWrite(JsonRpcResponse.Success(request.Id, result));
            return Task.CompletedTask;
        }

        private JsonElement BuildToolResult(JsonRpcRequest request)
        {
            LastToolCall = request.GetParams<CallToolRequest>();
            return JsonSerializer.SerializeToElement(new
            {
                structuredContent = Structured ? new { note = "note contents" } : null,
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = "note contents",
                    },
                },
                isError = false,
            }, McpJsonDefaults.Options);
        }

        private async IAsyncEnumerable<JsonRpcMessage> ReadMessagesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var message in _messages.Reader.ReadAllAsync(cancellationToken))
            {
                yield return message;
            }
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            _messages.Writer.TryComplete();
            Disconnected?.Invoke(
                this,
                new TransportDisconnectedEventArgs { Reason = "disposed" });
            return ValueTask.CompletedTask;
        }
    }
}
