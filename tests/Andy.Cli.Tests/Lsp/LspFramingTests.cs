using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Lsp;
using Andy.Cli.Lsp.Protocol;
using Xunit;

namespace Andy.Cli.Tests.Lsp;

/// <summary>
/// The base-protocol framing, which is where a language server's misbehaviour lands first. Every
/// case here must resolve without an exception escaping: a server writing a stack trace to stdout
/// is a normal Tuesday, not a reason to take down the agent.
/// </summary>
public sealed class LspFramingTests
{
    private static async Task<string?> ReadOne(string wire, Action<string>? onMalformed = null)
    {
        var stream = new LoopbackStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(wire));
        stream.CompleteWriting();
        return await new LspFrameReader(stream).ReadFrameAsync(onMalformed, CancellationToken.None);
    }

    [Fact]
    public async Task ReadsASingleFrame()
    {
        var frame = await ReadOne("Content-Length: 13\r\n\r\n{\"jsonrpc\":1}");
        Assert.Equal("{\"jsonrpc\":1}", frame);
    }

    [Fact]
    public async Task ContentLengthCountsBytesNotCharacters()
    {
        // A message with non-ASCII text is the case a character-oriented reader gets wrong.
        var payload = "{\"m\":\"café über\"}";
        var bytes = Encoding.UTF8.GetByteCount(payload);
        Assert.True(bytes > payload.Length);

        var frame = await ReadOne($"Content-Length: {bytes}\r\n\r\n{payload}");
        Assert.Equal(payload, frame);
    }

    [Fact]
    public async Task ExtraHeadersAreIgnored()
    {
        var frame = await ReadOne(
            "Content-Type: application/vscode-jsonrpc; charset=utf-8\r\nContent-Length: 2\r\n\r\n{}");
        Assert.Equal("{}", frame);
    }

    [Fact]
    public async Task NonFrameJunkIsSkippedAndTheNextFrameIsStillRead()
    {
        var reasons = new System.Collections.Generic.List<string>();
        var frame = await ReadOne(
            "server started, listening\r\n\r\nContent-Length: 2\r\n\r\n{}",
            reasons.Add);

        Assert.Equal("{}", frame);
        Assert.NotEmpty(reasons);
    }

    [Fact]
    public async Task EndOfStreamReturnsNullRatherThanThrowing()
    {
        Assert.Null(await ReadOne(string.Empty));
        Assert.Null(await ReadOne("Content-Length: 100\r\n\r\n{\"truncated\":"));
    }

    [Fact]
    public async Task NonNumericContentLengthIsDiscarded()
    {
        var reasons = new System.Collections.Generic.List<string>();
        var frame = await ReadOne("Content-Length: banana\r\n\r\nContent-Length: 2\r\n\r\n{}", reasons.Add);

        Assert.Equal("{}", frame);
        Assert.NotEmpty(reasons);
    }

    [Fact]
    public async Task WrittenFramesRoundTrip()
    {
        var stream = new LoopbackStream();
        await LspFrameWriter.WriteAsync(stream, "{\"a\":1}", CancellationToken.None);
        await LspFrameWriter.WriteAsync(stream, "{\"b\":2}", CancellationToken.None);
        stream.CompleteWriting();

        var reader = new LspFrameReader(stream);
        Assert.Equal("{\"a\":1}", await reader.ReadFrameAsync(null, CancellationToken.None));
        Assert.Equal("{\"b\":2}", await reader.ReadFrameAsync(null, CancellationToken.None));
        Assert.Null(await reader.ReadFrameAsync(null, CancellationToken.None));
    }

    [Fact]
    public void FilePathsRoundTripThroughFileUris()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "a b", "c.fake");
        var uri = LspUri.FromPath(path);

        Assert.StartsWith("file://", uri);
        Assert.Equal(System.IO.Path.GetFullPath(path), LspUri.ToPath(uri));
    }
}
