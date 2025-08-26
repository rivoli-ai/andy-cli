namespace Andy.Cli.Chat;

public sealed class ChatConfiguration
{
    public string? ApiKey { get; init; }
    public string? BaseUrl { get; init; }
    public string? Model { get; init; }
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }

    public static ChatConfiguration Load()
    {
        var apiKey = Environment.GetEnvironmentVariable("CEREBRAS_API_KEY");
        var baseUrl = Environment.GetEnvironmentVariable("CEREBRAS_BASE_URL");
        var model = Environment.GetEnvironmentVariable("CEREBRAS_MODEL");
        var tempStr = Environment.GetEnvironmentVariable("CEREBRAS_TEMPERATURE");
        var maxTokensStr = Environment.GetEnvironmentVariable("CEREBRAS_MAX_TOKENS");

        double? temp = null;
        if (double.TryParse(tempStr, out var t)) temp = t;

        int? maxTokens = null;
        if (int.TryParse(maxTokensStr, out var mt)) maxTokens = mt;

        return new ChatConfiguration
        {
            ApiKey = apiKey,
            BaseUrl = baseUrl,
            Model = model,
            Temperature = temp,
            MaxTokens = maxTokens
        };
    }
}
