using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Andy.Cli.Headless;
using Andy.Cli.HeadlessConfig;
using Andy.Cli.Services.Prompts;
using Andy.Model.Llm;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.OneShot;

// rivoli-ai/andy-cli#279: lightweight one-shot prompt execution.
//
//     andy-cli run "explain this repository"
//     git diff | andy-cli run "review this diff"
//
// This is a low-friction FRONT END over the existing headless runtime, not a
// second agent host: it synthesizes a HeadlessRunConfig and calls the same
// HeadlessAgentRunner the config-driven contract uses. Cancellation, the
// wall-clock timeout, permission evaluation, digesting/redaction, the tool-usage
// audit, the NDJSON event schema, and the HeadlessExitCode values therefore come
// from one implementation and cannot drift between the two entry points.
//
// Two things differ from `run --headless --config <path>`, both deliberate:
//
//   1. Output. By default the final answer is printed to stdout as plain text
//      and event narration goes to stderr; `--json` swaps stdout back to the
//      verbatim NDJSON stream for machine consumers. `--output <path>` keeps the
//      config contract's durable file.
//   2. Permissions. With no `--allow-tool`, config.permissions is left null,
//      which is exactly the headless fail-closed profile: read-only built-ins
//      (read_file, list_directory, search_text, git_diff, ...) are auto-allowed
//      and every mutating built-in plus execute_command is DENIED. Headless
//      wires the permission engine with no interactive broker, so a denial is
//      recorded and the loop continues - the run can never block waiting for an
//      approval on redirected input.
public static class OneShotRunner
{
    // Slug recorded on the event stream so a consumer can tell one-shot runs
    // apart from container-launched ones. Matches the headless slug pattern.
    public const string AgentSlug = "one-shot";

    public static async Task<HeadlessExitCode> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        ILoggerFactory loggerFactory,
        TextReader? stdin = null,
        OneShotModelResolver? modelResolver = null,
        ILlmProvider? llmProviderOverride = null,
        CancellationToken ct = default)
    {
        var safeStdout = new BrokenPipeTolerantWriter(stdout);
        var safeStderr = new BrokenPipeTolerantWriter(stderr);

        var parsed = OneShotArgParser.Parse(args);
        if (parsed.Error is not null)
        {
            safeStderr.WriteLine(parsed.Error);
            safeStderr.WriteLine();
            safeStderr.WriteLine(OneShotArgParser.Usage);
            return HeadlessExitCode.ConfigError;
        }

        string stdinText;
        try
        {
            stdinText = parsed.NoStdin || stdin is null
                ? string.Empty
                : await stdin.ReadToEndAsync(ct);
        }
        catch (OperationCanceledException)
        {
            safeStderr.WriteLine("andy-cli run: cancelled while reading stdin.");
            return HeadlessExitCode.Cancelled;
        }
        catch (IOException ex)
        {
            safeStderr.WriteLine($"andy-cli run: failed to read stdin: {ex.Message}");
            return HeadlessExitCode.ConfigError;
        }

        var normalizedStdin = OneShotPrompt.NormalizeStdin(stdinText, out var truncated);
        if (truncated)
        {
            safeStderr.WriteLine(
                $"andy-cli run: piped stdin exceeded {OneShotPrompt.MaxStdinChars} characters and was truncated.");
        }

        var prompt = OneShotPrompt.Compose(OneShotPrompt.JoinWords(parsed.PromptWords), normalizedStdin);
        if (prompt.Length == 0)
        {
            safeStderr.WriteLine(
                "andy-cli run: no prompt. Provide prompt text as arguments, pipe it on stdin, or both.");
            safeStderr.WriteLine();
            safeStderr.WriteLine(OneShotArgParser.Usage);
            return HeadlessExitCode.ConfigError;
        }

        string workingDirectory;
        try
        {
            workingDirectory = Path.GetFullPath(parsed.Cwd ?? Directory.GetCurrentDirectory());
        }
        catch (Exception ex)
        {
            safeStderr.WriteLine($"andy-cli run: invalid `--cwd` value: {ex.Message}");
            return HeadlessExitCode.ConfigError;
        }

        if (!Directory.Exists(workingDirectory))
        {
            safeStderr.WriteLine($"andy-cli run: `--cwd` directory does not exist: {workingDirectory}");
            return HeadlessExitCode.ConfigError;
        }

        var resolver = modelResolver ?? OneShotModelSelection.ResolveFromEnvironment;
        var selection = resolver(parsed.Provider, parsed.Model);
        if (selection.Error is not null || selection.Provider is null || selection.Model is null)
        {
            safeStderr.WriteLine($"andy-cli run: {selection.Error ?? "could not resolve a provider and model."}");
            return HeadlessExitCode.ConfigError;
        }

        // The final answer always lands in a file because HeadlessAgentRunner's
        // publication step is what enforces output-format validation and required
        // actions. When the caller did not ask for a durable copy we use a private
        // temp file and delete it after printing.
        var ownsOutputFile = parsed.OutputFile is null;
        var outputPath = parsed.OutputFile
            ?? Path.Combine(Path.GetTempPath(), $"andy-oneshot-{Guid.NewGuid():N}.txt");

        var config = BuildConfig(parsed, prompt, workingDirectory, selection, outputPath);

        TextWriter eventStream = parsed.Ndjson
            ? safeStdout
            : new OneShotTextEventWriter(safeStderr);

        HeadlessExitCode code;
        try
        {
            code = await HeadlessAgentRunner.ExecuteAsync(
                config,
                eventStream: eventStream,
                stderr: safeStderr,
                loggerFactory: loggerFactory,
                llmProviderOverride: llmProviderOverride,
                ct: ct,
                currentBranchResolver: null,
                kickoffMessage: prompt);
        }
        finally
        {
            if (eventStream is OneShotTextEventWriter textWriter)
            {
                textWriter.Dispose();
            }
        }

        if (!parsed.Ndjson && code == HeadlessExitCode.Success)
        {
            WriteAnswer(outputPath, safeStdout);
        }

        if (ownsOutputFile)
        {
            TryDelete(outputPath);
        }

        safeStdout.Flush();
        safeStderr.Flush();
        return code;
    }

    internal static HeadlessRunConfig BuildConfig(
        OneShotArgs parsed,
        string prompt,
        string workingDirectory,
        OneShotModelResolution selection,
        string outputPath) => new()
        {
            SchemaVersion = 1,
            RunId = Guid.NewGuid(),
            Agent = new HeadlessAgent
            {
                Slug = AgentSlug,
                Instructions = BuildInstructions(workingDirectory, parsed.AllowedTools),
            },
            Model = new HeadlessModel { Provider = selection.Provider!, Id = selection.Model! },
            Tools = Array.Empty<HeadlessTool>(),
            Workspace = new HeadlessWorkspace { Root = workingDirectory },
            Output = new HeadlessOutput { File = outputPath, Stream = "stdout" },
            // Fail-closed by default: a null permissions block is exactly the
            // headless read-only profile. Only an explicit --allow-tool relaxes it.
            Permissions = parsed.AllowedTools.Count > 0
                ? new HeadlessPermissions { AllowedTools = parsed.AllowedTools }
                : null,
            Limits = new HeadlessLimits
            {
                MaxIterations = parsed.MaxIterations ?? OneShotArgParser.DefaultMaxIterations,
                TimeoutSeconds = parsed.TimeoutSeconds ?? OneShotArgParser.DefaultTimeoutSeconds,
            },
        };

    // The one-shot system prompt is the shared CLI prompt pipeline plus an
    // explicit statement of the non-interactive contract, so the model does not
    // waste turns asking a question nobody can answer or retrying a tool the
    // fail-closed profile will keep denying.
    internal static string BuildInstructions(string workingDirectory, IReadOnlyList<string> allowedTools)
    {
        var permissionNote = allowedTools.Count == 0
            ? "No mutating tools are permitted. Read-only tools (read_file, list_directory, "
                + "search_text, git_diff and similar) are available; every write, delete, move, "
                + "copy and execute_command call will be denied. Do not retry a denied tool."
            : "Permitted mutating tools for this run: " + string.Join(", ", allowedTools)
                + ". Every other mutating tool and any tool not listed will be denied. "
                + "Do not retry a denied tool.";

        return new SystemPromptBuilder()
            .WithCoreMandates()
            .WithResponseFormatting()
            .WithWorkflowGuidelines()
            .WithEnvironment(
                platform: RuntimeInformation.OSDescription,
                workingDirectory: workingDirectory,
                currentDate: DateTime.Now,
                timeZone: TimeZoneInfo.Local)
            .WithCustomInstructions(
                "## One-shot run\n\n"
                + "You are running non-interactively for a single prompt. There is no user to "
                + "answer follow-up questions and no approval prompt: make reasonable assumptions "
                + "and finish in one pass. Your final message is the entire answer the user sees, "
                + "so make it self-contained.\n\n"
                + permissionNote)
            .Build();
    }

    private static void WriteAnswer(string outputPath, TextWriter stdout)
    {
        string answer;
        try
        {
            answer = File.ReadAllText(outputPath, Encoding.UTF8);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        if (answer.Length == 0)
        {
            return;
        }

        stdout.WriteLine(answer.TrimEnd('\r', '\n'));
        stdout.Flush();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup of our own temp file.
        }
    }
}
