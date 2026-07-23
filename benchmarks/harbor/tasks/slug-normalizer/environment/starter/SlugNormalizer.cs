namespace SlugTools;

public static class SlugNormalizer
{
    public static string Normalize(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.Trim().ToLowerInvariant().Replace(" ", "-");
    }
}
