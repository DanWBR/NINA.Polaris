#!/usr/bin/env bash
# Build and publish the two large data packs to the fixed `data-pack` release.
#
# This used to be .github/workflows/thumb-pack.yml, which checked the source
# data out of Git LFS. Both trees are now maintainer-local and ignored, because
# in LFS they cost the account 461 MB on every clone of a public repo, plus a
# copy per release-matrix job, while reaching users only through this release,
# where GitHub does not meter bandwidth. So the build moved to where the data
# lives: your machine.
#
#   polaris-dso-thumbs.zip              full DSO thumbnail set   (DsoThumbPackService)
#   polaris-ncnn-models.zip             ncnn GPU-Vulkan models   (NcnnModelPackService)
#   polaris-camera-sdk-linux-x64.zip    native camera .so (x64)  (CameraSdkPackService)
#   polaris-camera-sdk-linux-arm64.zip  native camera .so (arm64)(CameraSdkPackService)
#
# Run it after regenerating either set. Nothing here is automatic any more, so
# the counts are checked before anything is uploaded: publishing a pack with
# half the thumbs in it is worse than not publishing.
#
# Usage:
#   scripts/publish-data-packs.sh              build + upload
#   scripts/publish-data-packs.sh --dry-run    build + verify, upload nothing
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

DRY_RUN=0
[ "${1:-}" = "--dry-run" ] && DRY_RUN=1

THUMBS="src/NINA.Polaris/wwwroot/sky/data/skydata/dso-thumbs"
NCNN="src/NINA.Polaris/wwwroot/graxpert/models/ncnn"
OUT="$REPO_ROOT/.data-packs"

command -v zip >/dev/null || { echo "!! zip nao encontrado"; exit 1; }
# gh is only needed to upload, so a --dry-run works on a machine without it.
[ "$DRY_RUN" = "1" ] || command -v gh >/dev/null \
    || { echo "!! gh nao encontrado (necessario para enviar; use --dry-run para so montar)"; exit 1; }

rm -rf "$OUT"; mkdir -p "$OUT"

# ---- DSO thumbnails -------------------------------------------------------
count=$(find "$THUMBS" -name '*.jpg' 2>/dev/null | wc -l)
echo "==> $count miniaturas em $THUMBS"
if [ "$count" -lt 1000 ]; then
    echo "!! so $count miniaturas. Esta arvore e local e ignorada pelo git, entao"
    echo "   um clone novo nao a tem: gere as miniaturas antes de publicar."
    exit 1
fi
# -0 = store: the JPEGs are already compressed, deflate burns CPU for ~0 gain.
# -j flattens to a bare name/slug, which is what DsoThumbPackService expects.
( cd "$THUMBS" && zip -0 -j -q -r "$OUT/polaris-dso-thumbs.zip" . )

# ---- ncnn GPU models ------------------------------------------------------
bins=$(find "$NCNN" -name '*.bin' 2>/dev/null | wc -l)
echo "==> $bins modelos ncnn em $NCNN"
if [ "$bins" -lt 1 ]; then
    echo "!! nenhum modelo ncnn. Mesma historia: arvore local, gere antes."
    exit 1
fi
# Preserve the {family}-ai-models/{version}/ tree: NcnnModelPackService extracts
# into the writable models root's ncnn/ subtree, where the resolver's parallel
# layout looks. -0 store again, the weights are ~random.
( cd "$NCNN" && zip -0 -q -r "$OUT/polaris-ncnn-models.zip" . )

# ---- Native camera SDKs (Linux) ------------------------------------------
# One zip per arch with the vendor .so files FLAT at the root under their bare
# names, which the per-vendor resolvers probe in POLARIS_NATIVE_SDK_DIR
# (CameraSdkPackService extracts here). `cp -L` resolves the in-repo dev
# symlinks (ASI/PlayerOne point at the SONAME-versioned real file). These .so
# are committed to the repo, so a fresh clone has them (unlike the two trees
# above). libusb-1.0 is a system dependency on the target, not bundled.
build_camera_sdk_zip() {
    local arch="$1"        # x64 | arm64
    local asi svb poa toup alt
    if [ "$arch" = "x64" ]; then
        asi="camera_sdk/ZWO/ASI_linux_mac_SDK_V1.41/lib/x64/libASICamera2.so"
        svb="camera_sdk/SVBony/SVBony_Linux/SVBCameraSDK/lib/x64/libSVBCameraSDK.so"
        poa="camera_sdk/PlayerOne/Linux_v3.10.1/lib/x64/libPlayerOneCamera.so"
        toup="camera_sdk/ToupTek/linux/x64/libtoupcam.so"
        alt="camera_sdk/Altair/linux/x64/libaltaircam.so"
    else
        asi="camera_sdk/ZWO/ASI_linux_mac_SDK_V1.41/lib/armv8/libASICamera2.so"
        svb="camera_sdk/SVBony/SVBony_Linux/SVBCameraSDK/lib/armv8/libSVBCameraSDK.so"
        poa="camera_sdk/PlayerOne/Linux_v3.10.1/lib/arm64/libPlayerOneCamera.so"
        toup="camera_sdk/ToupTek/linux/arm64/libtoupcam.so"
        alt="camera_sdk/Altair/linux/arm64/glibc/libaltaircam.so"
    fi
    local stage="$OUT/camera-sdk-$arch"
    rm -rf "$stage"; mkdir -p "$stage"
    local missing=0
    for src in "$asi" "$svb" "$poa" "$toup" "$alt"; do
        if [ -e "$src" ]; then cp -L "$src" "$stage/$(basename "$src")"
        else echo "!! faltando: $src"; missing=1; fi
    done
    [ "$missing" = "1" ] && { echo "!! SDK de camera ($arch) incompleto, abortando"; exit 1; }
    local n; n=$(find "$stage" -name '*.so' | wc -l)
    echo "==> $n libs nativas de camera para $arch"
    ( cd "$stage" && zip -0 -j -q -r "$OUT/polaris-camera-sdk-linux-$arch.zip" . )
    rm -rf "$stage"
}
build_camera_sdk_zip x64
build_camera_sdk_zip arm64

echo
ls -la "$OUT"/*.zip | awk '{printf "  %8.1f MB  %s\n", $5/1048576, $9}'

# A zip that cannot be opened is worse than no zip: it fails on the user's
# machine, after the download, with no way back to a good copy.
for z in "$OUT"/*.zip; do
    unzip -t "$z" >/dev/null 2>&1 || { echo "!! $z nao passou no teste"; exit 1; }
done
echo "  ambos os zips testados"

if [ "$DRY_RUN" = "1" ]; then
    echo
    echo "--dry-run: nada foi enviado. Os pacotes estao em $OUT"
    exit 0
fi

echo
echo "==> enviando para o release data-pack"
gh release upload data-pack \
    "$OUT/polaris-dso-thumbs.zip" \
    "$OUT/polaris-ncnn-models.zip" \
    "$OUT/polaris-camera-sdk-linux-x64.zip" \
    "$OUT/polaris-camera-sdk-linux-arm64.zip" \
    --repo DanWBR/NINA.Polaris --clobber

echo "==> pronto"
gh release view data-pack --repo DanWBR/NINA.Polaris \
    --json assets --jq '.assets[] | select(.name | startswith("polaris-")) | "  \(.name)  \(.size/1048576 | floor) MB  \(.updatedAt[:10])"'
