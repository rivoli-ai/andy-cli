using Andy.Cli.Modes;
using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Model.Llm;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Andy.Cli.Tests.Modes;

/// <summary>
/// The active mode has to reach the model, both in the system prompt at construction and on every
/// turn afterwards (the engine's system prompt is fixed once the agent exists, so a mid-session
/// <c>/mode</c> switch would otherwise be invisible to it).
/// </summary>
public class AgentModePromptTests
{
    [Fact]
    public void PlanSection_StatesTheConstraintsAndThatTheyAreEnforced()
    {
        var section = AgentModePrompt.SystemPromptSection(AgentModeCatalog.Plan);

        Assert.Contains("Plan mode", section);
        Assert.Contains("enforced by the tool permission layer", section);
        Assert.Contains("MUST NOT run shell commands", section);
        Assert.Contains("MUST NOT write", section);
        Assert.Contains("MCP", section);
    }

    [Fact]
    public void PlanSection_TellsTheModelItCannotSwitchModesItself()
    {
        var section = AgentModePrompt.SystemPromptSection(AgentModeCatalog.Plan);

        Assert.Contains("you cannot switch modes", section);
        Assert.Contains("/mode build", section);
    }

    [Fact]
    public void PlanSection_DoesNotAskForAPlanFile()
    {
        // Out of scope for this slice: Plan mode is strictly non-mutating, so the model must not be
        // encouraged to write a plan document.
        var section = AgentModePrompt.SystemPromptSection(AgentModeCatalog.Plan);

        Assert.Contains("Do not try to create a plan file", section);
    }

    [Fact]
    public void BuildSection_MentionsTheModeButImposesNoConstraints()
    {
        var section = AgentModePrompt.SystemPromptSection(AgentModeCatalog.Build);

        Assert.Contains("Build mode", section);
        Assert.DoesNotContain("MUST NOT", section);
    }

    [Fact]
    public void TurnDirective_IsEmptyForBuild()
    {
        Assert.Null(AgentModePrompt.TurnDirective(AgentModeCatalog.Build));
    }

    [Fact]
    public void TurnDirective_RestatesPlanConstraints()
    {
        var directive = AgentModePrompt.TurnDirective(AgentModeCatalog.Plan);

        Assert.NotNull(directive);
        Assert.Contains("Active mode: Plan", directive!);
        Assert.Contains("MUST NOT run shell commands", directive);
    }

    [Fact]
    public void BuildMode_LeavesTheUserMessageUntouched()
    {
        using var service = CreateService(new AgentModeState(AgentMode.Build));

        Assert.Equal("refactor the parser", service.ComposeAgentMessage("refactor the parser"));
    }

    [Fact]
    public void NoModeState_LeavesTheUserMessageUntouched()
    {
        using var service = CreateService(null);

        Assert.Equal("refactor the parser", service.ComposeAgentMessage("refactor the parser"));
    }

    [Fact]
    public void PlanMode_PrefixesTheUserMessageWithTheConstraints()
    {
        using var service = CreateService(new AgentModeState(AgentMode.Plan));

        var composed = service.ComposeAgentMessage("refactor the parser");

        Assert.Contains("Active mode: Plan", composed);
        Assert.EndsWith("refactor the parser", composed);
    }

    [Fact]
    public void SwitchingModeMidSession_ChangesTheNextTurnsMessage()
    {
        var state = new AgentModeState(AgentMode.Build);
        using var service = CreateService(state);

        Assert.Equal("hello", service.ComposeAgentMessage("hello"));

        state.TrySet(AgentMode.Plan, ModeChangeSource.UserCommand, out _);

        Assert.Contains("Active mode: Plan", service.ComposeAgentMessage("hello"));
    }

    private static SimpleAssistantService CreateService(AgentModeState? modeState)
    {
        var registry = new Mock<IToolRegistry>();
        registry.Setup(x => x.Tools).Returns(new List<ToolRegistration>());
        registry.Setup(x => x.GetTools(
                It.IsAny<ToolCategory?>(), It.IsAny<ToolCapability?>(),
                It.IsAny<IEnumerable<string>?>(), It.IsAny<bool>()))
            .Returns(new List<ToolRegistration>());

        return new SimpleAssistantService(
            new Mock<ILlmProvider>().Object,
            registry.Object,
            new Mock<IToolExecutor>().Object,
            new FeedView(),
            "test-model",
            "test-provider",
            tokenCounter: null,
            loggerFactory: NullLoggerFactory.Instance,
            modeState: modeState);
    }
}
