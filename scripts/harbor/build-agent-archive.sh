#!/usr/bin/env bash

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
output_path="${1:-$repo_root/artifacts/harbor/andy-cli-linux-x64.tar.gz}"
temp_dir="$(mktemp -d "${TMPDIR:-/tmp}/andy-harbor-publish.XXXXXX")"
publish_dir="$temp_dir/publish"
lock_file="$temp_dir/packages.lock.json"

cleanup() {
    rm -rf -- "$temp_dir"
}
trap cleanup EXIT

mkdir -p "$(dirname "$output_path")"

dotnet publish "$repo_root/src/Andy.Cli/Andy.Cli.csproj" \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained true \
    --output "$publish_dir" \
    -p:PublishTrimmed=true \
    -p:PublishSingleFile=true \
    -p:InvariantGlobalization=true \
    -p:NuGetLockFilePath="$lock_file" \
    -p:InformationalVersion="harbor-$(git -C "$repo_root" rev-parse --short=9 HEAD)"

chmod +x "$publish_dir/andy-cli"
COPYFILE_DISABLE=1 tar -czf "$output_path" -C "$publish_dir" .

printf 'Andy Harbor archive: %s\n' "$output_path"
