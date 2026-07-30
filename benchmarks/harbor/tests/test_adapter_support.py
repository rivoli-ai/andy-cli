import unittest
import json
from pathlib import Path
from tempfile import TemporaryDirectory

from benchmarks.harbor.adapter_support import (
    ALLOWED_CODING_TOOLS,
    SYSTEM_DEPENDENCY_INSTALL_COMMAND,
    build_headless_config,
    compute_agent_budgets,
    parse_model_name,
    resolve_harbor_agent_timeout,
)


class ParseModelNameTests(unittest.TestCase):
    def test_preserves_slashes_in_openrouter_model_id(self) -> None:
        selection = parse_model_name("openrouter/moonshotai/kimi-k3")

        self.assertEqual("openrouter", selection.provider)
        self.assertEqual("moonshotai/kimi-k3", selection.model_id)
        self.assertEqual("OPENROUTER_API_KEY", selection.api_key_env)

    def test_local_provider_does_not_require_api_key(self) -> None:
        selection = parse_model_name("local/qwen3-coder")

        self.assertIsNone(selection.api_key_env)

    def test_rejects_unknown_provider(self) -> None:
        with self.assertRaisesRegex(ValueError, "Unsupported Andy provider"):
            parse_model_name("unknown/model")

    def test_requires_provider_model_format(self) -> None:
        with self.assertRaisesRegex(ValueError, "provider/model"):
            parse_model_name("gpt-5")


class BuildHeadlessConfigTests(unittest.TestCase):
    def test_builds_headless_v1_coding_run(self) -> None:
        model = parse_model_name("openai/gpt-5.4")

        config = build_headless_config(
            run_id="00000000-0000-0000-0000-000000000001",
            task_instruction="Fix the parser.",
            model=model,
            workspace_root="/workspace",
            output_file="/logs/agent/andy-final.txt",
            max_iterations=80,
            timeout_seconds=600,
        )

        self.assertEqual(1, config["schema_version"])
        self.assertEqual("env:OPENAI_API_KEY", config["model"]["api_key_ref"])
        self.assertEqual([], config["tools"])
        self.assertEqual(
            list(ALLOWED_CODING_TOOLS),
            config["permissions"]["allowed_tools"],
        )
        self.assertIn("Fix the parser.", config["agent"]["instructions"])
        self.assertEqual("/workspace", config["workspace"]["root"])

    def test_includes_agent_run_budgets(self) -> None:
        config = build_headless_config(
            run_id="00000000-0000-0000-0000-000000000001",
            task_instruction="Fix the parser.",
            model=parse_model_name("openrouter/xiaomi/mimo-v2.5"),
            workspace_root="/workspace",
            output_file="/logs/agent/andy-final.txt",
            max_iterations=150,
            timeout_seconds=840,
            max_output_tokens=8192,
            continuation_window_iterations=50,
            engine_timeout_seconds=810,
        )

        self.assertEqual(
            {
                "max_iterations": 150,
                "timeout_seconds": 840,
                "max_output_tokens": 8192,
                "continuation_window_iterations": 50,
                "engine_timeout_seconds": 810,
            },
            config["limits"],
        )

    def test_omits_api_key_ref_for_local_provider(self) -> None:
        config = build_headless_config(
            run_id="00000000-0000-0000-0000-000000000001",
            task_instruction="Fix the parser.",
            model=parse_model_name("local/qwen3-coder"),
            workspace_root="/workspace",
            output_file="/logs/agent/andy-final.txt",
            max_iterations=80,
            timeout_seconds=600,
        )

        self.assertNotIn("api_key_ref", config["model"])

    def test_requires_absolute_workspace(self) -> None:
        with self.assertRaisesRegex(ValueError, "absolute"):
            build_headless_config(
                run_id="00000000-0000-0000-0000-000000000001",
                task_instruction="Fix the parser.",
                model=parse_model_name("openai/gpt-5.4"),
                workspace_root="workspace",
                output_file="/logs/agent/andy-final.txt",
                max_iterations=80,
                timeout_seconds=600,
            )


class AgentBudgetTests(unittest.TestCase):
    def test_reserves_harbor_and_engine_cleanup_margins(self) -> None:
        budgets = compute_agent_budgets(3600)

        self.assertEqual(3600, budgets.harbor_timeout_seconds)
        self.assertEqual(3420, budgets.cli_timeout_seconds)
        self.assertEqual(3317, budgets.engine_timeout_seconds)

    def test_requested_cli_timeout_is_clamped_to_harbor_budget(self) -> None:
        budgets = compute_agent_budgets(360, requested_cli_timeout_seconds=12000)

        self.assertEqual(330, budgets.cli_timeout_seconds)
        self.assertLess(budgets.engine_timeout_seconds, budgets.cli_timeout_seconds)

    def test_resolves_effective_timeout_from_trial_and_task_cache(self) -> None:
        with TemporaryDirectory() as root:
            root_path = Path(root)
            logs_dir = root_path / "job" / "trial" / "agent"
            logs_dir.mkdir(parents=True)
            cache_root = root_path / "cache"
            digest = "a" * 64
            task_dir = cache_root / "terminal-bench" / "example" / digest
            task_dir.mkdir(parents=True)
            (task_dir / "task.toml").write_text(
                "[agent]\ntimeout_sec = 1200.0\n",
                encoding="utf-8",
            )
            (logs_dir.parent / "config.json").write_text(
                json.dumps(
                    {
                        "task": {
                            "name": "terminal-bench/example",
                            "ref": f"sha256:{digest}",
                        },
                        "agent": {"max_timeout_sec": 1000},
                        "timeout_multiplier": 1.5,
                    }
                ),
                encoding="utf-8",
            )

            timeout = resolve_harbor_agent_timeout(logs_dir, cache_root)

        self.assertEqual(1500.0, timeout)


class SystemDependencyCommandTests(unittest.TestCase):
    def test_supports_harbor_package_manager_matrix(self) -> None:
        for manager in ("apt-get", "dnf", "yum", "apk"):
            with self.subTest(manager=manager):
                self.assertIn(f"command -v {manager}", SYSTEM_DEPENDENCY_INSTALL_COMMAND)

    def test_requires_tar_and_trusted_certificates(self) -> None:
        self.assertIn("command -v tar", SYSTEM_DEPENDENCY_INSTALL_COMMAND)
        self.assertIn("ca-certificates", SYSTEM_DEPENDENCY_INSTALL_COMMAND)
        self.assertIn("exit 1", SYSTEM_DEPENDENCY_INSTALL_COMMAND)


if __name__ == "__main__":
    unittest.main()
