using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Andy.Cli.Services.FileMentions;

/// <summary>A one-based, inclusive line range attached to a mention.</summary>
public readonly record struct LineRange(int Start, int End)
{
    /// <summary>Render the range back into mention suffix form (<c>#L12-L40</c> or <c>#L12</c>).</summary>
    public override string ToString() => Start == End ? $"L{Start}" : $"L{Start}-L{End}";
}

/// <summary>A <c>@</c> token located in prompt text. <see cref="End"/> is exclusive.</summary>
public readonly record struct MentionToken(int Start, int End)
{
    /// <summary>Number of characters the token occupies, including the leading <c>@</c>.</summary>
    public int Length => End - Start;
}

/// <summary>
/// Parsing for the <c>@path</c> mention syntax:
/// <list type="bullet">
/// <item><description><c>@src/Foo.cs</c> - plain path, forward or backslash separators.</description></item>
/// <item><description><c>@"docs/my notes.md"</c> - quoted, for paths containing spaces or <c>#</c>.</description></item>
/// <item><description><c>@src/Foo.cs#L12-L40</c> and <c>@src/Foo.cs#12-40</c> - one-based inclusive line ranges.</description></item>
/// </list>
/// This type is pure text handling: it never touches the filesystem.
/// </summary>
public static class FileMentionSyntax
{
    private static readonly Regex RangeRegex = new(
        @"^L?(?<start>\d+)(?:-L?(?<end>\d+))?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Characters that may sit immediately before a <c>@</c> for it to start a mention.
    /// Anything else (a letter, digit, or <c>@</c>) means the <c>@</c> is part of a larger token
    /// such as an email address and must not open the picker.
    /// </summary>
    private const string AllowedPrecedingCharacters = "([{<\"'`,;:";

    /// <summary>True when a mention can start at <paramref name="index"/> in <paramref name="text"/>.</summary>
    public static bool IsMentionStart(string text, int index)
    {
        if (text is null || index < 0 || index >= text.Length || text[index] != '@')
        {
            return false;
        }
        if (index == 0)
        {
            return true;
        }

        char previous = text[index - 1];
        return char.IsWhiteSpace(previous) || AllowedPrecedingCharacters.IndexOf(previous) >= 0;
    }

    /// <summary>
    /// Compute the exclusive end index of the mention token that starts at <paramref name="start"/>.
    /// Quoted mentions run to the closing quote (plus any <c>#range</c> suffix); unquoted mentions
    /// run to the next whitespace character.
    /// </summary>
    public static int FindTokenEnd(string text, int start)
    {
        int i = start + 1;
        if (i < text.Length && text[i] == '"')
        {
            i++;
            while (i < text.Length && text[i] != '"' && text[i] != '\n')
            {
                i++;
            }
            if (i < text.Length && text[i] == '"')
            {
                i++;
            }
        }

        while (i < text.Length && !char.IsWhiteSpace(text[i]))
        {
            i++;
        }
        return i;
    }

    /// <summary>
    /// Find the mention token the cursor is currently editing. The cursor must sit strictly after
    /// the <c>@</c> and no further than the token's end, so a cursor placed before the <c>@</c> or
    /// past the end of the token does not open the picker.
    /// </summary>
    public static bool TryFindMentionAtCursor(string text, int cursor, out MentionToken token)
    {
        token = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        cursor = Math.Clamp(cursor, 0, text.Length);
        for (int i = cursor - 1; i >= 0; i--)
        {
            char c = text[i];
            if (c == '\n')
            {
                return false;
            }
            if (c == '@')
            {
                if (!IsMentionStart(text, i))
                {
                    return false;
                }

                int end = FindTokenEnd(text, i);
                if (cursor > i && cursor <= end)
                {
                    token = new MentionToken(i, end);
                    return true;
                }
                return false;
            }
            if (char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Every mention token in <paramref name="text"/>, in document order.</summary>
    public static IReadOnlyList<MentionToken> FindAll(string text)
    {
        var tokens = new List<MentionToken>();
        if (string.IsNullOrEmpty(text))
        {
            return tokens;
        }

        for (int i = 0; i < text.Length; i++)
        {
            if (!IsMentionStart(text, i))
            {
                continue;
            }

            int end = FindTokenEnd(text, i);
            if (end > i + 1)
            {
                tokens.Add(new MentionToken(i, end));
            }
            i = end - 1;
        }

        return tokens;
    }

    /// <summary>
    /// Split what the user has typed so far inside a mention, up to the cursor, into the fuzzy
    /// search query and any line range they already appended. Quotes are dropped and separators
    /// normalised so <c>@src\Foo</c> searches for <c>src/Foo</c>.
    /// </summary>
    public static (string Query, LineRange? Range) QueryUpToCursor(string text, MentionToken token, int cursor)
    {
        cursor = Math.Clamp(cursor, token.Start + 1, token.End);
        string body = text.Substring(token.Start + 1, cursor - token.Start - 1);
        var (path, range, _) = SplitBody(body);
        return (path, range);
    }

    /// <summary>Split a mention body (everything after the <c>@</c>) into a path and optional range.</summary>
    /// <returns>
    /// The normalised path, the parsed range if the mention carried one, and the path as it would
    /// read if the <c>#</c> suffix were actually part of the file name.
    /// </returns>
    public static (string Path, LineRange? Range, string PathIncludingSuffix) SplitBody(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return (string.Empty, null, string.Empty);
        }

        if (body.StartsWith('"'))
        {
            int closing = body.IndexOf('"', 1);
            if (closing > 0)
            {
                string quoted = NormalizeSeparators(body.Substring(1, closing - 1));
                string remainder = body.Substring(closing + 1);
                LineRange? quotedRange = null;
                if (remainder.StartsWith('#') && TryParseRange(remainder.Substring(1), out var parsedQuoted))
                {
                    quotedRange = parsedQuoted;
                }
                return (quoted, quotedRange, quoted);
            }

            // Unterminated quote: treat the rest as a literal path.
            string unterminated = NormalizeSeparators(body.Substring(1));
            return (unterminated, null, unterminated);
        }

        string normalized = NormalizeSeparators(body);
        int hash = normalized.LastIndexOf('#');
        if (hash > 0 && TryParseRange(normalized.Substring(hash + 1), out var range))
        {
            return (normalized.Substring(0, hash), range, normalized);
        }

        return (normalized, null, normalized);
    }

    /// <summary>Parse a <c>#</c> suffix such as <c>L12-L40</c>, <c>12-40</c>, <c>L12</c> or <c>12</c>.</summary>
    public static bool TryParseRange(string suffix, out LineRange range)
    {
        range = default;
        if (string.IsNullOrEmpty(suffix))
        {
            return false;
        }

        var match = RangeRegex.Match(suffix);
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["start"].Value, out int start) || start < 1)
        {
            return false;
        }

        int end = start;
        if (match.Groups["end"].Success)
        {
            if (!int.TryParse(match.Groups["end"].Value, out end) || end < 1)
            {
                return false;
            }
        }

        if (end < start)
        {
            (start, end) = (end, start);
        }

        range = new LineRange(start, end);
        return true;
    }

    /// <summary>
    /// Render a workspace-relative path as mention text, quoting it when it contains characters
    /// that would otherwise end the token or be read as a line range.
    /// </summary>
    public static string FormatMention(string relativePath, bool isDirectory = false, LineRange? range = null)
    {
        string path = NormalizeSeparators(relativePath ?? string.Empty);
        if (isDirectory && !path.EndsWith('/'))
        {
            path += "/";
        }

        bool needsQuotes = NeedsQuoting(path);
        string body = needsQuotes ? "\"" + path + "\"" : path;
        if (range is LineRange r)
        {
            body += "#" + r.ToString();
        }
        return "@" + body;
    }

    /// <summary>True when a path must be quoted to survive a round trip through mention syntax.</summary>
    public static bool NeedsQuoting(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }
        foreach (var c in path)
        {
            if (char.IsWhiteSpace(c) || c == '"' || c == '#')
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Normalise Windows separators to forward slashes and drop a leading <c>./</c> so that
    /// <c>@src\Foo.cs</c> and <c>@./src/Foo.cs</c> resolve to the same workspace-relative path.
    /// </summary>
    public static string NormalizeSeparators(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        string normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized.Substring(2);
        }
        return normalized;
    }
}
