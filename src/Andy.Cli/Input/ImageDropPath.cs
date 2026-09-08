namespace Andy.Cli.Input;

public static class ImageDropPath
{
    public static bool TryParse(string text, out string path)
    {
        path = "";
        text = text.Trim();
        if (text.Length == 0 || text.Any(char.IsControl)) return false;
        if (!Andy.Cli.Editor.EditorCommandLine.TryTokenize(text, out var tokens, out _) || tokens.Count != 1) return false;
        var candidate = tokens[0];
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            if (!string.IsNullOrEmpty(uri.Host) && uri.Host != "localhost") return false;
            candidate = uri.LocalPath;
        }
        if (!Path.IsPathFullyQualified(candidate)) return false;
        if (Path.GetExtension(candidate).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp")) return false;
        path = candidate;
        return true;
    }
}
