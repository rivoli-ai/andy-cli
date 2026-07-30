using System.IO;
using System.Text;
using System.Text.Json;

namespace Andy.Cli.OneShot;

// rivoli-ai/andy-cli#279: renders the headless NDJSON event stream as concise
// human text.
//
// The one-shot runner hands this writer to HeadlessAgentRunner in place of the
// NDJSON sink, so the *same* emitter, the same events, and the same ordering
// drive both output modes - text mode is a projection of the machine stream,
// never a second code path that could drift from it.
//
// Everything rendered here goes to stderr so stdout stays exactly the model's
// final answer and `andy-cli run "..." > answer.txt` does the obvious thing.
// Unknown event kinds are ignored, matching the additive event contract.
public sealed class OneShotTextEventWriter : TextWriter
{
    private readonly TextWriter _human;
    private readonly StringBuilder _line = new();

    public OneShotTextEventWriter(TextWriter human)
    {
        _human = human;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        if (value == '\n')
        {
            FlushLine();
            return;
        }
        if (value == '\r')
        {
            return;
        }
        _line.Append(value);
    }

    public override void Write(string? value)
    {
        if (value is null)
        {
            return;
        }
        foreach (var c in value)
        {
            Write(c);
        }
    }

    public override void WriteLine(string? value)
    {
        Write(value);
        FlushLine();
    }

    public override void Flush()
    {
        SafeWriter.Guard(() => _human.Flush());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            FlushLine();
            Flush();
        }
        base.Dispose(disposing);
    }

    private void FlushLine()
    {
        if (_line.Length == 0)
        {
            return;
        }

        var json = _line.ToString();
        _line.Clear();
        Render(json);
    }

    private void Render(string json)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Not an event line (should not happen); pass it through verbatim
            // rather than dropping diagnostics on the floor.
            SafeWriter.Guard(() => _human.WriteLine(json));
            return;
        }

        if (!root.TryGetProperty("kind", out var kindElement))
        {
            return;
        }

        var kind = kindElement.GetString();
        if (!root.TryGetProperty("data", out var data))
        {
            return;
        }

        switch (kind)
        {
            case "tool_call_finished":
                RenderToolCall(data);
                break;
            case "error":
                RenderError(data);
                break;
            default:
                // started / llm_chunk / tool_call_started / tool_usage_audit /
                // required_action_verification / output_written / finished carry
                // no information a human needs on a one-shot run; the answer and
                // the exit code say it all. Use --json for the full stream.
                break;
        }
    }

    private void RenderToolCall(JsonElement data)
    {
        var toolName = GetString(data, "tool_name") ?? "(tool)";
        var outcome = GetString(data, "outcome")
            ?? (data.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True ? "success" : "failed");
        var duration = data.TryGetProperty("duration_ms", out var ms) && ms.TryGetInt64(out var value)
            ? value
            : 0L;

        SafeWriter.Guard(() => _human.WriteLine($"[tool] {toolName} {outcome} ({duration} ms)"));
    }

    private void RenderError(JsonElement data)
    {
        var message = GetString(data, "message");
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }
        var fatal = data.TryGetProperty("fatal", out var f) && f.ValueKind == JsonValueKind.True;
        SafeWriter.Guard(() => _human.WriteLine($"[{(fatal ? "error" : "warn")}] {message}"));
    }

    private static string? GetString(JsonElement data, string property)
        => data.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}

// rivoli-ai/andy-cli#279: `andy-cli run "..." | head -1` closes the read end of
// the pipe while the agent is still producing. The write then fails with
// IOException (EPIPE) or on an already-disposed stream. A one-shot run must not
// turn that into an unhandled crash or a bogus exit code, so every write to a
// caller-owned stream goes through this guard.
internal static class SafeWriter
{
    public static void Guard(Action write)
    {
        try
        {
            write();
        }
        catch (IOException)
        {
            // Broken pipe: the consumer stopped reading. Keep running.
        }
        catch (ObjectDisposedException)
        {
            // Destination already closed.
        }
    }
}

// Wraps a caller-owned TextWriter so a broken pipe cannot abort the run.
internal sealed class BrokenPipeTolerantWriter : TextWriter
{
    private readonly TextWriter _inner;

    public BrokenPipeTolerantWriter(TextWriter inner)
    {
        _inner = inner;
    }

    public override Encoding Encoding => _inner.Encoding;

    public override void Write(char value) => SafeWriter.Guard(() => _inner.Write(value));

    public override void Write(string? value) => SafeWriter.Guard(() => _inner.Write(value));

    public override void WriteLine(string? value) => SafeWriter.Guard(() => _inner.WriteLine(value));

    public override void Flush() => SafeWriter.Guard(() => _inner.Flush());
}
