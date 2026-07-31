using System;
using System.IO;
using System.Linq;
using Andy.Cli.Modes;
using Andy.Cli.Widgets;
using Xunit;

namespace Andy.Cli.Tests.Modes;

/// <summary>
/// The connection-time MCP opt-in offer (issue #278 follow-up). The offer must never grant anything
/// on its own, must provide both the server-wide and the per-tool choice, and must not nag.
/// </summary>
public sealed class McpPlanOptInPromptTests : IDisposable
{
    private readonly string _root;
    private readonly string _project;
    private readonly string _user;

    public McpPlanOptInPromptTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "andy-mcp-optin-" + Guid.NewGuid().ToString("N")[..8]);
        _project = Path.Combine(_root, "project");
        _user = Path.Combine(_root, "home");
        Directory.CreateDirectory(_project);
        Directory.CreateDirectory(_user);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Test hygiene only.
        }
    }

    private PlanModeGrantStore Store() => new(_project, _user);

    private static readonly string[] DocsTools = { "mcp_docs_search", "mcp_docs_fetch" };

    [Fact]
    public void AConnectedServerOpensAnOffer()
    {
        var store = Store();
        var prompt = new McpPlanOptInPrompt(store);

        Assert.True(prompt.Enqueue("docs", DocsTools));
        Assert.True(prompt.IsOpen);
        Assert.Equal("docs", prompt.ServerName);
        Assert.Equal(DocsTools, prompt.Tools.ToArray());
    }

    [Fact]
    public void MerelyShowingTheOfferGrantsNothing()
    {
        var store = Store();
        var prompt = new McpPlanOptInPrompt(store);
        prompt.Enqueue("docs", DocsTools);

        Assert.False(store.CurrentPolicy.Evaluate("mcp_docs_search", null).Allowed);
        Assert.True(store.List().IsEmpty);
    }

    [Fact]
    public void SkippingGrantsNothingAndClosesTheOffer()
    {
        var store = Store();
        var prompt = new McpPlanOptInPrompt(store);
        prompt.Enqueue("docs", DocsTools);

        var message = prompt.Skip();

        Assert.False(prompt.IsOpen);
        Assert.Contains("stays denied", message);
        Assert.False(store.CurrentPolicy.Evaluate("mcp_docs_search", null).Allowed);
        Assert.True(store.List().IsEmpty);
    }

    [Fact]
    public void ServerWideChoiceGrantsEveryToolIncludingFutureOnes()
    {
        var store = Store();
        var prompt = new McpPlanOptInPrompt(store);
        prompt.Enqueue("docs", DocsTools);

        var message = prompt.GrantServerWide();

        Assert.False(prompt.IsOpen);
        Assert.Contains("every tool", message);
        Assert.True(store.CurrentPolicy.Evaluate("mcp_docs_search", null).Allowed);
        Assert.True(store.CurrentPolicy.Evaluate("mcp_docs_fetch", null).Allowed);
        Assert.True(store.CurrentPolicy.Evaluate("mcp_docs_added_next_week", null).Allowed);
    }

    [Fact]
    public void PerToolChoiceGrantsOnlyTheTickedTools()
    {
        var store = Store();
        var prompt = new McpPlanOptInPrompt(store);
        prompt.Enqueue("docs", DocsTools);

        prompt.ToggleSelected(); // ticks index 0 (mcp_docs_search)
        var message = prompt.GrantSelectedTools();

        Assert.False(prompt.IsOpen);
        Assert.Contains("mcp_docs_search", message);
        Assert.True(store.CurrentPolicy.Evaluate("mcp_docs_search", null).Allowed);
        Assert.False(store.CurrentPolicy.Evaluate("mcp_docs_fetch", null).Allowed);
        // A per-tool grant must NOT cover tools discovered later.
        Assert.False(store.CurrentPolicy.Evaluate("mcp_docs_added_next_week", null).Allowed);
    }

    [Fact]
    public void ConfirmingWithNothingTickedKeepsTheOfferOpen()
    {
        var store = Store();
        var prompt = new McpPlanOptInPrompt(store);
        prompt.Enqueue("docs", DocsTools);

        var message = prompt.GrantSelectedTools();

        Assert.Empty(message);
        Assert.True(prompt.IsOpen);
        Assert.True(store.List().IsEmpty);
    }

    [Fact]
    public void NavigationAndTogglingTrackTheCursor()
    {
        var store = Store();
        var prompt = new McpPlanOptInPrompt(store);
        prompt.Enqueue("docs", DocsTools);

        prompt.MoveSelection(1);
        Assert.Equal(1, prompt.CursorIndex);
        prompt.ToggleSelected();
        Assert.Equal(new[] { 1 }, prompt.SelectedIndices.ToArray());

        prompt.ToggleSelected();
        Assert.Empty(prompt.SelectedIndices);

        // Clamped at both ends.
        prompt.MoveSelection(10);
        Assert.Equal(1, prompt.CursorIndex);
        prompt.MoveSelection(-10);
        Assert.Equal(0, prompt.CursorIndex);
    }

    [Fact]
    public void OffersAreQueuedOneServerAtATime()
    {
        var store = Store();
        var prompt = new McpPlanOptInPrompt(store);

        prompt.Enqueue("docs", DocsTools);
        prompt.Enqueue("jira", new[] { "mcp_jira_get_issue" });

        Assert.Equal("docs", prompt.ServerName);
        Assert.Equal(1, prompt.PendingCount);

        prompt.Skip();

        Assert.True(prompt.IsOpen);
        Assert.Equal("jira", prompt.ServerName);
        Assert.Equal(0, prompt.PendingCount);

        prompt.Skip();
        Assert.False(prompt.IsOpen);
    }

    [Fact]
    public void ADeclinedServerIsNotOfferedAgainOnTheNextStart()
    {
        var store = Store();
        new McpPlanOptInPrompt(store).Enqueue("docs", DocsTools);
        new McpPlanOptInPrompt(store).Skip(); // no-op; the first prompt owns the offer

        var first = new McpPlanOptInPrompt(store);
        first.Enqueue("docs", DocsTools);
        first.Skip();

        // Next process start: same server, same tools.
        var next = new McpPlanOptInPrompt(Store());
        Assert.False(next.Enqueue("docs", DocsTools));
        Assert.False(next.IsOpen);
    }

    [Fact]
    public void ANewToolOnADeclinedServerIsOfferedAgain()
    {
        var store = Store();
        var first = new McpPlanOptInPrompt(store);
        first.Enqueue("docs", DocsTools);
        first.Skip();

        var next = new McpPlanOptInPrompt(Store());

        Assert.True(next.Enqueue("docs", DocsTools.Append("mcp_docs_brand_new").ToArray()));
        // Only the tool that is still denied and not yet offered needs a decision.
        Assert.Contains("mcp_docs_brand_new", next.Tools);
    }

    [Fact]
    public void AnAlreadyGrantedServerIsNotOffered()
    {
        var store = Store();
        store.GrantServer("docs");

        var prompt = new McpPlanOptInPrompt(store);

        Assert.False(prompt.Enqueue("docs", DocsTools));
        Assert.False(prompt.IsOpen);
    }

    [Fact]
    public void OnlyTheStillDeniedToolsAreOffered()
    {
        var store = Store();
        store.GrantTools(new[] { "mcp_docs_search" });

        var prompt = new McpPlanOptInPrompt(store);
        prompt.Enqueue("docs", DocsTools);

        Assert.Equal(new[] { "mcp_docs_fetch" }, prompt.Tools.ToArray());
    }

    [Fact]
    public void AServerWithNoToolsIsNotOffered()
    {
        var prompt = new McpPlanOptInPrompt(Store());

        Assert.False(prompt.Enqueue("docs", Array.Empty<string>()));
        Assert.False(prompt.IsOpen);
    }
}
