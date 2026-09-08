using System.Text;
using Andy.Cli.Commands;
using Andy.Cli.Services.Prompts;

namespace Andy.Cli.Tests.Services.Prompts;

public class RepositoryInstructionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "instructions-" + Guid.NewGuid().ToString("N"));
    public RepositoryInstructionsTests() => Directory.CreateDirectory(Path.Combine(_root, ".git"));

    [Fact]
    public void LoadsOnlyApplicableScopesInRootToLeafOrder()
    {
        Write(_root, "root rule");
        var child = Path.Combine(_root, "src");
        var nested = Path.Combine(child, "feature");
        Write(child, "child rule");
        Write(nested, "nested rule");
        Write(Path.Combine(_root, "unrelated"), "must not load");
        var set = RepositoryInstructions.Resolve(nested);
        Assert.Equal(_root, set.Root);
        Assert.Equal(new[] { "root rule", "child rule", "nested rule" }, set.Sources.Select(s => s.Content));
        var prompt = set.ToPrompt();
        Assert.DoesNotContain("must not load", prompt);
        Assert.True(prompt.IndexOf("root rule", StringComparison.Ordinal) < prompt.IndexOf("child rule", StringComparison.Ordinal));
        Assert.Contains("cannot grant permissions", prompt);
    }

    [Fact]
    public void ParentOutsideBoundaryIsNotLoaded_OutsideTargetRejected()
    {
        var boundary = Path.Combine(_root, "workspace");
        Write(_root, "outside rule");
        Write(boundary, "inside rule");
        var set = RepositoryInstructions.Resolve(boundary, boundary);
        Assert.Equal("inside rule", Assert.Single(set.Sources).Content);
        Assert.Throws<ArgumentException>(() => RepositoryInstructions.Resolve(_root, boundary));
    }

    [Fact]
    public void InvalidUtf8AndOversizedFilesProduceDiagnostics()
    {
        File.WriteAllBytes(Path.Combine(_root, "AGENTS.md"), [0xff, 0xff]);
        var child = Path.Combine(_root, "child");
        Write(child, new string('x', RepositoryInstructions.MaxFileBytes + 1));
        var set = RepositoryInstructions.Resolve(child);
        Assert.Empty(set.ToPrompt());
        Assert.Contains("DecoderFallbackException", set.Diagnostics());
        Assert.Contains("size limit", set.Diagnostics());
    }

    [Fact]
    public void TotalPromptIncludingAttributionIsBounded()
    {
        var cwd = _root;
        for (var i = 0; i < 8; i++) { Write(cwd, new string('x', 16000)); cwd = Path.Combine(cwd, "nested"); }
        Directory.CreateDirectory(cwd);
        var set = RepositoryInstructions.Resolve(cwd);
        Assert.True(Encoding.UTF8.GetByteCount(set.ToPrompt()) <= RepositoryInstructions.MaxTotalBytes);
        Assert.Contains(set.Sources, s => s.Status.Contains("size limit"));
    }

    [Fact]
    public void SymlinkedInstructionAndDirectoryAreExcluded()
    {
        var outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(outside, "do not include");
        File.CreateSymbolicLink(Path.Combine(_root, "AGENTS.md"), outside);
        var target = Path.Combine(_root, "target");
        Write(target, "also excluded through link");
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, target);
        var set = RepositoryInstructions.Resolve(link, _root);
        Assert.Empty(set.ToPrompt());
        Assert.Contains("symbolic-link file", set.Diagnostics());
        Assert.Contains("symbolic-link directory", set.Diagnostics());
    }

    [Fact]
    public async Task PromptBuildersAndSourceCommandUseTheSameResolver()
    {
        Write(_root, "shared instruction sentinel");
        var expected = RepositoryInstructions.Resolve(_root).ToPrompt();
        Assert.EndsWith(expected, SystemPrompts.GetDefaultCliPrompt(_root));
        Assert.EndsWith(expected, SystemPrompts.GetPromptWithTools([], workingDirectory: _root));
        var command = await new SkillsCommand(workspaceDirectory: _root).ExecuteAsync(["instructions"]);
        Assert.True(command.Success);
        Assert.Contains("loaded:", command.Message);
        Assert.Contains(Path.Combine(_root, "AGENTS.md"), command.Message);
    }

    [Fact]
    public async Task HeadlessAndAcpSendSameApplicableInstructionsAsInteractive()
    {
        Write(_root, "cross-mode sentinel");
        var expected = RepositoryInstructions.Resolve(_root).ToPrompt();
        var requests = new List<Andy.Model.Llm.LlmRequest>();
        var llm = new Moq.Mock<Andy.Model.Llm.ILlmProvider>();
        llm.Setup(p => p.StreamCompleteAsync(Moq.It.IsAny<Andy.Model.Llm.LlmRequest>(), Moq.It.IsAny<CancellationToken>()))
            .Throws(new NotSupportedException());
        llm.Setup(p => p.CompleteAsync(Moq.It.IsAny<Andy.Model.Llm.LlmRequest>(), Moq.It.IsAny<CancellationToken>()))
            .Returns((Andy.Model.Llm.LlmRequest request, CancellationToken _) =>
            {
                requests.Add(request); return Task.FromResult(new Andy.Model.Llm.LlmResponse
                { AssistantMessage = new Andy.Model.Model.Message { Role = Andy.Model.Model.Role.Assistant, Content = "ok" } });
            });
        var config = new Andy.Cli.HeadlessConfig.HeadlessRunConfig
        {
            SchemaVersion = 1,
            RunId = Guid.NewGuid(),
            Agent = new() { Slug = "instructions", Instructions = "Respond." },
            Model = new() { Provider = "stub", Id = "stub" },
            Tools = [],
            Workspace = new() { Root = _root },
            Output = new() { File = Path.Combine(_root, "answer.txt"), Stream = "stdout" },
            Limits = new() { MaxIterations = 2, TimeoutSeconds = 10 }
        };
        var diagnostics = new StringWriter();
        Assert.Equal(Andy.Cli.HeadlessConfig.HeadlessExitCode.Success, await Andy.Cli.Headless.HeadlessAgentRunner.ExecuteAsync(
            config, new StringWriter(), diagnostics, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, llm.Object));
        Assert.Contains("loaded:", diagnostics.ToString());
        var registry = new Moq.Mock<Andy.Tools.Core.IToolRegistry>();
        registry.SetupGet(r => r.Tools).Returns(Array.Empty<Andy.Tools.Core.ToolRegistration>());
        registry.Setup(r => r.GetTools()).Returns(Array.Empty<Andy.Tools.Core.ToolRegistration>());
        using var acp = new Andy.Cli.ACP.AndyAgentProvider(llm.Object, registry.Object, new Moq.Mock<Andy.Tools.Core.IToolExecutor>().Object);
        var session = await acp.CreateSessionAsync(new Andy.Acp.Core.Agent.NewSessionParams { Cwd = _root }, default);
        await acp.ProcessPromptAsync(session.SessionId, new Andy.Acp.Core.Agent.PromptMessage { Text = "go" },
            new Moq.Mock<Andy.Acp.Core.Agent.IResponseStreamer>().Object, default);
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request => Assert.Contains(expected, request.SystemPrompt));
        Assert.EndsWith(expected, SystemPrompts.GetPromptWithTools([], workingDirectory: _root));
    }

    private static void Write(string scope, string text)
    { Directory.CreateDirectory(scope); File.WriteAllText(Path.Combine(scope, "AGENTS.md"), text); }
    public void Dispose() => Directory.Delete(_root, true);
}
