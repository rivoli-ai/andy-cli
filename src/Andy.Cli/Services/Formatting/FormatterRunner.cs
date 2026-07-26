using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services.Formatting;

/// <summary>
/// Applies the matching formatters to ONE file that Andy just mutated, in the catalog's
/// deterministic order.
///
/// Scope is deliberately narrow (acceptance criterion "only changed files are formatted unless the
/// user explicitly requests a broader operation"): this class formats the single path it is given
/// and never walks a directory or a glob. A broader operation would be a separate, explicit command.
/// </summary>
public sealed class FormatterRunner
{
    private readonly FormatterCatalog _catalog;
    private readonly IFormatterProcessRunner _processRunner;
    private readonly IFormatterPermissionGate _permissionGate;
    private readonly ILogger? _logger;

    public FormatterRunner(
        FormatterCatalog catalog,
        IFormatterProcessRunner processRunner,
        IFormatterPermissionGate permissionGate,
        ILogger? logger = null)
    {
        _catalog = catalog ?? FormatterCatalog.Empty;
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _permissionGate = permissionGate ?? UngatedFormatterPermission.Instance;
        _logger = logger;
    }

    /// <summary>True when at least one formatter would run for this path (no process is started).</summary>
    public bool HasFormattersFor(string filePath) => _catalog.SelectFor(filePath).Count > 0;

    /// <summary>
    /// Run every matching formatter against <paramref name="absolutePath"/>. Returns one result per
    /// formatter that was considered runnable, in execution order.
    ///
    /// Cancellation is honoured between formatters and inside each process; a cancelled run is
    /// reported as a failure rather than swallowed, because the file is left unformatted either way.
    /// </summary>
    public async Task<IReadOnlyList<FormatterRunResult>> RunAsync(
        string absolutePath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var matches = _catalog.SelectFor(absolutePath);
        if (matches.Count == 0)
        {
            return Array.Empty<FormatterRunResult>();
        }

        var results = new List<FormatterRunResult>(matches.Count);
        var identity = FormatterTargetIdentity.Capture(absolutePath);

        foreach (var match in matches)
        {
            var result = await RunOneAsync(match, absolutePath, workingDirectory, identity, cancellationToken)
                .ConfigureAwait(false);
            results.Add(result);

            if (result.IsFatalToPipeline)
            {
                _logger?.LogWarning(
                    "[FORMATTER] Stopping after {Formatter} for {Path}: {Outcome}",
                    result.FormatterName, absolutePath, result.Outcome);
                break;
            }
        }

        return results;
    }

    private async Task<FormatterRunResult> RunOneAsync(
        FormatterMatch match,
        string absolutePath,
        string workingDirectory,
        FormatterTargetIdentity identity,
        CancellationToken cancellationToken)
    {
        var definition = match.Definition;
        var stopwatch = Stopwatch.StartNew();

        if (cancellationToken.IsCancellationRequested)
        {
            return new FormatterRunResult(definition.Name, FormatterOutcome.Cancelled, null,
                "cancelled before the formatter was started", stopwatch.Elapsed);
        }

        var effectiveWorkingDirectory = ResolveWorkingDirectory(definition, workingDirectory, absolutePath);
        var commandLine = definition.DescribeCommandLine(absolutePath);

        // CONSENT FIRST: nothing is launched until the gate says yes. See ToolGateFormatterPermission
        // for how normal permission rules - and, once #278 lands, Plan mode - deny here.
        FormatterPermissionVerdict verdict;
        try
        {
            verdict = await _permissionGate.CheckAsync(
                new FormatterCommandRequest(definition.Name, commandLine, effectiveWorkingDirectory, absolutePath),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new FormatterRunResult(definition.Name, FormatterOutcome.Cancelled, null,
                "cancelled while waiting for permission", stopwatch.Elapsed);
        }

        if (!verdict.Allowed)
        {
            return new FormatterRunResult(definition.Name, FormatterOutcome.PermissionDenied, null,
                FormatterDiagnostics.Redact(verdict.Reason), stopwatch.Elapsed);
        }

        var contentBefore = TryReadAllText(absolutePath);

        FormatterProcessResult processResult;
        try
        {
            processResult = await _processRunner.RunAsync(
                new FormatterProcessRequest(
                    definition.Command,
                    definition.ResolveArguments(absolutePath),
                    effectiveWorkingDirectory,
                    definition.Timeout),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new FormatterRunResult(definition.Name, FormatterOutcome.Cancelled, null,
                "cancelled while the formatter was running", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            return new FormatterRunResult(definition.Name, FormatterOutcome.NonZeroExit, null,
                FormatterDiagnostics.Redact(ex.Message), stopwatch.Elapsed);
        }

        var diagnostics = FormatterDiagnostics.Summarize(processResult.StandardError, processResult.StandardOutput);

        if (!processResult.Started)
        {
            return new FormatterRunResult(definition.Name, FormatterOutcome.CommandNotFound, null,
                FormatterDiagnostics.Redact(processResult.StartFailure), stopwatch.Elapsed);
        }

        if (processResult.Cancelled)
        {
            return new FormatterRunResult(definition.Name, FormatterOutcome.Cancelled, null,
                diagnostics, stopwatch.Elapsed);
        }

        if (processResult.TimedOut)
        {
            return new FormatterRunResult(definition.Name, FormatterOutcome.TimedOut, null,
                Combine($"exceeded {definition.Timeout.TotalSeconds:0.#}s", diagnostics), stopwatch.Elapsed);
        }

        // The target check runs even on a clean exit: a zero exit code says nothing about whether
        // the file Andy wrote is still there.
        var breach = FormatterTargetGuard.Check(identity);
        if (breach is not null)
        {
            return new FormatterRunResult(definition.Name, breach.Value.Outcome, processResult.ExitCode,
                Combine(breach.Value.Reason, diagnostics), stopwatch.Elapsed);
        }

        if (processResult.ExitCode != 0)
        {
            return new FormatterRunResult(definition.Name, FormatterOutcome.NonZeroExit, processResult.ExitCode,
                diagnostics, stopwatch.Elapsed);
        }

        var contentAfter = TryReadAllText(absolutePath);
        var changed = !string.Equals(contentBefore, contentAfter, StringComparison.Ordinal);
        return new FormatterRunResult(
            definition.Name,
            changed ? FormatterOutcome.Changed : FormatterOutcome.NoChange,
            processResult.ExitCode,
            string.Empty,
            stopwatch.Elapsed);
    }

    private static string Combine(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(second))
        {
            return first ?? string.Empty;
        }

        return string.IsNullOrWhiteSpace(first) ? second! : first + "\n" + second;
    }

    private static string ResolveWorkingDirectory(
        FormatterDefinition definition, string sessionWorkingDirectory, string absolutePath)
    {
        var fallback = string.IsNullOrWhiteSpace(sessionWorkingDirectory)
            ? Path.GetDirectoryName(absolutePath) ?? Directory.GetCurrentDirectory()
            : sessionWorkingDirectory;

        if (string.IsNullOrWhiteSpace(definition.WorkingDirectory))
        {
            return fallback;
        }

        try
        {
            var resolved = Path.IsPathRooted(definition.WorkingDirectory)
                ? definition.WorkingDirectory
                : Path.GetFullPath(Path.Combine(fallback, definition.WorkingDirectory));
            return Directory.Exists(resolved) ? resolved : fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
