using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andy.Acp.Core.Agent;
using Andy.Cli.ACP;
using Andy.Engine;
using Andy.Model.Llm;
using Andy.Tools.Core;
using Andy.Tools.Execution;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Language.Flow;
using Xunit;

namespace Andy.Cli.Tests.ACP;

/// <summary>
/// Tests for AndyAgentProvider - the ACP integration for Andy.CLI
/// </summary>
public class AndyAgentProviderTests
{
    private readonly Mock<ILlmProvider> _mockLlmProvider;
    private readonly Mock<IToolRegistry> _mockToolRegistry;
    private readonly Mock<IToolExecutor> _mockToolExecutor;
    private readonly Mock<ILogger<AndyAgentProvider>> _mockLogger;
    private readonly AndyAgentProvider _provider;

    public AndyAgentProviderTests()
    {
        _mockLlmProvider = new Mock<ILlmProvider>();
        _mockToolRegistry = new Mock<IToolRegistry>();
        _mockToolExecutor = new Mock<IToolExecutor>();
        _mockLogger = new Mock<ILogger<AndyAgentProvider>>();

        // Setup tool registry to return empty tools list
        // GetTools has optional parameters, so we need to setup all overloads
        _mockToolRegistry.Setup(x => x.GetTools(
            It.IsAny<ToolCategory?>(),
            It.IsAny<ToolCapability?>(),
            It.IsAny<IEnumerable<string>?>(),
            It.IsAny<bool>()))
            .Returns(new List<ToolRegistration>());

        _provider = new AndyAgentProvider(
            _mockLlmProvider.Object,
            _mockToolRegistry.Object,
            _mockToolExecutor.Object,
            _mockLogger.Object);
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenLlmProviderIsNull()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new AndyAgentProvider(
            null!,
            _mockToolRegistry.Object,
            _mockToolExecutor.Object,
            _mockLogger.Object));
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenToolRegistryIsNull()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new AndyAgentProvider(
            _mockLlmProvider.Object,
            null!,
            _mockToolExecutor.Object,
            _mockLogger.Object));
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenToolExecutorIsNull()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new AndyAgentProvider(
            _mockLlmProvider.Object,
            _mockToolRegistry.Object,
            null!,
            _mockLogger.Object));
    }

    [Fact]
    public void GetCapabilities_ReturnsCorrectCapabilities()
    {
        // Act
        var capabilities = _provider.GetCapabilities();

        // Assert
        Assert.NotNull(capabilities);
        Assert.True(capabilities.LoadSession);
        Assert.False(capabilities.AudioPrompts);
        Assert.False(capabilities.ImagePrompts);
        Assert.True(capabilities.EmbeddedContext);
    }

    [Fact]
    public async Task CreateSessionAsync_CreatesNewSession_WithGeneratedId()
    {
        // Arrange
        var parameters = new NewSessionParams();

        // Act
        var metadata = await _provider.CreateSessionAsync(parameters, CancellationToken.None);

        // Assert
        Assert.NotNull(metadata);
        Assert.NotNull(metadata.SessionId);
        Assert.StartsWith("session-", metadata.SessionId);
        Assert.Equal("assistant", metadata.Mode);
        Assert.Equal("andy-cli", metadata.Model);
        Assert.NotNull(metadata.Metadata);
        Assert.Equal("andy-cli", metadata.Metadata["provider"]);
        Assert.Equal(0, metadata.Metadata["tools_count"]); // Based on mocked tools (empty list)
    }

    [Fact]
    public async Task CreateSessionAsync_AcceptsNullParameters()
    {
        // Act
        var metadata = await _provider.CreateSessionAsync(null, CancellationToken.None);

        // Assert
        Assert.NotNull(metadata);
        Assert.NotNull(metadata.SessionId);
    }

    [Fact]
    public async Task LoadSessionAsync_ReturnsSessionMetadata_ForExistingSession()
    {
        // Arrange
        var createResult = await _provider.CreateSessionAsync(null, CancellationToken.None);
        var sessionId = createResult.SessionId;

        // Act
        var metadata = await _provider.LoadSessionAsync(sessionId, CancellationToken.None);

        // Assert
        Assert.NotNull(metadata);
        Assert.Equal(sessionId, metadata.SessionId);
        Assert.Equal("assistant", metadata.Mode);
        Assert.Equal("andy-cli", metadata.Model);
    }

    [Fact]
    public async Task LoadSessionAsync_ReturnsNull_ForNonExistentSession()
    {
        // Act
        var metadata = await _provider.LoadSessionAsync("non-existent-session", CancellationToken.None);

        // Assert
        Assert.Null(metadata);
    }

    [Fact]
    public async Task ProcessPromptAsync_ReturnsError_WhenSessionNotFound()
    {
        // Arrange
        var prompt = new PromptMessage { Text = "Hello" };
        var mockStreamer = new Mock<IResponseStreamer>();

        // Act
        var response = await _provider.ProcessPromptAsync(
            "non-existent-session",
            prompt,
            mockStreamer.Object,
            CancellationToken.None);

        // Assert
        Assert.NotNull(response);
        Assert.Equal(StopReason.Error, response.StopReason);
        Assert.Contains("Session not found", response.Error);
    }

    [Fact]
    public async Task SetSessionModeAsync_ReturnsFalse_ForNonImplementedFeature()
    {
        // Arrange
        var createResult = await _provider.CreateSessionAsync(null, CancellationToken.None);
        var sessionId = createResult.SessionId;

        // Act
        var result = await _provider.SetSessionModeAsync(sessionId, "code", CancellationToken.None);

        // Assert - Mode switching not implemented yet
        Assert.False(result);
    }

    [Fact]
    public async Task SetSessionModelAsync_ReturnsFalse_ForNonImplementedFeature()
    {
        // Arrange
        var createResult = await _provider.CreateSessionAsync(null, CancellationToken.None);
        var sessionId = createResult.SessionId;

        // Act
        var result = await _provider.SetSessionModelAsync(sessionId, "gpt-4", CancellationToken.None);

        // Assert - Model switching not implemented yet
        Assert.False(result);
    }

    [Fact]
    public async Task CancelSessionAsync_CompletesSuccessfully()
    {
        // Arrange
        var createResult = await _provider.CreateSessionAsync(null, CancellationToken.None);
        var sessionId = createResult.SessionId;

        // Act & Assert - Should not throw
        await _provider.CancelSessionAsync(sessionId, CancellationToken.None);
    }

    [Fact]
    public async Task MultipleSessionsCanBeCreated()
    {
        // Act
        var session1 = await _provider.CreateSessionAsync(null, CancellationToken.None);
        var session2 = await _provider.CreateSessionAsync(null, CancellationToken.None);
        var session3 = await _provider.CreateSessionAsync(null, CancellationToken.None);

        // Assert
        Assert.NotEqual(session1.SessionId, session2.SessionId);
        Assert.NotEqual(session2.SessionId, session3.SessionId);
        Assert.NotEqual(session1.SessionId, session3.SessionId);

        // Verify all can be loaded
        var loaded1 = await _provider.LoadSessionAsync(session1.SessionId, CancellationToken.None);
        var loaded2 = await _provider.LoadSessionAsync(session2.SessionId, CancellationToken.None);
        var loaded3 = await _provider.LoadSessionAsync(session3.SessionId, CancellationToken.None);

        Assert.NotNull(loaded1);
        Assert.NotNull(loaded2);
        Assert.NotNull(loaded3);
    }

    [Fact]
    public async Task SessionId_IsUniqueGuid()
    {
        // Act
        var session = await _provider.CreateSessionAsync(null, CancellationToken.None);

        // Assert
        var guidPart = session.SessionId.Replace("session-", "");
        Assert.True(Guid.TryParse(guidPart, out _), "Session ID should contain a valid GUID");
    }

    [Fact]
    public async Task CreateSessionAsync_TracksCreatedTime()
    {
        // Arrange
        var beforeCreate = DateTime.UtcNow.AddSeconds(-1);

        // Act
        var session = await _provider.CreateSessionAsync(null, CancellationToken.None);

        // Assert
        var afterCreate = DateTime.UtcNow.AddSeconds(1);
        Assert.True(session.CreatedAt >= beforeCreate);
        Assert.True(session.CreatedAt <= afterCreate);
    }
}
