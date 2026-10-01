#!/bin/bash
# Point each vendor camera SDK soname at the newest library actually present.
#
# WHY THIS RUNS AT ALL
#
# The SBC images build the INDI stack and the vendor SDKs from source, so
# those files are real, working, and owned by no package. dpkg therefore
# reports the SDK as "not installed", and anything that believes dpkg will
# install the distribution's copy on top. On Raspberry Pi OS that copy is
# libasi 1.27, from December 2022, and installing it repoints all four ZWO
# sonames at older libraries:
#
#   libASICamera2.so.1  -> .so.1.27   (the image built 1.41)
#   libEAFFocuser.so.1  -> .so.1.4    (the image built 1.7.7)
#   libEFWFilter.so.1   -> .so.1.7
#   libUSB2ST4Conv.so.1 -> .so.1.0
#
# The newer files stay on disk, unused. Only the focuser fails loudly, with
# "undefined symbol: EAFStepRange", because 1.4 does not export it; the
# camera simply drops two years of SDK and says nothing. That is the part
# worth repairing automatically: nobody can report a fault they cannot see.
#
# The updater no longer causes this, but the fix ships inside the package,
# and the script that performs an update is the one already on disk. So the
# update that delivers the fix is still run by the old code, and anyone
# updating into it takes the damage once. This repairs that, and repairs
# every machine already in that state.
#
# WHAT IT WILL NOT DO
#
#   - create a link for a library that is not installed,
#   - touch anything outside the known vendor families,
#   - move a soname backwards: it only ever points at the highest version,
#   - fail the service. A repair that cannot run is logged and skipped.
set -u

LIBS="libASICamera2 libEAFFocuser libEFWFilter libUSB2ST4Conv
      libSVBCameraSDK libPlayerOneCamera libtoupcam libaltaircam"
DIRS="/usr/lib/aarch64-linux-gnu /usr/lib/x86_64-linux-gnu /usr/lib/arm-linux-gnueabihf /usr/lib"

changed=0
for dir in ${POLARIS_SDK_LIBDIRS:-$DIRS}; do
    [ -d "$dir" ] || continue
    for name in $LIBS; do
        # The real libraries, newest last. A soname link (libX.so.1) and the
        # development link (libX.so) are excluded: they are what we rewrite,
        # not candidates to point at.
        newest=""
        for f in $(ls -1v "$dir/$name".so.* 2>/dev/null); do
            [ -f "$f" ] || continue
            [ -L "$f" ] && continue
            case "${f##*/}" in
                "$name".so.*.*) newest="$f" ;;   # 1.7.7, 1.41, 1.0: a real build
            esac
        done
        [ -n "$newest" ] || continue

        target=$(basename "$newest")
        current=$(readlink "$dir/$name.so.1" 2>/dev/null || true)
        [ "$current" = "$target" ] && continue

        ln -sf "$target" "$dir/$name.so.1" 2>/dev/null || continue
        ln -sf "$name.so.1" "$dir/$name.so" 2>/dev/null || true
        echo "polaris-sdk-symlinks: $dir/$name.so.1 ${current:-<none>} -> $target"
        changed=1
    done
done

[ "$changed" -eq 1 ] && ldconfig 2>/dev/null
exit 0
