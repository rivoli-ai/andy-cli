#!/usr/bin/env python3
"""Drive the packaged Andy CLI through the minimal ACP v1 handshake."""

import argparse
import json
import os
import queue
import subprocess
import sys
import threading
import time


def _reader(stream, messages, raw_lines):
    for line in iter(stream.readline, ""):
        raw_lines.append(line.rstrip("\r\n"))
        messages.put(line)


def _stderr_reader(stream, raw_lines):
    for line in iter(stream.readline, ""):
        raw_lines.append(line.rstrip("\r\n"))


def _response(messages, request_id, timeout_seconds):
    deadline = time.monotonic() + timeout_seconds
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("timed out waiting for ACP response id={}".format(request_id))
        try:
            line = messages.get(timeout=remaining)
        except queue.Empty as exc:
            raise TimeoutError(
                "timed out waiting for ACP response id={}".format(request_id)
            ) from exc

        try:
            message = json.loads(line)
        except json.JSONDecodeError as exc:
            raise RuntimeError("ACP stdout was not JSON: {!r}".format(line)) from exc
        if message.get("id") == request_id:
            return message


def _send(process, request):
    process.stdin.write(json.dumps(request, separators=(",", ":")) + "\n")
    process.stdin.flush()


def run(binary, cwd, timeout_seconds):
    binary = os.path.abspath(binary)
    cwd = os.path.abspath(cwd)
    process = subprocess.Popen(
        [binary, "--acp"],
        cwd=cwd,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        bufsize=1,
    )
    messages = queue.Queue()
    stdout_lines = []
    stderr_lines = []
    stdout_thread = threading.Thread(
        target=_reader, args=(process.stdout, messages, stdout_lines), daemon=True
    )
    stderr_thread = threading.Thread(
        target=_stderr_reader, args=(process.stderr, stderr_lines), daemon=True
    )
    stdout_thread.start()
    stderr_thread.start()

    try:
        _send(
            process,
            {
                "jsonrpc": "2.0",
                "id": 1,
                "method": "initialize",
                "params": {"protocolVersion": 1, "clientCapabilities": {}},
            },
        )
        initialize = _response(messages, 1, timeout_seconds)
        if "error" in initialize:
            raise RuntimeError("initialize returned error: {}".format(initialize["error"]))
        protocol_version = initialize.get("result", {}).get("protocolVersion")
        if not isinstance(protocol_version, int):
            raise RuntimeError("initialize result did not contain integer protocolVersion")

        _send(
            process,
            {
                "jsonrpc": "2.0",
                "id": 2,
                "method": "session/new",
                "params": {"cwd": cwd, "mcpServers": []},
            },
        )
        new_session = _response(messages, 2, timeout_seconds)
        if "error" in new_session:
            raise RuntimeError("session/new returned error: {}".format(new_session["error"]))
        session_result = new_session.get("result", {})
        session_id = session_result.get("sessionId")
        if not isinstance(session_id, str) or not session_id:
            raise RuntimeError("session/new result did not contain sessionId")
        config_options = session_result.get("configOptions")
        model_option = next(
            (
                option
                for option in config_options
                if isinstance(option, dict)
                and option.get("id") == "model"
                and option.get("category") == "model"
                and option.get("currentValue")
            ),
            None,
        ) if isinstance(config_options, list) else None
        if model_option is None:
            raise RuntimeError(
                "session/new result did not contain an active model config option"
            )

        _send(
            process,
            {
                "jsonrpc": "2.0",
                "id": 3,
                "method": "session/set_config_option",
                "params": {
                    "sessionId": session_id,
                    "configId": "model",
                    "value": model_option["currentValue"],
                },
            },
        )
        set_config = _response(messages, 3, timeout_seconds)
        if "error" in set_config:
            raise RuntimeError(
                "session/set_config_option returned error: {}".format(
                    set_config["error"]
                )
            )
        updated_options = set_config.get("result", {}).get("configOptions")
        if not isinstance(updated_options, list) or not any(
            option.get("id") == "model"
            and option.get("currentValue") == model_option["currentValue"]
            for option in updated_options
            if isinstance(option, dict)
        ):
            raise RuntimeError(
                "session/set_config_option did not return the active model option"
            )

        print(
            "acp-smoke: PASS - protocolVersion={} sessionId={} modelConfig=active".format(
                protocol_version, session_id
            )
        )
    except Exception:
        if stdout_lines:
            print("acp-smoke: stdout:", file=sys.stderr)
            for line in stdout_lines:
                print("  " + line, file=sys.stderr)
        if stderr_lines:
            print("acp-smoke: stderr:", file=sys.stderr)
            for line in stderr_lines:
                print("  " + line, file=sys.stderr)
        raise
    finally:
        if process.stdin:
            process.stdin.close()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("binary", help="path to the packaged andy-cli binary")
    parser.add_argument("--cwd", required=True, help="existing workspace for session/new")
    parser.add_argument("--timeout", type=float, default=15.0)
    args = parser.parse_args()

    try:
        run(args.binary, args.cwd, args.timeout)
    except Exception as exc:
        print("acp-smoke: FAIL - {}".format(exc), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
