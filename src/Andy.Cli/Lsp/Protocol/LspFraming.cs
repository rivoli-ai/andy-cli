using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Lsp.Protocol;

/// <summary>
/// Reads base-protocol framed messages ("Content-Length: N\r\n\r\n" + N bytes of UTF-8 JSON) off a
/// raw stream.
///
/// This is deliberately byte-oriented rather than a <see cref="StreamReader"/>: Content-Length
/// counts BYTES, and a character-oriented reader would mis-slice any message containing non-ASCII
/// text. It also has to survive garbage - a server that prints a stack trace to stdout, a partial
/// frame, a header without a length - without throwing, because a language server crashing mid
/// sentence must not propagate an exception into the agent loop.
/// </summary>
public sealed class LspFrameReader
{
    private const int MaxHeaderBytes = 8 * 1024;

    // Refuse absurd frames rather than allocating whatever a broken server claims it will send.
    private const int MaxContentLength = 32 * 1024 * 1024;

    private readonly Stream _stream;
    private byte[] _buffer = new byte[16 * 1024];
    private int _start;
    private int _end;

    public LspFrameReader(Stream stream) => _stream = stream;

    /// <summary>
    /// Reads the next well-formed frame. Returns null at end of stream. Malformed input is skipped
    /// and reported through <paramref name="onMalformed"/> rather than throwing.
    /// </summary>
    public async Task<string?> ReadFrameAsync(Action<string>? onMalformed, CancellationToken cancellationToken)
    {
        while (true)
        {
            var header = await ReadHeaderBlockAsync(onMalformed, cancellationToken).ConfigureAwait(false);
            if (header is null) return null;

            var contentLength = ParseContentLength(header);
            if (contentLength is null)
            {
                onMalformed?.Invoke("header block without a usable Content-Length was discarded");
                continue;
            }

            if (contentLength.Value == 0)
            {
                onMalformed?.Invoke("empty frame discarded");
                continue;
            }

            var body = await ReadExactAsync(contentLength.Value, cancellationToken).ConfigureAwait(false);
            if (body is null) return null;

            return body;
        }
    }

    private async Task<string?> ReadHeaderBlockAsync(Action<string>? onMalformed, CancellationToken cancellationToken)
    {
        var header = new StringBuilder();
        var consecutiveNewlines = 0;

        while (true)
        {
            if (_start >= _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var current = (char)_buffer[_start++];
            if (current == '\r') continue;
            if (current == '\n')
            {
                consecutiveNewlines++;
                if (consecutiveNewlines >= 2)
                {
                    return header.ToString();
                }
                header.Append('\n');
                continue;
            }

            consecutiveNewlines = 0;
            header.Append(current);

            if (header.Length > MaxHeaderBytes)
            {
                // A server writing plain text to stdout: drop what we have and resynchronize on the
                // next header terminator instead of failing the connection.
                onMalformed?.Invoke("oversized header block discarded");
                header.Clear();
            }
        }
    }

    private static int? ParseContentLength(string headerBlock)
    {
        foreach (var line in headerBlock.Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;

            var name = line.AsSpan(0, separator).Trim();
            if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;

            var value = line.AsSpan(separator + 1).Trim();
            if (int.TryParse(value, out var length) && length >= 0 && length <= MaxContentLength)
            {
                return length;
            }
            return null;
        }

        return null;
    }

    private async Task<string?> ReadExactAsync(int count, CancellationToken cancellationToken)
    {
        var body = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            var read = 0;
            while (read < count)
            {
                if (_start >= _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                var available = Math.Min(_end - _start, count - read);
                Array.Copy(_buffer, _start, body, read, available);
                _start += available;
                read += available;
            }

            return Encoding.UTF8.GetString(body, 0, count);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(body);
        }
    }

    private async Task<bool> FillAsync(CancellationToken cancellationToken)
    {
        _start = 0;
        _end = await _stream.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        return _end > 0;
    }
}

/// <summary>
/// Writes base-protocol framed messages to a raw stream.
///
/// Public alongside <see cref="LspFrameReader"/> so the deterministic test language server can
/// speak the exact same wire format the client parses, rather than a second implementation that
/// could drift from it.
/// </summary>
public static class LspFrameWriter
{
    public static async Task WriteAsync(Stream stream, string payload, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
