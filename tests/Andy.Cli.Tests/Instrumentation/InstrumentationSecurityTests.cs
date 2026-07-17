using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Andy.Cli.Instrumentation;
using Xunit;

namespace Andy.Cli.Tests.Instrumentation;

/// <summary>
/// Security tests for the gated, authenticated instrumentation server (issue #166).
/// Covers opt-in gating, credential enforcement, CORS/loopback posture, sensitive
/// field redaction, bounded history, bind-conflict handling and deterministic disposal.
/// </summary>
public class InstrumentationSecurityTests
{
    // ---- Options / gating -------------------------------------------------

    [Fact]
    public void Options_DisabledByDefault_WhenNoFlagOrEnv()
    {
        var previous = Environment.GetEnvironmentVariable("ANDY_INSTRUMENTATION");
        try
        {
            Environment.SetEnvironmentVariable("ANDY_INSTRUMENTATION", null);
            var options = InstrumentationOptions.FromEnvironmentAndArgs(Array.Empty<string>());
            Assert.False(options.Enabled);
            Assert.False(options.IncludeSensitive);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANDY_INSTRUMENTATION", previous);
        }
    }

    [Fact]
    public void Options_EnabledByCliFlag()
    {
        var options = InstrumentationOptions.FromEnvironmentAndArgs(new[] { "--instrumentation" });
        Assert.True(options.Enabled);
    }

    [Fact]
    public void Options_EnabledByEnvironmentVariable()
    {
        var previous = Environment.GetEnvironmentVariable("ANDY_INSTRUMENTATION");
        try
        {
            Environment.SetEnvironmentVariable("ANDY_INSTRUMENTATION", "1");
            var options = InstrumentationOptions.FromEnvironmentAndArgs(Array.Empty<string>());
            Assert.True(options.Enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANDY_INSTRUMENTATION", previous);
        }
    }

    [Fact]
    public void Options_IncludeSensitiveRequiresExplicitOptIn()
    {
        var enabledOnly = InstrumentationOptions.FromEnvironmentAndArgs(new[] { "--instrumentation" });
        Assert.False(enabledOnly.IncludeSensitive);

        var withSensitive = InstrumentationOptions.FromEnvironmentAndArgs(
            new[] { "--instrumentation", "--instrumentation-include-sensitive" });
        Assert.True(withSensitive.IncludeSensitive);
    }

    // ---- Auth helper ------------------------------------------------------

    [Fact]
    public void Auth_GeneratesDistinctUnguessableTokens()
    {
        var a = InstrumentationAuth.GenerateToken();
        var b = InstrumentationAuth.GenerateToken();
        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 32);
        // URL-safe: no characters that would need escaping in a query string.
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
        Assert.DoesNotContain('=', a);
    }

    [Fact]
    public void Auth_RejectsMissingOrInvalidToken()
    {
        var expected = InstrumentationAuth.GenerateToken();
        Assert.False(InstrumentationAuth.IsAuthorized(null, expected));
        Assert.False(InstrumentationAuth.IsAuthorized("", expected));
        Assert.False(InstrumentationAuth.IsAuthorized("wrong", expected));
        Assert.False(InstrumentationAuth.IsAuthorized(expected, null));
    }

    [Fact]
    public void Auth_AcceptsMatchingToken()
    {
        var expected = InstrumentationAuth.GenerateToken();
        Assert.True(InstrumentationAuth.IsAuthorized(expected, expected));
    }

    // ---- Redaction --------------------------------------------------------

    [Fact]
    public void Redactor_HidesUserMessage()
    {
        var evt = new LlmRequestEvent
        {
            Provider = "openai",
            Model = "gpt-4",
            UserMessage = "my secret prompt",
            EstimatedInputTokens = 42
        };

        var redacted = (LlmRequestEvent)InstrumentationRedactor.ForOutput(evt, includeSensitive: false);

        Assert.Equal(InstrumentationRedactor.Placeholder, redacted.UserMessage);
        // Non-sensitive metadata preserved.
        Assert.Equal("openai", redacted.Provider);
        Assert.Equal("gpt-4", redacted.Model);
        Assert.Equal(42, redacted.EstimatedInputTokens);
    }

    [Fact]
    public void Redactor_HidesModelResponse()
    {
        var evt = new LlmResponseEvent { Response = "sensitive answer", ResponseLength = 16 };
        var redacted = (LlmResponseEvent)InstrumentationRedactor.ForOutput(evt, includeSensitive: false);
        Assert.Equal(InstrumentationRedactor.Placeholder, redacted.Response);
        Assert.Equal(16, redacted.ResponseLength);
    }

    [Fact]
    public void Redactor_HidesToolParametersAndResults()
    {
        var call = new ToolCallEvent
        {
            ToolName = "read_file",
            Parameters = { ["path"] = "/etc/passwd" }
        };
        var redactedCall = (ToolCallEvent)InstrumentationRedactor.ForOutput(call, includeSensitive: false);
        Assert.Equal("read_file", redactedCall.ToolName);
        Assert.Equal(InstrumentationRedactor.Placeholder, redactedCall.Parameters["path"]);

        var complete = new ToolCompleteEvent { ToolName = "read_file", Result = "root:x:0:0" };
        var redactedComplete = (ToolCompleteEvent)InstrumentationRedactor.ForOutput(complete, includeSensitive: false);
        Assert.Equal(InstrumentationRedactor.Placeholder, redactedComplete.Result);
        Assert.Null(redactedComplete.ResultData);
    }

    [Fact]
    public void Redactor_IncludeSensitiveReturnsOriginal()
    {
        var evt = new LlmRequestEvent { UserMessage = "keep me" };
        var result = InstrumentationRedactor.ForOutput(evt, includeSensitive: true);
        Assert.Same(evt, result);
    }

    // ---- Bounded history --------------------------------------------------

    [Fact]
    public void Hub_HistoryIsBounded()
    {
        var hub = InstrumentationHub.Instance;
        hub.Clear();
        for (int i = 0; i < InstrumentationHub.MaxEventHistory + 500; i++)
        {
            hub.Publish(new DiagnosticEvent { Message = $"evt {i}" });
        }

        var count = 0;
        foreach (var _ in hub.GetEventHistory())
        {
            count++;
        }

        Assert.True(count <= InstrumentationHub.MaxEventHistory,
            $"history should be capped at {InstrumentationHub.MaxEventHistory} but was {count}");
        hub.Clear();
    }

    [Fact]
    public void Hub_SubscriptionCanBeRemoved()
    {
        var hub = InstrumentationHub.Instance;
        var before = hub.SubscriberCount;
        var sub = hub.Subscribe(_ => Task.CompletedTask);
        Assert.Equal(before + 1, hub.SubscriberCount);
        sub.Dispose();
        Assert.Equal(before, hub.SubscriberCount);
    }

    // ---- End-to-end HTTP (ephemeral port) --------------------------------

    [Fact]
    public async Task Server_RejectsRequestWithoutToken()
    {
        using var server = new InstrumentationServer(port: 0, includeSensitive: false);
        Assert.True(server.Start());

        using var client = new HttpClient();
        var response = await client.GetAsync($"http://localhost:{server.BoundPort}/history");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await server.StopAsync();
    }

    [Fact]
    public async Task Server_RejectsInvalidToken()
    {
        using var server = new InstrumentationServer(port: 0);
        Assert.True(server.Start());

        using var client = new HttpClient();
        var response = await client.GetAsync(
            $"http://localhost:{server.BoundPort}/history?token=not-the-real-token");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await server.StopAsync();
    }

    [Fact]
    public async Task Server_AcceptsValidTokenViaQuery()
    {
        using var server = new InstrumentationServer(port: 0);
        Assert.True(server.Start());

        using var client = new HttpClient();
        var response = await client.GetAsync(
            $"http://localhost:{server.BoundPort}/history?token={server.AuthToken}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await server.StopAsync();
    }

    [Fact]
    public async Task Server_AcceptsValidTokenViaHeader()
    {
        using var server = new InstrumentationServer(port: 0);
        Assert.True(server.Start());

        using var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{server.BoundPort}/history");
        request.Headers.Add(InstrumentationAuth.TokenHeaderName, server.AuthToken);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await server.StopAsync();
    }

    [Fact]
    public async Task Server_DoesNotEmitWildcardCors()
    {
        using var server = new InstrumentationServer(port: 0);
        Assert.True(server.Start());

        using var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"http://localhost:{server.BoundPort}/history?token={server.AuthToken}");
        request.Headers.Add("Origin", "http://evil.example");
        var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"),
            "server must not advertise wildcard/any cross-origin access");

        await server.StopAsync();
    }

    [Fact]
    public async Task Server_RedactsSensitiveFieldsByDefault()
    {
        InstrumentationHub.Instance.Clear();
        InstrumentationHub.Instance.Publish(new LlmRequestEvent
        {
            Provider = "openai",
            Model = "gpt-4",
            UserMessage = "TOP-SECRET-USER-MESSAGE"
        });

        using var server = new InstrumentationServer(port: 0, includeSensitive: false);
        Assert.True(server.Start());

        using var client = new HttpClient();
        var body = await client.GetStringAsync(
            $"http://localhost:{server.BoundPort}/history?token={server.AuthToken}");

        Assert.DoesNotContain("TOP-SECRET-USER-MESSAGE", body);
        Assert.Contains(InstrumentationRedactor.Placeholder, body);

        await server.StopAsync();
        InstrumentationHub.Instance.Clear();
    }

    [Fact]
    public async Task Server_IncludesSensitiveFieldsWhenOptedIn()
    {
        InstrumentationHub.Instance.Clear();
        InstrumentationHub.Instance.Publish(new LlmRequestEvent
        {
            Provider = "openai",
            Model = "gpt-4",
            UserMessage = "VISIBLE-USER-MESSAGE"
        });

        using var server = new InstrumentationServer(port: 0, includeSensitive: true);
        Assert.True(server.Start());

        using var client = new HttpClient();
        var body = await client.GetStringAsync(
            $"http://localhost:{server.BoundPort}/history?token={server.AuthToken}");

        Assert.Contains("VISIBLE-USER-MESSAGE", body);

        await server.StopAsync();
        InstrumentationHub.Instance.Clear();
    }

    [Fact]
    public async Task Server_DashboardEmbedsToken()
    {
        using var server = new InstrumentationServer(port: 0);
        Assert.True(server.Start());

        using var client = new HttpClient();
        var html = await client.GetStringAsync(
            $"http://localhost:{server.BoundPort}/?token={server.AuthToken}");

        Assert.Contains(server.AuthToken, html);
        Assert.DoesNotContain("__ANDY_INSTRUMENTATION_TOKEN__", html);

        await server.StopAsync();
    }

    // ---- Bind-conflict handling ------------------------------------------

    [Fact]
    public async Task Server_SecondBindToSameFixedPortFailsCleanly()
    {
        using var first = new InstrumentationServer(port: 0);
        Assert.True(first.Start());
        var port = first.BoundPort;

        using var second = new InstrumentationServer(port: port);
        var started = second.Start();

        Assert.False(started);
        Assert.False(second.IsRunning);
        // A failed bind must not advertise a dashboard URL.
        Assert.Null(second.DashboardUrl);

        await first.StopAsync();
    }

    // ---- Disposal releases resources -------------------------------------

    [Fact]
    public async Task Server_DisposeReleasesPortSoItCanBeReused()
    {
        int port;
        using (var server = new InstrumentationServer(port: 0))
        {
            Assert.True(server.Start());
            port = server.BoundPort;
        } // Dispose() here should release the port.

        // Re-binding the exact port must now succeed.
        using var reuse = new InstrumentationServer(port: port);
        Assert.True(reuse.Start());
        Assert.True(reuse.IsRunning);
        await reuse.StopAsync();
    }

    [Fact]
    public async Task Server_StopUnsubscribesFromHub()
    {
        var before = InstrumentationHub.Instance.SubscriberCount;

        var server = new InstrumentationServer(port: 0);
        Assert.True(server.Start());
        Assert.Equal(before + 1, InstrumentationHub.Instance.SubscriberCount);

        await server.StopAsync();
        Assert.Equal(before, InstrumentationHub.Instance.SubscriberCount);
    }
}
