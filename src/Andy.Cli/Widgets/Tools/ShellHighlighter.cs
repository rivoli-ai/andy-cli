using System;
using System.Collections.Generic;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets.Tools
{
    /// <summary>
    /// Syntax highlighting for shell command lines (issues #247, #251).
    ///
    /// <see cref="CodeHighlighter"/> cannot do this job: its rules are built for C#-like
    /// languages, where a PascalCase word is a type and a word before "(" is a call. In a shell
    /// line the meaningful distinctions are different - the executable, its flags, quoted
    /// arguments, variable expansions, and the operators that join commands - so this is a small
    /// dedicated tokenizer rather than another language mode bolted onto the existing one.
    ///
    /// Colors are taken from the theme's syntax roles so a highlighted command matches the code
    /// blocks elsewhere in the feed.
    /// </summary>
    public static class ShellHighlighter
    {
        // Words that introduce a new command position, so what follows them is an executable
        // rather than an argument ("sudo dotnet build", "env FOO=1 make").
        private static readonly HashSet<string> CommandPrefixes = new(StringComparer.Ordinal)
        {
            "sudo", "env", "time", "nohup", "xargs", "command", "exec", "nice", "doas"
        };

        // Operators that end one command and begin another, so the next word is an executable.
        private static readonly HashSet<string> Separators = new(StringComparer.Ordinal)
        {
            "|", "||", "&&", ";", "&", "|&"
        };

        /// <summary>Tokenize and color one command line.</summary>
        public static StyledLine Highlight(string? command, Themes.Theme? theme = null)
        {
            theme ??= Themes.Theme.Current;
            if (string.IsNullOrEmpty(command)) return StyledLine.Empty;

            var spans = new List<StyledSpan>();
            int i = 0, n = command.Length;
            bool expectCommand = true;

            while (i < n)
            {
                char c = command[i];

                if (char.IsWhiteSpace(c))
                {
                    int j = i;
                    while (j < n && char.IsWhiteSpace(command[j])) j++;
                    spans.Add(StyledSpan.Plain(command.Substring(i, j - i)));
                    i = j;
                    continue;
                }

                // A comment runs to end of line, but only when '#' starts a word - "foo#bar" and
                // a URL fragment are not comments.
                if (c == '#' && (spans.Count == 0 || EndsWithWhitespace(spans)))
                {
                    spans.Add(new StyledSpan(command.Substring(i), theme.SyntaxComment, DL.CellAttrFlags.None));
                    break;
                }

                if (c == '\'' || c == '"')
                {
                    int j = ReadQuoted(command, i);
                    spans.Add(new StyledSpan(command.Substring(i, j - i), theme.SyntaxString, DL.CellAttrFlags.None));
                    i = j;
                    expectCommand = false;
                    continue;
                }

                if (c == '$')
                {
                    int j = ReadVariable(command, i);
                    spans.Add(new StyledSpan(command.Substring(i, j - i), theme.SyntaxType, DL.CellAttrFlags.None));
                    i = j;
                    expectCommand = false;
                    continue;
                }

                if (IsOperatorChar(c))
                {
                    int j = i;
                    while (j < n && IsOperatorChar(command[j])) j++;
                    var op = command.Substring(i, j - i);
                    spans.Add(new StyledSpan(op, theme.SyntaxKeyword, DL.CellAttrFlags.None));
                    i = j;
                    // After a pipe or a list separator the next word runs a new program.
                    expectCommand = Separators.Contains(op) || op.StartsWith("|", StringComparison.Ordinal);
                    continue;
                }

                // A bare word: executable, flag, or argument.
                {
                    int j = i;
                    while (j < n && !char.IsWhiteSpace(command[j]) && !IsOperatorChar(command[j])
                           && command[j] != '\'' && command[j] != '"' && command[j] != '$') j++;
                    var word = command.Substring(i, j - i);
                    i = j;

                    if (word.StartsWith("-", StringComparison.Ordinal) && word.Length > 1)
                    {
                        spans.Add(new StyledSpan(word, theme.SyntaxKeyword, DL.CellAttrFlags.None));
                    }
                    else if (expectCommand)
                    {
                        spans.Add(new StyledSpan(word, theme.SyntaxType, DL.CellAttrFlags.Bold));
                        // "sudo dotnet build": the word after a prefix is still a command.
                        expectCommand = CommandPrefixes.Contains(word);
                    }
                    else if (LooksNumeric(word))
                    {
                        spans.Add(new StyledSpan(word, theme.SyntaxNumber, DL.CellAttrFlags.None));
                    }
                    else
                    {
                        spans.Add(StyledSpan.Plain(word));
                    }
                }
            }

            return new StyledLine(spans);
        }

        private static bool EndsWithWhitespace(List<StyledSpan> spans)
        {
            var last = spans[^1].Text;
            return last.Length > 0 && char.IsWhiteSpace(last[^1]);
        }

        private static int ReadQuoted(string s, int start)
        {
            char quote = s[start];
            int i = start + 1;
            while (i < s.Length)
            {
                if (s[i] == '\\' && quote == '"' && i + 1 < s.Length) { i += 2; continue; }
                if (s[i] == quote) return i + 1;
                i++;
            }
            return s.Length; // Unterminated quote: color the remainder as a string.
        }

        private static int ReadVariable(string s, int start)
        {
            int i = start + 1;
            if (i < s.Length && s[i] == '{')
            {
                while (i < s.Length && s[i] != '}') i++;
                return Math.Min(s.Length, i + 1);
            }
            if (i < s.Length && (s[i] == '(' || s[i] == '?' || s[i] == '$' || s[i] == '@')) return i + 1;
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
            return i;
        }

        private static bool IsOperatorChar(char c)
            => c is '|' or '&' or ';' or '>' or '<' or '(' or ')';

        private static bool LooksNumeric(string word)
            => word.Length > 0 && char.IsDigit(word[0]) && double.TryParse(word, out _);
    }
}
