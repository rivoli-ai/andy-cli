using System.Text.Json;
using Andy.Acp.Core.Agent;
using Andy.Acp.Core.Transport;
using Andy.Engine;

namespace Andy.Cli.ACP;

public interface IAcpHistoryReplay
{
    Task ReplayAsync(string sessionId, TranscriptSnapshot snapshot, IResponseStreamer streamer, CancellationToken token);
}

/// <summary>
/// Stable ACP v1 replay. The public streamer lacks user-message output, so use
/// the server's public transport for that variant. The server remains v1-only.
/// </summary>
public sealed class AcpHistoryReplay(ITransport transport) : IAcpHistoryReplay
{
    public async Task ReplayAsync(string sessionId, TranscriptSnapshot snapshot, IResponseStreamer streamer, CancellationToken token)
    {
        foreach (var turn in snapshot.Turns)
        {
            foreach (var message in new[] { turn.User }.Concat(turn.Interleaved).Concat(
                turn.FinalAssistant is null ? [] : new[] { turn.FinalAssistant }))
            {
                token.ThrowIfCancellationRequested();
                if (message.Role == "user")
                {
                    foreach (var chunk in Chunks(message.Content))
                        await transport.WriteMessageAsync(JsonSerializer.Serialize(new
                        {
                            jsonrpc = "2.0",
                            method = "session/update",
                            @params = new { sessionId, update = new { sessionUpdate = "user_message_chunk", content = new { type = "text", text = chunk } } }
                        }), token).ConfigureAwait(false);
                }
                else if (message.Role == "assistant")
                {
                    foreach (var chunk in Chunks(message.Content))
                        await streamer.SendMessageChunkAsync(chunk, token).ConfigureAwait(false);
                    foreach (var call in message.ToolCalls)
                        await streamer.SendToolCallAsync(new ToolCall
                        {
                            Id = call.Id,
                            Name = call.Name,
                            Status = "in_progress",
                            Input = JsonSerializer.Deserialize<JsonElement>(call.ArgumentsJson)
                        }, token).ConfigureAwait(false);
                }
                foreach (var result in message.ToolResults)
                    await streamer.SendToolResultAsync(new ToolResult
                    {
                        CallId = result.CallId,
                        IsError = result.IsError,
                        Content = result.ResultJson.Length <= 8000 ? result.ResultJson : result.ResultJson[..8000] + "\n[truncated]"
                    }, token).ConfigureAwait(false);
            }
        }
    }

    private static IEnumerable<string> Chunks(string text)
    {
        for (var offset = 0; offset < text.Length;)
        {
            var length = Math.Min(4096, text.Length - offset);
            if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1])) length--;
            yield return text.Substring(offset, length);
            offset += length;
        }
    }
}
