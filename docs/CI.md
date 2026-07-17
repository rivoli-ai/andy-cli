# Continuous Integration

This repository enforces a single set of quality gates on every pull request,
every push to `main`, and every release. The gates live in a reusable workflow
so pull-request CI and the release workflows run exactly the same checks.

## Workflows

| File | Trigger | Purpose |
| --- | --- | --- |
| `.github/workflows/validate.yml` | `workflow_call` (reusable) | Single source of truth for all validation gates. |
| `.github/workflows/ci.yml` | `pull_request`, `push` to `main` | Required status check. Calls `validate.yml`. |
| `.github/workflows/release.yml` | tag `v*`, manual dispatch | Cross-platform release. `build` needs `validate`. |
| `.github/workflows/release-signed.yml` | manual dispatch | Signed release. Every build job needs `validate`. |

Because the release workflows depend on `validate` via `needs:`, a release can
never be built or published on a tree that has not passed the full test suite,
the format check, the vulnerability audit, and the secret scan.

## Required checks

The `validate.yml` pipeline runs three jobs:

1. **Build, format and test** (`ubuntu-latest`)
   - `dotnet restore`
   - `dotnet build` in Release with compiler warnings left visible (warnings are
     neither suppressed nor promoted to errors).
   - `dotnet format --verify-no-changes` (fails if the tree is not formatted).
   - The FULL test suite with `--collect:"XPlat Code Coverage"`.
   - A readable coverage summary rendered by ReportGenerator into the job summary
     panel, plus the Cobertura report uploaded as an artifact.

2. **Vulnerability audit and secret scan** (`ubuntu-latest`)
   - `dotnet list package --vulnerable --include-transitive`; the job FAILS if any
     `High` or `Critical` advisory affects a direct or transitive package.
   - `gitleaks detect --no-git` over the working tree. The pinned gitleaks binary
     is downloaded and run directly (no third-party marketplace Action executes in
     CI), matching the existing release workflow.

3. **Packaged smoke and headless contract** (`ubuntu-latest` and `macos-latest`)
   - Runs the headless contract/schema tests on each OS (hermetic; no model key
     or network needed).
   - Publishes the self-contained single-file CLI for the runner and smoke-tests
     it with `andy-cli --version`.

## Supply-chain notes

All `actions/*` steps are pinned to a full commit SHA (with the human-readable
version in a trailing comment) rather than a floating tag, per issue #173. When
bumping an action, update both the SHA and the comment.

## Local equivalents

Run these before pushing to reproduce the CI gates locally. They use the same
solution and project paths as CI.

```bash
# Restore
dotnet restore Andy.Cli.sln

# Build with warnings visible (do not suppress warnings)
dotnet build Andy.Cli.sln --configuration Release --no-restore

# Verify formatting (CI runs --verify-no-changes; run without it to auto-fix)
dotnet format Andy.Cli.sln --verify-no-changes
#   ...or fix in place:
dotnet format Andy.Cli.sln

# Full test suite with coverage
dotnet test Andy.Cli.sln \
  --collect:"XPlat Code Coverage" \
  --results-directory ./TestResults

# Optional: render the coverage summary locally
reportgenerator \
  -reports:"./TestResults/*/coverage.cobertura.xml" \
  -targetdir:"./TestResults/CoverageReport" \
  -reporttypes:TextSummary

# Transitive vulnerability audit (CI fails on High/Critical)
dotnet list Andy.Cli.sln package --vulnerable --include-transitive

# Headless contract/schema tests only
dotnet test tests/Andy.Cli.Tests/Andy.Cli.Tests.csproj \
  --filter "FullyQualifiedName~HeadlessConfigSchemaTests|FullyQualifiedName~HeadlessConfigContractTests"

# Packaged-binary smoke test (adjust the runtime for your platform)
dotnet publish src/Andy.Cli/Andy.Cli.csproj \
  --configuration Release --runtime osx-arm64 \
  --self-contained true --output ./smoke-publish \
  -p:PublishSingleFile=true
./smoke-publish/andy-cli --version
```
