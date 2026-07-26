using System.IO;
using Andy.Cli.Headless;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.HeadlessConfig;

// Entry point for `andy-cli run --headless --config <path>`.
//
// AQ2 (rivoli-ai/andy-cli#47) introduced this as scaffolding — arg parsing,
// config loading, exit semantics — and stubbed the agent loop with an
// exit-0 diagnostic. AQ3 (rivoli-ai/andy-cli#44) replaces that stub with a
// real loop in Andy.Cli.Headless.HeadlessAgentRunner; this file remains
// the surface every error path funnels through to keep the
// HeadlessExitCode contract in one place.
public static class HeadlessRunner
{
    public static async Task<HeadlessExitCode> RunAsync(
        string[] args,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        ILoggerFactory? loggerFactory = null,
        CancellationToken ct = default)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;
        loggerFactory ??= LoggerFactory.Create(builder => builder.AddConsole(o =>
            o.LogToStandardErrorThreshold = LogLevel.Information));

        try
        {
            var parsed = ParseArgs(args);
            if (parsed.Error is not null)
            {
                stderr.WriteLine(parsed.Error);
                stderr.WriteLine();
                stderr.WriteLine(Usage);
                return HeadlessExitCode.ConfigError;
            }

            var load = await HeadlessConfigLoader.TryLoadAsync(parsed.ConfigPath!, ct);
            if (!load.IsSuccess)
            {
                stderr.WriteLine(load.Error);
                return HeadlessExitCode.ConfigError;
            }

            var config = load.Config!;

            // rivoli-ai/andy-cli#180: honor output.stream. 'stdout' (default) streams
            // the NDJSON events to standard output; 'fifo' redirects them to the named
            // FIFO at event_sink.path (guaranteed present by config validation). The
            // runtime opens that path for writing and streams every event to it.
            TextWriter eventStream = stdout;
            StreamWriter? fifoWriter = null;
            if (string.Equals(config.Output.Stream, "fifo", StringComparison.Ordinal))
            {
                var fifoPath = config.EventSink!.Path!;
                try
                {
                    fifoWriter = OpenFifoWriter(fifoPath);
                    eventStream = fifoWriter;
                }
                catch (Exception ex)
                {
                    stderr.WriteLine(
                        $"andy-cli run --headless: failed to open FIFO event sink '{fifoPath}': {ex.Message}");
                    return HeadlessExitCode.AgentFailure;
                }
            }

            try
            {
                return await HeadlessAgentRunner.ExecuteAsync(
                    config,
                    eventStream: eventStream,
                    stderr: stderr,
                    loggerFactory: loggerFactory,
                    llmProviderOverride: null,
                    ct: ct,
                    mode: parsed.Mode);
            }
            finally
            {
                fifoWriter?.Flush();
                fifoWriter?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            stderr.WriteLine("andy-cli run --headless: cancelled.");
            return HeadlessExitCode.Cancelled;
        }
        catch (Exception ex)
        {
            stderr.WriteLine(
                $"andy-cli run --headless: internal error: {ex.GetType().Name}: {ex.Message}");
            return HeadlessExitCode.InternalError;
        }
    }

    // Opens a writer over the named FIFO (or file) at <paramref name="path"/> for
    // the event stream. FileMode.Open requires the FIFO to already exist - the
    // container runtime creates it with mkfifo before launch. AutoFlush keeps each
    // NDJSON line visible to the reader as soon as it is written.
    private static StreamWriter OpenFifoWriter(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        return new StreamWriter(stream) { AutoFlush = true };
    }

    private const string Usage =
        "Usage: andy-cli run --headless --config <path> [--mode <build|plan>]\n"
        + "  --headless        Non-interactive execution driven entirely by the config file (required).\n"
        + "  --config <path>   Path to a headless-config.v1 JSON file (required).\n"
        + "  --mode <id>       Primary operating mode: build (default) or plan (read-only).\n"
        + "                    An unknown mode is rejected; the run never falls back to build.";

    // Internal for tests: the arg parser is the fail-closed boundary for `--mode`, so it is
    // exercised directly rather than only through a full headless run.
    internal static ParsedArgs ParseArgsForTest(string[] args) => ParseArgs(args);

    private static ParsedArgs ParseArgs(string[] args)
    {
        // args[0] is guaranteed to be "run" by the dispatcher; parse the remainder.
        var remaining = args.Length > 0 && string.Equals(args[0], "run", StringComparison.Ordinal)
            ? args.AsSpan(1)
            : args.AsSpan();

        var headless = false;
        string? configPath = null;
        var mode = Andy.Cli.Modes.AgentModeCatalog.DefaultMode;

        for (var i = 0; i < remaining.Length; i++)
        {
            var token = remaining[i];
            switch (token)
            {
                case "--headless":
                    headless = true;
                    break;
                case "--config":
                    if (i + 1 >= remaining.Length)
                    {
                        return ParsedArgs.ErrorOnly("`--config` requires a path argument.");
                    }
                    configPath = remaining[++i];
                    break;
                case "--mode":
                    if (i + 1 >= remaining.Length)
                    {
                        return ParsedArgs.ErrorOnly(
                            $"`--mode` requires a mode argument ({Andy.Cli.Modes.AgentModeCatalog.KnownIds}).");
                    }

                    // Fail closed (issue #278): an unrecognized mode is a hard config error, never a
                    // silent fallback to the mutation-capable default.
                    var requested = remaining[++i];
                    if (!Andy.Cli.Modes.AgentModeCatalog.TryParse(requested, out var parsedMode)
                        || parsedMode is null)
                    {
                        return ParsedArgs.ErrorOnly(
                            $"Unknown mode '{requested}'. Known modes: {Andy.Cli.Modes.AgentModeCatalog.KnownIds}.");
                    }

                    mode = parsedMode.Mode;
                    break;
                default:
                    return ParsedArgs.ErrorOnly($"Unknown argument: {token}");
            }
        }

        if (!headless)
        {
            return ParsedArgs.ErrorOnly(
                "`--headless` is required. Interactive `andy-cli run` without --headless is not supported.");
        }
        if (configPath is null)
        {
            return ParsedArgs.ErrorOnly("`--config <path>` is required.");
        }

        return new ParsedArgs { ConfigPath = configPath, Mode = mode };
    }

    internal readonly record struct ParsedArgs
    {
        public string? ConfigPath { get; init; }
        public string? Error { get; init; }

        /// <summary>The selected primary mode; Build unless `--mode plan` was supplied.</summary>
        public Andy.Cli.Modes.AgentMode Mode { get; init; }

        public static ParsedArgs ErrorOnly(string message) => new() { Error = message };
    }
}
