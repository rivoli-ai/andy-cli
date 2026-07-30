using System;
using System.IO;
using System.Threading.Tasks;

namespace Andy.Cli.Lsp.Protocol;

/// <summary>
/// The byte pipe to a language server, plus enough process detail to explain what went wrong.
///
/// The abstraction exists so the protocol stack can be exercised end to end against a deterministic
/// in-repo server over real streams, without depending on any language-server binary being
/// installed. Production uses <see cref="StdioLspTransport"/>.
/// </summary>
public interface ILspTransport : IAsyncDisposable
{
    /// <summary>Stream the client writes requests to (the server's stdin).</summary>
    Stream Input { get; }

    /// <summary>Stream the client reads responses from (the server's stdout).</summary>
    Stream Output { get; }

    /// <summary>Whether the server has gone away.</summary>
    bool HasExited { get; }

    /// <summary>Exit code once the server has exited, when one is available.</summary>
    int? ExitCode { get; }

    /// <summary>Human-readable description used in status output and error messages.</summary>
    string Description { get; }

    /// <summary>
    /// The last few lines the server wrote to stderr. This is the single most useful thing to show
    /// a user whose server refuses to start ("command not found", "missing SDK", a stack trace).
    /// </summary>
    string StandardErrorTail { get; }

    /// <summary>Forcibly end the server. Must be safe to call repeatedly and after exit.</summary>
    void Terminate();
}
