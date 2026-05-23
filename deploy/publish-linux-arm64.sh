#!/bin/bash
# =============================================================================
# N.I.N.A. Polaris - Publish Script for Linux ARM64 (Raspberry Pi)
# =============================================================================
# Builds a self-contained deployment for linux-arm64.
# Run from any directory; paths are resolved relative to this script.
# =============================================================================

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT="$REPO_ROOT/src/NINA.Polaris/NINA.Polaris.csproj"
OUTPUT_DIR="$REPO_ROOT/publish/linux-arm64"
RID="linux-arm64"

echo "============================================================================="
echo "  N.I.N.A. Polaris - Publishing for $RID"
echo "============================================================================="
echo ""

# ---------------------------------------------------------------------------
# Verify project file exists
# ---------------------------------------------------------------------------
if [[ ! -f "$PROJECT" ]]; then
    echo "ERROR: Project file not found: $PROJECT"
    exit 1
fi

# ---------------------------------------------------------------------------
# Clean previous output
# ---------------------------------------------------------------------------
if [[ -d "$OUTPUT_DIR" ]]; then
    echo "Cleaning previous publish output ..."
    rm -rf "$OUTPUT_DIR"
fi

# ---------------------------------------------------------------------------
# Publish
# ---------------------------------------------------------------------------
echo "Building and publishing ..."
echo ""

dotnet publish "$PROJECT" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -o "$OUTPUT_DIR"

echo ""

# ---------------------------------------------------------------------------
# Report output size
# ---------------------------------------------------------------------------
if command -v du &>/dev/null; then
    TOTAL_SIZE=$(du -sh "$OUTPUT_DIR" | cut -f1)
    FILE_COUNT=$(find "$OUTPUT_DIR" -type f | wc -l)
    echo "============================================================================="
    echo "  Publish complete!"
    echo "  Output:     $OUTPUT_DIR"
    echo "  Total size: $TOTAL_SIZE  ($FILE_COUNT files)"
    echo "============================================================================="
fi

echo ""
echo "To deploy to your Raspberry Pi:"
echo ""
echo "  1. Copy the published files to your Pi:"
echo "     scp -r $OUTPUT_DIR pi@<pi-address>:/tmp/nina-polaris"
echo ""
echo "  2. SSH into the Pi and run the installer:"
echo "     ssh pi@<pi-address>"
echo "     sudo ./deploy/install.sh /tmp/nina-polaris"
echo ""
echo "  Or use rsync for faster incremental updates:"
echo "     rsync -avz --progress $OUTPUT_DIR/ pi@<pi-address>:/tmp/nina-polaris/"
echo ""
