using System.Globalization;
using System.Linq;

namespace Andy.Cli.OneShot;

// rivoli-ai/andy-cli#279: parsed form of the lightweight one-shot invocation
//
//     andy-cli run [options] "<prompt>"
//     git diff | andy-cli run [options] "review this diff"
//
// The strict, config-driven contract (`andy-cli run --headless --config <path>`)
// is parsed elsewhere (HeadlessConfig.HeadlessRunner) and is untouched by this
// type: HeadlessRunner dispatches here only when `--headless` is absent.
public sealed record OneShotArgs
{
    // Positional tokens, in the order the shell handed them over. Joined with a
    // single space to form the positional half of the prompt.
    public IReadOnlyList<string> PromptWords { get; init; } = Array.Empty<string>();

    // --json / --ndjson: emit the headless NDJSON event stream on stdout instead
    // of concise human text.
    public bool Ndjson { get; init; }

    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? Cwd { get; init; }
    public int? TimeoutSeconds { get; init; }
    public int? MaxIterations { get; init; }

    // Tools relaxed from the fail-closed default profile for this run.
    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();

    // Optional durable copy of the final answer. When absent the answer is only
    // printed (text mode) or carried on the event stream (NDJSON mode).
    public string? OutputFile { get; init; }

    // Ignore redirected stdin even when it is present.
    public bool NoStdin { get; init; }

    // Non-null when parsing failed; the caller prints it followed by Usage and
    // returns ConfigError (exit 2).
    public string? Error { get; init; }

    public static OneShotArgs Failed(string message) => new() { Error = message };
}

public static class OneShotArgParser
{
    public const int DefaultTimeoutSeconds = 300;
    public const int DefaultMaxIterations = 25;

    // Mirrors the headless schema's limits ranges so a one-shot run cannot ask
    // for something the config-driven contract would reject.
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 86_400;
    public const int MinMaxIterations = 1;
    public const int MaxMaxIterations = 10_000;

    public const string Usage =
        "Usage:\n"
        + "  andy-cli run [options] \"<prompt>\"\n"
        + "  <command> | andy-cli run [options] \"<prompt>\"\n"
        + "  andy-cli run --headless --config <path>\n"
        + "\n"
        + "Options:\n"
        + "  --json, --ndjson      Emit the NDJSON event stream on stdout instead of concise text.\n"
        + "  --provider <name>     LLM provider (default: detected from the environment).\n"
        + "  --model <id>          Model id (default: the provider's remembered or default model).\n"
        + "  --cwd <path>          Working directory for the run (default: the current directory).\n"
        + "  --timeout <seconds>   Wall-clock timeout, 1-86400 (default: 300).\n"
        + "  --max-iterations <n>  Agent turn budget, 1-10000 (default: 25).\n"
        + "  --allow-tool <id>     Permit one mutating tool (repeatable; comma-separated lists allowed).\n"
        + "  --output <path>       Also write the final answer to <path>.\n"
        + "  --no-stdin            Ignore redirected stdin.\n"
        + "  --                    Treat every following token as prompt text.\n"
        + "\n"
        + "Input: positional prompt text and piped stdin are combined deterministically,\n"
        + "positional text first, stdin fenced between\n"
        + "  --- begin piped stdin ---\n"
        + "  --- end piped stdin ---\n"
        + "when both are present. Either source alone is used verbatim.\n"
        + "\n"
        + "Permissions: without --allow-tool the run uses the fail-closed read-only\n"
        + "profile. Mutating tools and execute_command are denied and the run never\n"
        + "prompts for approval, so it is safe on redirected input.";

    // True when the caller selected the strict, config-driven headless contract.
    // Tokens after a bare `--` are prompt text and never mode selectors.
    public static bool SelectsStrictHeadless(string[] args)
    {
        foreach (var token in Remainder(args))
        {
            if (string.Equals(token, "--", StringComparison.Ordinal))
            {
                return false;
            }
            if (string.Equals(token, "--headless", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    // Drops the leading `run` verb the dispatcher passes through as args[0].
    public static IReadOnlyList<string> Remainder(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "run", StringComparison.Ordinal))
        {
            return args.Skip(1).ToArray();
        }
        return args;
    }

    public static OneShotArgs Parse(string[] args)
    {
        var tokens = Remainder(args);
        var words = new List<string>();
        var allowedTools = new List<string>();

        var ndjson = false;
        var noStdin = false;
        string? provider = null;
        string? model = null;
        string? cwd = null;
        string? outputFile = null;
        int? timeout = null;
        int? maxIterations = null;
        var literal = false;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (literal)
            {
                words.Add(token);
                continue;
            }

            switch (token)
            {
                case "--":
                    literal = true;
                    continue;
                case "--json":
                case "--ndjson":
                    ndjson = true;
                    continue;
                case "--no-stdin":
                    noStdin = true;
                    continue;
                case "--headless":
                    // Unreachable via HeadlessRunner (which dispatches on this
                    // flag), but keep an explicit, honest message for direct callers.
                    return OneShotArgs.Failed(
                        "`--headless` selects the config-driven contract and must be used with `--config <path>`.");
                case "--config":
                    return OneShotArgs.Failed(
                        "`--config` requires `--headless`. Interactive-style `andy-cli run \"<prompt>\"` "
                        + "does not take a config file; use `andy-cli run --headless --config <path>` instead.");
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var (name, inlineValue) = SplitInline(token);
                switch (name)
                {
                    case "--provider":
                        if (!TryTakeValue(tokens, ref i, inlineValue, name, out var providerValue, out var providerError))
                        {
                            return OneShotArgs.Failed(providerError!);
                        }
                        provider = providerValue;
                        continue;
                    case "--model":
                        if (!TryTakeValue(tokens, ref i, inlineValue, name, out var modelValue, out var modelError))
                        {
                            return OneShotArgs.Failed(modelError!);
                        }
                        model = modelValue;
                        continue;
                    case "--cwd":
                        if (!TryTakeValue(tokens, ref i, inlineValue, name, out var cwdValue, out var cwdError))
                        {
                            return OneShotArgs.Failed(cwdError!);
                        }
                        cwd = cwdValue;
                        continue;
                    case "--output":
                        if (!TryTakeValue(tokens, ref i, inlineValue, name, out var outputValue, out var outputError))
                        {
                            return OneShotArgs.Failed(outputError!);
                        }
                        outputFile = outputValue;
                        continue;
                    case "--allow-tool":
                        if (!TryTakeValue(tokens, ref i, inlineValue, name, out var toolValue, out var toolError))
                        {
                            return OneShotArgs.Failed(toolError!);
                        }
                        foreach (var part in toolValue!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!allowedTools.Contains(part, StringComparer.Ordinal))
                            {
                                allowedTools.Add(part);
                            }
                        }
                        continue;
                    case "--timeout":
                        if (!TryTakeValue(tokens, ref i, inlineValue, name, out var timeoutValue, out var timeoutError))
                        {
                            return OneShotArgs.Failed(timeoutError!);
                        }
                        if (!TryParseBounded(timeoutValue!, name, MinTimeoutSeconds, MaxTimeoutSeconds, out var timeoutSeconds, out var timeoutRangeError))
                        {
                            return OneShotArgs.Failed(timeoutRangeError!);
                        }
                        timeout = timeoutSeconds;
                        continue;
                    case "--max-iterations":
                        if (!TryTakeValue(tokens, ref i, inlineValue, name, out var iterValue, out var iterError))
                        {
                            return OneShotArgs.Failed(iterError!);
                        }
                        if (!TryParseBounded(iterValue!, name, MinMaxIterations, MaxMaxIterations, out var iterations, out var iterRangeError))
                        {
                            return OneShotArgs.Failed(iterRangeError!);
                        }
                        maxIterations = iterations;
                        continue;
                    default:
                        return OneShotArgs.Failed($"Unknown argument: {token}");
                }
            }

            if (token.Length > 1 && token[0] == '-')
            {
                return OneShotArgs.Failed($"Unknown argument: {token}");
            }

            words.Add(token);
        }

        return new OneShotArgs
        {
            PromptWords = words,
            Ndjson = ndjson,
            NoStdin = noStdin,
            Provider = provider,
            Model = model,
            Cwd = cwd,
            OutputFile = outputFile,
            TimeoutSeconds = timeout,
            MaxIterations = maxIterations,
            AllowedTools = allowedTools,
        };
    }

    // Supports both `--model gpt-4o` and `--model=gpt-4o`.
    private static (string Name, string? InlineValue) SplitInline(string token)
    {
        var eq = token.IndexOf('=', StringComparison.Ordinal);
        return eq < 0 ? (token, null) : (token[..eq], token[(eq + 1)..]);
    }

    private static bool TryTakeValue(
        IReadOnlyList<string> tokens,
        ref int index,
        string? inlineValue,
        string name,
        out string? value,
        out string? error)
    {
        if (inlineValue is not null)
        {
            if (inlineValue.Length == 0)
            {
                value = null;
                error = $"`{name}` requires a non-empty value.";
                return false;
            }
            value = inlineValue;
            error = null;
            return true;
        }

        if (index + 1 >= tokens.Count)
        {
            value = null;
            error = $"`{name}` requires a value.";
            return false;
        }

        value = tokens[++index];
        error = null;
        return true;
    }

    private static bool TryParseBounded(
        string raw,
        string name,
        int min,
        int max,
        out int value,
        out string? error)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error = $"`{name}` expects an integer, got '{raw}'.";
            return false;
        }
        if (value < min || value > max)
        {
            error = $"`{name}` must be between {min} and {max}, got {value}.";
            return false;
        }
        error = null;
        return true;
    }
}
