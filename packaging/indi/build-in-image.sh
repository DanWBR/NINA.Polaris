#!/bin/bash
# build-in-image.sh: build one indi-3rdparty driver .deb inside a chroot of a
# published Polaris image, then install-test it on a fresh copy of that image.
#
# Why a chroot of the image: the images run INDI core 2.2.2 from two different
# sources (a source build on Debian 13, the PPA's libindi1 on Ubuntu), and the
# driver has to link exactly what the image runs. Compiling in the image itself
# under qemu-user gets that for free. See packaging/indi/PLAN.md.
#
# Usage (root, on Linux or WSL with qemu-user-static and 7z):
#   bash build-in-image.sh --conf packages/indi-eqmod.conf \
#                          --image rpi4-polaris-v0.98.211.7z --family deb13
#
#   --image   a published .7z archive or an extracted .img. An archive is
#             extracted into --work; the archive itself is never modified.
#   --family  deb13        rpi4, rpi5 (INDI core built from source, no package
#                          owns it: the deb gets a preinst guard instead)
#             ubuntu24.04  radxa-dragon-q6a (PPA libindi1 + libindi-dev)
#             ubuntu22.04  opi4pro, opizero3 (PPA libindi1, no libindi-dev:
#                          pass --libindi-dev, exported by a 24.04 build;
#                          both come from the same PPA snapshot)
#   --export-libindi-dev TAR   (24.04 build) save its libindi headers for 22.04
#   --libindi-dev TAR          (22.04 build) headers to build against
#   --test-only DEB    skip the build; install-test DEB on this image (e.g. the
#                      deb13 build on rpi5, which carries Debian's old package)
#   --work DIR         scratch for extracted images (default /var/tmp/indi-build)
#   --outdir DIR       where the .deb lands (default ./out)
#   --keep-image       keep the extracted .img (it is modified by the build)
#
# The conf sets, besides what build-driver-deb.sh reads:
#   SRC_REPO, SRC_TAG, SRC_DIR   the upstream source (sparse clone of SRC_DIR)
#   DEB_VERSION                  upstream version + "+danwbrN"; "~<family>" is
#                                appended here
#
# Install test, on the same image: dpkg -i over whatever the image had, the
# package owns every driver binary, the installed binaries are the deb's and
# differ from the image's old ones, ldd resolves, a stray /usr/local/bin copy
# is removed, each driver answers getProperties, and a reinstall succeeds.

set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
CONF="" IMAGE="" FAMILY="" EXPORT_DEV="" USE_DEV="" TEST_DEB=""
WORK=/var/tmp/indi-build OUTDIR="$PWD/out" KEEP=0

die() { echo "ERROR: $*" >&2; exit 1; }
while [ $# -gt 0 ]; do
    case "$1" in
        --conf)               CONF="$2"; shift 2;;
        --image)              IMAGE="$2"; shift 2;;
        --family)             FAMILY="$2"; shift 2;;
        --export-libindi-dev) EXPORT_DEV="$2"; shift 2;;
        --libindi-dev)        USE_DEV="$2"; shift 2;;
        --test-only)          TEST_DEB="$2"; shift 2;;
        --work)               WORK="$2"; shift 2;;
        --outdir)             OUTDIR="$2"; shift 2;;
        --keep-image)         KEEP=1; shift;;
        *) die "unknown arg: $1";;
    esac
done
[ "$(id -u)" = 0 ] || die "run as root (losetup, mount, chroot)"
[ -f "$CONF" ] || die "--conf not found: $CONF"
[ -f "$IMAGE" ] || die "--image not found: $IMAGE"
case "$FAMILY" in deb13|ubuntu22.04|ubuntu24.04) ;; *) die "--family must be deb13, ubuntu22.04 or ubuntu24.04";; esac
[ "$FAMILY" = ubuntu22.04 ] && [ -z "$TEST_DEB" ] && [ ! -f "$USE_DEV" ] \
    && die "ubuntu22.04 needs --libindi-dev (export it from an ubuntu24.04 build)"

# The repo may be a Windows checkout: drop CRs from everything bash reads.
CONF_LF=$(mktemp); tr -d '\r' < "$CONF" > "$CONF_LF"
# shellcheck disable=SC1090
. "$CONF_LF"
for v in DEB_PACKAGE SRC_REPO SRC_TAG SRC_DIR DEB_VERSION; do
    [ -n "${!v:-}" ] || die "$CONF must set $v"
done
VERSION="$DEB_VERSION~$FAMILY"
mkdir -p "$WORK" "$OUTDIR"
OUTDIR="$(cd "$OUTDIR" && pwd)"
say() { echo "[$FAMILY] $*"; }

# --- image ----------------------------------------------------------------
case "$IMAGE" in
    *.7z)
        dir="$WORK/$(basename "$IMAGE" .7z)"; rm -rf "$dir"; mkdir -p "$dir"
        say "extract $(basename "$IMAGE")"
        7z e -y -o"$dir" "$IMAGE" >/dev/null || die "extract"
        IMG=$(ls "$dir"/*.img | head -1); [ -f "$IMG" ] || die "no .img in the archive"
        ;;
    *.img) IMG="$IMAGE"; KEEP=1 ;;
    *) die "--image must be .7z or .img" ;;
esac

MNT=$(mktemp -d /mnt/indi-build.XXXX)
LOOP=$(losetup -fP --show "$IMG") || die "losetup"
RESOLV_BAK=""
cleanup() {
    if [ -n "$RESOLV_BAK" ]; then
        rm -f "$MNT/etc/resolv.conf"
        cp -a --no-dereference "$RESOLV_BAK" "$MNT/etc/resolv.conf"; rm -f "$RESOLV_BAK"
    fi
    for d in dev/pts dev proc sys; do mountpoint -q "$MNT/$d" && umount -l "$MNT/$d"; done
    mountpoint -q "$MNT" && umount "$MNT"
    losetup -d "$LOOP" 2>/dev/null
    rmdir "$MNT" 2>/dev/null
    [ "$KEEP" = 1 ] || rm -f "$IMG"
    rm -f "$CONF_LF"
}
trap cleanup EXIT

# The rootfs partition varies per board (p1, p2, p3 on the Q6A): take the one
# holding both /etc and /usr/bin.
ROOT=""
for p in $(lsblk -lnpo NAME "$LOOP" | tail -n +2); do
    mount "$p" "$MNT" 2>/dev/null || continue
    if [ -d "$MNT/etc" ] && [ -d "$MNT/usr/bin" ]; then ROOT=$p; break; fi
    umount "$MNT"
done
[ -n "$ROOT" ] || die "no rootfs partition"
say "rootfs $ROOT ($(. "$MNT/etc/os-release"; echo "$PRETTY_NAME"))"

# qemu-aarch64 in binfmt_misc with the F flag, so it works inside a chroot
# that has no qemu of its own. WSL loses the registration on restart.
if [ ! -e /proc/sys/fs/binfmt_misc/qemu-aarch64 ]; then
    [ -e /proc/sys/fs/binfmt_misc/register ] || mount -t binfmt_misc none /proc/sys/fs/binfmt_misc
    printf '%s' ':qemu-aarch64:M::\x7fELF\x02\x01\x01\x00\x00\x00\x00\x00\x00\x00\x00\x00\x02\x00\xb7\x00:\xff\xff\xff\xff\xff\xff\xff\x00\xff\xff\xff\xff\xff\xff\xff\xff\xfe\xff\xff\xff:/usr/bin/qemu-aarch64-static:F' \
        > /proc/sys/fs/binfmt_misc/register || die "binfmt registration"
fi
mount --bind /dev "$MNT/dev"; mount --bind /dev/pts "$MNT/dev/pts"
mount -t proc proc "$MNT/proc"; mount -t sysfs sys "$MNT/sys"
if [ -e "$MNT/etc/resolv.conf" ] || [ -L "$MNT/etc/resolv.conf" ]; then
    RESOLV_BAK=$(mktemp); rm -f "$RESOLV_BAK"
    cp -a --no-dereference "$MNT/etc/resolv.conf" "$RESOLV_BAK"; rm -f "$MNT/etc/resolv.conf"
fi
cp /etc/resolv.conf "$MNT/etc/resolv.conf"
CH() { chroot "$MNT" /bin/bash -c "$1"; }

# --- build ----------------------------------------------------------------
B=/tmp/indi-build
if [ -z "$TEST_DEB" ]; then
    SRC="$WORK/src-$(basename "$SRC_REPO" .git)-$SRC_TAG"
    if [ ! -d "$SRC/$SRC_DIR" ]; then
        rm -rf "$SRC"
        git clone -q --depth 1 --branch "$SRC_TAG" --filter=blob:none --sparse "$SRC_REPO" "$SRC" || die "clone"
        git -C "$SRC" sparse-checkout set "$SRC_DIR" cmake_modules || die "sparse checkout"
    fi
    say "source $SRC_TAG ($(git -C "$SRC" rev-parse --short HEAD)) $SRC_DIR"
    rm -rf "${MNT:?}$B"; mkdir -p "$MNT$B/src" "$MNT$B/pkg/templates" "$MNT$B/out"
    cp -r "$SRC/$SRC_DIR" "$SRC/cmake_modules" "$MNT$B/src/"
    tr -d '\r' < "$HERE/build-driver-deb.sh" > "$MNT$B/pkg/build-driver-deb.sh"
    cp "$CONF_LF" "$MNT$B/pkg/package.conf"
    for f in "$HERE"/templates/*; do tr -d '\r' < "$f" > "$MNT$B/pkg/templates/$(basename "$f")"; done

    if [ "$FAMILY" = ubuntu22.04 ]; then
        tar -C "$MNT" -xf "$USE_DEV" || die "unpack --libindi-dev"
        for l in indidriver indiAlignmentDriver indiclient indilx200; do
            ln -sf "lib$l.so.2" "$MNT/usr/lib/aarch64-linux-gnu/lib$l.so"
        done
    fi

    say "apt build dependencies"
    CH "apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends cmake make g++ pkg-config libnova-dev libgsl-dev zlib1g-dev dpkg-dev ${SRC_BUILD_DEPS:-}" 2>&1 | tail -2
    CH "command -v cmake && command -v dpkg-shlibdeps" >/dev/null || die "build dependencies missing"

    if [ -n "$EXPORT_DEV" ]; then
        [ -f "$MNT/usr/include/libindi/indiversion.h" ] || die "this image has no libindi headers to export"
        tar -C "$MNT" -cf "$EXPORT_DEV" usr/include/libindi \
            $(cd "$MNT" && ls usr/lib/aarch64-linux-gnu/pkgconfig/libindi.pc 2>/dev/null) || die "export headers"
        say "libindi headers exported to $EXPORT_DEV"
    fi

    say "configure + build (qemu, a few minutes)"
    t0=$(date +%s)
    CH "cd $B && cmake -S src/$SRC_DIR -B build -DCMAKE_INSTALL_PREFIX=/usr -DCMAKE_BUILD_TYPE=Release ${SRC_CMAKE_ARGS:-} > cmake.log 2>&1 && cmake --build build -j\$(nproc) > make.log 2>&1 && DESTDIR=$B/stage cmake --install build > install.log 2>&1" \
        || { tail -30 "$MNT$B/cmake.log" "$MNT$B/make.log" 2>/dev/null; die "build"; }
    say "built in $(( $(date +%s) - t0 )) s; $(grep -m1 'Found INDI' "$MNT$B/cmake.log" | sed 's/^-- //')"

    # Depends from the binaries (dpkg-shlibdeps), with libindi per family.
    BINS=$(cd "$MNT$B/stage" && ls usr/bin/indi_* | sed "s#^#$B/stage/#" | tr '\n' ' ')
    DEPS=$(CH "mkdir -p $B/shl/debian && cd $B/shl && printf 'Source: $DEB_PACKAGE\n\nPackage: $DEB_PACKAGE\nArchitecture: any\n' > debian/control && dpkg-shlibdeps -O --ignore-missing-info $BINS 2>/dev/null" | sed -n 's/^shlibs:Depends=//p')
    [ -n "$DEPS" ] || die "dpkg-shlibdeps produced nothing"
    REQ=""
    case "$FAMILY" in
        deb13)
            for l in $(readelf -d $(echo "$BINS" | sed "s#$B#$MNT$B#g") 2>/dev/null | sed -n 's/.*NEEDED.*\[\(libindi[^]]*\)\].*/\1/p' | sort -u); do
                REQ="$REQ --require-file /usr/lib/aarch64-linux-gnu/$l"
            done ;;
        ubuntu*)
            DEPS=$(echo "$DEPS" | sed -E 's/libindi1( \([^)]*\))?/libindi1 (>= 2.2.2)/')
            echo "$DEPS" | grep -q libindi1 || DEPS="$DEPS, libindi1 (>= 2.2.2)" ;;
    esac
    say "depends: $DEPS"
    CH "cd $B/pkg && bash build-driver-deb.sh --conf package.conf --version '$VERSION' --source $B/stage --manifest $B/build/install_manifest.txt --depends '$DEPS' $REQ --outdir $B/out" \
        || die "package"
    TEST_DEB="$OUTDIR/$(basename "$(ls "$MNT$B"/out/*.deb)")"
    cp "$MNT$B"/out/*.deb "$TEST_DEB" || die "copy deb"
fi

# --- install test ---------------------------------------------------------
[ -f "$TEST_DEB" ] || die "no deb to test: $TEST_DEB"
say "install test: $(basename "$TEST_DEB")"
DBINS=$(dpkg-deb -c "$TEST_DEB" | grep -o '\./usr/bin/indi_[^ ]*' | sed 's#^\./##')
[ -n "$DBINS" ] || die "the deb has no driver binaries"
cp "$TEST_DEB" "$MNT/tmp/indi-test.deb"
say "before: $DEB_PACKAGE $(CH "dpkg-query -W -f='\${Version}' $DEB_PACKAGE 2>/dev/null || echo 'not installed'")"
declare -A OLD
mkdir -p "$MNT/usr/local/bin"
for b in $DBINS; do
    [ -f "$MNT/$b" ] && OLD[$b]=$(sha256sum "$MNT/$b" | cut -d' ' -f1)
    # A stray source build, as users who compile by hand leave behind.
    [ -f "$MNT/$b" ] && cp "$MNT/$b" "$MNT/usr/local/bin/$(basename "$b")"
done
CH "dpkg -i /tmp/indi-test.deb" 2>&1 | tail -3
CH "dpkg-query -W -f='\${Status}' $DEB_PACKAGE" | grep -q "install ok installed" || die "not installed"
fail=0
for b in $DBINS; do
    name=$(basename "$b")
    want=$(dpkg-deb --fsys-tarfile "$TEST_DEB" | tar -xO "./$b" | sha256sum | cut -d' ' -f1)
    have=$(sha256sum "$MNT/$b" | cut -d' ' -f1)
    owner=$(CH "dpkg -S /$b 2>/dev/null" | cut -d: -f1)
    miss=$(CH "ldd /$b | grep 'not found'")
    defs=$(CH "printf '<getProperties version=\"1.7\"/>\n' | timeout 30 /$b 2>/dev/null | grep -c '<def'")
    stray=$([ -e "$MNT/usr/local/bin/$name" ] && echo "STILL THERE" || echo removed)
    st="ok"
    [ "$have" = "$want" ] || st="NOT THE DEB'S BINARY"
    [ -n "${OLD[$b]:-}" ] && [ "${OLD[$b]}" = "$want" ] && st="SAME AS THE IMAGE'S OLD BINARY"
    [ "$owner" = "$DEB_PACKAGE" ] || st="$st, owner '$owner'"
    [ -z "$miss" ] || st="$st, ldd: $miss"
    [ "${defs:-0}" -gt 0 ] || st="$st, no getProperties answer"
    [ "$stray" = removed ] || st="$st, /usr/local copy $stray"
    say "  $name: $st (${defs} definitions)"
    [ "$st" = ok ] || fail=1
done
CH "dpkg -i /tmp/indi-test.deb" >/dev/null 2>&1 && say "reinstall: ok" || { say "reinstall FAILED"; fail=1; }
CH "rm -f /tmp/indi-test.deb"
[ "$fail" = 0 ] || die "install test failed"
say "PASS  $TEST_DEB"
say "sha256 $(sha256sum "$TEST_DEB" | cut -d' ' -f1)  size $(stat -c%s "$TEST_DEB")"
