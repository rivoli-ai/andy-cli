# SDK and Dependency Policy

Updated: 2026-07-21

This document describes how Andy CLI pins its .NET SDK, records its known-good
dependency graph, and tracks compatibility with the Andy engine and TUI.

## 1. .NET SDK band

andy-cli targets `net8.0` and must be built with a **.NET 8 SDK**. The SDK is
pinned in [`global.json`](../global.json):

```json
{
  "sdk": {
    "version": "8.0.0",
    "rollForward": "latestMinor",
    "allowPrerelease": false
  }
}
```

- `version: 8.0.0` sets the .NET 8 line as the floor without pinning a specific
  feature band, so any installed 8.0.x SDK satisfies it.
- `rollForward: latestMinor` selects the **highest installed .NET 8 SDK** on the
  host (for example 8.0.100, 8.0.204, or 8.0.4xx) but never rolls forward to
  .NET 9 or later. This is deliberately different from `latestMajor`, which
  previously let a host with only .NET 9 installed build the CLI against the
  wrong toolchain and silently pull in C# 13 / .NET 9-only language behavior.
- The policy accepts any installed .NET 8.x SDK, so local developers whose only
  .NET 8 SDK is a lower feature band (for example 8.0.100 or 8.0.204) are not
  hard-failed, while .NET 9.x hosts are still refused.

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

The release workflows run the following gate. The reusable PR validation relies
on `actions/setup-dotnet` plus `global.json` and does not currently invoke the
helper directly.

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

Release builds restore in locked mode so the graph cannot drift:

```bash
dotnet restore Andy.Cli.sln --locked-mode
```

Regenerate the lock files intentionally (after a deliberate dependency bump)
with `dotnet restore Andy.Cli.sln --force-evaluate` and commit the result.

The reusable PR validation currently runs `dotnet restore Andy.Cli.sln` without
`--locked-mode`. Because lock-file generation is enabled, that restore still
uses and updates the locked graph, but CI should eventually use `--locked-mode`
to fail instead of accepting a lock-file change in the ephemeral checkout.

## 4. API / contract compatibility with engine and TUI

andy-cli shares contracts with andy-engine (engine events, tool contexts) and
andy-tui2 (TUI primitives). Those are consumed as NuGet packages; the versions
in the manifest are the source of truth for what the CLI is known-good against.

The package-based build is the only implemented compatibility gate today.
`scripts/check-source-compat.sh` is explicitly a discovery stub: it verifies that
configured sibling checkout paths exist, then reports that composite build
wiring is not implemented. It does **not** replace package references, build the
sibling repositories, or test source compatibility.

A real source-compatibility gate would need to:

1. Check out or locate `rivoli-ai/andy-engine` and `rivoli-ai/andy-tui2` at known
   revisions.
2. Pack them into an isolated local feed or inject temporary project references.
3. Restore/build/test Andy CLI against those artifacts.
4. Leave the committed package references and lock files unchanged.

Until that exists, update `dependency-manifest.json` in the same commit as an
Andy package bump and rely on the pinned package graph as the release contract.

## Current known-good snapshot

Do not duplicate the full package list in prose. As of 2026-07-21 the manifest
records Andy.Engine `2026.7.21-rc.78`, Andy.Tui `2026.7.21-rc.162`, and the exact
versions of every other direct Andy package. `dependency-manifest.json`, the
project files, and `packages.lock.json` are authoritative if those versions
change.
