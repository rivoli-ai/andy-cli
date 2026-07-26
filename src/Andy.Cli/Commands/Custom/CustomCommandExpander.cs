using System;
using System.Collections.Generic;
using System.Text;

namespace Andy.Cli.Commands.Custom;

/// <summary>
/// Substitutes argument placeholders in a Markdown command template.
/// </summary>
/// <remarks>
/// Supported placeholders:
/// <list type="bullet">
/// <item><c>$ARGUMENTS</c> / <c>${ARGUMENTS}</c> - the raw argument text exactly as typed.</item>
/// <item><c>$1</c> .. <c>$9</c> and <c>${1}</c> .. <c>${9}</c> - the n-th argument with quotes removed.</item>
/// <item><c>$$</c> - a literal dollar sign, so a template can talk about shell variables.</item>
/// </list>
/// A missing positional expands to the empty string (never to the literal <c>$3</c>), and any
/// other <c>$</c> is left exactly as written. Substitution is single-pass: text substituted in
/// from an argument is never rescanned for placeholders, so a user cannot smuggle
/// <c>$ARGUMENTS</c> in through an argument value.
///
/// SECURITY (issue #281): expansion is pure string slicing. There is no shell, no process,
/// and no command substitution of any kind. Argument text is inert.
/// </remarks>
public static class CustomCommandExpander
{
    private const string ArgumentsToken = "ARGUMENTS";

    /// <summary>Expand a template against a raw argument string.</summary>
    public static string ExpandTemplate(string template, string? rawArguments)
    {
        if (string.IsNullOrEmpty(template))
            return "";

        var raw = (rawArguments ?? "").Trim();
        var positional = CustomCommandArguments.Parse(raw);
        var sb = new StringBuilder(template.Length + raw.Length);

        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if (c != '$')
            {
                sb.Append(c);
                continue;
            }

            // "$$" -> literal "$"
            if (i + 1 < template.Length && template[i + 1] == '$')
            {
                sb.Append('$');
                i++;
                continue;
            }

            if (TryReadPlaceholder(template, i, out var token, out int consumed))
            {
                if (token == ArgumentsToken)
                {
                    sb.Append(raw);
                }
                else
                {
                    int index = token[0] - '0';
                    sb.Append(index >= 1 && index <= positional.Count ? positional[index - 1] : "");
                }
                i += consumed - 1;
                continue;
            }

            // Anything else stays literal ("$5.00", "$PATH", a trailing "$").
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>True when the template consumes the whole argument string.</summary>
    public static bool ReferencesArguments(string template)
    {
        if (string.IsNullOrEmpty(template)) return false;
        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] != '$') continue;
            if (i + 1 < template.Length && template[i + 1] == '$') { i++; continue; }
            if (TryReadPlaceholder(template, i, out var token, out int consumed) && token == ArgumentsToken)
                return true;
            if (consumed > 1) i += consumed - 1;
        }
        return false;
    }

    /// <summary>The highest <c>$1</c>..<c>$9</c> referenced by a template, or 0 when there are none.</summary>
    public static int MaxPositionalReferenced(string template)
    {
        int max = 0;
        if (string.IsNullOrEmpty(template)) return 0;
        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] != '$') continue;
            if (i + 1 < template.Length && template[i + 1] == '$') { i++; continue; }
            if (TryReadPlaceholder(template, i, out var token, out int consumed))
            {
                if (token != ArgumentsToken)
                    max = Math.Max(max, token[0] - '0');
                i += consumed - 1;
            }
        }
        return max;
    }

    /// <summary>
    /// Read a placeholder starting at the '$' in <paramref name="index"/>. On success
    /// <paramref name="token"/> is either "ARGUMENTS" or a single digit "1".."9", and
    /// <paramref name="consumed"/> is the number of characters the placeholder occupies.
    /// </summary>
    private static bool TryReadPlaceholder(string template, int index, out string token, out int consumed)
    {
        token = "";
        consumed = 1;
        int i = index + 1;
        if (i >= template.Length)
            return false;

        bool braced = template[i] == '{';
        if (braced) i++;

        int start = i;
        if (i < template.Length && template[i] >= '1' && template[i] <= '9')
        {
            token = template[i].ToString();
            i++;
            // "$12" is not "$1" followed by "2": treat multi-digit as unsupported and literal.
            if (!braced && i < template.Length && char.IsDigit(template[i]))
            {
                token = "";
                return false;
            }
        }
        else
        {
            while (i < template.Length && char.IsLetter(template[i]))
                i++;
            var word = template.Substring(start, i - start);
            if (!string.Equals(word, ArgumentsToken, StringComparison.Ordinal))
                return false;
            token = ArgumentsToken;
        }

        if (braced)
        {
            if (i >= template.Length || template[i] != '}')
            {
                token = "";
                return false;
            }
            i++;
        }

        consumed = i - index;
        return true;
    }
}
