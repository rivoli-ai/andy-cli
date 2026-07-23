# Andy CLI Harbor benchmark

This integration runs the locally built Andy CLI as a custom installed agent in
[Harbor](https://github.com/harbor-framework/harbor). Each trial uploads the
same self-contained Linux archive that would be released to users, invokes
Andy's headless runtime, and leaves these artifacts in the Harbor agent logs:

- `andy-headless-config.json` - generated `headless-config.v1` input
- `andy-events.jsonl` - structured Andy event stream
- `andy-stderr.txt` - diagnostics from the CLI
- `andy-final.txt` - final model response, when the run succeeds

The bundled smoke dataset contains two isolated .NET 8 repair tasks with hidden,
deterministic verifiers. It is intended as a fast harness regression gate before
running larger datasets such as Terminal-Bench or SWE-bench.

## Status

As of 2026-07-22, the adapter is validated against Harbor 0.20.0. Both bundled
reference solutions pass Harbor's Docker verifier with a reward of 1.0. A
trimmed, self-contained Andy archive has also been installed and invoked through
the adapter, including structured `started`, `error`, `tool_usage_audit`, and
`finished` event collection. On 2026-07-23, a live OpenRouter run with
`moonshotai/kimi-k3` passed `retry-policy` with a reward of 1.0; the
`slug-normalizer` trial was interrupted by an upstream rate-limit response. A
second live run with the current `xiaomi/mimo-v2.5` model passed both tasks with
a mean reward of 1.0 and no exceptions in 2 minutes 31 seconds. The older
`xiaomi/mimo-v2-flash` endpoint is deprecated by OpenRouter.

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
  --agent-env "OPENAI_API_KEY=$OPENAI_API_KEY" \
  --n-concurrent 1
```

Useful adapter overrides are available through Harbor's `--agent-kwarg` option:

```bash
--agent-kwarg max_iterations=150 --agent-kwarg timeout_seconds=1200
```

Set `ANDY_WORKSPACE_ROOT` with `--agent-env` only for task images whose Docker
working directory is not the repository to modify. Otherwise the adapter uses
the container's current working directory.

## Larger benchmark suites

Once the smoke tasks are stable, use the same custom agent with a Harbor dataset:

```bash
PYTHONPATH="$PWD" harbor run \
  --dataset terminal-bench@2.0 \
  --agent benchmarks.harbor.andy_agent:AndyCli \
  --model openai/gpt-5.4 \
  --agent-env "ANDY_CLI_ARCHIVE=$PWD/artifacts/harbor/andy-cli-linux-x64.tar.gz" \
  --agent-env "OPENAI_API_KEY=$OPENAI_API_KEY"
```

Start with one task and one concurrent trial while validating a new provider or
model. Public suites can be expensive and execute untrusted model-generated code;
keep Harbor's container isolation enabled.

## Adapter checks

The config translation tests use only Python's standard library:

```bash
python3 -m unittest discover -s benchmarks/harbor/tests -v
```
