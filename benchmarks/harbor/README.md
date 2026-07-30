# Andy CLI Harbor benchmark

This integration runs the locally built Andy CLI as a custom installed agent in
[Harbor](https://github.com/harbor-framework/harbor). Each trial uploads the
same self-contained Linux archive that would be released to users, invokes
Andy's headless runtime, and leaves these artifacts in the Harbor agent logs:

- `andy-headless-config.json` - generated `headless-config.v1` input
- `andy-events.jsonl` - structured Andy event stream
- `andy-stderr.txt` - diagnostics from the CLI
- `andy-final.txt` - final model response, when the run succeeds
- `andy-budget.json` - resolved Harbor, CLI, and Engine deadlines

The bundled smoke dataset contains two isolated .NET 8 repair tasks with hidden,
deterministic verifiers. It is intended as a fast harness regression gate before
running larger datasets such as Terminal-Bench or SWE-bench.

## Status

As of 2026-07-28, the adapter is validated against Harbor 0.20.0. Both bundled
reference solutions pass Harbor's Docker verifier with a reward of 1.0. A
trimmed, self-contained Andy archive has also been installed and invoked through
the adapter, including structured `started`, `error`, `tool_usage_audit`, and
`finished` event collection. On 2026-07-23, a live OpenRouter run with
`moonshotai/kimi-k3` passed `retry-policy` with a reward of 1.0; the
`slug-normalizer` trial was interrupted by an upstream rate-limit response. A
second live run with the current `xiaomi/mimo-v2.5` model passed both tasks with
a mean reward of 1.0 and no exceptions in 2 minutes 31 seconds. The older
`xiaomi/mimo-v2-flash` endpoint is deprecated by OpenRouter. An official
Terminal-Bench 2 run then passed `terminal-bench/fix-git` with a reward of 1.0
and no exceptions in 2 minutes 11 seconds. On the hard
`terminal-bench/cancel-async-tasks` task, Andy completed normally and passed
five of six verifier tests, but received a reward of 0.0.

On 2026-07-24, Andy and `xiaomi/mimo-v2.5` completed all four tasks marked
`easy` in Terminal-Bench 2:

| Task | Reward | Result |
| --- | ---: | --- |
| `fix-git` | 1.0 | Passed both verifier tests |
| `prove-plus-comm` | 1.0 | Passed all four verifier tests |
| `cobol-modernization` | 0.0 | Reached the official 900-second timeout; the generated Python program failed one of three verifier tests |
| `overfull-hbox` | 0.0 | Reached Andy's 100-turn limit with one overfull box remaining; passed three of four verifier tests |

The aggregate easy-tier score is 2/4, with a mean reward of 0.5. An initial
`overfull-hbox` attempt failed before model execution because its minimal image
did not include trusted CA certificates. That infrastructure-only attempt is
excluded from the score; the adapter now installs `ca-certificates`, and the
replacement trial reached the model and official verifier successfully.

On 2026-07-28, Andy completed a 30-task Terminal-Bench 2 sample with 10 passes,
20 failures, and a mean reward of 0.333. Sixteen trials reached their agent
timeout; `overfull-hbox` still passed its verifier from the persisted workspace.
See the
[full configuration and per-task results](results/terminal-bench-30-2026-07-28.md).

As of 2026-07-29, scored Terminal-Bench runs resolve each task's effective
Harbor timeout from the trial config and cached task package. The adapter
reserves at least 5% (and 30 seconds) for Harbor cleanup, then at least 3% (and
5 seconds) for CLI cleanup. It fails closed when that task deadline cannot be
resolved, records all three deadlines in `andy-budget.json` and Harbor metadata,
and defaults to 150 total turns in 50-turn continuation windows with an
8192-token response budget.

## Prerequisites

- Docker running locally
- The .NET 8 SDK
- Python 3.12 or newer and `uv`
- Harbor 0.20 or newer: `uv tool install harbor`
- An API key supported by the selected Andy provider

Set `HARBOR_BIN` when Harbor is available at a non-global path:

```bash
export HARBOR_BIN="/path/to/harbor"
```

## Run the smoke dataset

From the repository root:

```bash
export OPENAI_API_KEY="..."
./scripts/harbor/run-smoke.sh openai/gpt-5.4
```

The wrapper publishes the current checkout for `linux-x64` when necessary and
runs both tasks sequentially. Delete
`artifacts/harbor/andy-cli-linux-x64.tar.gz` when you need to force a rebuild, or
run the builder explicitly:

```bash
./scripts/harbor/build-agent-archive.sh
```

The benchmark archive uses .NET invariant globalization so the self-contained
CLI also starts in minimal task images that do not include ICU. The adapter
installs trusted CA certificates so HTTPS model providers also work in minimal
task images that omit them.

Harbor model names are translated as `provider/model-id`. Slashes after the
provider are preserved, so an OpenRouter model can be run as:

```bash
export OPENROUTER_API_KEY="..."
./scripts/harbor/run-smoke.sh openrouter/moonshotai/kimi-k3
```

Supported providers are `anthropic`, `openai`, `openrouter`, `google`,
`cerebras`, `groq`, and `local`.

## Run Harbor directly

The wrapper is equivalent to:

```bash
PYTHONPATH="$PWD" harbor run \
  --path "$PWD/benchmarks/harbor/tasks" \
  --agent benchmarks.harbor.andy_agent:AndyCli \
  --model openai/gpt-5.4 \
  --agent-env "ANDY_CLI_ARCHIVE=$PWD/artifacts/harbor/andy-cli-linux-x64.tar.gz" \
  --n-concurrent 1
```

Useful adapter overrides are available through Harbor's `--agent-kwarg` option:

```bash
--agent-kwarg max_iterations=150 \
--agent-kwarg max_output_tokens=8192 \
--agent-kwarg continuation_window_iterations=50 \
--agent-kwarg timeout_seconds=1200
```

An explicit `timeout_seconds` only lowers the derived CLI deadline; it never
extends the task's Harbor deadline.

Set `ANDY_WORKSPACE_ROOT` with `--agent-env` only for task images whose Docker
working directory is not the repository to modify. Otherwise the adapter uses
the container's current working directory.

## Run Terminal-Bench

The Terminal-Bench wrapper defaults to the single `terminal-bench/fix-git`
validation task:

```bash
export OPENROUTER_API_KEY="..."
./scripts/harbor/run-terminal-bench.sh openrouter/xiaomi/mimo-v2.5
```

The wrapper grants broad tool permissions only inside Harbor's disposable
container and sets the workspace root to `/`, because Terminal-Bench tasks may
work outside the image's default working directory.

Pass a task glob and limit to expand the run deliberately:

```bash
HARBOR_CONCURRENCY=2 ./scripts/harbor/run-terminal-bench.sh \
  openrouter/xiaomi/mimo-v2.5 'terminal-bench/*' 4
```

Terminal-Bench 2 currently contains 89 tasks. Running the entire dataset can
take substantial time and model spend.

## Run Terminal-Bench directly

Once the smoke tasks are stable, use the same custom agent with a Harbor dataset:

```bash
PYTHONPATH="$PWD" harbor run \
  --dataset terminal-bench/terminal-bench-2 \
  --include-task-name terminal-bench/fix-git \
  --agent benchmarks.harbor.andy_agent:AndyCli \
  --model openrouter/xiaomi/mimo-v2.5 \
  --agent-env "ANDY_CLI_ARCHIVE=$PWD/artifacts/harbor/andy-cli-linux-x64.tar.gz" \
  --agent-env "ANDY_PERMISSION_MODE=bypass" \
  --agent-env "ANDY_WORKSPACE_ROOT=/"
```

Provider API keys are inherited from the Harbor process and transferred through
an ephemeral container file for both smoke and Terminal-Bench runs. Do not pass
them with `--agent-env`: Harbor retains agent environment values in its job
configuration.

Start with one task and one concurrent trial while validating a new provider or
model. Public suites can be expensive and execute untrusted model-generated code;
keep Harbor's container isolation enabled.

## Completion summary (2026-07-29)

- Derived nested deadlines from Harbor's effective per-task timeout.
- Added bounded continuation and output-token settings to generated headless
  configs.
- Persisted deadline metadata and required exact task timeout resolution for
  scored Terminal-Bench runs.

## Adapter checks

The config translation tests use only Python's standard library:

```bash
python3 -m unittest discover -s benchmarks/harbor/tests -v
```
