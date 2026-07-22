#!/usr/bin/env bash
#
# Build and test Andy CLI against source checkouts of Andy.Engine and Andy.Tui.
# The package-based build remains the release gate; this is an opt-in composite
# compatibility check for changes that cross repository boundaries.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="${SOURCE_COMPAT_REPO_ROOT:-$(cd "${SCRIPT_DIR}/.." && pwd)}"
ENGINE_SRC="${ENGINE_SRC:-${REPO_ROOT}/../andy-engine}"
TUI_SRC="${TUI_SRC:-${REPO_ROOT}/../andy-tui2}"
ENGINE_REVISION="${ENGINE_REVISION:-HEAD}"
TUI_REVISION="${TUI_REVISION:-HEAD}"
DOTNET_CMD="${SOURCE_COMPAT_DOTNET:-dotnet}"
GIT_CMD="${SOURCE_COMPAT_GIT:-git}"
TEMP_PARENT="${SOURCE_COMPAT_TEMP_PARENT:-${TMPDIR:-/tmp}}"

usage() {
  printf '%s\n' \
    'Usage: scripts/check-source-compat.sh [options]' \
    '' \
    'Options:' \
    '  --engine-src PATH       Andy.Engine checkout (default: ../andy-engine)' \
    '  --tui-src PATH          Andy.Tui checkout (default: ../andy-tui2)' \
    '  --engine-revision REF   Required checked-out Engine revision (default: HEAD)' \
    '  --tui-revision REF      Required checked-out TUI revision (default: HEAD)' \
    '  --help                  Show this help'
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --engine-src)
      ENGINE_SRC="${2:?--engine-src requires a path}"
      shift 2
      ;;
    --tui-src)
      TUI_SRC="${2:?--tui-src requires a path}"
      shift 2
      ;;
    --engine-revision)
      ENGINE_REVISION="${2:?--engine-revision requires a ref}"
      shift 2
      ;;
    --tui-revision)
      TUI_REVISION="${2:?--tui-revision requires a ref}"
      shift 2
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      printf 'check-source-compat: unknown argument: %s\n' "$1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

ENGINE_SRC="$(cd "${ENGINE_SRC}" 2>/dev/null && pwd)" || {
  printf "check-source-compat: Engine source repo not found at '%s'.\n" "${ENGINE_SRC}" >&2
  exit 2
}
TUI_SRC="$(cd "${TUI_SRC}" 2>/dev/null && pwd)" || {
  printf "check-source-compat: TUI source repo not found at '%s'.\n" "${TUI_SRC}" >&2
  exit 2
}

ENGINE_PROJECT="${ENGINE_SRC}/src/Andy.Engine/Andy.Engine.csproj"
TUI_PROJECT="${TUI_SRC}/src/Andy.Tui/Andy.Tui.csproj"
for project in "${ENGINE_PROJECT}" "${TUI_PROJECT}"; do
  if [ ! -f "${project}" ]; then
    printf "check-source-compat: required project not found at '%s'.\n" "${project}" >&2
    exit 2
  fi
done

resolve_revision() {
  local repo="$1"
  local requested="$2"
  local label="$3"
  local head
  local expected
  head="$("${GIT_CMD}" -C "${repo}" rev-parse HEAD)"
  expected="$("${GIT_CMD}" -C "${repo}" rev-parse "${requested}^{commit}")"
  if [ "${head}" != "${expected}" ]; then
    printf 'check-source-compat: %s checkout is at %s, not requested revision %s (%s).\n' \
      "${label}" "${head}" "${requested}" "${expected}" >&2
    exit 2
  fi
  printf '%s' "${head}"
}

ENGINE_SHA="$(resolve_revision "${ENGINE_SRC}" "${ENGINE_REVISION}" 'Engine')"
TUI_SHA="$(resolve_revision "${TUI_SRC}" "${TUI_REVISION}" 'TUI')"

snapshot_status() {
  "${GIT_CMD}" -C "$1" status --porcelain=v1 --untracked-files=all
}

CLI_STATUS_BEFORE="$(snapshot_status "${REPO_ROOT}")"
ENGINE_STATUS_BEFORE="$(snapshot_status "${ENGINE_SRC}")"
TUI_STATUS_BEFORE="$(snapshot_status "${TUI_SRC}")"

mkdir -p "${TEMP_PARENT}"
TEMP_ROOT="$(mktemp -d "${TEMP_PARENT%/}/andy-source-compat.XXXXXX")"
WORK_CLI="${TEMP_ROOT}/andy-cli"
OVERLAY_TARGETS="${TEMP_ROOT}/SourceCompat.targets"

cleanup() {
  local exit_code=$?
  if [ -n "${TEMP_ROOT:-}" ] && [ -d "${TEMP_ROOT}" ]; then
    rm -rf -- "${TEMP_ROOT}"
  fi
  exit "${exit_code}"
}
trap cleanup EXIT INT TERM

mkdir -p "${WORK_CLI}"
tar -C "${REPO_ROOT}" \
  --exclude='.git' \
  --exclude='TestResults' \
  --exclude='artifacts' \
  --exclude='packages.lock.json' \
  --exclude='*/bin' \
  --exclude='*/obj' \
  -cf - . | tar -C "${WORK_CLI}" -xf -

cat > "${OVERLAY_TARGETS}" <<'EOF'
<Project>
  <ItemGroup Condition="'$(MSBuildProjectName)' == 'Andy.Cli' or '$(MSBuildProjectName)' == 'Andy.Cli.Tests'">
    <PackageReference Remove="Andy.Engine" />
    <PackageReference Remove="Andy.Tui" />
    <ProjectReference Include="$(SourceCompatEngineProject)" PrivateAssets="All" />
    <ProjectReference Include="$(SourceCompatTuiRoot)/src/*/*.csproj"
                      Exclude="$(SourceCompatTuiRoot)/src/Andy.Tui/Andy.Tui.csproj"
                      PrivateAssets="All" />
  </ItemGroup>
</Project>
EOF

read_package_version() {
  local project="$1"
  local version
  version="$("${DOTNET_CMD}" msbuild "${project}" -nologo -getProperty:PackageVersion | tail -n 1)"
  if [ -z "${version}" ]; then
    version='unknown'
  fi
  printf '%s' "${version}"
}

ENGINE_PACKAGE_VERSION="$(read_package_version "${ENGINE_PROJECT}")"
TUI_PACKAGE_VERSION="$(read_package_version "${TUI_PROJECT}")"

COMMON_PROPERTIES=(
  "-p:DirectoryBuildTargetsPath=${OVERLAY_TARGETS}"
  "-p:SourceCompatEngineProject=${ENGINE_PROJECT}"
  "-p:SourceCompatTuiRoot=${TUI_SRC}"
  '-p:RestorePackagesWithLockFile=false'
  '-p:RestoreForceEvaluate=true'
  '-p:CI=true'
)
COMPAT_PROJECT="${WORK_CLI}/tests/Andy.Cli.Tests/Andy.Cli.Tests.csproj"

printf 'check-source-compat: engine source = %s (%s)\n' "${ENGINE_SRC}" "${ENGINE_SHA}"
printf 'check-source-compat: tui source    = %s (%s)\n' "${TUI_SRC}" "${TUI_SHA}"
printf 'check-source-compat: workspace     = %s\n' "${WORK_CLI}"

"${DOTNET_CMD}" restore "${COMPAT_PROJECT}" "${COMMON_PROPERTIES[@]}"
"${DOTNET_CMD}" build "${COMPAT_PROJECT}" \
  --configuration Release --no-restore "${COMMON_PROPERTIES[@]}"
"${DOTNET_CMD}" test "${COMPAT_PROJECT}" \
  --configuration Release --no-build --no-restore "${COMMON_PROPERTIES[@]}"

verify_unchanged() {
  local repo="$1"
  local before="$2"
  local label="$3"
  local after
  after="$(snapshot_status "${repo}")"
  if [ "${after}" != "${before}" ]; then
    printf 'check-source-compat: %s working tree changed during the compatibility run.\n' "${label}" >&2
    return 1
  fi
}

verify_unchanged "${REPO_ROOT}" "${CLI_STATUS_BEFORE}" 'CLI'
verify_unchanged "${ENGINE_SRC}" "${ENGINE_STATUS_BEFORE}" 'Engine'
verify_unchanged "${TUI_SRC}" "${TUI_STATUS_BEFORE}" 'TUI'

json_escape() {
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  value="${value//$'\n'/\\n}"
  printf '%s' "${value}"
}

printf 'SOURCE_COMPAT_SUMMARY={"status":"passed","engine":{"revision":"%s","package_version":"%s"},"tui":{"revision":"%s","package_version":"%s"}}\n' \
  "$(json_escape "${ENGINE_SHA}")" \
  "$(json_escape "${ENGINE_PACKAGE_VERSION}")" \
  "$(json_escape "${TUI_SHA}")" \
  "$(json_escape "${TUI_PACKAGE_VERSION}")"
