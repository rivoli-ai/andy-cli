using System.IO;

namespace Andy.Cli.HeadlessConfig;

// Entry point for `andy-cli run --headless --config <path>` (AQ2,
// rivoli-ai/andy-cli#47). Returns a HeadlessExitCode so the caller (wired
// in Program.Main) can hand it straight to Environment.Exit.
//
// AQ2's job is the *scaffolding* — argument parsing, config loading, exit
// semantics. The actual agent loop (AQ3+) is not implemented yet; a valid
// config today is acknowledged with a diagnostic and Success, which lets
// the Epic AP configurator exercise the full path end-to-end before AQ3
// lands.
public static class HeadlessRunner
{
    public static async Task<HeadlessExitCode> RunAsync(
        string[] args,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        CancellationToken ct = default)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;

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

            // AQ3+ wires the real agent loop here. Until it lands, acknowledge the
            // load so AP's configurator can smoke the full path.
            var config = load.Config!;
            stdout.WriteLine(
                $"andy-cli run --headless: loaded config for run {config.RunId} "
                    + $"(agent={config.Agent.Slug}, model={config.Model.Provider}:{config.Model.Id}, "
                    + $"tools={config.Tools.Count}, limits.max_iterations={config.Limits.MaxIterations}, "
                    + $"limits.timeout_seconds={config.Limits.TimeoutSeconds}). "
                    + "Agent loop not yet implemented (AQ3); exiting 0.");
            return HeadlessExitCode.Success;
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
