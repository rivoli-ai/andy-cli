#!/bin/bash

# Build release binaries for all platforms
# Usage: ./scripts/build-release.sh [version]

set -e

VERSION=${1:-"v0.0.0-local"}
OUTPUT_DIR="release-artifacts"

# The version shown to users (VersionInfo reads AssemblyInformationalVersion, which accepts any
# string), with any leading "v" stripped.
RAW_VERSION="${VERSION#v}"

# AssemblyVersion/FileVersion must be a numeric, dot-separated System.Version with each component
# in 0..65534 — a value like "2026061601" or a SemVer pre-release would fail the build. Derive a
# safe numeric version from RAW_VERSION (first up-to-4 digit groups, each clamped), defaulting to
# 0.0.0. The display string still comes from InformationalVersion below, so users see RAW_VERSION.
SAFE_VERSION="$(printf '%s' "$RAW_VERSION" | grep -oE '[0-9]+' | head -4 \
    | awk '{ n=$1+0; if (n>65534) n=65534; print n }' | paste -sd. -)"
[ -z "$SAFE_VERSION" ] && SAFE_VERSION="0.0.0"

echo "Building Andy CLI $RAW_VERSION (assembly version $SAFE_VERSION)"
echo "================================"

# Clean previous builds
rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

# Array of platforms to build
declare -a PLATFORMS=(
    "osx-arm64:andy-cli-macos-arm64"
    "osx-x64:andy-cli-macos-x64"
    "linux-x64:andy-cli-linux-x64"
    "linux-arm64:andy-cli-linux-arm64"
    "win-x64:andy-cli-windows-x64"
    "win-arm64:andy-cli-windows-arm64"
)

# Build each platform
for platform in "${PLATFORMS[@]}"; do
    IFS=':' read -r runtime artifact_name <<< "$platform"

    echo ""
    echo "Building $artifact_name ($runtime)..."
    echo "--------------------------------"

    # Build directory for this platform
    BUILD_DIR="$OUTPUT_DIR/build/$artifact_name"

    # Publish. InformationalVersion carries the human-readable release string (what the binary
    # reports); Version/FileVersion get the sanitized numeric version so the build never fails on
    # a non-System.Version release name.
    dotnet publish src/Andy.Cli/Andy.Cli.csproj \
        --configuration Release \
        --runtime "$runtime" \
        --self-contained true \
        --output "$BUILD_DIR" \
        -p:PublishTrimmed=true \
        -p:PublishSingleFile=true \
        -p:Version="$SAFE_VERSION" \
        -p:FileVersion="$SAFE_VERSION" \
        -p:InformationalVersion="$RAW_VERSION"

    # Create archive and checksum in subshells so the script's working directory
    # never changes. (The previous `cd "$BUILD_DIR"` went three levels deep but
    # `cd ../..` only came back two, stranding the loop in $OUTPUT_DIR after the
    # first platform and breaking every subsequent publish path.)
    if [[ "$runtime" == win-* ]]; then
        archive="${artifact_name}.zip"
        ( cd "$BUILD_DIR" && zip -r "../../${archive}" ./* )
    else
        archive="${artifact_name}.tar.gz"
        ( cd "$BUILD_DIR" && chmod +x andy-cli && tar -czf "../../${archive}" ./* )
    fi

    (
        cd "$OUTPUT_DIR"
        if command -v sha256sum &> /dev/null; then
            sha256sum "$archive" > "${archive}.sha256"
        else
            shasum -a 256 "$archive" > "${archive}.sha256"
        fi
    )

    echo "✓ Built $artifact_name"
done

# Clean up build directories
rm -rf "$OUTPUT_DIR/build"

echo ""
echo "================================"
echo "Build complete!"
echo ""
echo "Artifacts created in $OUTPUT_DIR/:"
ls -lh "$OUTPUT_DIR"

echo ""
echo "To create a GitHub release:"
echo "  1. Create and push a tag: git tag $VERSION && git push origin $VERSION"
echo "  2. Go to GitHub → Releases → Draft a new release"
echo "  3. Upload files from $OUTPUT_DIR/"
echo "  4. Add release notes and publish"
