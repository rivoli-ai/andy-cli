# Continuous integration and releases

Updated: 2026-07-21

Pull requests, pushes to `main`, and releases share the validation workflow in
`.github/workflows/validate.yml`.

## Workflows

| File | Trigger | Purpose |
| --- | --- | --- |
| `validate.yml` | `workflow_call` | Reusable build/test, security, and packaged-smoke gates. |
| `ci.yml` | Pull requests and pushes to `main` | Calls `validate.yml`; this is the normal required CI check. |
| `release.yml` | Tags matching `v*` or manual dispatch | Validates, builds six self-contained archives, generates checksums/SBOM, smoke-tests supported runners, and publishes only for a tag. |
| `release-signed-todo.yml` | Manual dispatch | Explicitly unsigned placeholder for future signing/notarization work. Do not treat its output as signed. |

All third-party GitHub Actions currently used by these workflows are pinned to
immutable commit SHAs. Human-readable version comments are informational; the
SHA is the executed version.

## Reusable validation gates

`validate.yml` contains three jobs.

### Build, format, and test

Runs on Ubuntu with .NET 8 and performs:

1. `dotnet restore Andy.Cli.sln --locked-mode`
2. Release build with compiler warnings visible.
3. `dotnet format Andy.Cli.sln --verify-no-changes --no-restore`.
4. The full xUnit suite with Coverlet Cobertura coverage.
5. A ReportGenerator summary in the job summary and coverage artifacts retained
   for 14 days.

Warnings are not suppressed or promoted to errors. Formatting is a hard gate.

### Dependency and secret security

Runs a transitive NuGet vulnerability audit and fails when the report contains a
High or Critical advisory. A command/network failure also fails the job; it is
never interpreted as a clean scan.

The job downloads a pinned gitleaks release, verifies its SHA-256, and scans the
checked-out tree with `--no-git`. Bump the gitleaks version and checksum together.

### Packaged smoke and headless contract

On Ubuntu and macOS, CI:

- Runs the headless JSON schema/contract tests without model credentials.
- Publishes a self-contained single-file binary for the runner.
- Executes `andy-cli --version` against the packaged artifact.
- Drives the packaged ACP server through `initialize` and `session/new` over
  stdio without sending a model prompt or requiring provider credentials.

The release smoke matrix runs the same ACP handshake through
`scripts/smoke-test.sh` on Linux x64, macOS x64/ARM64, and Windows x64. This
catches trimming, packaging, native-runtime, ACP serialization, and schema
issues that a normal framework-dependent build can miss.

## Release pipeline

`release.yml` is the supported release workflow. It:

1. Requires the reusable validation and an additional release-tree secret scan.
2. Restores the locked dependency graph.
3. Uses `scripts/build-release.sh` to build macOS, Linux, and Windows artifacts
   for x64 and ARM64.
4. Generates a CycloneDX JSON SBOM.
5. Generates per-archive and aggregate SHA-256 checksums.
6. Smoke-tests Linux x64, macOS ARM64, macOS x64 under Rosetta, and Windows x64.
7. Publishes the GitHub Release, checksums, SBOM, and build provenance only when
   the workflow was triggered by a tag.

Linux ARM64 and Windows ARM64 artifacts are built but are not smoke-tested
because the workflow does not currently use native runners for those targets.

## Manual source compatibility

`source-compat.yml` is an opt-in `workflow_dispatch` check for changes spanning
andy-cli, andy-engine, and andy-tui2. Its Engine and TUI revision inputs are
checked out explicitly, then `scripts/check-source-compat.sh` builds and runs
the full CLI test project against those source trees. It does not replace the
package-based validation or release gates.

The `release-signed-todo.yml` workflow is intentionally named and labeled as an
unsigned placeholder. Signing certificates, notarization, verification steps,
and maintainer-owned secrets must be configured before it can become a signed
release path.

## Local equivalents

Run the relevant gates before pushing:

```bash
dotnet restore Andy.Cli.sln --locked-mode
dotnet build Andy.Cli.sln --configuration Release --no-restore
dotnet format Andy.Cli.sln --verify-no-changes --no-restore
dotnet test Andy.Cli.sln \
  --configuration Release \
  --no-build \
  --collect:"XPlat Code Coverage" \
  --results-directory ./TestResults
dotnet list Andy.Cli.sln package --vulnerable --include-transitive
```

To fix formatting, run `dotnet format Andy.Cli.sln` and review the resulting
diff. To render a local coverage summary:

```bash
reportgenerator \
  -reports:"./TestResults/*/coverage.cobertura.xml" \
  -targetdir:"./TestResults/CoverageReport" \
  -reporttypes:TextSummary
```

After an intentional dependency change, refresh every committed lock file with
`dotnet restore Andy.Cli.sln --force-evaluate`, review the lock-file diff, and
commit it with the package change.

Run the packaged smoke suite used by the release workflow with:

```bash
scripts/build-release.sh 0.0.0-local
mkdir -p /tmp/andy-cli-smoke
tar -xzf release-artifacts/andy-cli-macos-arm64.tar.gz \
  -C /tmp/andy-cli-smoke
scripts/smoke-test.sh /tmp/andy-cli-smoke 0.0.0-local
```

Select the archive for the current host. The workflow files are the authoritative
executable specification when this guide and CI differ.
