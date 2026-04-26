using System.Diagnostics;
using System.IO;
using System.Text;
using Andy.Cli.HeadlessConfig;
using Andy.Tools.Core;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Headless.Tools;

// Adapter exposing a HeadlessTool with transport=cli as an Andy.Tools ITool.
//
// The headless config carries a fixed `command` prefix (e.g. ["andy-issues-cli",
// "search"]); the LLM supplies the trailing arguments at call time as a
// JSON `args` array of strings. We pass the entire argv via
// ProcessStartInfo.ArgumentList so .NET handles per-arg quoting — no shell,
// no string concatenation, no expansion. This mirrors the andy-containers
// hardening pattern (rivoli-ai/andy-containers#139, #140) where shell
// metacharacters in untrusted input can no longer leak into the spawned
// process.
//
// Reject-list at the boundary: each LLM-supplied arg must be non-null and
// must not contain a NUL byte. Everything else (including spaces, quotes,
// backticks) flows through verbatim — argv is by definition not parsed
// further by the receiving process.
public sealed class CliSubprocessTool : ITool
{
    private readonly HeadlessTool _config;
    private readonly ILogger<CliSubprocessTool>? _logger;

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
        return errors;
    }

    public bool CanExecuteWithPermissions(ToolPermissions permissions) => true;

    public async Task<ToolResult> ExecuteAsync(
        Dictionary<string, object?> parameters,
        ToolExecutionContext context)
    {
        var ct = context.CancellationToken;
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

    public Task DisposeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

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

    private static ToolMetadata BuildMetadata(HeadlessTool config) => new()
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
