#!/usr/bin/env bash
#
# assert-sdk-version.sh
#
# Fails unless the .NET SDK selected by global.json is a .NET 8 SDK.
# Run locally before building, and in CI as a gate ahead of restore/build so a
# host with only a newer major SDK (for example .NET 9) fails loudly instead of
# silently building the CLI against the wrong toolchain.
#
# Policy and rationale: docs/SDK_AND_DEPENDENCIES.md (issue #172).
#
# Usage:
#   scripts/assert-sdk-version.sh              # expect major version 8
#   EXPECTED_MAJOR=8 scripts/assert-sdk-version.sh
#
# Exit codes: 0 = SDK matches the expected major; 1 = mismatch or dotnet missing.

set -euo pipefail

EXPECTED_MAJOR="${EXPECTED_MAJOR:-8}"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "assert-sdk-version: 'dotnet' was not found on PATH." >&2
  exit 1
fi

# `dotnet --version` resolves through global.json from the current directory,
# so run this script from the repository root (or a subdirectory of it).
SDK_VERSION="$(dotnet --version)"

case "${SDK_VERSION}" in
  "${EXPECTED_MAJOR}".*)
    echo "assert-sdk-version: OK - selected .NET SDK ${SDK_VERSION} (major ${EXPECTED_MAJOR})."
    exit 0
    ;;
  *)
    echo "assert-sdk-version: FAIL - selected .NET SDK ${SDK_VERSION}, expected major ${EXPECTED_MAJOR}.x." >&2
    echo "  global.json pins the ${EXPECTED_MAJOR}.0 feature band. Install a .NET ${EXPECTED_MAJOR} SDK" >&2
    echo "  (see docs/SDK_AND_DEPENDENCIES.md) or check that global.json was not modified." >&2
    exit 1
    ;;
esac
