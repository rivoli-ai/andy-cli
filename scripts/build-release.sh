#!/bin/bash

# Build release binaries for all platforms
# Usage: ./scripts/build-release.sh [version]

set -e

VERSION=${1:-"v0.0.0-local"}
OUTPUT_DIR="release-artifacts"

echo "Building Andy CLI $VERSION"
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
)

# Build each platform
for platform in "${PLATFORMS[@]}"; do
    IFS=':' read -r runtime artifact_name <<< "$platform"

    echo ""
    echo "Building $artifact_name ($runtime)..."
    echo "--------------------------------"

    # Build directory for this platform
    BUILD_DIR="$OUTPUT_DIR/build/$artifact_name"

    # Publish
    dotnet publish src/Andy.Cli/Andy.Cli.csproj \
        --configuration Release \
        --runtime "$runtime" \
        --self-contained true \
        --output "$BUILD_DIR" \
        -p:PublishTrimmed=true \
        -p:PublishSingleFile=true

    # Create archive
    cd "$BUILD_DIR"

    if [[ "$runtime" == win-* ]]; then
        # Windows: create zip
        zip -r "../../${artifact_name}.zip" ./*
        cd ../..

        # Calculate checksum
        if command -v sha256sum &> /dev/null; then
            sha256sum "${artifact_name}.zip" > "${artifact_name}.zip.sha256"
        else
            shasum -a 256 "${artifact_name}.zip" > "${artifact_name}.zip.sha256"
        fi
    else
        # Unix: create tar.gz
        chmod +x andy-cli
        tar -czf "../../${artifact_name}.tar.gz" ./*
        cd ../..

        # Calculate checksum
        if command -v sha256sum &> /dev/null; then
            sha256sum "${artifact_name}.tar.gz" > "${artifact_name}.tar.gz.sha256"
        else
            shasum -a 256 "${artifact_name}.tar.gz" > "${artifact_name}.tar.gz.sha256"
        fi
    fi

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
