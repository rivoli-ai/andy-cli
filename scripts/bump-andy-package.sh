#!/usr/bin/env bash
# Bump one Andy.* package to a new version everywhere it is pinned, then verify.
#
# The version is recorded in three places that must not drift: the PackageReference
# in src/Andy.Cli/Andy.Cli.csproj, the matching entry in dependency-manifest.json
# (the known-good graph this repo is verified against), and the committed
# packages.lock.json files that make restores reproducible. Missing any one of them
# breaks a locked-mode restore on a clean machine (NU1004/NU1403) rather than here.
#
# Usage:
#   scripts/bump-andy-package.sh Andy.Permissions 2026.7.25-rc.15
set -euo pipefail

if [[ $# -ne 2 ]]; then
    echo "usage: $0 <PackageId> <NewVersion>" >&2
    exit 2
fi

package="$1"
version="$2"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

csproj="src/Andy.Cli/Andy.Cli.csproj"
manifest="dependency-manifest.json"

current="$(grep -o "Include=\"${package}\" Version=\"[^\"]*\"" "$csproj" | sed 's/.*Version="//; s/"//')"
if [[ -z "$current" ]]; then
    echo "error: ${package} is not referenced in ${csproj}" >&2
    exit 1
fi
echo "==> ${package}: ${current} -> ${version}"

# 1. The project reference.
perl -pi -e "s{(Include=\"\Q${package}\E\" Version=\")[^\"]*(\")}{\${1}${version}\${2}}" "$csproj"

# 2. The known-good dependency graph.
perl -pi -e "s{(\"\Q${package}\E\"\s*:\s*\")[^\"]*(\")}{\${1}${version}\${2}}" "$manifest"

# 3. The lock files. --force-evaluate is what actually rewrites them; a plain
#    restore would fail against the old lock instead of updating it.
echo "==> restoring (regenerating lock files)"
dotnet restore Andy.Cli.sln --force-evaluate

# 4. Prove a clean machine could restore this exact graph.
echo "==> verifying locked-mode restore"
dotnet restore Andy.Cli.sln --locked-mode

echo "==> building"
dotnet build Andy.Cli.sln --nologo

echo
echo "Bumped. Remaining steps:"
echo "  - update the 'updated' date in ${manifest}"
echo "  - dotnet test"
echo "  - commit ${csproj}, ${manifest} and both packages.lock.json files together"
