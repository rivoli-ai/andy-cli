using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Andy.Cli.HeadlessConfig;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Headless.Tools;

// Adapter exposing a HeadlessTool with transport=cli as an Andy.Tools ITool.
//
// The headless config carries a fixed `command` prefix (e.g. ["andy-issues-cli",
// "search"]); the LLM supplies the trailing arguments at call time.
//
// Two input modes (design brief: argv vs JSON bridging):
//
// **argv mode** (default, InputMode == null or "argv"):
//   The LLM supplies an `args` string array. The runtime prepends _config.Command
//   and passes the entire argv via ProcessStartInfo.ArgumentList so .NET handles
//   per-arg quoting — no shell, no string concatenation, no expansion.
//
// **json mode** (InputMode == "json"):
//   The LLM supplies an `arguments` object. The runtime serializes it as JSON and
//   writes it to the subprocess's stdin, then closes stdin (EOF). The subprocess
//   reads the JSON from stdin. Stdout/stderr capture and exit-code semantics are
//   unchanged.
//
// Reject-list at the boundary: each LLM-supplied arg must be non-null and
// must not contain a NUL byte. Everything else (including spaces, quotes,
// backticks) flows through verbatim — argv is by definition not parsed
// further by the receiving process.
public sealed class CliSubprocessTool : ITool
{
    private readonly HeadlessTool _config;
    private readonly ILogger<CliSubprocessTool>? _logger;

    // Max JSON payload size (1 MB). Prevents unbounded memory allocation
    // if the LLM produces an enormous parameter object.
    private const int MaxJsonPayloadBytes = 1_048_576;

    public CliSubprocessTool(HeadlessTool config, ILogger<CliSubprocessTool>? logger = null)
    {
        _config = config;
        _logger = logger;
        Metadata = BuildMetadata(config);
    }

    public ToolMetadata Metadata { get; }

    public Task InitializeAsync(
        Dictionary<string, object?>? configuration = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public IList<string> ValidateParameters(Dictionary<string, object?>? parameters)
    {
        var errors = new List<string>();
        if (parameters is null) return errors;

        if (IsJsonMode)
        {
            // JSON mode: validate `arguments` as a non-null object.
            if (parameters.TryGetValue("arguments", out var argsValue) && argsValue is not null)
            {
                if (argsValue is string)
                {
                    errors.Add("arguments must be a JSON object, not a string.");
                }
                else if (argsValue is IDictionary)
                {
                    // Dictionary is the expected type — accepted silently.
                }
                else if (argsValue is System.Collections.IEnumerable)
                {
                    errors.Add("arguments must be a JSON object, not an array.");
                }
            }
        }
        else
        {
            // argv mode (default): validate `args` as an array of strings.
            if (parameters.TryGetValue("args", out var argsValue) && argsValue is not null)
            {
                if (argsValue is not System.Collections.IEnumerable enumerable || argsValue is string)
                {
                    errors.Add("args must be an array of strings.");
                }
                else
                {
                    foreach (var item in enumerable)
                    {
                        if (item is not string s)
                        {
                            errors.Add($"args must contain strings only; got {item?.GetType().Name ?? "null"}.");
                            break;
                        }
                        if (s.Contains('\0'))
                        {
                            errors.Add("args must not contain NUL bytes.");
                            break;
                        }
                    }
                }
            }
        }

        return errors;
    }

    public bool CanExecuteWithPermissions(ToolPermissions permissions) => true;

    public async Task<ToolResult> ExecuteAsync(
        Dictionary<string, object?> parameters,
        ToolExecutionContext context)
    {
        var ct = context.CancellationToken;

        if (IsJsonMode)
        {
            return await ExecuteJsonModeAsync(parameters, ct);
        }

        return await ExecuteArgvModeAsync(parameters, ct);
    }

    public Task DisposeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    // ─── JSON mode implementation ────────────────────────────────────────

    private async Task<ToolResult> ExecuteJsonModeAsync(
        Dictionary<string, object?> parameters,
        CancellationToken ct)
    {
        // Extract the arguments object. When absent or null, send an empty JSON object.
        object? argsObj = null;
        if (parameters.TryGetValue("arguments", out var val) && val is not null)
        {
            argsObj = val;
        }

        // Serialize to JSON bytes. Preserve the LLM's property names verbatim —
        // do NOT apply SnakeCaseLower or any naming policy.
        byte[] jsonBytes;
        try
        {
            var payload = argsObj ?? new Dictionary<string, object?>();
            jsonBytes = JsonSerializer.SerializeToUtf8Bytes(
                payload,
                new JsonSerializerOptions { PropertyNamingPolicy = null });
        }
        catch (Exception ex)
        {
            return ToolResult.Failure($"Failed to serialize arguments to JSON: {ex.Message}");
        }

        if (jsonBytes.Length > MaxJsonPayloadBytes)
        {
            return ToolResult.Failure(
                $"JSON payload exceeds maximum size ({jsonBytes.Length:N0} bytes > {MaxJsonPayloadBytes:N0} limit).");
        }

        // Build ProcessStartInfo: the binary (or command prefix) is the entry
        // point. No extra argv args in JSON mode — the structured input goes
        // via stdin.
        var argv = MaterializeArgv(null); // prefix only, no args appended
        if (argv.Count == 0)
        {
            return ToolResult.Failure("CLI tool has no command configured.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = argv[0],
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        for (var i = 1; i < argv.Count; i++)
        {
            psi.ArgumentList.Add(argv[i]);
        }

        _logger?.LogInformation(
            "CliSubprocessTool[{Tool}]: spawning {Binary} in JSON mode, payload {Size} bytes",
            _config.Name, argv[0], jsonBytes.Length);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return ToolResult.Failure($"Failed to spawn '{argv[0]}': {ex.Message}");
        }

        // Write JSON payload to stdin and close to send EOF.
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(jsonBytes, ct);
            process.StandardInput.Close();
        }
        catch (Exception ex)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return ToolResult.Failure($"Failed to write JSON to stdin: {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw;
        }

        var exitCode = process.ExitCode;
        var stdoutText = stdout.ToString();
        var stderrText = stderr.ToString();

        if (exitCode != 0)
        {
            return ToolResult.Failure(
                $"{_config.Name} exited {exitCode}. stderr: {Truncate(stderrText, 4000)}");
        }

        return ToolResult.Success(stdoutText);
    }

    // ─── argv mode implementation (original) ────────────────────────────

    private async Task<ToolResult> ExecuteArgvModeAsync(
        Dictionary<string, object?> parameters,
        CancellationToken ct)
    {
        var argv = MaterializeArgv(parameters);
        if (argv.Count == 0)
        {
            return ToolResult.Failure("CLI tool has no command configured.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = argv[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        for (var i = 1; i < argv.Count; i++)
        {
            psi.ArgumentList.Add(argv[i]);
        }

        _logger?.LogInformation(
            "CliSubprocessTool[{Tool}]: spawning {Binary} with {ArgCount} extra args",
            _config.Name, argv[0], argv.Count - 1);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return ToolResult.Failure($"Failed to spawn '{argv[0]}': {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw;
        }

        var exitCode = process.ExitCode;
        var stdoutText = stdout.ToString();
        var stderrText = stderr.ToString();

        if (exitCode != 0)
        {
            return ToolResult.Failure(
                $"{_config.Name} exited {exitCode}. stderr: {Truncate(stderrText, 4000)}");
        }

        return ToolResult.Success(stdoutText);
    }

    // ─── Shared helpers ──────────────────────────────────────────────────

    private bool IsJsonMode => string.Equals(_config.InputMode, "json", StringComparison.OrdinalIgnoreCase);

    private List<string> MaterializeArgv(IReadOnlyDictionary<string, object?>? parameters)
    {
        // Fixed prefix from config: ["binary", "subcommand", ...].
        // If `command` is unset, fall back to just `binary` so the schema's
        // CLI variant (which requires `binary` and treats `command` as
        // optional) stays honoured.
        var argv = new List<string>();
        if (_config.Command is { Count: > 0 } cmd)
        {
            argv.AddRange(cmd);
        }
        else if (!string.IsNullOrEmpty(_config.Binary))
        {
            argv.Add(_config.Binary);
        }

        if (parameters is null) return argv;
        if (!parameters.TryGetValue("args", out var argsValue) || argsValue is null) return argv;

        // Already validated by ValidateParameters; defensive cast here.
        if (argsValue is System.Collections.IEnumerable enumerable && argsValue is not string)
        {
            foreach (var item in enumerable)
            {
                if (item is string s) argv.Add(s);
            }
        }
        return argv;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

    private static ToolMetadata BuildMetadata(HeadlessTool config)
    {
        var isJson = string.Equals(config.InputMode, "json", StringComparison.OrdinalIgnoreCase);

        if (isJson)
        {
            return new()
            {
                Id = config.Name,
                Name = config.Name,
                Description =
                    $"CLI subprocess tool (JSON mode). Invokes the configured binary and writes the LLM's `arguments` object as JSON to the subprocess's stdin.",
                Version = "1.0.0",
                Category = ToolCategory.System,
                Parameters =
                [
                    new ToolParameter
                    {
                        Name = "arguments",
                        Description = "JSON object sent to the subprocess via stdin. The subprocess reads a JSON object from stdin, processes it, and writes the result to stdout.",
                        Type = "object",
                        Required = false,
                    }
                ],
            };
        }

        // argv mode (default)
        return new()
        {
            Id = config.Name,
            Name = config.Name,
            Description =
                $"CLI subprocess tool. Invokes the configured binary with a fixed prefix and the LLM-supplied `args` array appended.",
            Version = "1.0.0",
            Category = ToolCategory.System,
            Parameters =
            [
                new ToolParameter
                {
                    Name = "args",
                    Description = "Arguments to append to the configured command prefix (each becomes a separate argv entry; no shell expansion).",
                    Type = "array",
                    Required = false,
                }
            ],
        };
    }
}
