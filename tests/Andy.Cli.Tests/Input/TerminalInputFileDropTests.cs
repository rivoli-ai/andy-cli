// Copyright (c) Rivoli AI 2026. All rights reserved.

using System;
using System.IO;
using System.Linq;
using System.Text;
using Andy.Cli.Input;
using Xunit;

namespace Andy.Cli.Tests.Input;

/// <summary>
/// Tests for iTerm2 OSC 1337 and Kitty APC _G file-drop sequence detection
/// in <see cref="TerminalInputParser"/>.
/// </summary>
public class TerminalInputFileDropTests : IDisposable
{
    private readonly string _dropDir;

    public TerminalInputFileDropTests()
    {
        _dropDir = Path.Combine(Path.GetTempPath(), "andy-drops");
        CleanupDropDir();
    }

    public void Dispose()
    {
        CleanupDropDir();
        GC.SuppressFinalize(this);
    }

    private void CleanupDropDir()
    {
        if (Directory.Exists(_dropDir))
        {
            try
            {
                Directory.Delete(_dropDir, recursive: true);
            }
            catch
            {
                // Best effort: another test or process may hold the directory.
            }
        }
    }

    // ---- helpers ----

    private static TerminalInputParser Parser() => new();

    private static TerminalInputEvent Single(IReadOnlyList<TerminalInputEvent> evs)
    {
        Assert.Single(evs);
        return evs[0];
    }

    private static FileDropEvent AssertFileDrop(TerminalInputEvent ev)
    {
        Assert.Equal(TerminalInputKind.FileDrop, ev.Kind);
        Assert.NotNull(ev.FileDrop);
        FileDropEvent drop = ev.FileDrop!.Value;
        return drop;
    }

    // iTerm2: ESC ] 1337;File=name=<b64name>;size=<n>:<b64data> BEL
    private static byte[] Iterm2FilePayload(string fileName, byte[] fileContent)
    {
        string b64Name = Convert.ToBase64String(Encoding.UTF8.GetBytes(fileName));
        string b64Data = Convert.ToBase64String(fileContent);
        string payload = $"\x1b]1337;File=name={b64Name};size={fileContent.Length}:{b64Data}\x07";
        return Encoding.ASCII.GetBytes(payload);
    }

    // Kitty: ESC _ a=T;f=100;t=f;d=<b64data>;m=0 ESC \
    private static byte[] KittyFilePayload(string b64Data, bool moreChunks = false)
    {
        string m = moreChunks ? "m=1" : "m=0";
        string payload = $"\x1b_Ga=T;f=100;t=f;d={b64Data};{m}\x1b\\";
        return Encoding.ASCII.GetBytes(payload);
    }

    // ---- iTerm2 OSC 1337 tests ----

    [Fact]
    public void Iterm2FileDrop_BelTerminator_ProducesFileDropEvent()
    {
        byte[] content = Encoding.UTF8.GetBytes("hello world");
        byte[] seq = Iterm2FilePayload("test.txt", content);

        var ev = Single(Parser().Feed(seq));
        var drop = AssertFileDrop(ev);

        Assert.Equal(FileDropSource.EscapeSequence, drop.Source);
        Assert.Equal("test.txt", drop.FileName);
        Assert.True(File.Exists(drop.FilePath));
        Assert.Equal(content, File.ReadAllBytes(drop.FilePath));
    }

    [Fact]
    public void Iterm2FileDrop_StTerminator_ProducesFileDropEvent()
    {
        byte[] content = Encoding.UTF8.GetBytes("st-terminated");
        string b64Name = Convert.ToBase64String(Encoding.UTF8.GetBytes("st.bin"));
        string b64Data = Convert.ToBase64String(content);
        // ST terminator: ESC \
        byte[] seq = Encoding.ASCII.GetBytes($"\x1b]1337;File=name={b64Name}:{b64Data}\x1b\\");

        var ev = Single(Parser().Feed(seq));
        var drop = AssertFileDrop(ev);

        Assert.Equal("st.bin", drop.FileName);
        Assert.Equal(content, File.ReadAllBytes(drop.FilePath));
    }

    [Fact]
    public void Iterm2FileDrop_SplitAcrossFeeds_DecodesCorrectly()
    {
        byte[] content = Encoding.UTF8.GetBytes("chunked");
        byte[] seq = Iterm2FilePayload("split.dat", content);

        var parser = Parser();
        int mid = seq.Length / 2;
        // Feed first half, then second half
        var first = parser.Feed(seq, mid);
        Assert.Empty(first); // incomplete
        var second = parser.Feed(seq.Skip(mid).ToArray());
        var ev = Single(second);
        var drop = AssertFileDrop(ev);

        Assert.Equal("split.dat", drop.FileName);
    }

    [Fact]
    public void Iterm2FileDrop_EmptyPayload_EmitsNothing()
    {
        string b64Name = Convert.ToBase64String(Encoding.UTF8.GetBytes("empty.bin"));
        byte[] seq = Encoding.ASCII.GetBytes($"\x1b]1337;File=name={b64Name}:\x07");

        var evs = Parser().Feed(seq);
        Assert.Empty(evs);
    }

    [Fact]
    public void Iterm2FileDrop_NonFileOsc_IsIgnored()
    {
        // A non-1337 OSC should be silently consumed (no file drop)
        byte[] seq = Encoding.ASCII.GetBytes("\x1b]0;some title\x07");
        var evs = Parser().Feed(seq);
        Assert.Empty(evs);
    }

    [Fact]
    public void Iterm2FileDrop_InvalidBase64_IsIgnoredGracefully()
    {
        byte[] seq = Encoding.ASCII.GetBytes("\x1b]1337;File=name=aGVsbG8:not-valid-base64!!!\x07");
        var evs = Parser().Feed(seq);
        Assert.Empty(evs);
    }

    [Fact]
    public void Iterm2FileDrop_PayloadExceedsMaxSize_EmitsNothing()
    {
        // 11 MB encoded payload exceeds MaxFileDropSizeBytes (10 MB).
        byte[] oversized = new byte[11 * 1024 * 1024];
        oversized[0] = 0xFF;
        byte[] seq = Iterm2FilePayload("big.bin", oversized);

        var evs = Parser().Feed(seq);

        Assert.Empty(evs);
    }

    // ---- Kitty APC _G tests ----

    [Fact]
    public void KittyFileDrop_SingleChunk_ProducesFileDropEvent()
    {
        byte[] content = Encoding.UTF8.GetBytes("kitty-data");
        string b64Data = Convert.ToBase64String(content);
        byte[] seq = KittyFilePayload(b64Data);

        var ev = Single(Parser().Feed(seq));
        var drop = AssertFileDrop(ev);

        Assert.Equal(FileDropSource.EscapeSequence, drop.Source);
        Assert.True(File.Exists(drop.FilePath));
        Assert.Equal(content, File.ReadAllBytes(drop.FilePath));
    }

    [Fact]
    public void KittyFileDrop_ChunkedTransfer_IsIgnoredGracefully()
    {
        string b64Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("partial"));
        byte[] seq = KittyFilePayload(b64Data, moreChunks: true);

        var evs = Parser().Feed(seq);
        Assert.Empty(evs);
    }

    [Fact]
    public void KittyFileDrop_NonFileApc_IsIgnored()
    {
        // APC that isn't a file transfer (no t=f) — silently consumed
        byte[] seq = Encoding.ASCII.GetBytes("\x1b_Gi=31;OK\x1b\\");
        var evs = Parser().Feed(seq);
        Assert.Empty(evs);
    }

    [Fact]
    public void KittyFileDrop_SplitAcrossFeeds_DecodesCorrectly()
    {
        byte[] content = Encoding.UTF8.GetBytes("split-kitty");
        string b64Data = Convert.ToBase64String(content);
        byte[] seq = KittyFilePayload(b64Data);

        var parser = Parser();
        int mid = seq.Length / 2;
        Assert.Empty(parser.Feed(seq, mid)); // incomplete
        var second = parser.Feed(seq.Skip(mid).ToArray());
        var ev = Single(second);
        AssertFileDrop(ev); // asserts Kind=FileDrop and FileDrop != null
    }

    [Fact]
    public void KittyFileDrop_InvalidBase64_IsIgnoredGracefully()
    {
        byte[] seq = Encoding.ASCII.GetBytes("\x1b_Ga=T;t=f;d=!!!invalid!!!;m=0\x1b\\");
        var evs = Parser().Feed(seq);
        Assert.Empty(evs);
    }

    [Fact]
    public void KittyFileDrop_PayloadExceedsMaxSize_EmitsNothing()
    {
        byte[] oversized = new byte[11 * 1024 * 1024];
        oversized[0] = 0xFF;
        string b64Data = Convert.ToBase64String(oversized);
        byte[] seq = KittyFilePayload(b64Data);

        var evs = Parser().Feed(seq);

        Assert.Empty(evs);
    }

    // ---- Mixed sequence tests ----

    [Fact]
    public void FileDropFollowedByKeystroke_DecodesBoth()
    {
        byte[] content = Encoding.UTF8.GetBytes("mix");
        byte[] fileSeq = Iterm2FilePayload("mix.txt", content);
        byte[] keyBytes = Encoding.ASCII.GetBytes("x");
        byte[] combined = fileSeq.Concat(keyBytes).ToArray();

        var evs = Parser().Feed(combined);
        Assert.Equal(2, evs.Count);
        Assert.Equal(TerminalInputKind.FileDrop, evs[0].Kind);
        Assert.Equal(TerminalInputKind.Key, evs[1].Kind);
        Assert.Equal('x', evs[1].Key.KeyChar);
    }

    [Fact]
    public void WheelEventThenFileDrop_DecodesBoth()
    {
        // wheel-up SGR mouse, then iTerm2 file drop
        byte[] wheelSeq = Encoding.ASCII.GetBytes("\x1b[<64;10;5M");
        byte[] fileSeq = Iterm2FilePayload("post-wheel.txt", Encoding.UTF8.GetBytes("data"));
        byte[] combined = wheelSeq.Concat(fileSeq).ToArray();

        var evs = Parser().Feed(combined);
        Assert.Equal(2, evs.Count);
        Assert.Equal(TerminalInputKind.Wheel, evs[0].Kind);
        Assert.Equal(TerminalInputKind.FileDrop, evs[1].Kind);
    }

    // ---- FileDropEvent / FileDropSource / TerminalInputEvent struct tests ----

    [Fact]
    public void FileDropEvent_Create_ExtractsFileNameFromPath()
    {
        var evt = FileDropEvent.Create("/some/path/image.png", null, FileDropSource.PathPaste);
        Assert.Equal("/some/path/image.png", evt.FilePath);
        Assert.Equal("image.png", evt.FileName);
        Assert.Equal(FileDropSource.PathPaste, evt.Source);
    }

    [Fact]
    public void FileDropEvent_Create_UsesExplicitFileNameWhenProvided()
    {
        var evt = FileDropEvent.Create("/some/path/data.bin", "custom.dat", FileDropSource.EscapeSequence);
        Assert.Equal("custom.dat", evt.FileName);
    }

    [Fact]
    public void TerminalInputEvent_FromFileDrop_SetsKindAndPayload()
    {
        var drop = FileDropEvent.Create("/tmp/test.txt", "test.txt", FileDropSource.EscapeSequence);
        var ev = TerminalInputEvent.FromFileDrop(drop);

        Assert.Equal(TerminalInputKind.FileDrop, ev.Kind);
        Assert.NotNull(ev.FileDrop);
        var fd = ev.FileDrop!.Value;
        Assert.Equal("/tmp/test.txt", fd.FilePath);
        Assert.Equal(0, ev.WheelDelta);
    }

    [Fact]
    public void TerminalInputEvent_FromKey_DoesNotSetFileDrop()
    {
        var ev = TerminalInputEvent.FromKey(new ConsoleKeyInfo(
            'a', ConsoleKey.A, false, false, false));
        Assert.Equal(TerminalInputKind.Key, ev.Kind);
        Assert.Null(ev.FileDrop);
    }
}
