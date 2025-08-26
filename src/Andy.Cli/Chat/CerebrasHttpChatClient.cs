using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Text.Json.Serialization;

namespace Andy.Cli.Chat;

public sealed class CerebrasHttpChatClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    public string Model { get; }
    private readonly double _temperature;
    private readonly int _maxTokens;

    public CerebrasHttpChatClient(ChatConfiguration config)
    {
        _baseUrl = string.IsNullOrWhiteSpace(config.BaseUrl) ? "https://api.cerebras.ai" : config.BaseUrl!.TrimEnd('/');
        _apiKey = config.ApiKey ?? throw new InvalidOperationException("Cerebras API key not configured. Set CEREBRAS_API_KEY.");
        Model = string.IsNullOrWhiteSpace(config.Model) ? "llama3.1-8b" : config.Model!;
        _temperature = config.Temperature ?? 0.2;
        _maxTokens = config.MaxTokens ?? 1024;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        _httpClient.Timeout = TimeSpan.FromSeconds(120);
    }

    public async Task<string> CreateCompletionAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        var url = _baseUrl + "/v1/chat/completions";
        var request = new ChatCompletionsRequest
        {
            Model = Model,
            Temperature = _temperature,
            MaxTokens = _maxTokens,
            Stream = false,
            Messages = messages.Select(m => new ChatMessageDto { Role = m.Role, Content = m.Content }).ToList()
        };

        var json = JsonSerializer.Serialize(request, ChatJson.Options);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Cerebras API error {(int)response.StatusCode}: {payload}");
        }

        var completion = JsonSerializer.Deserialize<ChatCompletionsResponse>(payload, ChatJson.Options)
                         ?? throw new InvalidOperationException("Invalid response from Cerebras API");
        var text = completion.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
        return text;
    }

    public sealed record ChatMessage(string Role, string Content);

    private sealed record ChatCompletionsRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
        [JsonPropertyName("messages")] public List<ChatMessageDto> Messages { get; set; } = new();
        [JsonPropertyName("temperature")] public double? Temperature { get; set; }
        [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
        [JsonPropertyName("stream")] public bool? Stream { get; set; }
    }

    private sealed record ChatMessageDto
    {
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
        [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    }

    private sealed record ChatCompletionsResponse
    {
        [JsonPropertyName("choices")] public List<Choice>? Choices { get; set; }
    }

    private sealed record Choice
    {
        [JsonPropertyName("message")] public ChatMessageDto? Message { get; set; }
    }

    private static class ChatJson
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
}
