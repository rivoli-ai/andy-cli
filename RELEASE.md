# Release Process Documentation

This document describes how to create and publish releases for Andy CLI.

## Quick Start

Releases are produced **only in CI**, from the clean revision that a version tag points at.
Do not upload locally built binaries to a GitHub Release; `scripts/build-release.sh` is for
local verification only.

To cut a release:

1. Ensure `main` is green and the version is what you want to ship.
2. Create and push a version tag:
   ```bash
   git tag v2026.6.16
   git push origin v2026.6.16
   ```
3. The **Release Build** workflow (`.github/workflows/release.yml`) runs automatically on the
   pushed tag. You can also trigger it manually from the **Actions** tab (builds and smoke-tests;
   it only publishes a GitHub Release when triggered by a tag).

The workflow runs this pipeline:
- **secret-scan** (gate) - pinned, checksum-verified gitleaks scan of the working tree
- **validate** (gate) - restore, build, and the full test suite (mirrors issue #171's quality gates)
- **build** - six self-contained platform binaries plus a CycloneDX SBOM (`sbom.json`)
- **smoke-test** - exercises each binary via `scripts/smoke-test.sh`
- **release** - generates `SHA256SUMS.txt`, records signed build-provenance attestations, and
  publishes the GitHub Release with all artifacts

### Supply-chain hardening

- Every third-party GitHub action is pinned to an immutable commit SHA (the trailing `# vX.Y.Z`
  comment is informational). When bumping an action, update both the SHA and the comment.
- The gitleaks scanner is pinned to a fixed version and its archive checksum is verified with
  `sha256sum -c` before it is executed - there is no "download latest". Bump `GITLEAKS_VERSION`
  and `GITLEAKS_LINUX_X64_SHA256` together (checksum from the release's `*_checksums.txt`).
- The SBOM tool (`CycloneDX`) is pinned via `CYCLONEDX_VERSION`.
- Release binaries are never committed. Published build output is git-ignored (see
  [Regenerating publish output](#regenerating-publish-output-local)).

## Workflows

### `release.yml` - Standard Release

**Use this for:** Quick releases without code signing

**Platforms built:**
- macOS ARM64 (Apple Silicon)
- macOS x64 (Intel)
- Linux x64
- Linux ARM64
- Windows x64
- Windows ARM64

**Artifacts generated:**
- Trimmed, single-file binaries
- Compressed archives (.tar.gz for Unix, .zip for Windows)
- SHA256 checksums for all archives (`SHA256SUMS.txt`)
- A CycloneDX SBOM (`sbom.json`)
- Signed build-provenance attestations for every archive (verify with
  `gh attestation verify <archive> --repo <owner>/<repo>`)

### `release-signed-todo.yml` - Signing NOT yet configured

**Status: this workflow does NOT produce signed output.** It was previously named
`release-signed.yml` / "Release (Signed)", but all signing and notarization steps are commented
out and no signing certificates are configured, so it builds the same unsigned binaries as
`release.yml`. It has been renamed and relabeled ("Release (Unsigned - signing NOT configured)")
so it does not falsely claim signed output, and it runs on manual dispatch only.

To turn it into a real signed release: add the certificate secrets (below), uncomment and finish
the signing/notarization steps, add a per-platform verification step that fails the job on an
unsigned binary, then rename it back. Until then, use `release.yml` for actual releases.

**Signing scaffolding present (commented out, requires setup):**
- Code signing support for macOS
- GPG signing support for Linux
- Authenticode signing support for Windows

## Setting Up Code Signing (Optional)

Code signing increases user trust by verifying that binaries come from you and haven't been tampered with.

### macOS Code Signing

Required secrets:
- `MACOS_CERTIFICATE` - Base64-encoded .p12 certificate
- `MACOS_CERTIFICATE_PWD` - Certificate password
- `APPLE_DEVELOPER_ID` - Developer ID Application identity
- `APPLE_ID` - Your Apple ID email
- `APPLE_ID_PASSWORD` - App-specific password
- `APPLE_TEAM_ID` - Your Apple Team ID

Steps to set up:
1. Export your Developer ID certificate from Keychain Access as .p12
2. Encode it: `base64 -i certificate.p12 | pbcopy`
3. Add secrets in GitHub: Settings -> Secrets and variables -> Actions
4. Uncomment the signing steps in `release-signed-todo.yml`

### Linux GPG Signing

Required secrets:
- `GPG_PRIVATE_KEY` - Base64-encoded GPG private key
- `GPG_PASSPHRASE` - GPG key passphrase

Steps to set up:
1. Generate or export your GPG key: `gpg --armor --export-secret-keys YOUR_KEY_ID | base64`
2. Add secrets in GitHub
3. Uncomment the signing steps in `release-signed-todo.yml`

### Windows Authenticode Signing

Required secrets:
- `WINDOWS_CERTIFICATE` - Base64-encoded .pfx certificate
- `WINDOWS_CERTIFICATE_PASSWORD` - Certificate password

Steps to set up:
1. Obtain a code signing certificate from a trusted CA
2. Export as .pfx and encode: `[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx"))`
3. Add secrets in GitHub
4. Uncomment the signing steps in `release-signed-todo.yml`

## Release Checklist

Before creating a release:

- [ ] All tests pass locally
- [ ] CHANGELOG.md is updated
- [ ] Version is bumped in relevant files
- [ ] Main branch is stable
- [ ] All PRs for the release are merged
- [ ] Review open issues and close resolved ones

## Release Notes

The workflow automatically generates a release notes template with:
- Download links for all platforms
- Installation instructions
- Checksum verification commands

You should **edit the release after creation** to add:
- What's new in this version
- Bug fixes
- Breaking changes
- Known issues

Use `.github/RELEASE_TEMPLATE.md` as a guide.

## Versioning

We follow [Semantic Versioning](https://semver.org/):

- **Major** (v2.0.0): Breaking changes
- **Minor** (v1.1.0): New features, backward compatible
- **Patch** (v1.0.1): Bug fixes, backward compatible
- **Pre-release** (v1.0.0-beta.1): Testing versions

## Testing Releases

### Pre-releases

Mark a release as "pre-release" when:
- Testing new features
- Beta/RC versions
- Breaking changes that need validation

Pre-releases:
- Are marked as "Pre-release" on GitHub
- Don't show as "Latest" release
- Are visible to users who opt-in

### Testing locally before release

Build and test manually:

```bash
# Build for your platform
dotnet publish src/Andy.Cli/Andy.Cli.csproj \
  --configuration Release \
  --runtime osx-arm64 \
  --self-contained true \
  --output ./test-publish \
  -p:PublishTrimmed=true \
  -p:PublishSingleFile=true

# Test the binary
./test-publish/andy-cli
```

## Troubleshooting

### Build fails on a platform

- Check the Actions logs for specific errors
- Test locally with the same `dotnet publish` command
- Ensure all dependencies support the target runtime

### Release workflow doesn't start

- Check you have permissions to run workflows
- Verify the workflow file syntax is correct
- Check branch protection rules

### Artifacts are too large

The current configuration uses:
- PublishTrimmed: true (reduces size ~70%)
- PublishSingleFile: true (single binary)

Typical sizes:
- macOS ARM64: ~29 MB
- macOS x64: ~29 MB
- Linux x64: ~28 MB
- Linux ARM64: ~28 MB
- Windows x64: ~28 MB
- Windows ARM64: ~28 MB

### Code signing fails

- Verify all secrets are set correctly
- Check certificate expiration dates
- Ensure certificate matches the signing identity
- Review Apple/Microsoft/GPG documentation for recent changes

## Post-Release

After creating a release:

1. **Test the downloads**: Download and test each platform's binary
2. **Update documentation**: Update README if needed
3. **Announce**: Share on social media, Discord, etc.
4. **Monitor**: Watch for user feedback and issues
5. **Plan next release**: Add items to project board

## Regenerating publish output (local)

Published build output (the `publish/` directory, self-contained binaries, `*.pdb` debug
symbols, SBOMs, checksums, and archives) is **not tracked in git** - it is listed in
`.gitignore`. Official release artifacts are produced only in CI from a clean tagged revision.

To regenerate the ignored output locally for testing:

```bash
# All six platform archives + checksums into ./release-artifacts (git-ignored)
./scripts/build-release.sh v0.0.0-local

# Or a single-platform publish into ./publish (git-ignored)
dotnet publish src/Andy.Cli/Andy.Cli.csproj \
  --configuration Release \
  --runtime osx-arm64 \
  --self-contained true \
  --output ./publish/andy-cli-macos-arm64 \
  -p:PublishTrimmed=true \
  -p:PublishSingleFile=true
```

These outputs are for local verification only. Do not `git add` them and do not upload
locally built binaries to a GitHub Release - let CI build and publish from the tag.

## Local build (verification only, do NOT publish)

`scripts/build-release.sh` produces the same six archives locally so you can verify a build
before tagging. Never upload these to a GitHub Release - releases must come from CI:

```bash
# Build all platforms locally into release-artifacts/ (git-ignored)
./scripts/build-release.sh v0.0.0-local
```

The `scripts/build-release.sh` script already exists and is executable, so you can run it directly as shown above.

## Questions?

For questions about the release process:
- Open an issue with the `question` label
- Tag maintainers: @your-username
