#!/usr/bin/env bash
set -euo pipefail

# N.I.N.A. Polaris - Publish for Linux x64 (Intel/AMD mini PCs, NUCs)

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/../src/NINA.Polaris/NINA.Polaris.csproj"
OUTPUT_DIR="$SCRIPT_DIR/../publish/linux-x64"
RUNTIME="linux-x64"
CONFIG="Release"

echo ""
echo "=========================================="
echo "  N.I.N.A. Polaris - Publish linux-x64"
echo "=========================================="
echo ""

if ! command -v dotnet &>/dev/null; then
    echo "[ERROR] dotnet CLI not found. Install .NET SDK first."
    exit 1
fi

echo "[INFO] Cleaning previous build..."
rm -rf "$OUTPUT_DIR"

echo "[INFO] Publishing for $RUNTIME..."
dotnet publish "$PROJECT_DIR" \
    -c "$CONFIG" \
    -r "$RUNTIME" \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    -o "$OUTPUT_DIR"

chmod +x "$OUTPUT_DIR/NINA.Polaris"

SIZE=$(du -sh "$OUTPUT_DIR" | cut -f1)
echo ""
echo "=========================================="
echo "  Publish Complete!"
echo "=========================================="
echo ""
echo "[INFO] Output: $OUTPUT_DIR"
echo "[INFO] Size: $SIZE"
echo "[INFO] Runtime: $RUNTIME (self-contained)"
echo ""
