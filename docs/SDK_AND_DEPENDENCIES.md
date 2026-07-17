# SDK and Dependency Policy

This document describes how andy-cli pins its .NET SDK, records its known-good
dependency graph, and checks compatibility with the Andy engine and TUI. It
covers the requirements of issue #172.

## 1. .NET SDK band

andy-cli targets `net8.0` and must be built with a **.NET 8 SDK**. The SDK is
pinned in [`global.json`](../global.json):

```json
{
  "sdk": {
    "version": "8.0.302",
    "rollForward": "latestFeature",
    "allowPrerelease": false
  }
}
```

- `version: 8.0.302` selects the 8.0.3xx feature band as the floor.
- `rollForward: latestFeature` allows the newest installed feature band **within
  .NET 8** (for example 8.0.4xx) but never rolls forward to .NET 9 or later. This
  is deliberately different from `latestMajor`, which previously let a host with
  only .NET 9 installed build the CLI against the wrong toolchain and silently
  pull in C# 13 / .NET 9-only language behavior.
- The pin is intentionally not tighter than a feature band, so any 8.0.3xx (or
  newer 8.0.x) SDK satisfies it and 8.0.302 remains valid where available.

### Why this matters

Building `net8.0` code with a .NET 9 SDK changes the effective C# language
version and can let .NET 9-only API overloads (for example the
`System.Text.Json` `params ReadOnlySpan<>` constructors, which require C# 13
`params collections`) compile locally yet fail on a real .NET 8 SDK. Pinning the
band keeps local dev and CI on the same compiler.

### How to update the SDK band

1. Decide the new floor version (for example moving to a newer 8.0.x patch, or a
   future major-version migration).
2. Edit `global.json` (`version`, and `rollForward` only if the major changes).
3. Update the `sdk` block in [`dependency-manifest.json`](../dependency-manifest.json).
4. Run `dotnet --version` from the repo root and confirm it reports the intended
   band, then `dotnet restore Andy.Cli.sln --locked-mode` and
   `dotnet build Andy.Cli.sln`.
5. For a major-version move, regenerate the lock files (`dotnet restore
   --force-evaluate`) and re-run the full test suite.

### CI gate (SDK check)

CI must fail fast if the selected SDK is not .NET 8. Use the helper script:

```bash
scripts/assert-sdk-version.sh          # exits non-zero unless dotnet --version starts with 8.
```

Recommended workflow step (if issue #171 owns the CI workflow, add this step
there; the script is the portable contract either way):

```yaml
- uses: actions/setup-dotnet@v4
  with:
    dotnet-version: '8.0.x'
- name: Assert .NET 8 SDK
  run: scripts/assert-sdk-version.sh
```

## 2. Machine-readable dependency manifest

[`dependency-manifest.json`](../dependency-manifest.json) at the repo root
records the exact, known-good versions the CLI is verified against: the SDK band
plus the Andy ecosystem packages (Andy.Engine, Andy.Tui,
Andy.CodeIndex.Infrastructure, Andy.Permissions, and the rest of the Andy.*
graph). Update it in the same commit that changes `global.json` or any Andy.*
`PackageReference` in `src/Andy.Cli/Andy.Cli.csproj`.

## 3. Reproducible restore (NuGet lock files)

Every project enables `RestorePackagesWithLockFile`, so restore is pinned by a
committed `packages.lock.json`:

- `src/Andy.Cli/packages.lock.json`
- `src/Andy.Cli.Headless.Contract/packages.lock.json`
- `tests/Andy.Cli.Tests/packages.lock.json`

Release and CI builds should restore in locked mode so the graph cannot drift:

```bash
dotnet restore Andy.Cli.sln --locked-mode
```

Regenerate the lock files intentionally (after a deliberate dependency bump)
with `dotnet restore Andy.Cli.sln --force-evaluate` and commit the result.

## 4. API / contract compatibility with engine and TUI

andy-cli shares contracts with andy-engine (engine events, tool contexts) and
andy-tui2 (TUI primitives). Those are consumed as NuGet packages; the versions
in the manifest are the source of truth for what the CLI is known-good against.

To catch contract drift **before** a package is cut, build the CLI against the
sibling source repos. A full cross-repo composite build cannot run in this
repo's CI (the sibling paths are not guaranteed to exist on the runner), so it
is documented here as the recommended approach and provided as a local helper
stub:

- **Local composite build:** clone `rivoli-ai/andy-engine` and
  `rivoli-ai/andy-tui2` next to this repo and run
  `scripts/check-source-compat.sh` (honors `ENGINE_SRC` / `TUI_SRC`). The stub
  detects the source repos and documents the pack/ProjectReference wiring a full
  implementation would add.
- **Recommended CI equivalent (owned by the CI epic, e.g. #171):** a separate,
  opt-in matrix job that checks out andy-engine and andy-tui2 at chosen
  revisions (composite checkout or submodule), builds the CLI against them, and
  runs the test suite. Keep it distinct from the default package-based build so
  the reproducible, pinned-graph build stays the release gate.
