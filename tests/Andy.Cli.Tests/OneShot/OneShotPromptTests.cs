// rivoli-ai/andy-cli#279: the documented, deterministic combination of
// positional prompt text and piped stdin. The separator is part of the CLI's
// public contract (docs/headless-runtime.md), so it is pinned here.

using Andy.Cli.OneShot;
using Xunit;

namespace Andy.Cli.Tests.OneShot;

public class OneShotPromptTests
{
    [Fact]
    public void Compose_PositionalOnly_IsUsedVerbatim()
    {
        Assert.Equal("review this diff", OneShotPrompt.Compose("review this diff", string.Empty));
    }

    [Fact]
    public void Compose_StdinOnly_IsUsedVerbatim()
    {
        Assert.Equal("diff --git a/x b/x", OneShotPrompt.Compose(string.Empty, "diff --git a/x b/x"));
    }

    [Fact]
    public void Compose_Both_PutsPositionalFirstInsideDocumentedFence()
    {
        var composed = OneShotPrompt.Compose("review this diff", "diff --git a/x b/x");

        Assert.Equal(
            "review this diff\n\n"
            + "--- begin piped stdin ---\n"
            + "diff --git a/x b/x\n"
            + "--- end piped stdin ---",
            composed);
    }

    [Fact]
    public void Compose_IsStable_AcrossRepeatedCalls()
    {
        var a = OneShotPrompt.Compose("summarize", "line1\nline2");
        var b = OneShotPrompt.Compose("summarize", "line1\nline2");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compose_Neither_IsEmpty()
    {
        Assert.Equal(string.Empty, OneShotPrompt.Compose(null, null));
        Assert.Equal(string.Empty, OneShotPrompt.Compose("   ", string.Empty));
    }

    [Fact]
    public void NormalizeStdin_DropsTrailingNewlinesButKeepsInternalStructure()
    {
        var normalized = OneShotPrompt.NormalizeStdin("a\n\nb\n\n", out var truncated);

        Assert.False(truncated);
        Assert.Equal("a\n\nb", normalized);
    }

    [Fact]
    public void NormalizeStdin_WhitespaceOnly_CountsAsAbsent()
    {
        Assert.Equal(string.Empty, OneShotPrompt.NormalizeStdin("   \n\t\n", out _));
        Assert.Equal(string.Empty, OneShotPrompt.NormalizeStdin(null, out _));
    }

    [Fact]
    public void NormalizeStdin_PreservesUnicodeIncludingAstralCodePoints()
    {
        const string unicode = "café 你好 مرحبا 🚀";

        var normalized = OneShotPrompt.NormalizeStdin(unicode + "\n", out var truncated);

        Assert.False(truncated);
        Assert.Equal(unicode, normalized);
        Assert.Contains(unicode, OneShotPrompt.Compose("translate", normalized), StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeStdin_LargeInput_IsTruncatedAtTheDocumentedBound()
    {
        var huge = new string('x', OneShotPrompt.MaxStdinChars + 5_000);

        var normalized = OneShotPrompt.NormalizeStdin(huge, out var truncated);

        Assert.True(truncated);
        Assert.Equal(OneShotPrompt.MaxStdinChars, normalized.Length);
    }

    [Fact]
    public void NormalizeStdin_LargeInputBelowTheBound_IsNotTruncated()
    {
        var large = new string('y', OneShotPrompt.MaxStdinChars);

        var normalized = OneShotPrompt.NormalizeStdin(large, out var truncated);

        Assert.False(truncated);
        Assert.Equal(OneShotPrompt.MaxStdinChars, normalized.Length);
    }

    [Fact]
    public void NormalizeStdin_TruncationNeverSplitsASurrogatePair()
    {
        // Fill exactly to the bound so the character at the cut boundary is the
        // high half of an astral code point.
        var text = new string('a', OneShotPrompt.MaxStdinChars - 1) + "🚀" + "tail";

        var normalized = OneShotPrompt.NormalizeStdin(text, out var truncated);

        Assert.True(truncated);
        Assert.Equal(OneShotPrompt.MaxStdinChars - 1, normalized.Length);
        Assert.False(char.IsHighSurrogate(normalized[^1]));
    }

    [Fact]
    public void JoinWords_UsesASingleSpaceAndTrims()
    {
        Assert.Equal("a b c", OneShotPrompt.JoinWords(new[] { "a", "b", "c" }));
        Assert.Equal(string.Empty, OneShotPrompt.JoinWords(Array.Empty<string>()));
    }
}
