using Andy.Cli.Services;
using Xunit;

namespace Andy.Cli.Tests.Services;

public class ContextWindowTests
{
    // ── Anthropic ──────────────────────────────────────────────

    [Theory]
    [InlineData("claude-opus-4", "anthropic", 200_000)]
    [InlineData("claude-3-5-sonnet-20241022", "anthropic", 200_000)]
    [InlineData("claude-3-haiku", "anthropic", 200_000)]
    [InlineData("anything", "anthropic", 200_000)]            // provider match
    [InlineData("claude-opus-4", null, 200_000)]              // model-only match
    public void GetMaxTokens_Anthropic(string model, string? provider, int expected)
    {
        Assert.Equal(expected, ContextWindow.GetMaxTokens(model, provider));
    }

    // ── Google Gemini ──────────────────────────────────────────

    [Theory]
    [InlineData("gemini-1.5-pro", "google", 1_000_000)]
    [InlineData("gemini-2.0-flash-exp", "google", 1_000_000)]
    [InlineData("any-model", "gemini", 1_000_000)]             // provider match
    [InlineData("gemini-pro", null, 1_000_000)]               // model-only match
    public void GetMaxTokens_Google(string model, string? provider, int expected)
    {
        Assert.Equal(expected, ContextWindow.GetMaxTokens(model, provider));
    }

    // ── Qwen ───────────────────────────────────────────────────

    [Theory]
    [InlineData("qwen2.5-72b", "openrouter", 128_000)]
    [InlineData("qwen3-32b", "openrouter", 128_000)]
    [InlineData("qwen-turbo", "qwen", 128_000)]
    [InlineData("qwen2.5-coder-32b", "ollama", 128_000)]
    [InlineData("qwen-long", "qwen", 1_000_000)]
    [InlineData("qwen-1m", "qwen", 1_000_000)]
    public void GetMaxTokens_Qwen(string model, string provider, int expected)
    {
        Assert.Equal(expected, ContextWindow.GetMaxTokens(model, provider));
    }

    // ── DeepSeek ───────────────────────────────────────────────

    [Theory]
    [InlineData("deepseek-v3", "deepseek", 128_000)]
    [InlineData("deepseek-r1", "openrouter", 128_000)]
    [InlineData("deepseek-coder-v2", "ollama", 128_000)]
    [InlineData("deepseek-v2-chat", "deepseek", 128_000)]
    public void GetMaxTokens_DeepSeek(string model, string provider, int expected)
    {
        Assert.Equal(expected, ContextWindow.GetMaxTokens(model, provider));
    }

    // ── Mistral ────────────────────────────────────────────────

    [Theory]
    [InlineData("codestral-latest", "mistral", 256_000)]
    [InlineData("mistral-large-latest", "mistral", 128_000)]
    [InlineData("mistral-nemo", "mistral", 128_000)]
    [InlineData("mistral-medium", "mistral", 32_000)]
    [InlineData("mistral-small", "mistral", 32_000)]
    [InlineData("mistral-7b", "ollama", 32_000)]              // model-only match (unknown tier -> conservative 32K)
    public void GetMaxTokens_Mistral(string model, string provider, int expected)
    {
        Assert.Equal(expected, ContextWindow.GetMaxTokens(model, provider));
    }

    // ── OpenAI reasoning models (o1, o3, o4) ──────────────────

    [Theory]
    [InlineData("o1-preview", "openai", 128_000)]
    [InlineData("o1-mini", "openai", 128_000)]
    [InlineData("o3-mini", "openai", 128_000)]
    [InlineData("o4-mini", "openai", 128_000)]
    [InlineData("o1", "openai", 128_000)]                       // exact match, length==2
    [InlineData("o1-preview-2024-12-17", "openai", 128_000)]
    [InlineData("/o1", "openrouter", 128_000)]                 // slash-prefixed provider path
    [InlineData("/o3", "openrouter", 128_000)]
    [InlineData("o1.1", "openai", 128_000)]                   // dot boundary after version
    [InlineData("o11", "openai", 128_000)]                    // digit boundary after version
    public void GetMaxTokens_OpenAIReasoning(string model, string provider, int expected)
    {
        Assert.Equal(expected, ContextWindow.GetMaxTokens(model, provider));
    }

    // ── OpenAI GPT families ───────────────────────────────────

    [Theory]
    [InlineData("openai/gpt-4o", "openai", 128_000)]
    [InlineData("gpt-4.1", "openai", 128_000)]
    [InlineData("gpt-4-turbo", "openai", 128_000)]
    [InlineData("gpt-4", "openai", 8_192)]
    [InlineData("gpt-3.5-turbo", "openai", 16_385)]
    [InlineData("gpt-5-codex", "openai", 128_000)]
    [InlineData("gpt-5.1-codex-mini", "openai", 128_000)]
    public void GetMaxTokens_OpenAIGPT(string model, string provider, int expected)
    {
        Assert.Equal(expected, ContextWindow.GetMaxTokens(model, provider));
    }

    // ── Meta Llama ─────────────────────────────────────────────

    [Theory]
    [InlineData("llama-3.3-70b", "cerebras", 8_192)]
    [InlineData("llama3.2", "ollama", 8_192)]
    public void GetMaxTokens_Llama(string model, string provider, int expected)
    {
        Assert.Equal(expected, ContextWindow.GetMaxTokens(model, provider));
    }

    // ── Boundary matching: ensure no false positives ────────────

    [Theory]
    [InlineData("ollama", "ollama")]             // 'o' + 'l' should NOT match reasoning prefix
    [InlineData("openai", "openai")]             // 'o' + 'p' should NOT match
    [InlineData("openrouter", "openrouter")]     // 'o' + 'r' should NOT match
    public void GetMaxTokens_OllamaNotFalseMatchedAsReasoningModel(string model, string provider)
    {
        // These should NOT return 128K; they should fall through to unknown (0)
        // or match some other rule. Verify they don't hit the reasoning model path.
        int result = ContextWindow.GetMaxTokens(model, provider);
        // ollama with llama model would match Llama (8K), but provider-only "ollama"
        // with model "ollama" has no llama substring -> 0
        // "openai" / "openrouter" as both model and provider -> 0
        Assert.True(result != 128_000 || model.Contains("llama"),
            $"'{model}' should not match the 128K reasoning-model path but got {result}");
    }

    // ── Null / empty handling ──────────────────────────────────

    [Theory]
    [InlineData("some-unknown-model", "mystery")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void GetMaxTokens_UnknownReturnsZero(string? model, string? provider)
    {
        Assert.Equal(0, ContextWindow.GetMaxTokens(model, provider));
    }

    // ── Case insensitivity ─────────────────────────────────────

    [Fact]
    public void GetMaxTokens_IsCaseInsensitive()
    {
        Assert.Equal(200_000, ContextWindow.GetMaxTokens("CLAUDE-OPUS", "ANTHROPIC"));
        Assert.Equal(128_000, ContextWindow.GetMaxTokens("DeepSeek-V3", "DeepSeek"));
        Assert.Equal(128_000, ContextWindow.GetMaxTokens("QWEN2.5-72B", "QWEN"));
        Assert.Equal(256_000, ContextWindow.GetMaxTokens("CODESTRAL-LATEST", "MISTRAL"));
        Assert.Equal(128_000, ContextWindow.GetMaxTokens("O1-MINI", "OpenAI"));
    }
}
