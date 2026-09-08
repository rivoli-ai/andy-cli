using System.Text.Json;
using Andy.Cli.Headless;
using Andy.Cli.HeadlessConfig;
using Andy.Model.Llm;
using Andy.Model.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Andy.Cli.Tests.Headless;

public class BuildVerificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "build-verify-" + Guid.NewGuid().ToString("N"));
    public BuildVerificationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task RealCompileErrorsFeedSameRunCorrectionBeforeSuccess()
    {
        File.WriteAllText(Path.Combine(_root, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var prompts = new List<string>();
        var provider = Provider(call =>
        {
            File.WriteAllText(Path.Combine(_root, "Sample.cs"), call == 1 ? "public class Broken { public void Wrong.Name() {} }" : "public class Fixed { public void Name() {} }");
        }, prompts);
        var output = new StringWriter();
        var config = Config(allowed: true);
        var code = await HeadlessAgentRunner.ExecuteAsync(config, output, new StringWriter(), NullLoggerFactory.Instance, provider);
        Assert.True(code == HeadlessExitCode.Success, output.ToString());
        Assert.Equal(2, prompts.Count);
        Assert.Contains("verification failed", prompts[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CS", prompts[1]);
        var checks = Events(output).Where(e => e.GetProperty("kind").GetString() == "build_verification").ToArray();
        Assert.Equal(2, checks.Length);
        Assert.False(checks[0].GetProperty("data").GetProperty("passed").GetBoolean());
        Assert.True(checks[1].GetProperty("data").GetProperty("passed").GetBoolean());
        Assert.Equal("done", File.ReadAllText(config.Output.File));
    }

    [Theory]
    [InlineData(3, 2, HeadlessExitCode.AgentFailure)]
    [InlineData(1, 1, HeadlessExitCode.Timeout)]
    public async Task FailedVerificationCannotPublishAndRespectsTurnBudget(int maxTurns, int expectedCalls, HeadlessExitCode expected)
    {
        var prompts = new List<string>();
        var config = Config(true) with
        {
            Verification = new() { Commands = ["exit 1"], MaxAttempts = 2 },
            Limits = new() { MaxIterations = maxTurns, TimeoutSeconds = 30 }
        };
        var code = await HeadlessAgentRunner.ExecuteAsync(config, new StringWriter(), new StringWriter(), NullLoggerFactory.Instance, Provider(_ => { }, prompts));
        Assert.Equal(expected, code);
        Assert.Equal(expectedCalls, prompts.Count);
        Assert.False(File.Exists(config.Output.File));
    }

    [Fact]
    public async Task VerificationNeverBypassesExecuteCommandPermission()
    {
        var marker = Path.Combine(_root, "forbidden");
        var config = Config(false) with { Verification = new() { Commands = ["touch '" + marker + "'"], MaxAttempts = 1 } };
        var output = new StringWriter();
        var code = await HeadlessAgentRunner.ExecuteAsync(config, output, new StringWriter(), NullLoggerFactory.Instance, Provider(_ => { }, []));
        Assert.Equal(HeadlessExitCode.AgentFailure, code);
        Assert.False(File.Exists(marker));
        Assert.False(File.Exists(config.Output.File));
        Assert.Contains("denied", output.ToString());
    }

    [Fact]
    public void AutomaticVerificationSkipsUnchangedWorkspaceAndDetectsEdits()
    {
        var path = Path.Combine(_root, "source.cs");
        File.WriteAllText(path, "original");
        var verification = new BuildVerification(_root, null);
        Assert.Empty(verification.Commands());
        File.WriteAllText(path, "changed");
        Assert.Equal(new[] { "dotnet build --nologo", "dotnet test --nologo" }, verification.Commands());
    }

    [Fact]
    public async Task VerificationRequiresExitEvidenceAndRedactsStructuredDiagnostics()
    {
        var executor = new Mock<Andy.Tools.Core.IToolExecutor>();
        executor.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<Andy.Tools.Core.ToolExecutionContext?>()))
            .ReturnsAsync(new Andy.Tools.Core.ToolExecutionResult
            {
                IsSuccessful = true,
                Data = new Dictionary<string, object?> { ["stdout"] = "error CS0106: api_key=sk-secret123456789" }
            });
        var gate = new BuildVerification(_root, new() { Commands = ["echo api_key=sk-secret123456789"] });
        var result = Assert.Single(await gate.RunAsync(executor.Object, default));
        Assert.False(result.Passed);
        Assert.Null(result.ExitCode);
        Assert.Equal("CS0106", Assert.Single(result.Diagnostics).Code);
        Assert.DoesNotContain("sk-secret123456789", BuildVerification.Feedback([result]));
    }

    private HeadlessRunConfig Config(bool allowed) => new()
    {
        SchemaVersion = 1,
        RunId = Guid.NewGuid(),
        Agent = new() { Slug = "verify", Instructions = "Produce code and report done." },
        Model = new() { Provider = "stub", Id = "stub" },
        Tools = [],
        Workspace = new() { Root = _root },
        Output = new() { File = Path.Combine(_root, "answer.txt"), Stream = "stdout" },
        Permissions = new() { AllowedTools = allowed ? ["execute_command"] : [] },
        Limits = new() { MaxIterations = 4, TimeoutSeconds = 90 }
    };

    private static ILlmProvider Provider(Action<int> act, List<string> prompts)
    {
        var provider = new Mock<ILlmProvider>();
        provider.Setup(p => p.StreamCompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>())).Throws(new NotSupportedException());
        provider.Setup(p => p.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns((LlmRequest request, CancellationToken _) =>
            {
                prompts.Add(request.Messages.Last().Content ?? ""); act(prompts.Count);
                return Task.FromResult(new LlmResponse { AssistantMessage = new Message { Role = Role.Assistant, Content = "done" } });
            });
        return provider.Object;
    }
    private static JsonElement[] Events(StringWriter writer) => writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
