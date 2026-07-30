"""Pure helpers shared by the Harbor adapter and its tests."""

from __future__ import annotations

import json
import math
from pathlib import Path
import tomllib
from dataclasses import dataclass
from typing import Any


SUPPORTED_PROVIDERS = frozenset(
    {"anthropic", "openai", "openrouter", "google", "cerebras", "groq", "local"}
)

PROVIDER_API_KEY_ENV = {
    "anthropic": "ANTHROPIC_API_KEY",
    "openai": "OPENAI_API_KEY",
    "openrouter": "OPENROUTER_API_KEY",
    "google": "GOOGLE_API_KEY",
    "cerebras": "CEREBRAS_API_KEY",
    "groq": "GROQ_API_KEY",
    "local": None,
}

ALLOWED_CODING_TOOLS = (
    "write_file",
    "delete_file",
    "move_file",
    "copy_file",
    "file_editor",
    "replace_text",
    "create_directory",
    "execute_command",
)

SYSTEM_DEPENDENCY_INSTALL_COMMAND = (
    "if command -v tar >/dev/null 2>&1 && "
    "(test -s /etc/ssl/certs/ca-certificates.crt || "
    "test -s /etc/pki/tls/certs/ca-bundle.crt || "
    "test -s /etc/ssl/cert.pem); then "
    ":; "
    "elif command -v apt-get >/dev/null 2>&1; then "
    "apt-get update && "
    "DEBIAN_FRONTEND=noninteractive apt-get install -y tar ca-certificates; "
    "elif command -v dnf >/dev/null 2>&1; then "
    "dnf install -y tar ca-certificates; "
    "elif command -v yum >/dev/null 2>&1; then "
    "yum install -y tar ca-certificates; "
    "elif command -v apk >/dev/null 2>&1; then "
    "apk add --no-cache tar ca-certificates; "
    "else "
    "echo 'Andy requires tar and trusted CA certificates' >&2; "
    "exit 1; "
    "fi"
)


@dataclass(frozen=True)
class ModelSelection:
    provider: str
    model_id: str
    api_key_env: str | None


@dataclass(frozen=True)
class AgentBudgets:
    harbor_timeout_seconds: int
    cli_timeout_seconds: int
    engine_timeout_seconds: int


def compute_agent_budgets(
    harbor_timeout_seconds: float,
    requested_cli_timeout_seconds: int | None = None,
) -> AgentBudgets:
    """Reserve cleanup time inside Harbor's effective agent deadline."""
    if harbor_timeout_seconds <= 0:
        raise ValueError("Harbor timeout must be positive")
    if requested_cli_timeout_seconds is not None and requested_cli_timeout_seconds < 1:
        raise ValueError("Requested CLI timeout must be positive")

    harbor_timeout = max(1, math.floor(harbor_timeout_seconds))
    harbor_margin = max(30, math.ceil(harbor_timeout * 0.05))
    maximum_cli_timeout = max(1, harbor_timeout - harbor_margin)
    cli_timeout = (
        maximum_cli_timeout
        if requested_cli_timeout_seconds is None
        else min(requested_cli_timeout_seconds, maximum_cli_timeout)
    )
    engine_margin = max(5, math.ceil(cli_timeout * 0.03))
    engine_timeout = max(1, cli_timeout - engine_margin)
    return AgentBudgets(harbor_timeout, cli_timeout, engine_timeout)


def resolve_harbor_agent_timeout(
    logs_dir: Path,
    cache_root: Path | None = None,
) -> float | None:
    """Resolve the effective task timeout from Harbor's trial and task cache."""
    trial_config_path = logs_dir.parent / "config.json"
    if not trial_config_path.is_file():
        return None

    try:
        trial_config = json.loads(trial_config_path.read_text(encoding="utf-8"))
        task = trial_config["task"]
        agent = trial_config.get("agent", {})
        override_timeout = agent.get("override_timeout_sec")

        if override_timeout is not None:
            base_timeout = float(override_timeout)
        else:
            task_name = str(task["name"])
            digest = str(task["ref"]).removeprefix("sha256:")
            task_cache_root = cache_root or (
                Path.home() / ".cache" / "harbor" / "tasks" / "packages"
            )
            task_config_path = task_cache_root.joinpath(
                *task_name.split("/"),
                digest,
                "task.toml",
            )
            task_config = tomllib.loads(
                task_config_path.read_text(encoding="utf-8")
            )
            base_timeout = float(task_config["agent"]["timeout_sec"])

        max_timeout = agent.get("max_timeout_sec")
        if max_timeout is not None:
            base_timeout = min(base_timeout, float(max_timeout))
        multiplier = trial_config.get("agent_timeout_multiplier")
        if multiplier is None:
            multiplier = trial_config.get("timeout_multiplier")
        return base_timeout * float(multiplier if multiplier is not None else 1.0)
    except (KeyError, OSError, TypeError, ValueError, json.JSONDecodeError, tomllib.TOMLDecodeError):
        return None


def parse_model_name(model_name: str | None) -> ModelSelection:
    """Convert Harbor's provider/model notation to Andy's model contract."""
    if not model_name or "/" not in model_name:
        raise ValueError("Model name must use Harbor's provider/model format")

    provider, model_id = model_name.split("/", 1)
    provider = provider.strip().lower()
    model_id = model_id.strip()

    if provider not in SUPPORTED_PROVIDERS:
        supported = ", ".join(sorted(SUPPORTED_PROVIDERS))
        raise ValueError(f"Unsupported Andy provider '{provider}'. Supported: {supported}")
    if not model_id:
        raise ValueError("Model id cannot be empty")

    return ModelSelection(provider, model_id, PROVIDER_API_KEY_ENV[provider])


def build_agent_instructions(task_instruction: str) -> str:
    if not task_instruction.strip():
        raise ValueError("Task instruction cannot be empty")

    return (
        "You are an autonomous coding agent operating inside a benchmark container. "
        "Inspect the workspace, implement the requested outcome, and run relevant "
        "checks before finishing. Modify the workspace instead of only describing a "
        "solution.\n\nTask:\n"
        f"{task_instruction.strip()}"
    )


def build_headless_config(
    *,
    run_id: str,
    task_instruction: str,
    model: ModelSelection,
    workspace_root: str,
    output_file: str,
    max_iterations: int,
    timeout_seconds: int,
    max_output_tokens: int | None = None,
    continuation_window_iterations: int | None = None,
    engine_timeout_seconds: int | None = None,
) -> dict[str, Any]:
    """Build a headless-config.v1 document for one Harbor trial."""
    if not workspace_root.startswith("/"):
        raise ValueError("Workspace root must be an absolute container path")
    if not output_file.startswith("/"):
        raise ValueError("Output file must be an absolute container path")
    if max_iterations < 1:
        raise ValueError("max_iterations must be at least 1")
    if timeout_seconds < 1:
        raise ValueError("timeout_seconds must be at least 1")
    if max_output_tokens is not None and max_output_tokens < 256:
        raise ValueError("max_output_tokens must be at least 256")
    if (
        continuation_window_iterations is not None
        and not 1 <= continuation_window_iterations <= max_iterations
    ):
        raise ValueError(
            "continuation_window_iterations must be between 1 and max_iterations"
        )
    if (
        engine_timeout_seconds is not None
        and not 1 <= engine_timeout_seconds < timeout_seconds
    ):
        raise ValueError(
            "engine_timeout_seconds must be positive and smaller than timeout_seconds"
        )

    model_config: dict[str, Any] = {
        "provider": model.provider,
        "id": model.model_id,
    }
    if model.api_key_env is not None:
        model_config["api_key_ref"] = f"env:{model.api_key_env}"

    limits: dict[str, int] = {
        "max_iterations": max_iterations,
        "timeout_seconds": timeout_seconds,
    }
    if max_output_tokens is not None:
        limits["max_output_tokens"] = max_output_tokens
    if continuation_window_iterations is not None:
        limits["continuation_window_iterations"] = continuation_window_iterations
    if engine_timeout_seconds is not None:
        limits["engine_timeout_seconds"] = engine_timeout_seconds

    return {
        "schema_version": 1,
        "run_id": run_id,
        "agent": {
            "slug": "andy-harbor",
            "instructions": build_agent_instructions(task_instruction),
            "output_format": "plain",
        },
        "model": model_config,
        "tools": [],
        "workspace": {"root": workspace_root},
        "output": {"file": output_file, "stream": "stdout"},
        "permissions": {"allowed_tools": list(ALLOWED_CODING_TOOLS)},
        "limits": limits,
    }
