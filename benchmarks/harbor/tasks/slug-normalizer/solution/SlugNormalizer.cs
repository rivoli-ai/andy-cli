using System.Text;

namespace SlugTools;

public static class SlugNormalizer
{
    public static string Normalize(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var slug = new StringBuilder(input.Length);
        var pendingSeparator = false;

        foreach (var character in input)
        {
            if (character is >= 'A' and <= 'Z')
            {
                if (pendingSeparator && slug.Length > 0)
                {
                    slug.Append('-');
                }

                slug.Append((char)(character + ('a' - 'A')));
                pendingSeparator = false;
            }
            else if (character is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (pendingSeparator && slug.Length > 0)
                {
                    slug.Append('-');
                }

                slug.Append(character);
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = slug.Length > 0;
            }
        }

        return slug.ToString();
    }
}
