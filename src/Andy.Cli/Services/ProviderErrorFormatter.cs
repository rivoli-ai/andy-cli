using System.Globalization;
using Andy.Cli.Services.Sessions;
using Andy.Llm.Errors;

namespace Andy.Cli.Services;

public static class ProviderErrorFormatter
{
    public static string Format(LlmProviderError error)
    {
        var heading = $"Provider error: {error.Provider}";
        if (error.StatusCode is { } status) heading += $" (HTTP {status})";
        if (error.RetryAfterSeconds is { } seconds)
            heading += "\nRetry after: " + seconds.ToString("0.###", CultureInfo.InvariantCulture) + (seconds == 1 ? " second" : " seconds");
        return Plain(heading + "\n" + error.Message);
    }

    public static string Plain(string message)
    {
        var redacted = new SessionRedactor().RedactText(message);
        var text = string.Concat(redacted.Select(c => c == '\n' || c == '\t' || !char.IsControl(c) ? c : ' '));
        return text.Length <= 2400 ? text : text[..2400] + "...";
    }
}
