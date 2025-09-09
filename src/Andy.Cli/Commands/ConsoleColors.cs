namespace Andy.Cli.Commands;

/// <summary>
/// Helper class for adding ANSI color codes to console output
/// </summary>
public static class ConsoleColors
{
    // ANSI color codes
    private const string Reset = "\u001b[0m";
    private const string Red = "\u001b[31m";
    private const string Green = "\u001b[32m";
    private const string Yellow = "\u001b[33m";
    private const string Blue = "\u001b[34m";
    private const string Cyan = "\u001b[36m";
    private const string Bold = "\u001b[1m";
    private const string BlackBg = "\u001b[40m";
    private const string DefaultFg = "\u001b[39m"; // Reset foreground only

    /// <summary>
    /// Wraps an entire line with black background
    /// </summary>
    public static string WrapLine(string line) => $"{BlackBg}{line}{Reset}";

    public static string Success(string text) => $"{Green}{text}{Reset}";
    public static string Error(string text) => $"{Red}{text}{Reset}";
    public static string Warning(string text) => $"{Yellow}{text}{Reset}";
    public static string Info(string text) => $"{Cyan}{text}{Reset}";
    public static string Primary(string text) => $"{Blue}{text}{Reset}";

    public static string BoldSuccess(string text) => $"{Bold}{Green}{text}{Reset}";
    public static string BoldError(string text) => $"{Bold}{Red}{text}{Reset}";
    public static string BoldWarning(string text) => $"{Bold}{Yellow}{text}{Reset}";

    // Status indicators with colors
    public static string OkStatus() => $"[{Green}OK{DefaultFg}]";
    public static string ErrorStatus() => $"[{Red}X{DefaultFg}]";
    public static string SetStatus() => $"[{Green}SET{DefaultFg}]";

    // Prefixes with colors
    public static string ErrorPrefix(string message) => $"{Red}Error:{Reset} {message}";
    public static string WarningPrefix(string message) => $"{Yellow}Warning:{Reset} {message}";
    public static string NotePrefix(string message) => $"{Cyan}Note:{Reset} {message}";
    public static string SuccessPrefix(string message) => $"{Green}Success:{Reset} {message}";
}