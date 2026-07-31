using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Andy.Cli.Tests.Lsp;

/// <summary>
/// A one-way in-memory byte pipe with real stream semantics: writes are chunked exactly as the
/// writer produced them, reads block until bytes arrive, and completion reads back as end of
/// stream (Read returning 0).
///
/// This exists so the deterministic test language server can be driven through the SAME framing
/// and JSON-RPC code paths as a real stdio server, without depending on any language-server binary
/// being installed on the machine or on CI. Anonymous OS pipes would also work but bring their own
/// platform quirks; this is fully deterministic and cancellable.
/// </summary>
public sealed class LoopbackStream : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    private ReadOnlyMemory<byte> _current = ReadOnlyMemory<byte>.Empty;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Signals end of stream to the reader.</summary>
    public void CompleteWriting() => _chunks.Writer.TryComplete();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_current.Length == 0)
        {
            try
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }
            }
            catch (ChannelClosedException)
            {
                return 0;
            }

            if (_chunks.Reader.TryRead(out var chunk))
            {
                _current = chunk;
            }
        }

        var count = Math.Min(buffer.Length, _current.Length);
        _current.Span[..count].CopyTo(buffer.Span);
        _current = _current[count..];
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return ValueTask.CompletedTask;
        _chunks.Writer.TryWrite(buffer.ToArray());
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        _chunks.Writer.TryWrite(buffer.AsSpan(offset, count).ToArray());

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) CompleteWriting();
        base.Dispose(disposing);
    }
}
