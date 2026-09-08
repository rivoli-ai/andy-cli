using Andy.Cli.HeadlessConfig;

namespace Andy.Cli.Tests.HeadlessConfig;

public class McpGatewayValidationTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("file:///tmp/gateway", false)]
    [InlineData("https://example.test/mcp", true)]
    public void GatewayMustResolveToHttp(string? gateway, bool valid)
    {
        var config = new HeadlessRunConfig { McpGateway = gateway,
            Tools = [new HeadlessTool { Name = "tool", Transport = "mcp" }] };
        Assert.Equal(valid, HeadlessConfigValidator.Validate(config) == null);
    }
}
