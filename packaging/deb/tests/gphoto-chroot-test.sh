#!/usr/bin/env bash
# Run the postinst's gphoto section against a fake root shaped like an IMAGE
# BUILD: no /run/systemd/system, no systemctl on PATH.
#
# Same trap as brltty-chroot-test.sh, one bus over. The desktop's gphoto
# volume monitor claims a DSLR's PTP interface and indi_gphoto_ccd is locked
# out with "Camera open error (-53): Could not claim the USB device" (Canon
# EOS 1500D on the Raspberry Pi 4 image, 2026-09-20). If the masking sat
# inside a systemd guard, every freshly flashed card would ship with the
# monitor armed while a live `apt install` stayed fine.
#
# Run before publishing images:  bash packaging/deb/tests/gphoto-chroot-test.sh
set -u
POSTINST="$(cd "$(dirname "$0")/.." && pwd)/DEBIAN/postinst"
ROOT=$(mktemp -d)

# Whole section only: from the 5c-2 banner to the udevadm reload that follows.
start=$(grep -n -- '---- 5c-2\. get the desktop' "$POSTINST" | cut -d: -f1)
end=$(grep -n -- 'if command -v udevadm' "$POSTINST" | head -1 | cut -d: -f1)
sed -n "${start},$((end - 1))p" "$POSTINST" \
  | sed -e "s#/etc/systemd/user#$ROOT/etc/systemd/user#g" \
        -e "s#\[ -d /run/systemd/system \]#[ -d $ROOT/run/systemd/system ]#g" \
  > "$ROOT/run.sh"

bash -n "$ROOT/run.sh" || { echo "extracted block does not parse"; exit 1; }
PATH=/usr/bin:/bin bash "$ROOT/run.sh" >/dev/null 2>&1

fail=0
U="$ROOT/etc/systemd/user/gvfs-gphoto2-volume-monitor.service"
if [ "$(readlink "$U" 2>/dev/null)" = "/dev/null" ]; then
    echo "  masked            gvfs-gphoto2-volume-monitor.service"
else
    echo "  NOT MASKED        gvfs-gphoto2-volume-monitor.service"; fail=1
fi

# And the udev rule that does the real work has to be in the package.
RULE="$(cd "$(dirname "$0")/.." && pwd)/lib/udev/rules.d/99-polaris-gphoto.rules"
if grep -q 'ID_GPHOTO2' "$RULE" 2>/dev/null; then
    echo "  udev rule present ID_GPHOTO2 cleared"
else
    echo "  UDEV RULE MISSING or does not clear ID_GPHOTO2"; fail=1
fi

rm -rf "$ROOT"
[ "$fail" = 0 ] && echo "PASS: an image built in a chroot ships with the gphoto monitor disarmed" \
                || echo "FAIL"
exit $fail
