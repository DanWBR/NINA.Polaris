#!/usr/bin/env bash
# Build and publish ONLY the native camera-SDK packs to the fixed `data-pack`
# release. Split out of publish-data-packs.sh so it does not depend on the
# maintainer-local dso-thumbs / ncnn trees (which gate that script), and so it
# runs on a machine without `zip` (falls back to Python's zipfile).
#
#   polaris-camera-sdk-linux-x64.zip     vendor .so (x64)    (CameraSdkPackService)
#   polaris-camera-sdk-linux-arm64.zip   vendor .so (arm64)  (CameraSdkPackService)
#
# The .so are committed in-repo under camera_sdk/**, so a fresh clone has them.
# `cp -L` resolves the ASI/PlayerOne SONAME dev symlinks to real content.
#
# Run from anywhere. On Windows PowerShell use:  bash scripts/publish-camera-sdk-pack.sh
#
# Usage:
#   scripts/publish-camera-sdk-pack.sh              build + upload
#   scripts/publish-camera-sdk-pack.sh --dry-run    build + verify, upload nothing
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

DRY_RUN=0
[ "${1:-}" = "--dry-run" ] && DRY_RUN=1

OUT="$REPO_ROOT/.data-packs"
[ "$DRY_RUN" = "1" ] || command -v gh >/dev/null \
    || { echo "!! gh nao encontrado (necessario para enviar; use --dry-run para so montar)"; exit 1; }

# Zip a staging dir (flat) into $2. Prefer `zip -0 -j`; fall back to Python
# (deflate) when zip is absent, e.g. on a stock Windows Git-Bash.
zip_dir() {
    local stage="$1" out="$2"
    if command -v zip >/dev/null; then
        ( cd "$stage" && zip -q -j -r "$out" . )
    elif command -v python >/dev/null || command -v python3 >/dev/null; then
        local py; py="$(command -v python || command -v python3)"
        "$py" - "$stage" "$out" <<'PY'
import sys, os, zipfile
stage, out = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    for f in sorted(os.listdir(stage)):
        z.write(os.path.join(stage, f), f)
PY
    else
        echo "!! nem 'zip' nem 'python' encontrados para montar o zip"; exit 1
    fi
}

build() {
    local arch="$1"; shift
    local stage="$OUT/camera-sdk-$arch"; rm -rf "$stage"; mkdir -p "$stage"
    local src
    for src in "$@"; do
        [ -e "$src" ] && cp -L "$src" "$stage/$(basename "$src")" \
            || { echo "!! faltando: $src"; exit 1; }
    done
    local n; n=$(find "$stage" -name '*.so' | wc -l)
    echo "==> $n libs nativas de camera para $arch"
    zip_dir "$stage" "$OUT/polaris-camera-sdk-linux-$arch.zip"
    rm -rf "$stage"
}

command -v cp >/dev/null || { echo "!! cp nao encontrado"; exit 1; }
mkdir -p "$OUT"

build x64 \
    camera_sdk/ZWO/ASI_linux_mac_SDK_V1.41/lib/x64/libASICamera2.so \
    camera_sdk/SVBony/SVBony_Linux/SVBCameraSDK/lib/x64/libSVBCameraSDK.so \
    camera_sdk/PlayerOne/Linux_v3.10.1/lib/x64/libPlayerOneCamera.so \
    camera_sdk/ToupTek/linux/x64/libtoupcam.so \
    camera_sdk/Altair/linux/x64/libaltaircam.so

build arm64 \
    camera_sdk/ZWO/ASI_linux_mac_SDK_V1.41/lib/armv8/libASICamera2.so \
    camera_sdk/SVBony/SVBony_Linux/SVBCameraSDK/lib/armv8/libSVBCameraSDK.so \
    camera_sdk/PlayerOne/Linux_v3.10.1/lib/arm64/libPlayerOneCamera.so \
    camera_sdk/ToupTek/linux/arm64/libtoupcam.so \
    camera_sdk/Altair/linux/arm64/glibc/libaltaircam.so

echo
ls -la "$OUT"/polaris-camera-sdk-linux-*.zip | awk '{printf "  %8.1f MB  %s\n", $5/1048576, $9}'

if [ "$DRY_RUN" = "1" ]; then
    echo
    echo "--dry-run: nada foi enviado. Os pacotes estao em $OUT"
    exit 0
fi

echo
echo "==> enviando para o release data-pack"
gh release upload data-pack \
    "$OUT/polaris-camera-sdk-linux-x64.zip" \
    "$OUT/polaris-camera-sdk-linux-arm64.zip" \
    --repo DanWBR/NINA.Polaris --clobber

echo "==> pronto"
gh release view data-pack --repo DanWBR/NINA.Polaris \
    --json assets --jq '.assets[] | select(.name | startswith("polaris-camera-sdk")) | "  \(.name)  \(.size/1048576 | floor) MB  \(.updatedAt[:10])"'
