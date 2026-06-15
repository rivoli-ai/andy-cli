using Andy.Cli.Services;
using Xunit;

namespace Andy.Cli.Tests.Services
{
    /// <summary>
    /// Regression tests for the turn-limit response handling.
    ///
    /// Bug: when the agent exhausts its turn budget, Andy.Engine returns the FULL
    /// conversation history (every raw tool-result payload, with embedded CRLFs from
    /// e.g. an HTTP error body) as the response, with StopReason "max_turns_exceeded".
    /// The CLI rendered that verbatim, flooding the feed with raw tool JSON and a wall
    /// of blank lines. SelectResponseContent must replace it with a concise notice.
    /// </summary>
    public class MaxTurnsResponseTests
    {
        // A representative slice of the engine's history dump, including the CRLFs that
        // showed up in the feed (from a GitHub 403 HTML body embedded in a tool result).
        private const string HistoryDump =
            "Conversation history (FULL - no truncation):\r\n" +
            "[3] Role: Tool\r\nContent: {\"success\":true}\r\nToolCalls Count: 1\r\n" +
            "  - ToolResult: http_request (CallId: abc, IsError: True)\r\n\r\n\r\n\r\n";

        [Theory]
        [InlineData("max_turns_exceeded")]
        [InlineData("MAX_TURNS_EXCEEDED")]
        public void IsMaxTurnsExceeded_MatchesCaseInsensitively(string stopReason)
        {
            Assert.True(SimpleAssistantService.IsMaxTurnsExceeded(stopReason));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("completed")]
        [InlineData("stop")]
        public void IsMaxTurnsExceeded_FalseForOthers(string? stopReason)
        {
            Assert.False(SimpleAssistantService.IsMaxTurnsExceeded(stopReason));
        }

        [Fact]
        public void TurnLimit_SuppressesHistoryDump_AndShowsNotice()
        {
            // Even though a (huge, dump-shaped) response is present, the turn-limit path
            // must not render it.
            var content = SimpleAssistantService.SelectResponseContent(
                HistoryDump, success: false, stopReason: "max_turns_exceeded");

            Assert.DoesNotContain("Role:", content);
            Assert.DoesNotContain("ToolCalls Count", content);
            Assert.DoesNotContain("Conversation history", content);
            Assert.DoesNotContain("\r\n", content);
            Assert.Contains("turn", content); // the concise notice mentions the turn limit
        }

        [Fact]
        public void NormalResponse_PassesThrough()
        {
            var content = SimpleAssistantService.SelectResponseContent(
                "Here is the answer.", success: true, stopReason: "completed");
            Assert.Equal("Here is the answer.", content);
        }

        [Fact]
        public void EmptyFailedResponse_ShowsError()
        {
            var content = SimpleAssistantService.SelectResponseContent(
                response: "", success: false, stopReason: "provider_error");
            Assert.Contains("Error", content);
            Assert.Contains("provider_error", content);
        }
    }
}
