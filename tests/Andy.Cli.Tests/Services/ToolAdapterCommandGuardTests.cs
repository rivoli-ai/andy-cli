using Andy.Cli.Services.Adapters;
using Andy.Tools.Core;
using Moq;

namespace Andy.Cli.Tests.Services;

public class ToolAdapterCommandGuardTests
{
    [Theory]
    [InlineData("1")]
    [InlineData(" 123 ")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task InvalidCommandNeverReachesExecutor(string? command)
    {
        var executor = new Mock<IToolExecutor>(MockBehavior.Strict);
        var adapter = new ToolAdapter("execute_command", new Mock<IToolRegistry>().Object, executor.Object);
        var result = await adapter.ExecuteAsync(new Andy.Model.Model.ToolCall
        {
            Id = "guard",
            Name = "execute_command",
            ArgumentsJson = System.Text.Json.JsonSerializer.Serialize(new { command })
        });
        Assert.True(result.IsError);
        executor.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(": > output.txt")]
    [InlineData("( echo hello )")]
    [InlineData("{ echo hello; }")]
    [InlineData("FOO=bar")]
    [InlineData("FOO=bar dotnet test")]
    [InlineData("dotnet test")]
    [InlineData("./1")]
    [InlineData("printf '%s' 1")]
    public void PreservesShellSyntax(string command) =>
        Assert.Null(ToolAdapter.GetInvalidCommandReason(new Dictionary<string, object?> { ["command"] = command }));

    [Fact]
    public void RejectsMissingOrNonStringCommand()
    {
        Assert.NotNull(ToolAdapter.GetInvalidCommandReason(new Dictionary<string, object?>()));
        Assert.NotNull(ToolAdapter.GetInvalidCommandReason(new Dictionary<string, object?> { ["command"] = 1 }));
    }
}
