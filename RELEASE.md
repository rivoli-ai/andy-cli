# Release Process Documentation

This document describes how to create and publish releases for Andy CLI.

## Quick Start

To create a new release:

1. Go to **Actions** tab in GitHub
2. Select **Release** workflow (or **Release (Signed)** for signed builds)
3. Click **Run workflow**
4. Enter the version (e.g., `v1.0.0`)
5. Choose if it's a pre-release
6. Click **Run workflow**

The workflow will:
- Build binaries for all platforms
- Run tests
- Generate checksums
- Create a GitHub Release with downloadable artifacts

## Workflows

### `release.yml` - Standard Release

**Use this for:** Quick releases without code signing

**Platforms built:**
- macOS ARM64 (Apple Silicon)
- macOS x64 (Intel)
- Linux x64
- Linux ARM64
- Windows x64

**Artifacts generated:**
- Trimmed, single-file binaries
- Compressed archives (.tar.gz for Unix, .zip for Windows)
- SHA256 checksums for all archives

### `release-signed.yml` - Signed Release

**Use this for:** Production releases with code signing

**Additional features:**
- Code signing support for macOS (commented out, requires setup)
- GPG signing support for Linux (commented out, requires setup)
- Authenticode signing support for Windows (commented out, requires setup)

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
3. Add secrets in GitHub: Settings → Secrets and variables → Actions
4. Uncomment the signing steps in `release-signed.yml`

### Linux GPG Signing

Required secrets:
- `GPG_PRIVATE_KEY` - Base64-encoded GPG private key
- `GPG_PASSPHRASE` - GPG key passphrase

Steps to set up:
1. Generate or export your GPG key: `gpg --armor --export-secret-keys YOUR_KEY_ID | base64`
2. Add secrets in GitHub
3. Uncomment the signing steps in `release-signed.yml`

### Windows Authenticode Signing

Required secrets:
- `WINDOWS_CERTIFICATE` - Base64-encoded .pfx certificate
- `WINDOWS_CERTIFICATE_PASSWORD` - Certificate password

Steps to set up:
1. Obtain a code signing certificate from a trusted CA
2. Export as .pfx and encode: `[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx"))`
3. Add secrets in GitHub
4. Uncomment the signing steps in `release-signed.yml`

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

## Manual Release (Alternative)

If you prefer local builds:

```bash
# Build all platforms locally
./scripts/build-release.sh v1.0.0

# This will create release-artifacts/ with all binaries
# Upload manually to GitHub Releases
```

(Note: build-release.sh script would need to be created)

## Questions?

For questions about the release process:
- Open an issue with the `question` label
- Tag maintainers: @your-username
