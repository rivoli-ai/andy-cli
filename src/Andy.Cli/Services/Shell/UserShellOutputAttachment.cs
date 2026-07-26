using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Andy.Cli.Services.Sessions;

namespace Andy.Cli.Services.Shell;

/// <summary>
/// Holds the results of recent user-invoked shell commands so the user can EXPLICITLY hand one to
/// the model (issue #286).
///
/// Output from shell mode is never fed to the model automatically. That is the whole point of the
/// separation: the user gets a quick local command without silently spending context, and without
/// the model acquiring facts the user did not choose to share. The <c>/attach</c> action is the
/// only path from this buffer into a prompt, it is always user-initiated, and what it produces is
/// REDACTED first, because unlike the feed (the user's own terminal) a prompt leaves the machine.
///
/// The buffer is bounded and holds only what shell mode produced this session; it is not persisted.
/// </summary>
public sealed class UserShellOutputAttachment
{
    /// <summary>How many recent commands stay attachable.</summary>
    public const int Capacity = 10;

    /// <summary>Characters of combined output carried into a prompt before it is trimmed.</summary>
    public const int MaxAttachedCharacters = 8000;

    private readonly object _sync = new();
    private readonly List<UserShellCommandResult> _results = new();
    private readonly SessionRedactor _redactor;

    public UserShellOutputAttachment(SessionRedactor? redactor = null)
    {
        _redactor = redactor ?? new SessionRedactor();
    }

    /// <summary>Number of commands currently attachable (most recent first when indexed).</summary>
    public int Count
    {
        get { lock (_sync) return _results.Count; }
    }

    /// <summary>Records a completed command. The oldest entry is dropped once capacity is reached.</summary>
    public void Record(UserShellCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_sync)
        {
            _results.Add(result);
            while (_results.Count > Capacity) _results.RemoveAt(0);
        }
    }

    /// <summary>Forgets everything recorded so far (used when the session is reset or replaced).</summary>
    public void Clear()
    {
        lock (_sync) _results.Clear();
    }

    /// <summary>
    /// The command at <paramref name="index"/> counting back from the most recent (1 = latest).
    /// Returns null when nothing matches, so the caller can report it rather than throw.
    /// </summary>
    public UserShellCommandResult? Peek(int index = 1)
    {
        lock (_sync)
        {
            if (index < 1 || index > _results.Count) return null;
            return _results[^index];
        }
    }

    /// <summary>
    /// The one-line summaries shown by <c>/attach</c> with no argument, newest first, so the user
    /// can see what is on offer before spending context on it.
    /// </summary>
    public IReadOnlyList<string> DescribeAvailable()
    {
        lock (_sync)
        {
            return _results
                .AsEnumerable()
                .Reverse()
                .Select((r, i) => string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}. {1}  ({2}, {3} chars)",
                    i + 1,
                    Summarize(r.Command),
                    r.StatusLabel,
                    r.StandardOutput.Length + r.StandardError.Length))
                .ToArray();
        }
    }

    /// <summary>
    /// The redacted, fenced block for the command at <paramref name="index"/>, ready to insert into
    /// the composer. Returns null when no such command exists.
    /// </summary>
    public string? BuildAttachment(int index = 1)
    {
        var result = Peek(index);
        return result is null ? null : Format(result.Redact(_redactor));
    }

    /// <summary>
    /// Renders one result as the text the model will see. Stated plainly as a command the USER ran,
    /// so the model never mistakes it for something it invoked itself and does not "helpfully"
    /// re-run it.
    /// </summary>
    internal static string Format(UserShellCommandResult result)
    {
        var sb = new StringBuilder();
        sb.Append("Output of a shell command I ran myself (exit ")
          .Append(result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "n/a")
          .Append(", in ")
          .Append(result.WorkingDirectory)
          .AppendLine("):");
        sb.AppendLine();
        sb.Append("$ ").AppendLine(result.Command);
        sb.AppendLine();

        var body = new StringBuilder();
        if (!string.IsNullOrEmpty(result.StandardOutput))
        {
            body.AppendLine(result.StandardOutput.TrimEnd());
        }
        if (!string.IsNullOrEmpty(result.StandardError))
        {
            if (body.Length > 0) body.AppendLine();
            body.AppendLine("[stderr]");
            body.AppendLine(result.StandardError.TrimEnd());
        }
        if (body.Length == 0)
        {
            body.AppendLine("(no output)");
        }

        var text = body.ToString();
        if (text.Length > MaxAttachedCharacters)
        {
            var dropped = text.Length - MaxAttachedCharacters;
            text = text[..MaxAttachedCharacters]
                + $"\n[attachment truncated - {dropped:N0} characters omitted]\n";
        }

        // Fence with a marker long enough that output containing its own ``` cannot end the block
        // early and leak the rest as prose.
        var fence = LongestFence(text);
        sb.AppendLine(fence);
        sb.Append(text);
        if (!text.EndsWith('\n')) sb.AppendLine();
        sb.AppendLine(fence);
        return sb.ToString();
    }

    /// <summary>A backtick fence at least one longer than the longest run inside the content.</summary>
    private static string LongestFence(string content)
    {
        int longest = 0, run = 0;
        foreach (var c in content)
        {
            if (c == '`') { run++; longest = Math.Max(longest, run); }
            else run = 0;
        }
        return new string('`', Math.Max(3, longest + 1));
    }

    private static string Summarize(string command)
    {
        var single = command.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return single.Length <= 60 ? single : single[..57] + "...";
    }
}
