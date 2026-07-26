using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Andy.Cli.Services.Formatting;

namespace Andy.Cli.Tests.Services.Formatting;

/// <summary>
/// A scripted <see cref="IFormatterProcessRunner"/>. Formatter behaviour is expressed as a callback
/// per command, which is what makes "the formatter deleted the file", "the formatter timed out",
/// and "the formatter exited 2 with stderr" deterministic tests rather than shell scripts.
/// </summary>
internal sealed class FakeFormatterProcessRunner : IFormatterProcessRunner
{
    private readonly Dictionary<string, Func<FormatterProcessRequest, FormatterProcessResult>> _behaviours =
        new(StringComparer.Ordinal);

    private Func<FormatterProcessRequest, FormatterProcessResult>? _fallback;

    /// <summary>Every request the runner received, in order. Used to assert deterministic ordering.</summary>
    public List<FormatterProcessRequest> Invocations { get; } = new();

    public FakeFormatterProcessRunner OnCommand(
        string command, Func<FormatterProcessRequest, FormatterProcessResult> behaviour)
    {
        _behaviours[command] = behaviour;
        return this;
    }

    public FakeFormatterProcessRunner Fallback(Func<FormatterProcessRequest, FormatterProcessResult> behaviour)
    {
        _fallback = behaviour;
        return this;
    }

    public Task<FormatterProcessResult> RunAsync(
        FormatterProcessRequest request, CancellationToken cancellationToken)
    {
        Invocations.Add(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (_behaviours.TryGetValue(request.Command, out var behaviour))
        {
            return Task.FromResult(behaviour(request));
        }

        return Task.FromResult(_fallback?.Invoke(request) ?? Success());
    }

    public static FormatterProcessResult Success(string stdout = "", string stderr = "")
        => new(Started: true, ExitCode: 0, stdout, stderr, TimedOut: false, Cancelled: false, StartFailure: null);

    public static FormatterProcessResult Failure(int exitCode, string stderr)
        => new(Started: true, ExitCode: exitCode, string.Empty, stderr, TimedOut: false, Cancelled: false, StartFailure: null);

    public static FormatterProcessResult TimedOut(string stderr = "")
        => new(Started: true, ExitCode: -1, string.Empty, stderr, TimedOut: true, Cancelled: false, StartFailure: null);

    public static FormatterProcessResult Cancelled(string stderr = "")
        => new(Started: true, ExitCode: -1, string.Empty, stderr, TimedOut: false, Cancelled: true, StartFailure: null);

    public static FormatterProcessResult NotStarted(string reason)
        => FormatterProcessResult.NotStarted(reason);
}

/// <summary>A gate that records what it was asked and answers from a fixed policy.</summary>
internal sealed class RecordingFormatterPermissionGate : IFormatterPermissionGate
{
    private readonly Func<FormatterCommandRequest, FormatterPermissionVerdict> _policy;

    public RecordingFormatterPermissionGate(Func<FormatterCommandRequest, FormatterPermissionVerdict> policy)
        => _policy = policy;

    public List<FormatterCommandRequest> Requests { get; } = new();

    public Task<FormatterPermissionVerdict> CheckAsync(
        FormatterCommandRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_policy(request));
    }
}
