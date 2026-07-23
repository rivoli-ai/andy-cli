import unittest

from benchmarks.harbor.adapter_support import (
    ALLOWED_CODING_TOOLS,
    build_headless_config,
    parse_model_name,
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


if __name__ == "__main__":
    unittest.main()
