using System.Text;

namespace Andy.Cli.OneShot;

// rivoli-ai/andy-cli#279: deterministic combination of positional prompt text
// and piped stdin.
//
// Contract (documented in docs/headless-runtime.md):
//
//   both present  -> "<positional>\n\n--- begin piped stdin ---\n<stdin>\n--- end piped stdin ---"
//   positional only -> "<positional>"
//   stdin only      -> "<stdin>"
//   neither         -> "" (the caller exits nonzero with usage)
//
// The order is always positional first: the instruction ("review this diff")
// precedes the material it applies to, and a shell pipeline therefore produces
// the same prompt on every platform and for every ordering of the shell's own
// argument expansion.
public static class OneShotPrompt
{
    public const string StdinBeginMarker = "--- begin piped stdin ---";
    public const string StdinEndMarker = "--- end piped stdin ---";

    // Upper bound on piped input. A pipeline that feeds a multi-hundred-megabyte
    // file would otherwise be silently turned into an unsendable request; cap it
    // deterministically and tell the operator on stderr instead.
    public const int MaxStdinChars = 1_048_576;

    public static string JoinWords(IReadOnlyList<string> words)
        => string.Join(' ', words).Trim();

    // Normalizes a raw stdin payload: trailing newlines from the producing
    // command are dropped, an all-whitespace payload counts as absent, and
    // oversized input is truncated on a character boundary that never splits a
    // surrogate pair.
    public static string NormalizeStdin(string? raw, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = raw.TrimEnd('\r', '\n');
        if (text.Length <= MaxStdinChars)
        {
            return text;
        }

        var cut = MaxStdinChars;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        truncated = true;
        return text[..cut];
    }

    public static string Compose(string? positional, string? stdin)
    {
        var left = (positional ?? string.Empty).Trim();
        var right = stdin ?? string.Empty;

        if (right.Length == 0)
        {
            return left;
        }
        if (left.Length == 0)
        {
            return right;
        }

        var builder = new StringBuilder(left.Length + right.Length + 64);
        builder.Append(left);
        builder.Append("\n\n");
        builder.Append(StdinBeginMarker);
        builder.Append('\n');
        builder.Append(right);
        builder.Append('\n');
        builder.Append(StdinEndMarker);
        return builder.ToString();
    }
}
