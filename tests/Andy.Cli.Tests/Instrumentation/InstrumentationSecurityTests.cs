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

    [Fact]
    public void Redactor_HidesStateChangeMemoryAndSubgoals()
    {
        var evt = new StateChangeEvent
        {
            ChangeType = "update",
            TurnIndex = 3,
            WorkingMemory = { ["objective"] = "SECRET-MEMORY-VALUE" },
            Subgoals = { "SECRET-SUBGOAL" }
        };

        var redacted = (StateChangeEvent)InstrumentationRedactor.ForOutput(evt, includeSensitive: false);

        // Structural metadata preserved.
        Assert.Equal("update", redacted.ChangeType);
        Assert.Equal(3, redacted.TurnIndex);
        // Key preserved, value masked.
        Assert.True(redacted.WorkingMemory.ContainsKey("objective"));
        Assert.Equal(InstrumentationRedactor.Placeholder, redacted.WorkingMemory["objective"]);
        // Subgoal count preserved, contents masked.
        Assert.Single(redacted.Subgoals);
        Assert.Equal(InstrumentationRedactor.Placeholder, redacted.Subgoals[0]);
    }

    [Fact]
    public void Redactor_HidesCritiqueFreeText()
    {
        var evt = new CritiqueEvent
        {
            GoalSatisfied = true,
            Assessment = "SECRET-ASSESSMENT",
            KnownGaps = { "SECRET-GAP" },
            Recommendation = "SECRET-RECOMMENDATION"
        };

        var redacted = (CritiqueEvent)InstrumentationRedactor.ForOutput(evt, includeSensitive: false);

        Assert.True(redacted.GoalSatisfied);
        Assert.Equal(InstrumentationRedactor.Placeholder, redacted.Assessment);
        Assert.Single(redacted.KnownGaps);
        Assert.Equal(InstrumentationRedactor.Placeholder, redacted.KnownGaps[0]);
        Assert.Equal(InstrumentationRedactor.Placeholder, redacted.Recommendation);
    }

    [Fact]
    public void Redactor_HidesDiagnosticMessageAndData()
    {
        var evt = new DiagnosticEvent
        {
            Level = "Warning",
            Source = "Engine",
            Message = "SECRET-DIAGNOSTIC-MESSAGE",
            Data = { ["detail"] = "SECRET-DATA-VALUE" }
        };

        var redacted = (DiagnosticEvent)InstrumentationRedactor.ForOutput(evt, includeSensitive: false);

        // Level and Source identify the component; kept.
        Assert.Equal("Warning", redacted.Level);
        Assert.Equal("Engine", redacted.Source);
        // Free-text message and data masked.
        Assert.Equal(InstrumentationRedactor.Placeholder, redacted.Message);
        Assert.True(redacted.Data.ContainsKey("detail"));
        Assert.Equal(InstrumentationRedactor.Placeholder, redacted.Data["detail"]);
    }

    [Fact]
    public void Redactor_UnknownEventTypeIsRedactedByDefault()
    {
        var evt = new LeakyUnknownEvent();
        var result = InstrumentationRedactor.ForOutput(evt, includeSensitive: false);

        // Fail-safe: an unhandled event type must not pass through verbatim.
        Assert.IsType<RedactedEvent>(result);
        var redacted = (RedactedEvent)result;
        Assert.Equal("LeakyUnknown", redacted.OriginalEventType);

        // The masked event must not carry the sensitive payload anywhere.
        var json = InstrumentationHub.Instance.SerializeEvent(redacted);
        Assert.DoesNotContain("SECRET-UNKNOWN-PAYLOAD", json);
    }

    /// <summary>Stand-in for a future event type the redactor has not been taught about.</summary>
    private sealed class LeakyUnknownEvent : InstrumentationEvent
    {
        public override string EventType => "LeakyUnknown";
        public string Secret { get; set; } = "SECRET-UNKNOWN-PAYLOAD";
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

    // ---- System prompt honors redaction mode -----------------------------

    [Fact]
    public async Task Server_SystemPromptRedactedWhenSensitiveNotOptedIn()
    {
        InstrumentationHub.Instance.SetSystemPrompt("SECRET-SYSTEM-PROMPT-CONTENT");

        using var server = new InstrumentationServer(port: 0, includeSensitive: false);
        Assert.True(server.Start());

        using var client = new HttpClient();
        var body = await client.GetStringAsync(
            $"http://127.0.0.1:{server.BoundPort}/system-prompt?token={server.AuthToken}");

        Assert.DoesNotContain("SECRET-SYSTEM-PROMPT-CONTENT", body);
        Assert.Contains(InstrumentationRedactor.Placeholder, body);

        await server.StopAsync();
        InstrumentationHub.Instance.SetSystemPrompt(string.Empty);
    }

    [Fact]
    public async Task Server_SystemPromptVisibleWhenSensitiveOptedIn()
    {
        InstrumentationHub.Instance.SetSystemPrompt("VISIBLE-SYSTEM-PROMPT-CONTENT");

        using var server = new InstrumentationServer(port: 0, includeSensitive: true);
        Assert.True(server.Start());

        using var client = new HttpClient();
        var body = await client.GetStringAsync(
            $"http://127.0.0.1:{server.BoundPort}/system-prompt?token={server.AuthToken}");

        Assert.Contains("VISIBLE-SYSTEM-PROMPT-CONTENT", body);

        await server.StopAsync();
        InstrumentationHub.Instance.SetSystemPrompt(string.Empty);
    }

    // ---- Advertised host matches the actual loopback bind ----------------

    [Fact]
    public void Server_AdvertisesBoundLoopbackIpNotLocalhost()
    {
        using var server = new InstrumentationServer(port: 0);
        Assert.True(server.Start());

        Assert.NotNull(server.DashboardUrl);
        Assert.Contains("127.0.0.1", server.DashboardUrl!);
        Assert.DoesNotContain("localhost", server.DashboardUrl!);

        server.Dispose();
    }

    // ---- SSE cleanup removes the specific disconnecting writer ------------

    [Fact]
    public async Task Server_SecondSubscriberStillReceivesAfterFirstDisconnects()
    {
        InstrumentationHub.Instance.Clear();

        // includeSensitive: true so Diagnostic messages are streamed verbatim and the
        // marker below is directly observable.
        using var server = new InstrumentationServer(port: 0, includeSensitive: true);
        Assert.True(server.Start());
        var eventsUrl = $"http://127.0.0.1:{server.BoundPort}/events?token={server.AuthToken}";

        var client1 = new HttpClient();
        var client2 = new HttpClient();
        var buffer2 = new System.Text.StringBuilder();
        var reader2Cts = new CancellationTokenSource();

        try
        {
            // Open the first stream and keep it live.
            var response1 = await client1.GetAsync(eventsUrl, HttpCompletionOption.ResponseHeadersRead);
            var stream1 = await response1.Content.ReadAsStreamAsync();
            var reader1 = new StreamReader(stream1);
            var read1Cts = new CancellationTokenSource();
            var read1 = ReadLinesAsync(reader1, new System.Text.StringBuilder(), read1Cts.Token);

            // Open the second stream and collect everything it receives.
            var response2 = await client2.GetAsync(eventsUrl, HttpCompletionOption.ResponseHeadersRead);
            var stream2 = await response2.Content.ReadAsStreamAsync();
            var reader2 = new StreamReader(stream2);
            var read2 = ReadLinesAsync(reader2, buffer2, reader2Cts.Token);

            // Wait until both streams are registered server-side.
            await WaitUntilAsync(() => server.ActiveStreamCount == 2, TimeSpan.FromSeconds(5));

            // Disconnect the first client and wait for the server to drop exactly its writer.
            read1Cts.Cancel();
            client1.Dispose();
            await WaitUntilAsync(() => server.ActiveStreamCount == 1, TimeSpan.FromSeconds(5));

            // Publish a distinctive event; the surviving client must still receive it.
            InstrumentationHub.Instance.Publish(new DiagnosticEvent
            {
                Level = "Info",
                Source = "Test",
                Message = "MARKER-AFTER-DISCONNECT"
            });

            var received = await WaitUntilAsync(
                () => { lock (buffer2) { return buffer2.ToString().Contains("MARKER-AFTER-DISCONNECT"); } },
                TimeSpan.FromSeconds(5));

            Assert.True(received, "surviving subscriber should still receive events after the other disconnects");
        }
        finally
        {
            reader2Cts.Cancel();
            client2.Dispose();
            await server.StopAsync();
            InstrumentationHub.Instance.Clear();
        }
    }

    private static async Task ReadLinesAsync(StreamReader reader, System.Text.StringBuilder sink, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null)
                {
                    break;
                }
                lock (sink)
                {
                    sink.AppendLine(line);
                }
            }
        }
        catch
        {
            // Stream aborted/cancelled - expected on disconnect.
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(50);
        }
        return condition();
    }
}
