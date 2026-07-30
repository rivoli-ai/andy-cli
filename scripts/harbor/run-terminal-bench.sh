#!/usr/bin/env bash

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
model_name="${1:-}"
task_pattern="${2:-terminal-bench/fix-git}"
task_limit="${3:-1}"
archive="$repo_root/artifacts/harbor/andy-cli-linux-x64.tar.gz"
harbor_bin="${HARBOR_BIN:-harbor}"
concurrency="${HARBOR_CONCURRENCY:-1}"

if [ -z "$model_name" ] || [[ "$model_name" != */* ]]; then
    echo "Usage: $0 <provider/model> [task-pattern] [task-limit]" >&2
    exit 2
fi

if ! [[ "$task_limit" =~ ^[1-9][0-9]*$ ]]; then
    echo "Task limit must be a positive integer" >&2
    exit 2
fi

if ! [[ "$concurrency" =~ ^[1-9][0-9]*$ ]]; then
    echo "HARBOR_CONCURRENCY must be a positive integer" >&2
    exit 2
fi

if ! command -v "$harbor_bin" >/dev/null 2>&1; then
    echo "Harbor is not available at '$harbor_bin'. Run: uv tool install harbor" >&2
    exit 2
fi

provider="${model_name%%/*}"
case "$provider" in
    anthropic) key_name="ANTHROPIC_API_KEY" ;;
    openai) key_name="OPENAI_API_KEY" ;;
    openrouter) key_name="OPENROUTER_API_KEY" ;;
    google) key_name="GOOGLE_API_KEY" ;;
    cerebras) key_name="CEREBRAS_API_KEY" ;;
    groq) key_name="GROQ_API_KEY" ;;
    local) key_name="" ;;
    *)
        echo "Unsupported Andy provider: $provider" >&2
        exit 2
        ;;
esac

if [ -n "$key_name" ] && [ -z "${!key_name:-}" ]; then
    echo "$key_name must be set" >&2
    exit 2
fi

if [ ! -f "$archive" ]; then
    "$script_dir/build-agent-archive.sh" "$archive"
fi

agent_env=(
    --agent-env "ANDY_CLI_ARCHIVE=$archive"
    --agent-env "ANDY_PERMISSION_MODE=bypass"
    --agent-env "ANDY_WORKSPACE_ROOT=/"
)

PYTHONPATH="$repo_root${PYTHONPATH:+:$PYTHONPATH}" \
"$harbor_bin" run \
    --dataset terminal-bench/terminal-bench-2 \
    --include-task-name "$task_pattern" \
    --n-tasks "$task_limit" \
    --agent benchmarks.harbor.andy_agent:AndyCli \
    --model "$model_name" \
    --n-concurrent "$concurrency" \
    --agent-kwarg require_harbor_timeout=true \
    --agent-kwarg max_iterations=150 \
    --agent-kwarg max_output_tokens=8192 \
    --agent-kwarg continuation_window_iterations=50 \
    "${agent_env[@]}"
