using SlugTools;

var failures = new List<string>();

Check("Hello, World!", "hello-world");
Check("  already--slugged  ", "already-slugged");
Check("one___two...three", "one-two-three");
Check("123 ABC 456", "123-abc-456");
Check("---", string.Empty);
Check(string.Empty, string.Empty);
Check("MIXED\tWhitespace\nand symbols", "mixed-whitespace-and-symbols");
Check("café menu", "caf-menu");

try
{
    SlugNormalizer.Normalize(null!);
    failures.Add("null input did not throw ArgumentNullException");
}
catch (ArgumentNullException)
{
}
catch (Exception ex)
{
    failures.Add($"null input threw {ex.GetType().Name}");
}

if (failures.Count == 0)
{
    Console.WriteLine("Slug normalizer verification passed.");
    return 0;
}

foreach (var failure in failures)
{
    Console.Error.WriteLine(failure);
}

return 1;

void Check(string input, string expected)
{
    var actual = SlugNormalizer.Normalize(input);
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
    {
        failures.Add($"Normalize({input}) expected '{expected}', got '{actual}'");
    }
}
