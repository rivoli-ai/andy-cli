#!/usr/bin/env bash
#
# check-source-compat.sh  (helper stub)
#
# Builds andy-cli against sibling SOURCE checkouts of andy-engine and andy-tui2
# instead of the pinned NuGet packages, to catch API/contract drift (shared
# engine events, tool contexts, TUI primitives) before a package is cut.
#
# This is a LOCAL / opt-in developer aid. CI cannot rely on the sibling paths
# existing, so the canonical CI gate stays on the pinned packages plus the
# committed lock files (see docs/SDK_AND_DEPENDENCIES.md, issue #172). The
# recommended CI equivalent is a separate composite-build matrix job that checks
# out rivoli-ai/andy-engine and rivoli-ai/andy-tui2 at a chosen revision; this
# stub documents the moving parts without wiring that job.
#
# Usage:
#   ENGINE_SRC=../andy-engine TUI_SRC=../andy-tui2 scripts/check-source-compat.sh
#
# What a full implementation would do (left as TODO on purpose):
#   1. dotnet pack the engine/TUI source repos into a local feed, OR add
#      ProjectReferences via a Directory.Build.props override.
#   2. Point andy-cli at that local feed / those project references.
#   3. dotnet build Andy.Cli.sln and dotnet test to surface contract breaks.
#   4. Restore the pinned packages afterwards.

set -euo pipefail

ENGINE_SRC="${ENGINE_SRC:-../andy-engine}"
TUI_SRC="${TUI_SRC:-../andy-tui2}"

echo "check-source-compat: engine source = ${ENGINE_SRC}"
echo "check-source-compat: tui source    = ${TUI_SRC}"

missing=0
for src in "${ENGINE_SRC}" "${TUI_SRC}"; do
  if [ ! -d "${src}" ]; then
    echo "check-source-compat: source repo not found at '${src}'." >&2
    missing=1
  fi
done

if [ "${missing}" -ne 0 ]; then
  echo "check-source-compat: skipping composite build (sibling source repos absent)." >&2
  echo "  Clone rivoli-ai/andy-engine and rivoli-ai/andy-tui2 next to this repo, or set" >&2
  echo "  ENGINE_SRC / TUI_SRC, to run the source-revision compatibility build locally." >&2
  # Exit 0: absence of source repos is not a failure of the CLI itself.
  exit 0
fi

echo "check-source-compat: source repos present."
echo "check-source-compat: composite build wiring is intentionally not implemented here."
echo "  See docs/SDK_AND_DEPENDENCIES.md for the recommended composite-checkout / CI-matrix approach."
