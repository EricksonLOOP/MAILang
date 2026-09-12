#!/usr/bin/env bash
# Build the platform-specific Python wheel for Linux x64 or macOS.
#
# Run from the repository root (the directory containing Mail.slnx):
#   bash sdk/python/scripts/build_wheel.sh
#
# The script auto-detects the RID from the current OS/arch.
# Output: sdk/python/dist/mail_runtime-0.2.0-*-{platform}.whl

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
SDK_ROOT="$REPO_ROOT/sdk/python"
CLI_PROJECT="$REPO_ROOT/src/Mail.Cli/Mail.Cli.csproj"
BIN_DIR="$SDK_ROOT/mail_runtime/_bin"

# Detect RID
OS="$(uname -s)"
ARCH="$(uname -m)"

case "$OS-$ARCH" in
    Linux-x86_64)  RID="linux-x64"  ;;
    Darwin-x86_64) RID="osx-x64"    ;;
    Darwin-arm64)  RID="osx-arm64"  ;;
    *)
        echo "Unsupported platform: $OS-$ARCH" >&2
        exit 1
        ;;
esac

PUBLISH_OUT="$REPO_ROOT/obj/publish/$RID"

echo "==> Publishing CLI ($RID) ..."
dotnet publish "$CLI_PROJECT" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$PUBLISH_OUT"

echo "==> Copying executable to _bin/ ..."
SRC="$PUBLISH_OUT/Mail.Cli"
DEST="$BIN_DIR/Mail.Cli"

if [ ! -f "$SRC" ]; then
    echo "Published executable not found at $SRC" >&2
    exit 1
fi

cp "$SRC" "$DEST"
chmod +x "$DEST"

echo "==> Building Python wheel ..."
cd "$SDK_ROOT"
python -m build --wheel

echo ""
echo "Wheel written to: $SDK_ROOT/dist/"
ls -lh "$SDK_ROOT/dist/"*.whl
