using System;
using System.Collections.Generic;
using System.Text;

namespace Andy.Cli.Commands.Custom;

/// <summary>
/// Splits the text typed after a slash command into positional arguments.
/// </summary>
/// <remarks>
/// Documented quoting rules (see docs/markdown-commands.md):
/// <list type="bullet">
/// <item>Arguments are separated by runs of whitespace.</item>
/// <item>Double or single quotes group whitespace into one argument; the quotes themselves
/// are removed from the positional value.</item>
/// <item>Inside double quotes, <c>\"</c> and <c>\\</c> are escapes. Single quotes are literal
/// throughout (shell-style), so <c>'it\'s'</c> is not an escape.</item>
/// <item>An unterminated quote is not an error: the remainder of the line becomes the final
/// argument. A prompt template must never fail to run because of a stray quote.</item>
/// <item><c>$ARGUMENTS</c> expands to the raw text exactly as typed, quotes included.</item>
/// </list>
/// No shell is involved at any point: this only slices a string.
/// </remarks>
public static class CustomCommandArguments
{
    /// <summary>Split raw argument text into positional values with quotes removed.</summary>
    public static IReadOnlyList<string> Parse(string? raw)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(raw))
            return result;

        var current = new StringBuilder();
        bool inToken = false;
        char quote = '\0';

        for (int i = 0; i < raw!.Length; i++)
        {
            char c = raw[i];

            if (quote != '\0')
            {
                if (quote == '"' && c == '\\' && i + 1 < raw.Length && (raw[i + 1] == '"' || raw[i + 1] == '\\'))
                {
                    current.Append(raw[i + 1]);
                    i++;
                    continue;
                }
                if (c == quote)
                {
                    quote = '\0';
                    continue;
                }
                current.Append(c);
                continue;
            }

            if (c == '"' || c == '\'')
            {
                quote = c;
                inToken = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
                continue;
            }

            current.Append(c);
            inToken = true;
        }

        if (inToken)
            result.Add(current.ToString());

        return result;
    }
}
