#!/bin/bash
# build-driver-deb.sh — turn a `make install`-produced file set into ONE .deb
# for an indi-3rdparty package (indi-asi, indi-svbony, ...), matching the
# layout already on the Polaris images (prefix /usr, orphan binaries).
#
# See packaging/indi/PLAN.md for the full rationale. Short version:
#   - You build the driver with cmake/make exactly as you do today, but install
#     into a STAGING dir (DESTDIR) instead of the live system.
#   - cmake writes <build>/install_manifest.txt listing every installed file.
#     THAT is the file list for the .deb — no hand-maintained allowlist.
#   - This script copies those files into a dpkg-deb tree, writes the control +
#     preinst (anti /usr/local shadow) + postinst (ldconfig), and builds the deb.
#
# Usage:
#   build-driver-deb.sh --conf packages/indi-asi.conf \
#                       --version 2.1.0+danwbr1 \
#                       --source  /path/to/DESTDIR/stage \
#                       --manifest build/libasi/install_manifest.txt \
#                       --manifest build/indi-asi/install_manifest.txt \
#                       [--arch arm64] [--outdir .]
#
#   --source  is the DESTDIR you installed into. Pass "/" to package files that
#             are already installed on the live system.
#   --manifest may be repeated (e.g. SDK shim + the driver).
#
# Output: <package>_<version>_<arch>.deb in --outdir, plus a printed size +
# sha256 ready to paste into manifest.json.
#
# Requires: dpkg-deb, sha256sum (any Debian/Ubuntu host or the Pi itself).

set -euo pipefail

CONF="" VERSION="" SOURCE="/" OUTDIR="$PWD"
ARCH="$(dpkg --print-architecture 2>/dev/null || echo arm64)"
declare -a MANIFESTS=()

die() { echo "ERROR: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
    case "$1" in
        --conf)     CONF="$2"; shift 2;;
        --version)  VERSION="$2"; shift 2;;
        --source)   SOURCE="$2"; shift 2;;
        --manifest) MANIFESTS+=("$2"); shift 2;;
        --arch)     ARCH="$2"; shift 2;;
        --outdir)   OUTDIR="$2"; shift 2;;
        *) die "unknown arg: $1";;
    esac
done

[ -n "$CONF" ]    || die "--conf is required"
[ -f "$CONF" ]    || die "conf not found: $CONF"
[ -n "$VERSION" ] || die "--version is required (e.g. 2.1.0+danwbr1)"
[ "${#MANIFESTS[@]}" -gt 0 ] || die "at least one --manifest is required"

# Per-package config (DEB_PACKAGE / DEB_DEPENDS / DEB_DESC_SHORT / DEB_DESC_LONG)
# shellcheck disable=SC1090
. "$CONF"
[ -n "${DEB_PACKAGE:-}" ] || die "$CONF must set DEB_PACKAGE"

# Normalise: drop a trailing slash so prefix-stripping is exact.
SOURCE="${SOURCE%/}"; [ -z "$SOURCE" ] && SOURCE="/"

HERE="$(cd "$(dirname "$0")" && pwd)"
TPL="$HERE/templates"
PKGROOT="$(mktemp -d)"
trap 'rm -rf "$PKGROOT"' EXIT
mkdir -p "$PKGROOT/DEBIAN"

echo "==> Packaging $DEB_PACKAGE $VERSION ($ARCH)"
echo "    source (DESTDIR): $SOURCE"

# --- 1. Copy the files listed in the install manifests -----------------------
# install_manifest.txt holds the ACTUAL written paths. With DESTDIR they are
# "$SOURCE/usr/...", on a live install they are "/usr/...". Strip $SOURCE to get
# the in-package path. Skip directories + missing entries (a manifest can list a
# symlink target that was pruned).
declare -a BINS=()
copied=0
for m in "${MANIFESTS[@]}"; do
    [ -f "$m" ] || die "manifest not found: $m"
    while IFS= read -r abs; do
        [ -n "$abs" ] || continue
        # Relative in-package path (strip DESTDIR prefix, then leading slash).
        rel="${abs#"$SOURCE"}"; rel="${rel#/}"
        # When --source / : abs is /usr/..., rel becomes usr/...
        [ "$rel" = "$abs" ] && rel="${abs#/}"
        src="$abs"
        [ -e "$src" ] || { echo "    skip (missing): $src" >&2; continue; }
        [ -d "$src" ] && continue
        dst="$PKGROOT/$rel"
        mkdir -p "$(dirname "$dst")"
        cp -a "$src" "$dst"
        copied=$((copied+1))
        # Track driver binaries for the preinst anti-shadow + chmod.
        case "$rel" in
            usr/bin/indi_*) BINS+=("$(basename "$rel")");;
        esac
    done < "$m"
done
[ "$copied" -gt 0 ] || die "no files copied — check --source and the manifests"
echo "    files: $copied   drivers: ${#BINS[@]}"

# --- 2. Permissions (dpkg-deb is picky) -------------------------------------
find "$PKGROOT/usr" -type d -exec chmod 0755 {} \; 2>/dev/null || true
find "$PKGROOT/usr" -type f -exec chmod 0644 {} \; 2>/dev/null || true
# Binaries + shared objects need exec.
[ -d "$PKGROOT/usr/bin" ] && find "$PKGROOT/usr/bin" -type f -exec chmod 0755 {} \;
find "$PKGROOT/usr" -name "*.so"   -exec chmod 0755 {} \; 2>/dev/null || true
find "$PKGROOT/usr" -name "*.so.*" -exec chmod 0755 {} \; 2>/dev/null || true

# --- 3. control -------------------------------------------------------------
{
    sed -e "s/__PACKAGE__/${DEB_PACKAGE}/g" \
        -e "s/__VERSION__/${VERSION}/g" \
        -e "s/__ARCH__/${ARCH}/g" \
        -e "s/__DEPENDS__/${DEB_DEPENDS:-libindidriver1}/g" \
        -e "s/__DESC_SHORT__/${DEB_DESC_SHORT:-INDI third-party driver (DanWBR build)}/g" \
        "$TPL/control.in"
    # Long description: indent each line one space (Debian continuation), or a
    # single " ." placeholder when not provided.
    if [ -n "${DEB_DESC_LONG:-}" ]; then
        printf '%s\n' "$DEB_DESC_LONG" | sed 's/^/ /'
    else
        echo " ."
    fi
} > "$PKGROOT/DEBIAN/control"

# --- 4. preinst: remove stale /usr/local copies of THIS package's binaries --
{
    cat "$TPL/preinst.in"
    echo "# --- generated per-package binary cleanup ---"
    for b in "${BINS[@]}"; do
        echo "rm -f \"/usr/local/bin/$b\" 2>/dev/null || true"
    done
    echo "exit 0"
} > "$PKGROOT/DEBIAN/preinst"

# --- 5. postinst: ldconfig (bundled SDK .so) --------------------------------
cp "$TPL/postinst.in" "$PKGROOT/DEBIAN/postinst"

chmod 0755 "$PKGROOT/DEBIAN/preinst" "$PKGROOT/DEBIAN/postinst"
chmod 0644 "$PKGROOT/DEBIAN/control"

# --- 6. Build ---------------------------------------------------------------
mkdir -p "$OUTDIR"
OUT="$OUTDIR/${DEB_PACKAGE}_${VERSION}_${ARCH}.deb"
rm -f "$OUT"
dpkg-deb --root-owner-group --build "$PKGROOT" "$OUT" >/dev/null
echo "==> Built $OUT"

# --- 7. manifest.json helper ------------------------------------------------
SIZE=$(stat -c%s "$OUT")
SHA=$(sha256sum "$OUT" | cut -d' ' -f1)
echo ""
echo "manifest.json entry:"
cat <<EOF
  {
    "package": "${DEB_PACKAGE}",
    "version": "${VERSION}",
    "arch": "${ARCH}",
    "url": "https://sourceforge.net/projects/<proj>/files/indi/$(basename "$OUT")/download",
    "size": ${SIZE},
    "sha256": "${SHA}",
    "summary": "<what this build fixes>"
  }
EOF
echo ""
echo "Quick checks:"
echo "  dpkg-deb -I \"$OUT\" | head -20"
echo "  dpkg-deb -c \"$OUT\" | head -40"
