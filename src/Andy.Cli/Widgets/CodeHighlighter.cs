using System;
using System.Collections.Generic;
using Andy.Cli.Themes;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets
{
    /// <summary>The kind of token produced by <see cref="CodeHighlighter"/>.</summary>
    public enum SyntaxKind { Default, Keyword, Type, String, Comment, Number, Identifier }

    /// <summary>Theme colors for syntax highlighting.</summary>
    public readonly struct SyntaxPalette
    {
        public readonly DL.Rgb24 Keyword, Type, Str, Comment, Number, Identifier, Default;

        public SyntaxPalette(DL.Rgb24 keyword, DL.Rgb24 type, DL.Rgb24 str, DL.Rgb24 comment,
                             DL.Rgb24 number, DL.Rgb24 identifier, DL.Rgb24 def)
        {
            Keyword = keyword; Type = type; Str = str; Comment = comment;
            Number = number; Identifier = identifier; Default = def;
        }

        public DL.Rgb24 ColorFor(SyntaxKind k) => k switch
        {
            SyntaxKind.Keyword => Keyword,
            SyntaxKind.Type => Type,
            SyntaxKind.String => Str,
            SyntaxKind.Comment => Comment,
            SyntaxKind.Number => Number,
            SyntaxKind.Identifier => Identifier,
            _ => Default,
        };

        public static SyntaxPalette FromTheme(Theme t) => new(
            t.SyntaxKeyword, t.SyntaxType, t.SyntaxString, t.SyntaxComment,
            t.SyntaxNumber, t.SyntaxIdentifier, t.Code);
    }

    /// <summary>
    /// A small, language-agnostic syntax tokenizer for code blocks and inline code.
    /// Distinguishes keywords, types/class names, method calls, strings, comments and
    /// numbers by color (never by underline). Knows C# and Python keyword sets; other
    /// languages fall back to C#-style comments/strings.
    /// </summary>
    public static class CodeHighlighter
    {
        private static readonly HashSet<string> CsKeywords = new(StringComparer.Ordinal)
        {
            "using", "namespace", "class", "struct", "interface", "enum", "record", "public",
            "private", "protected", "internal", "static", "readonly", "const", "void", "var",
            "new", "return", "async", "await", "if", "else", "for", "foreach", "while", "do",
            "switch", "case", "default", "break", "continue", "throw", "try", "catch", "finally",
            "true", "false", "null", "this", "base", "override", "virtual", "abstract", "sealed",
            "in", "out", "ref", "is", "as", "get", "set",
        };

        private static readonly HashSet<string> PyKeywords = new(StringComparer.Ordinal)
        {
            "def", "class", "return", "if", "elif", "else", "for", "while", "import", "from",
            "as", "True", "False", "None", "in", "and", "or", "not", "with", "yield", "lambda",
            "try", "except", "finally", "raise", "pass", "break", "continue", "global", "nonlocal",
            "is", "assert", "async", "await", "del",
        };

        public static List<(string Text, SyntaxKind Kind)> Tokenize(string line, string? lang)
        {
            var result = new List<(string, SyntaxKind)>();
            if (string.IsNullOrEmpty(line)) return result;

            bool py = lang != null && lang.StartsWith("py", StringComparison.OrdinalIgnoreCase);
            var keywords = py ? PyKeywords : CsKeywords;
            int n = line.Length, i = 0;

            while (i < n)
            {
                char c = line[i];

                // Line comments
                if (!py && c == '/' && i + 1 < n && line[i + 1] == '/') { result.Add((line.Substring(i), SyntaxKind.Comment)); break; }
                if (py && c == '#') { result.Add((line.Substring(i), SyntaxKind.Comment)); break; }

                // String literals
                if (c == '"' || (py && c == '\''))
                {
                    char q = c; int j = i + 1;
                    while (j < n && line[j] != q) { if (line[j] == '\\' && j + 1 < n) j += 2; else j++; }
                    j = Math.Min(n, j + 1);
                    result.Add((line.Substring(i, j - i), SyntaxKind.String)); i = j; continue;
                }

                // Whitespace
                if (char.IsWhiteSpace(c))
                {
                    int j = i; while (j < n && char.IsWhiteSpace(line[j])) j++;
                    result.Add((line.Substring(i, j - i), SyntaxKind.Default)); i = j; continue;
                }

                // Numbers
                if (char.IsDigit(c))
                {
                    int j = i; while (j < n && (char.IsLetterOrDigit(line[j]) || line[j] == '.' || line[j] == '_' || line[j] == 'x')) j++;
                    result.Add((line.Substring(i, j - i), SyntaxKind.Number)); i = j; continue;
                }

                // Identifiers / keywords / types / method calls
                if (char.IsLetter(c) || c == '_')
                {
                    int j = i + 1; while (j < n && (char.IsLetterOrDigit(line[j]) || line[j] == '_')) j++;
                    string tok = line.Substring(i, j - i);
                    SyntaxKind kind;
                    if (keywords.Contains(tok))
                    {
                        kind = SyntaxKind.Keyword;
                    }
                    else
                    {
                        int k = j; while (k < n && char.IsWhiteSpace(line[k])) k++;
                        bool isCall = k < n && line[k] == '(';       // method/function call
                        bool isType = char.IsUpper(tok[0]);          // class/type name (PascalCase)
                        kind = (isCall || isType) ? SyntaxKind.Type : SyntaxKind.Identifier;
                    }
                    result.Add((tok, kind)); i = j; continue;
                }

                // Punctuation / operators
                result.Add((line[i].ToString(), SyntaxKind.Default)); i++;
            }

            return result;
        }

        public static IEnumerable<(string Text, DL.Rgb24 Color)> Highlight(string line, string? lang, SyntaxPalette palette)
        {
            foreach (var (text, kind) in Tokenize(line, lang))
                yield return (text, palette.ColorFor(kind));
        }
    }
}
