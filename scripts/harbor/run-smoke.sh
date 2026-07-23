#!/usr/bin/env bash

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
model_name="${1:-}"
archive="$repo_root/artifacts/harbor/andy-cli-linux-x64.tar.gz"
harbor_bin="${HARBOR_BIN:-harbor}"

if [ -z "$model_name" ] || [[ "$model_name" != */* ]]; then
    echo "Usage: $0 <provider/model>" >&2
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

agent_env=(--agent-env "ANDY_CLI_ARCHIVE=$archive")
if [ -n "$key_name" ]; then
    agent_env+=(--agent-env "$key_name=${!key_name}")
fi

PYTHONPATH="$repo_root${PYTHONPATH:+:$PYTHONPATH}" \
"$harbor_bin" run \
    --path "$repo_root/benchmarks/harbor/tasks" \
    --agent benchmarks.harbor.andy_agent:AndyCli \
    --model "$model_name" \
    --n-concurrent 1 \
    "${agent_env[@]}"
