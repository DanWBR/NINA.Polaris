#!/usr/bin/env bash
# Does every vendor camera SDK declare the libraries whose symbols it calls?
#
# Run this after updating anything under camera_sdk/. A vendor .so that uses
# libusb without a DT_NEEDED entry for it does not fail at load time, it
# fails on the first call, and the dynamic loader answers that by calling
# _exit(127). There is no exception to catch, so the process simply dies and
# systemd restarts it five seconds later, for ever.
#
# That is not hypothetical. SVBony's x86-64 build of libSVBCameraSDK.so calls
# 22 libusb functions and declares none of them, while their arm64 build of
# the same version declares them properly. Every Raspberry Pi was fine and
# every mini PC crashed the moment a browser connected and cameras were
# enumerated. It was reported as constant network disconnection on an N95.
#
# SvbonyRegistry works around it by loading libusb with RTLD_GLOBAL before
# the SDK, and refusing to load the SDK when that fails. This script is how
# we find out whether the next SDK drop needs the same treatment.
set -u
cd "$(dirname "$0")/.."
SDK=camera_sdk

command -v readelf >/dev/null 2>&1 || { echo "readelf is required (apt install binutils)"; exit 2; }
[ -d "$SDK" ] || { echo "no $SDK directory here"; exit 2; }

# Libraries already known to do this and already worked around in code.
# Anything not on this list is a new one, and fails the check.
KNOWN="libSVBCameraSDK"

broken=0
unhandled=0
examined=0
printf '%-56s %-7s %-9s %s\n' "library" "uses" "declares" "verdict"

# -L follows the symlinks vendors ship (libFoo.so -> libFoo.so.1.2), and no
# path is excluded by name: an earlier version of this scan skipped ZWO
# because its folder is called ASI_linux_mac_SDK and the filter dropped
# anything matching "mac". A scan that quietly examines nothing reads exactly
# like a scan that found nothing wrong.
while IFS= read -r so; do
    real=$(readlink -f "$so") || continue
    [ -f "$real" ] || continue
    file -b "$real" 2>/dev/null | grep -q "^ELF" || continue
    arch=$(file -b "$real" | grep -oE "x86-64|aarch64|ARM" | head -1)

    uses=$(readelf -sW --dyn-syms "$real" 2>/dev/null | awk '$7=="UND"{print $8}' | grep -c '^libusb' || true)
    decl=$(readelf -d "$real" 2>/dev/null | grep -c 'libusb-1\.0' || true)
    examined=$((examined + 1))

    if [ "$uses" -gt 0 ] && [ "$decl" -eq 0 ]; then
        broken=$((broken + 1))
        base=$(basename "$so" | sed 's/\.so.*$//')
        if echo "$KNOWN" | grep -qw "$base"; then
            verdict="known ($arch): handled by the SvbonyRegistry preload"
        else
            verdict="NEW ($arch): nothing preloads for this one"
            unhandled=$((unhandled + 1))
        fi
        printf '%-56s %-7s %-9s %s\n' "${so#./}" "$uses" "$decl" "$verdict"
    fi
done < <(find "$SDK" \( -name "*.so" -o -name "*.so.*" \) 2>/dev/null | sort -u)

echo
echo "libraries examined: $examined"
if [ "$examined" -eq 0 ]; then
    echo "ERROR: the scan examined nothing, so it proved nothing."
    exit 2
fi
echo "call libusb without declaring it: $broken (of which unhandled: $unhandled)"
if [ "$unhandled" -gt 0 ]; then
    echo
    echo "A vendor library calls libusb and declares no dependency on it, and nothing"
    echo "preloads libusb for it. Left alone it will not fail to load: it will kill the"
    echo "process on the first call, and systemd will restart it every five seconds."
    echo "Give it the same treatment as SvbonyRegistry, then add it to KNOWN above."
    exit 1
fi
echo "OK: nothing new. The known ones are worked around in code."
