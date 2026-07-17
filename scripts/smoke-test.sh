#!/usr/bin/env bash

# Release smoke test for a packaged andy-cli binary.
#
# Exercises a self-contained release binary through the paths a user hits first,
# without any network access or LLM provider credentials:
#   1. Startup + version    - the binary launches and reports the version it was built as.
#   2. Help / usage         - the command dispatcher renders usage text.
#   3. Non-network tool path - the built-in tool catalog is initialised and listed.
#   4. Headless schema check - an invalid headless config is rejected by the embedded
#                              headless-config.v1 schema validator with the ConfigError
#                              exit code (2), proving the packaged binary validates input
#                              before any provider/network call.
#
# Works on Linux, macOS and Windows runners (run under bash; Windows uses git-bash).
#
# Usage: scripts/smoke-test.sh <extracted-dir> <expected-version>

set -euo pipefail

DIR="${1:?usage: smoke-test.sh <extracted-dir> <expected-version>}"
EXPECTED_RAW="${2:?usage: smoke-test.sh <extracted-dir> <expected-version>}"
EXPECTED="${EXPECTED_RAW#v}"

# Locate the binary (Unix: andy-cli, Windows: andy-cli.exe).
if [ -f "$DIR/andy-cli" ]; then
    BIN="$DIR/andy-cli"
elif [ -f "$DIR/andy-cli.exe" ]; then
    BIN="$DIR/andy-cli.exe"
else
    echo "smoke: FAIL - no andy-cli binary found in $DIR" >&2
    exit 1
fi
chmod +x "$BIN" 2>/dev/null || true

pass() { echo "smoke: PASS - $1"; }
fail() { echo "smoke: FAIL - $1" >&2; exit 1; }

echo "smoke: testing $BIN (expected version $EXPECTED)"

# 1) Startup + version.
version_out="$("$BIN" --version)"
echo "  version: $version_out"
echo "$version_out" | grep -q "andy-cli" || fail "version output missing 'andy-cli' marker"
echo "$version_out" | grep -qF "$EXPECTED" || fail "version output missing expected version $EXPECTED"
pass "startup + version"

# 2) Help / usage text.
help_out="$("$BIN" help)"
echo "$help_out" | grep -qi "Usage:" || fail "help output missing usage line"
echo "$help_out" | grep -qi "tools" || fail "help output missing commands list"
pass "help / usage"

# 3) Representative non-network tool path: list the built-in tool catalog. This
#    initialises the tool registry and renders it with no network access.
tools_out="$("$BIN" tools list)"
echo "$tools_out" | grep -qiE "tool|read_file|list" || fail "tools list produced no catalog"
pass "tool catalog listing (non-network)"

# 4) Headless schema validation: an intentionally invalid config must be rejected
#    with the ConfigError contract exit code (2). This fails before any LLM call.
bad_config="$(mktemp 2>/dev/null || echo "${TMPDIR:-/tmp}/andy-smoke-bad.json")"
printf '{ "schema_version": 1, "not_a_valid_field": true }\n' > "$bad_config"
set +e
"$BIN" run --headless --config "$bad_config" > "${bad_config}.out" 2>&1
code=$?
set -e
echo "  headless(invalid) exit=$code"
cat "${bad_config}.out" 2>/dev/null || true
rm -f "$bad_config" "${bad_config}.out" 2>/dev/null || true
[ "$code" -eq 2 ] || fail "headless invalid-config expected ConfigError exit 2, got $code"
pass "headless schema validation (ConfigError=2)"

echo "smoke: ALL CHECKS PASSED for $BIN ($EXPECTED)"
