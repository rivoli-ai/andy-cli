using System;
using System.Collections.Generic;
using System.Text;

namespace Andy.Cli.Editor;

/// <summary>
/// Splits a <c>VISUAL</c>/<c>EDITOR</c> value into a program plus argument vector
/// WITHOUT going through a shell.
///
/// <para><b>Documented grammar</b> (deliberately small; it is not a shell):</para>
/// <list type="bullet">
///   <item><description>Tokens are separated by spaces and tabs.</description></item>
///   <item><description><c>'single quotes'</c> keep everything between them literally; there are no escapes inside.</description></item>
///   <item><description><c>"double quotes"</c> keep everything literally except <c>\"</c> and <c>\\</c>.</description></item>
///   <item><description>Outside quotes a backslash escapes the next character on Unix. On Windows a
///     backslash is literal (it is a path separator), so quote paths there instead.</description></item>
///   <item><description>An unterminated quote or a trailing lone backslash is an error.</description></item>
/// </list>
///
/// <para><b>Nothing is expanded.</b> <c>$VAR</c>, <c>~</c>, <c>*</c>, <c>|</c>, <c>;</c>,
/// <c>&amp;&amp;</c> and redirection characters are passed to the editor as literal argument
/// text. Values such as <c>code --wait</c>, <c>"/Applications/My Editor/bin/edit" --wait</c>
/// and <c>emacsclient -nw -a ''</c> all work; shell pipelines do not, by design.</para>
/// </summary>
public static class EditorCommandLine
{
    /// <summary>
    /// Parse an editor command line. Returns false and sets <paramref name="error"/> when the
    /// value is blank or malformed.
    /// </summary>
    public static bool TryParse(
        string? value,
        out string fileName,
        out IReadOnlyList<string> arguments,
        out string? error)
    {
        fileName = string.Empty;
        arguments = Array.Empty<string>();
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "the value is empty";
            return false;
        }

        if (!TryTokenize(value!, out var tokens, out error))
            return false;

        if (tokens.Count == 0)
        {
            error = "the value contains no program name";
            return false;
        }

        fileName = tokens[0];
        arguments = tokens.Count > 1 ? tokens.GetRange(1, tokens.Count - 1) : new List<string>();
        return true;
    }

    /// <summary>Tokenize per the grammar documented on this class.</summary>
    internal static bool TryTokenize(string value, out List<string> tokens, out string? error)
    {
        tokens = new List<string>();
        error = null;

        // A backslash outside quotes is an escape only where it is not a path separator.
        bool backslashEscapes = !OperatingSystem.IsWindows();

        var current = new StringBuilder();
        bool hasToken = false;
        int i = 0;
        while (i < value.Length)
        {
            char c = value[i];

            if (c == ' ' || c == '\t')
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                i++;
                continue;
            }

            if (c == '\'')
            {
                hasToken = true;
                i++;
                int close = value.IndexOf('\'', i);
                if (close < 0)
                {
                    error = "an opening single quote is never closed";
                    return false;
                }
                current.Append(value, i, close - i);
                i = close + 1;
                continue;
            }

            if (c == '"')
            {
                hasToken = true;
                i++;
                bool closed = false;
                while (i < value.Length)
                {
                    char d = value[i];
                    if (d == '\\' && i + 1 < value.Length && (value[i + 1] == '"' || value[i + 1] == '\\'))
                    {
                        current.Append(value[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (d == '"')
                    {
                        closed = true;
                        i++;
                        break;
                    }
                    current.Append(d);
                    i++;
                }
                if (!closed)
                {
                    error = "an opening double quote is never closed";
                    return false;
                }
                continue;
            }

            if (c == '\\' && backslashEscapes)
            {
                if (i + 1 >= value.Length)
                {
                    error = "the value ends with a lone backslash";
                    return false;
                }
                hasToken = true;
                current.Append(value[i + 1]);
                i += 2;
                continue;
            }

            hasToken = true;
            current.Append(c);
            i++;
        }

        if (hasToken) tokens.Add(current.ToString());
        return true;
    }
}
