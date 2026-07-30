using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Auth;

/// <summary>
/// How the auth flows collect input and show progress. Implemented once for the console
/// (<see cref="ConsoleAuthPrompt"/>) and once for the TUI overlay, so the flow logic itself
/// never needs to know which front end it is running under.
/// </summary>
public interface IAuthPrompt
{
    /// <summary>
    /// Collects one field. Secret fields must be captured with echo suppressed. Returns null
    /// when the user cancels.
    /// </summary>
    Task<string?> PromptAsync(AuthFieldSpec field, CancellationToken cancellationToken);

    /// <summary>Shows a progress or instruction line. Never called with secret material.</summary>
    void Info(string message);

    /// <summary>Shows a warning line (for example the plaintext-fallback notice).</summary>
    void Warn(string message);

    /// <summary>
    /// Offers a URL to the user, opening a browser when one is available. Implementations must
    /// always print the URL too, so a headless session can copy it.
    /// </summary>
    void PresentUrl(string caption, string url);
}

/// <summary>
/// Console implementation used by <c>andy-cli auth ...</c>.
///
/// SECURITY: secret fields are read one key at a time with <c>Console.ReadKey(intercept: true)</c>
/// so the value is never echoed and never enters the shell's history. When stdin is redirected
/// (automation), the value is read as a single line instead - the caller is expected to pipe it
/// from a secret manager rather than pass it as an argument.
/// </summary>
public sealed class ConsoleAuthPrompt : IAuthPrompt
{
    private readonly TextWriterAdapter _out;
    private readonly bool _allowBrowser;

    public ConsoleAuthPrompt(System.IO.TextWriter? output = null, bool allowBrowser = true)
    {
        _out = new TextWriterAdapter(output ?? Console.Out);
        _allowBrowser = allowBrowser;
    }

    public Task<string?> PromptAsync(AuthFieldSpec field, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(field);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrEmpty(field.Hint))
        {
            _out.WriteLine("  " + field.Hint);
        }

        _out.Write(field.Label + (field.Required ? ": " : " (press Enter to skip): "));

        string? value;
        if (Console.IsInputRedirected || !field.IsSecret)
        {
            value = Console.ReadLine();
            if (field.IsSecret)
            {
                // Redirected stdin cannot be masked; make it obvious nothing was echoed by us.
                _out.WriteLine(Redaction.Mask);
            }
        }
        else
        {
            value = ReadMasked(cancellationToken);
            _out.WriteLine(string.Empty);
        }

        return Task.FromResult(value);
    }

    public void Info(string message) => _out.WriteLine(message);

    public void Warn(string message) => _out.WriteLine(message);

    public void PresentUrl(string caption, string url)
    {
        // Always print it: a headless or remote session has no browser to open.
        _out.WriteLine($"{caption}: {url}");
        if (_allowBrowser)
        {
            BrowserLauncher.TryOpen(url);
        }
    }

    private string? ReadMasked(CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    return builder.ToString();

                case ConsoleKey.Escape:
                    builder.Clear();
                    return null;

                case ConsoleKey.Backspace:
                    if (builder.Length > 0)
                    {
                        builder.Length--;
                        _out.Write("\b \b");
                    }

                    continue;
            }

            // Ctrl+C / Ctrl+D abandon the prompt without storing anything.
            if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key is ConsoleKey.C or ConsoleKey.D)
            {
                return null;
            }

            if (char.IsControl(key.KeyChar))
            {
                continue;
            }

            builder.Append(key.KeyChar);
            _out.Write("*");
        }
    }

    private sealed class TextWriterAdapter
    {
        private readonly System.IO.TextWriter _writer;

        public TextWriterAdapter(System.IO.TextWriter writer) => _writer = writer;

        public void Write(string text) => _writer.Write(text);

        public void WriteLine(string text) => _writer.WriteLine(text);
    }
}

/// <summary>
/// Opens a URL in the user's browser. Failure is never fatal: the URL has already been printed
/// by the time this is called, so a headless machine simply falls back to copy and paste.
/// </summary>
public static class BrowserLauncher
{
    public static bool TryOpen(string url)
    {
        // Only http(s) is ever handed to the shell, so a malformed provider URL cannot be
        // turned into a file:// or a custom-scheme command.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return Start("open", uri.AbsoluteUri);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var startInfo = new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true };
                using var process = Process.Start(startInfo);
                return process != null;
            }

            return Start("xdg-open", uri.AbsoluteUri);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool Start(string fileName, string argument)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        return process != null;
    }
}
