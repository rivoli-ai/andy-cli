"""Harbor installed-agent adapter for Andy CLI headless mode."""

from __future__ import annotations

import json
import shlex
from pathlib import Path
from tempfile import NamedTemporaryFile
from typing import override
from uuid import uuid4

from harbor.agents.installed.base import BaseInstalledAgent, with_prompt_template
from harbor.environments.base import BaseEnvironment
from harbor.models.agent.context import AgentContext

from .adapter_support import (
    SYSTEM_DEPENDENCY_INSTALL_COMMAND,
    build_headless_config,
    parse_model_name,
)


class AndyCli(BaseInstalledAgent):
    """Install a local Andy build and run it against Harbor tasks."""

    _ARCHIVE_ENV = "ANDY_CLI_ARCHIVE"
    _REMOTE_ARCHIVE = "/tmp/andy-cli-harbor.tar.gz"
    _REMOTE_INSTALL_DIR = "/installed-agent/andy-cli"
    _REMOTE_CONFIG = "/tmp/andy-headless-config.json"
    _REMOTE_API_KEY = "/tmp/andy-provider-api-key"
    _EVENTS_FILE = "/logs/agent/andy-events.jsonl"
    _STDERR_FILE = "/logs/agent/andy-stderr.txt"
    _OUTPUT_FILE = "/logs/agent/andy-final.txt"

    def __init__(
        self,
        logs_dir: Path,
        *args: object,
        max_iterations: int = 100,
        timeout_seconds: int = 900,
        **kwargs: object,
    ) -> None:
        super().__init__(logs_dir, *args, **kwargs)
        if max_iterations < 1:
            raise ValueError("max_iterations must be at least 1")
        if timeout_seconds < 1:
            raise ValueError("timeout_seconds must be at least 1")
        self._max_iterations = max_iterations
        self._timeout_seconds = timeout_seconds

    @staticmethod
    @override
    def name() -> str:
        return "andy-cli"

    @override
    def get_version_command(self) -> str | None:
        return "andy-cli --version"

    @override
    def parse_version(self, stdout: str) -> str:
        return stdout.strip().splitlines()[0] if stdout.strip() else "unknown"

    @override
    async def install(self, environment: BaseEnvironment) -> None:
        archive_value = self._get_env(self._ARCHIVE_ENV)
        if not archive_value:
            raise ValueError(
                f"{self._ARCHIVE_ENV} must point to a locally published Andy archive"
            )

        archive = Path(archive_value).expanduser().resolve()
        if not archive.is_file():
            raise ValueError(f"Andy archive does not exist: {archive}")

        # Keep installation compatible with Harbor 0.20.0 from PyPI as well as
        # newer source builds. The published base class does not yet expose the
        # ensure_system_dependencies helper.
        await self.exec_as_root(
            environment,
            command=SYSTEM_DEPENDENCY_INSTALL_COMMAND,
        )
        await environment.upload_file(archive, self._REMOTE_ARCHIVE)
        await self.exec_as_root(
            environment,
            command=(
                f"rm -rf {shlex.quote(self._REMOTE_INSTALL_DIR)} && "
                f"mkdir -p {shlex.quote(self._REMOTE_INSTALL_DIR)} && "
                f"tar -xzf {shlex.quote(self._REMOTE_ARCHIVE)} "
                f"-C {shlex.quote(self._REMOTE_INSTALL_DIR)} && "
                f"chmod +x {shlex.quote(self._REMOTE_INSTALL_DIR)}/andy-cli && "
                f"ln -sf {shlex.quote(self._REMOTE_INSTALL_DIR)}/andy-cli "
                "/usr/local/bin/andy-cli && "
                "andy-cli --version"
            ),
        )

    @override
    def populate_context_post_run(self, context: AgentContext) -> None:
        # Andy's v1 event stream does not yet expose token or cost totals.
        # The raw event stream remains available in the Harbor trial logs.
        return None

    async def _workspace_root(self, environment: BaseEnvironment) -> str:
        configured = self._get_env("ANDY_WORKSPACE_ROOT")
        if configured:
            if not configured.startswith("/"):
                raise ValueError("ANDY_WORKSPACE_ROOT must be an absolute path")
            return configured.rstrip("/") or "/"

        result = await environment.exec(command="pwd")
        if result.return_code != 0 or not result.stdout.strip():
            raise RuntimeError("Could not determine the task container's working directory")
        workspace_root = result.stdout.strip().splitlines()[-1]
        if not workspace_root.startswith("/"):
            raise RuntimeError(f"Container returned a non-absolute working directory: {workspace_root}")
        return workspace_root

    @override
    @with_prompt_template
    async def run(
        self,
        instruction: str,
        environment: BaseEnvironment,
        context: AgentContext,
    ) -> None:
        model = parse_model_name(self.model_name)
        api_key = (
            self._get_env(model.api_key_env)
            if model.api_key_env is not None
            else None
        )
        if model.api_key_env is not None and not api_key:
            raise ValueError(f"{model.api_key_env} must be set in the environment")

        workspace_root = await self._workspace_root(environment)
        config = build_headless_config(
            run_id=str(uuid4()),
            task_instruction=instruction,
            model=model,
            workspace_root=workspace_root,
            output_file=self._OUTPUT_FILE,
            max_iterations=self._max_iterations,
            timeout_seconds=self._timeout_seconds,
        )

        local_config = self.logs_dir / "andy-headless-config.json"
        local_config.parent.mkdir(parents=True, exist_ok=True)
        local_config.write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")
        await environment.upload_file(local_config, self._REMOTE_CONFIG)

        command_prefix = ""
        if model.api_key_env is not None and api_key is not None:
            with NamedTemporaryFile(mode="w", encoding="utf-8") as secret_file:
                secret_file.write(api_key)
                secret_file.flush()
                await environment.upload_file(
                    Path(secret_file.name),
                    self._REMOTE_API_KEY,
                )
            command_prefix += (
                f"export {model.api_key_env}="
                f"\"$(cat {shlex.quote(self._REMOTE_API_KEY)})\" && "
                f"rm -f {shlex.quote(self._REMOTE_API_KEY)} && "
            )

        permission_mode = self._get_env("ANDY_PERMISSION_MODE")
        if permission_mode:
            command_prefix += (
                "export ANDY_PERMISSION_MODE="
                f"{shlex.quote(permission_mode)} && "
            )

        await self.exec_as_agent(
            environment,
            cwd=workspace_root,
            timeout_sec=self._timeout_seconds + 30,
            command=(
                command_prefix
                + "mkdir -p /logs/agent && "
                "andy-cli run --headless "
                f"--config {shlex.quote(self._REMOTE_CONFIG)} "
                f"> >(tee {shlex.quote(self._EVENTS_FILE)}) "
                f"2> >(tee {shlex.quote(self._STDERR_FILE)} >&2)"
            ),
        )
