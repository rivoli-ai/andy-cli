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

            return await HeadlessAgentRunner.ExecuteAsync(
                load.Config!,
                eventStream: stdout,
                stderr: stderr,
                loggerFactory: loggerFactory,
                ct: ct);
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

    private const string Usage =
        "Usage: andy-cli run --headless --config <path>\n"
        + "  --headless        Non-interactive execution driven entirely by the config file (required).\n"
        + "  --config <path>   Path to a headless-config.v1 JSON file (required).";

    private static ParsedArgs ParseArgs(string[] args)
    {
        // args[0] is guaranteed to be "run" by the dispatcher; parse the remainder.
        var remaining = args.Length > 0 && string.Equals(args[0], "run", StringComparison.Ordinal)
            ? args.AsSpan(1)
            : args.AsSpan();

        var headless = false;
        string? configPath = null;

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

        return new ParsedArgs { ConfigPath = configPath };
    }

    private readonly record struct ParsedArgs
    {
        public string? ConfigPath { get; init; }
        public string? Error { get; init; }

        public static ParsedArgs ErrorOnly(string message) => new() { Error = message };
    }
}
