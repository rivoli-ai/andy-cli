using System;

namespace Andy.Cli.Hosting;

/// <summary>
/// Parsed session-resume startup flags for interactive mode (issue #231):
/// "--resume &lt;session-id&gt;" resumes a specific saved session and "--continue"
/// resumes the most recently updated one. Both flags start with '-' so they
/// already route to <see cref="CliMode.Interactive"/> in <see cref="CliModeSelector"/>.
/// </summary>
public sealed record SessionResumeArgs
{
    /// <summary>Session id given to --resume, or null.</summary>
    public string? SessionId { get; init; }

    /// <summary>True when --continue was passed (resume the most recent session).</summary>
    public bool ContinueLatest { get; init; }

    /// <summary>Set when the flags were malformed (e.g. --resume without an id).</summary>
    public string? Error { get; init; }

    public bool RequestsResume => Error is null && (ContinueLatest || SessionId is not null);

    public static SessionResumeArgs Parse(string[] args)
    {
        string? sessionId = null;
        var continueLatest = false;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--continue" || args[i] == "-c")
            {
                continueLatest = true;
            }
            else if (args[i] == "--resume" || args[i] == "-r")
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                {
                    return new SessionResumeArgs
                    {
                        Error = "--resume requires a session id (see 'andy-cli sessions')."
                    };
                }
                sessionId = args[++i];
            }
        }

        if (sessionId is not null && continueLatest)
        {
            return new SessionResumeArgs
            {
                Error = "--resume and --continue cannot be combined."
            };
        }

        return new SessionResumeArgs { SessionId = sessionId, ContinueLatest = continueLatest };
    }
}
