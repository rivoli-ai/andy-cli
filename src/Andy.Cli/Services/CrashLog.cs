using System;
using System.IO;
using System.Text;

namespace Andy.Cli.Services;

/// <summary>
/// Best-effort crash logger. The interactive TUI only surfaces <c>ex.Message</c> to the feed,
/// so a NullReferenceException shows up as the opaque "Object reference not set to an instance
/// of an object" with no stack trace. This writes the full exception (message, stack trace, and
/// all inner exceptions) to <c>~/.andy/logs/crash.log</c> so a crash can actually be diagnosed.
/// </summary>
public static class CrashLog
{
    private static readonly object Gate = new();

    /// <summary>Absolute path to the crash log file.</summary>
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".andy", "logs", "crash.log");

    /// <summary>Appends the full details of <paramref name="ex"/> to the crash log. Never throws.</summary>
    public static void Write(string context, Exception ex)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("===== ").Append(DateTimeOffset.Now.ToString("u"))
              .Append(" [").Append(context).Append("] =====").AppendLine();
            sb.AppendLine(ex.ToString()); // message + stack trace + inner exceptions
            sb.AppendLine();

            lock (Gate)
            {
                var dir = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.AppendAllText(Path, sb.ToString());
            }
        }
        catch
        {
            // Crash logging must never itself crash the app.
        }
    }
}
