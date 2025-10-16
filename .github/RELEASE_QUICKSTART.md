# Release Quick Start Guide

## Creating a Release (Simple)

1. **Go to GitHub Actions**
   - Navigate to your repository on GitHub
   - Click the **Actions** tab
   - Click **Release** workflow in the left sidebar

2. **Run the workflow**
   - Click **Run workflow** button (top right)
   - Enter version: `v1.0.0` (must start with 'v')
   - Check "Mark as pre-release" if testing
   - Click **Run workflow**

3. **Wait for completion** (~10-15 minutes)
   - Watch the workflow run
   - Check that all jobs complete successfully
   - Green checkmarks = success

4. **Edit the release**
   - Go to **Releases** tab
   - Click on the newly created release
   - Click **Edit release**
   - Add your release notes (see template below)
   - Click **Update release**

5. **Announce**
   - Share the release link
   - Users can download binaries for their platform

## Release Notes Template (Quick)

```markdown
## What's New

- Added feature X
- Improved performance of Y
- Fixed bug where Z

## Breaking Changes

- None

## Installation

See the installation instructions below for your platform.
```

## When to Release

- **Patch** (v1.0.1): Bug fixes only
- **Minor** (v1.1.0): New features, no breaking changes
- **Major** (v2.0.0): Breaking changes
- **Pre-release** (v1.0.0-beta.1): Testing new features

## Troubleshooting

**Workflow fails?**
- Check the logs in Actions tab
- Look for red X marks
- Click on failed job to see error details

**Need to delete a release?**
- Go to Releases
- Click on the release
- Click **Delete** button
- Also delete the git tag: `git push --delete origin v1.0.0`

**Made a mistake in release notes?**
- You can edit them anytime
- Go to Releases → Click release → Edit

## That's it!

For more details, see [RELEASE.md](../RELEASE.md)
