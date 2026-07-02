// Copyright (c) Rivoli AI 2026. All rights reserved.

using System;
using System.IO;
using Andy.Cli.Domain;

namespace Andy.Cli.Tests.Domain;

public class ImageAttachmentTests : IDisposable
{
    private readonly string _tempDir;

    public ImageAttachmentTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ImageAttachmentTests_" + Guid.NewGuid().ToString("N"));
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
        // Fill with non-zero bytes so size validation works for empty checks
        if (sizeBytes > 0) content[0] = 0xFF;
        File.WriteAllBytes(path, content);
        return path;
    }

    // ── TryCreate: success cases ──────────────────────────────────────────────

    [Fact]
    public void TryCreate_PngFile_ReturnsSuccessWithCorrectMetadata()
    {
        var path = CreateTempFile(".png", 1024, _tempDir);

        var result = ImageAttachment.TryCreate(path);

        Assert.True(result.IsSuccess);
        var attachment = result.SuccessValue;
        Assert.Equal(path, attachment.FilePath);
        Assert.Equal("png", attachment.Format);
        Assert.Equal(1024, attachment.SizeBytes);
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".gif")]
    [InlineData(".webp")]
    [InlineData(".bmp")]
    [InlineData(".svg")]
    [InlineData(".tiff")]
    [InlineData(".tif")]
    public void TryCreate_AcceptedFormats_ReturnsSuccess(string extension)
    {
        var path = CreateTempFile(extension, 200, _tempDir);

        var result = ImageAttachment.TryCreate(path);

        Assert.True(result.IsSuccess);
        Assert.Equal(extension.TrimStart('.').ToLowerInvariant(), result.SuccessValue.Format);
    }

    [Fact]
    public void TryCreate_PngFileWithUppercaseExtension_ReturnsSuccess()
    {
        var path = CreateTempFile(".PNG", 500, _tempDir);

        var result = ImageAttachment.TryCreate(path);

        Assert.True(result.IsSuccess);
        Assert.Equal("png", result.SuccessValue.Format);
    }

    // ── TryCreate: file not found ─────────────────────────────────────────────

    [Fact]
    public void TryCreate_FileNotFound_ReturnsFailureWithFileNotFoundCode()
    {
        var path = Path.Combine(_tempDir, "nonexistent.png");

        var result = ImageAttachment.TryCreate(path);

        Assert.True(result.IsFailure);
        Assert.Equal("FILE_NOT_FOUND", result.ErrorValue.Code);
        Assert.Contains("nonexistent.png", result.ErrorValue.Message);
    }

    // ── TryCreate: empty / null path ─────────────────────────────────────────

    [Fact]
    public void TryCreate_NullPath_ReturnsFailureWithEmptyPathCode()
    {
        var result = ImageAttachment.TryCreate(null!);

        Assert.True(result.IsFailure);
        Assert.Equal("EMPTY_PATH", result.ErrorValue.Code);
    }

    [Fact]
    public void TryCreate_WhitespacePath_ReturnsFailureWithEmptyPathCode()
    {
        var result = ImageAttachment.TryCreate("   ");

        Assert.True(result.IsFailure);
        Assert.Equal("EMPTY_PATH", result.ErrorValue.Code);
    }

    // ── TryCreate: unsupported format ─────────────────────────────────────────

    [Theory]
    [InlineData(".exe")]
    [InlineData(".txt")]
    [InlineData(".mp4")]
    public void TryCreate_UnsupportedFormat_ReturnsFailureWithUnsupportedFormatCode(string extension)
    {
        var path = CreateTempFile(extension, 100, _tempDir);

        var result = ImageAttachment.TryCreate(path);

        Assert.True(result.IsFailure);
        Assert.Equal("UNSUPPORTED_FORMAT", result.ErrorValue.Code);
        Assert.Contains(extension, result.ErrorValue.Message);
    }

    // ── TryCreate: no extension ──────────────────────────────────────────────

    [Fact]
    public void TryCreate_NoExtension_ReturnsFailureWithNoExtensionCode()
    {
        var path = Path.Combine(_tempDir, "noextfile");
        File.WriteAllBytes(path, [0xFF, 0x01]);

        var result = ImageAttachment.TryCreate(path);

        Assert.True(result.IsFailure);
        Assert.Equal("NO_EXTENSION", result.ErrorValue.Code);
    }

    // ── TryCreate: file too large ─────────────────────────────────────────────

    [Fact]
    public void TryCreate_FileTooLarge_ReturnsFailureWithFileTooLargeCode()
    {
        // Create a file just over the limit
        var oversizedPath = Path.Combine(_tempDir, "big.png");
        // Write in small chunks to avoid allocating 26 MB at once
        using (var fs = File.Create(oversizedPath))
        {
            var chunk = new byte[1024 * 1024]; // 1 MB
            for (int i = 0; i < 26; i++) fs.Write(chunk);
            fs.Write(new byte[1]); // +1 byte = 26,843,545 bytes total
        }

        var result = ImageAttachment.TryCreate(oversizedPath);

        Assert.True(result.IsFailure);
        Assert.Equal("FILE_TOO_LARGE", result.ErrorValue.Code);
        Assert.Contains("exceeds", result.ErrorValue.Message);
    }

    [Fact]
    public void TryCreate_FileExactlyAtMaxSize_ReturnsSuccess()
    {
        var path = Path.Combine(_tempDir, "at-limit.png");
        using (var fs = File.Create(path))
        {
            fs.SetLength(ImageAttachment.MaxSizeBytes);
        }

        var result = ImageAttachment.TryCreate(path);

        Assert.True(result.IsSuccess);
        Assert.Equal(ImageAttachment.MaxSizeBytes, result.SuccessValue.SizeBytes);
    }

    // ── TryCreate: empty file ──────────────────────────────────────────────────

    [Fact]
    public void TryCreate_EmptyFile_ReturnsFailureWithFileEmptyCode()
    {
        var path = CreateTempFile(".png", 0, _tempDir);

        var result = ImageAttachment.TryCreate(path);

        Assert.True(result.IsFailure);
        Assert.Equal("FILE_EMPTY", result.ErrorValue.Code);
    }

    // ── ValidationError ─────────────────────────────────────────────────────────

    [Fact]
    public void ValidationError_ToString_FormatsAsCodeAndMessage()
    {
        var error = new ValidationError("FILE_NOT_FOUND", "File not found: x.png");

        var str = error.ToString();

        Assert.Equal("[FILE_NOT_FOUND] File not found: x.png", str);
    }

    [Fact]
    public void ValidationError_NullCode_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => new ValidationError(null!, "msg"));
    }

    [Fact]
    public void ValidationError_EmptyMessage_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => new ValidationError("CODE", ""));
    }

    // ── Result pattern match ──────────────────────────────────────────────────

    [Fact]
    public void Result_Match_OnSuccess_InvokesSuccessCallback()
    {
        var path = CreateTempFile(".jpg", 10, _tempDir);
        var result = ImageAttachment.TryCreate(path);

        var output = result.Match(
            success => $"OK:{success.Format}",
            failure => $"FAIL:{failure.Code}");

        Assert.Equal("OK:jpg", output);
    }

    [Fact]
    public void Result_Match_OnFailure_InvokesFailureCallback()
    {
        var result = ImageAttachment.TryCreate("/no/such/file.png");

        var output = result.Match(
            success => $"OK:{success.Format}",
            failure => $"FAIL:{failure.Code}");

        Assert.Equal("FAIL:FILE_NOT_FOUND", output);
    }

    [Fact]
    public void Result_ValueOr_OnFailure_ReturnsFallback()
    {
        // Create a valid ImageAttachment via the factory for use as fallback
        var validPath = CreateTempFile(".png", 128, _tempDir);
        var fallback = ImageAttachment.TryCreate(validPath).SuccessValue;

        var failResult = Result<ImageAttachment, ValidationError>.Fail(
            new ValidationError("TEST", "test error"));

        var value = failResult.ValueOr(fallback);

        Assert.NotNull(value);
        Assert.Equal(validPath, value.FilePath);
    }

    // ── MaxSizeBytes constant ─────────────────────────────────────────────────

    [Fact]
    public void MaxSizeBytes_Is25MB()
    {
        Assert.Equal(25L * 1024 * 1024, ImageAttachment.MaxSizeBytes);
    }
}
