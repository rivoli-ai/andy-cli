using Andy.Cli.Services;
using Andy.Cli.Widgets;
using Andy.Llm.Errors;
using Andy.Model.Llm;
using Andy.Model.Model;
using Andy.Tools.Core;
using Moq;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Tests.Services;

public class ProviderErrorTests
{
    [Fact]
    public async Task RealEngineRetainsProviderFieldsAndRendersPlainRedError()
    {
        var provider = new Mock<ILlmProvider>();
        provider.SetupGet(p => p.Name).Returns("openrouter");
        var error = new LlmProviderError("Moonshot AI", 429, 1, "Model is temporarily rate-limited.");
        provider.Setup(p => p.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LlmProviderException(error));
        var registry = new Mock<IToolRegistry>();
        registry.SetupGet(r => r.Tools).Returns(Array.Empty<ToolRegistration>());
        registry.Setup(r => r.GetTools()).Returns(Array.Empty<ToolRegistration>());
        var feed = new FeedView();
        using var service = new SimpleAssistantService(provider.Object, registry.Object, new Mock<IToolExecutor>().Object,
            feed, "model", "openrouter");
        var response = await service.ProcessMessageAsync("Answer this question");
        Assert.Equal(error, service.LastProviderError);
        Assert.Contains("HTTP 429", response);
        Assert.Contains("Retry after: 1 second", response);
        var builder = new DL.DisplayListBuilder();
        feed.Render(new Andy.Tui.Layout.Rect(0, 0, 100, 30), new DL.DisplayListBuilder().Build(), builder);
        var runs = builder.Build().Ops.OfType<DL.TextRun>().Where(r => r.Content.Contains("HTTP 429") || r.Content.Contains("rate-limited")).ToArray();
        Assert.NotEmpty(runs);
        Assert.All(runs, r => Assert.Equal(Andy.Cli.Themes.Theme.Current.Error, r.Fg));
        Assert.DoesNotContain(runs, r => r.Content.Contains('\u001b'));

        provider.Setup(p => p.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { AssistantMessage = new Message { Role = Role.Assistant, Content = "Recovered" } });
        await service.ProcessMessageAsync("Try once more");
        Assert.Null(service.LastProviderError);
    }

    [Fact]
    public void UnknownStatusAndRetryAreNotInvented()
    {
        var text = ProviderErrorFormatter.Format(new("provider", null, null, "Network unavailable"));
        Assert.DoesNotContain("HTTP", text);
        Assert.DoesNotContain("Retry after", text);
        Assert.Contains("Network unavailable", text);
    }

    [Fact]
    public void ErrorTextRedactsSecretsAndBoundsControlBearingMessages()
    {
        var text = ProviderErrorFormatter.Format(new("provider", 500, null,
            "api_key=sk-aaaaaaaaaaaaaaaa\u001b\r " + new string('x', 3000)));
        Assert.DoesNotContain("sk-aaaaaaaaaaaaaaaa", text);
        Assert.Contains("[REDACTED]", text);
        Assert.DoesNotContain('\u001b', text);
        Assert.DoesNotContain('\r', text);
        Assert.True(text.Length <= 2403);
    }

    [Fact]
    public void ErrorItemWrapsAndUsesTypedColorWithoutInterpretingMarkup()
    {
        var item = new ErrorTextItem("**literal** \u001b[31m " + new string('x', 150));
        var builder = new DL.DisplayListBuilder();
        item.RenderSlice(0, 0, 30, 0, 50, new DL.DisplayListBuilder().Build(), builder);
        var runs = builder.Build().Ops.OfType<DL.TextRun>().ToArray();
        Assert.Equal(item.MeasureLineCount(30), runs.Length);
        Assert.All(runs, r => Assert.Equal(Andy.Cli.Themes.Theme.Current.Error, r.Fg));
        Assert.DoesNotContain(runs, r => r.Content.Contains('\u001b'));
        Assert.Contains(runs, r => r.Content.Contains("**literal**"));
    }
}
