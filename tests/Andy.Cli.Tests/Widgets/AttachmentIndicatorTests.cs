// Copyright (c) Rivoli AI 2026. All rights reserved.

using System;
using System.IO;
using Andy.Cli.Domain;
using Andy.Cli.Widgets;

namespace Andy.Cli.Tests.Widgets;

public class AttachmentIndicatorTests : IDisposable
{
    private readonly string _tempDir;

    public AttachmentIndicatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AttachmentIndicatorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static string CreateTempFile(string extension, int sizeBytes, string? subDir = null)
    {
        var dir = subDir ?? Path.GetTempPath();
        var path = Path.Combine(dir, $"testfile_{Guid.NewGuid():N}{extension}");
        var content = new byte[sizeBytes];
        if (sizeBytes > 0) content[0] = 0xFF;
        File.WriteAllBytes(path, content);
        return path;
    }

    private ImageAttachment CreateAttachment(string extension, int sizeBytes)
    {
        var path = CreateTempFile(extension, sizeBytes, _tempDir);
        var result = ImageAttachment.TryCreate(path);
        Assert.True(result.IsSuccess);
        return result.SuccessValue;
    }

    // ── Show / Dismiss ───────────────────────────────────────────────────────

    [Fact]
    public void Initially_NotVisible()
    {
        var indicator = new AttachmentIndicator();

        Assert.False(indicator.IsVisible);
        Assert.Null(indicator.Current);
        Assert.Equal(0, indicator.LineCount);
    }

    [Fact]
    public void Show_SetsVisibleAndCurrent()
    {
        var att = CreateAttachment(".png", 1024);
        var indicator = new AttachmentIndicator();

        indicator.Show(att);

        Assert.True(indicator.IsVisible);
        Assert.Same(att, indicator.Current);
        Assert.Equal(1, indicator.LineCount);
    }

    [Fact]
    public void Show_NullAttachment_Throws()
    {
        var indicator = new AttachmentIndicator();
        Assert.Throws<ArgumentNullException>(() => indicator.Show(null!));
    }

    [Fact]
    public void Dismiss_ClearsCurrentAndHides()
    {
        var att = CreateAttachment(".jpg", 200);
        var indicator = new AttachmentIndicator();
        indicator.Show(att);

        indicator.Dismiss();

        Assert.False(indicator.IsVisible);
        Assert.Null(indicator.Current);
        Assert.Equal(0, indicator.LineCount);
    }

    [Fact]
    public void Dismiss_WhenNotShowing_IsNoOp()
    {
        var indicator = new AttachmentIndicator();
        indicator.Dismiss(); // should not throw
        Assert.False(indicator.IsVisible);
    }

    [Fact]
    public void Show_ReplacesExistingAttachment()
    {
        var att1 = CreateAttachment(".png", 100);
        var att2 = CreateAttachment(".jpg", 200);
        var indicator = new AttachmentIndicator();

        indicator.Show(att1);
        Assert.Same(att1, indicator.Current);

        indicator.Show(att2);
        Assert.Same(att2, indicator.Current);
        Assert.True(indicator.IsVisible);
    }

    // ── FormatSize ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1L, "1 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1.0 MB")]
    [InlineData(10485760L, "10.0 MB")]
    [InlineData(1073741824L, "1.00 GB")]
    public void FormatSize_ReturnsCorrectHumanReadable(long bytes, string expected)
    {
        Assert.Equal(expected, AttachmentIndicator.FormatSize(bytes));
    }

    // ── LineCount ─────────────────────────────────────────────────────────────

    [Fact]
    public void LineCount_IsZeroBeforeShow()
    {
        var indicator = new AttachmentIndicator();
        Assert.Equal(0, indicator.LineCount);
    }

    [Fact]
    public void LineCount_IsOneAfterShow()
    {
        var att = CreateAttachment(".png", 50);
        var indicator = new AttachmentIndicator();

        indicator.Show(att);

        Assert.Equal(1, indicator.LineCount);
    }

    [Fact]
    public void LineCount_IsZeroAfterDismiss()
    {
        var att = CreateAttachment(".png", 50);
        var indicator = new AttachmentIndicator();
        indicator.Show(att);

        indicator.Dismiss();

        Assert.Equal(0, indicator.LineCount);
    }

    // ── Integration with FileDropEvent ─────────────────────────────────────────

    [Fact]
    public void Show_WithImageAttachment_DisplaysMetadata()
    {
        var att = CreateAttachment(".png", 4096);
        var indicator = new AttachmentIndicator();

        indicator.Show(att);

        Assert.True(indicator.IsVisible);
        Assert.Equal(att.FilePath, indicator.Current!.FilePath);
        Assert.Equal("png", indicator.Current.Format);
        Assert.Equal(4096, indicator.Current.SizeBytes);
    }
}
