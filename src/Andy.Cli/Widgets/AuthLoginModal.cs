using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Auth;
using DL = Andy.Tui.DisplayList;

namespace Andy.Cli.Widgets;

/// <summary>
/// The TUI counterpart of <see cref="ConsoleAuthPrompt"/>: an in-frame modal that collects
/// provider credentials while <c>/auth login</c> runs.
///
/// It follows the existing blocking-modal pattern used by the exit dialog - draw a frame, block
/// on one key, repeat - because the interactive key loop already awaits slash commands inline.
/// That keeps the change to Program.cs to a handful of lines.
///
/// SECURITY: a secret field is echoed as one asterisk per character and the typed value never
/// reaches the prompt line, the prompt history, the feed, or the session transcript. Escape (or
/// Ctrl+C) abandons the login without storing anything.
/// </summary>
public sealed class AuthLoginModal : IAuthPrompt
{
    private readonly Func<DL.DisplayList, Task> _renderAsync;
    private readonly Func<ConsoleKeyInfo> _readKey;
    private readonly Func<(int Width, int Height)> _viewport;
    private readonly List<string> _messages = new();

    public AuthLoginModal(
        Func<DL.DisplayList, Task> renderAsync,
        Func<ConsoleKeyInfo> readKey,
        Func<(int Width, int Height)> viewport)
    {
        _renderAsync = renderAsync ?? throw new ArgumentNullException(nameof(renderAsync));
        _readKey = readKey ?? throw new ArgumentNullException(nameof(readKey));
        _viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));
    }

    /// <summary>Lines shown in the modal so far. Never contains secret material.</summary>
    public IReadOnlyList<string> Messages => _messages;

    public async Task<string?> PromptAsync(AuthFieldSpec field, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(field);

        var buffer = new StringBuilder();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RenderAsync(field, buffer.ToString()).ConfigureAwait(false);

            var key = _readKey();

            if (key.Key == ConsoleKey.Enter)
            {
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Escape
                || (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key is ConsoleKey.C or ConsoleKey.D))
            {
                // Clear the buffer before returning so the secret does not linger in memory any
                // longer than necessary.
                buffer.Clear();
                return null;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }

                continue;
            }

            if (char.IsControl(key.KeyChar))
            {
                continue;
            }

            buffer.Append(key.KeyChar);
        }
    }

    public void Info(string message) => Add(message);

    public void Warn(string message) => Add(message);

    public void PresentUrl(string caption, string url)
    {
        // Always show the URL: a remote terminal may have no browser to open.
        Add($"{caption}:");
        Add(url);
        BrowserLauncher.TryOpen(url);
    }

    private void Add(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        _messages.Add(message);

        // Blocking is safe here: the interactive key loop is already awaiting this command, so
        // nothing else is competing to draw a frame.
        try
        {
            RenderAsync(field: null, currentValue: string.Empty).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // A failed intermediate repaint must never abort a login in progress.
        }
    }

    private async Task RenderAsync(AuthFieldSpec? field, string currentValue)
    {
        var (viewportWidth, viewportHeight) = _viewport();
        if (viewportWidth < 20 || viewportHeight < 8)
        {
            return;
        }

        var builder = new DL.DisplayListBuilder();
        builder.PushClip(new DL.ClipPush(0, 0, viewportWidth, viewportHeight));

        // Opaque backdrop so the transcript cannot be read behind the credential prompt (and so
        // a screen recording of the session shows nothing but the modal).
        builder.DrawRect(new DL.Rect(0, 0, viewportWidth, viewportHeight, new DL.Rgb24(0, 0, 0)));

        var boxWidth = Math.Min(80, viewportWidth - 4);
        var bodyLines = BuildBodyLines(field, currentValue, boxWidth - 4);
        var boxHeight = Math.Min(viewportHeight - 2, bodyLines.Count + 4);
        var boxX = (viewportWidth - boxWidth) / 2;
        var boxY = Math.Max(1, (viewportHeight - boxHeight) / 3);

        var background = new DL.Rgb24(30, 30, 40);
        var foreground = new DL.Rgb24(235, 235, 240);
        var dim = new DL.Rgb24(150, 150, 165);

        builder.PushClip(new DL.ClipPush(boxX, boxY, boxWidth, boxHeight));
        builder.DrawRect(new DL.Rect(boxX, boxY, boxWidth, boxHeight, background));
        builder.DrawBorder(new DL.Border(boxX, boxY, boxWidth, boxHeight, "double", new DL.Rgb24(120, 160, 220)));

        const string title = " Provider sign-in ";
        builder.DrawText(new DL.TextRun(
            boxX + Math.Max(1, (boxWidth - title.Length) / 2), boxY, title, foreground, background, DL.CellAttrFlags.Bold));

        var row = boxY + 1;
        var lastRow = boxY + boxHeight - 2;
        // Show the tail of the body when it does not all fit, so the active prompt stays visible.
        var first = Math.Max(0, bodyLines.Count - (lastRow - row + 1));
        for (var i = first; i < bodyLines.Count && row <= lastRow; i++, row++)
        {
            var (text, emphasized) = bodyLines[i];
            builder.DrawText(new DL.TextRun(
                boxX + 2, row, Truncate(text, boxWidth - 4), emphasized ? foreground : dim, background,
                emphasized ? DL.CellAttrFlags.Bold : DL.CellAttrFlags.None));
        }

        const string hints = "Enter confirm  Esc cancel";
        builder.DrawText(new DL.TextRun(
            boxX + Math.Max(1, (boxWidth - hints.Length) / 2), boxY + boxHeight - 1, hints, dim, background, DL.CellAttrFlags.None));

        builder.Pop();
        builder.Pop();

        await _renderAsync(builder.Build()).ConfigureAwait(false);
    }

    private List<(string Text, bool Emphasized)> BuildBodyLines(AuthFieldSpec? field, string currentValue, int width)
    {
        var lines = new List<(string, bool)>();

        foreach (var message in _messages)
        {
            foreach (var wrapped in Wrap(message, width))
            {
                lines.Add((wrapped, false));
            }
        }

        if (field != null)
        {
            lines.Add((string.Empty, false));
            if (!string.IsNullOrEmpty(field.Hint))
            {
                foreach (var wrapped in Wrap(field.Hint, width))
                {
                    lines.Add((wrapped, false));
                }
            }

            // Secret fields echo one asterisk per character - enough feedback to notice a typo's
            // length, and nothing more. Non-secret fields (an account label) echo normally.
            var available = Math.Max(0, width - field.Label.Length - 3);
            var shown = field.IsSecret
                ? new string('*', Math.Min(currentValue.Length, available))
                : Truncate(currentValue, available);
            lines.Add(($"{field.Label}: {shown}", true));
        }

        return lines;
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        if (width <= 0 || text.Length <= width)
        {
            yield return text;
            yield break;
        }

        for (var index = 0; index < text.Length; index += width)
        {
            yield return text.Substring(index, Math.Min(width, text.Length - index));
        }
    }

    private static string Truncate(string text, int max)
        => max <= 1 ? string.Empty : text.Length <= max ? text : text[..(max - 1)] + "…";
}
