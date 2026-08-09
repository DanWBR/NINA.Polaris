#!/usr/bin/env bash
# Run the postinst's brltty section against a fake root shaped like an IMAGE
# BUILD, not a live install: brltty's udev rule present, no /run/systemd/system,
# no systemctl on PATH.
#
# Why this exists. Images are built with `curtin in-target`, i.e. in a chroot.
# The first version of the brltty fix put the unit masking inside an
# `if [ -d /run/systemd/system ]` guard, so a build wrote the udev rule and
# skipped the units: the published image booted with brltty enabled, and the
# rule alone does not hold, because a running daemon finds the adapter over
# libusb anyway. A Gemini focuser would have gone missing on every freshly
# flashed card while a live `apt install` stayed fine, which is the kind of
# difference nobody notices until it is in the field.
#
# Run before publishing images:  bash packaging/deb/tests/brltty-chroot-test.sh
set -u
POSTINST="$(cd "$(dirname "$0")/.." && pwd)/DEBIAN/postinst"
ROOT=$(mktemp -d)
mkdir -p "$ROOT/usr/lib/udev/rules.d"
: > "$ROOT/usr/lib/udev/rules.d/85-brltty.rules"

# Whole sections only: from the 5c banner to the start of section 6.
start=$(grep -n -- '---- 5c\. get brltty off' "$POSTINST" | cut -d: -f1)
end=$(grep -n -- '---- 6\. systemd' "$POSTINST" | cut -d: -f1)
sed -n "${start},$((end - 1))p" "$POSTINST" \
  | sed -e "s#/etc/#$ROOT/etc/#g" \
        -e "s#/usr/lib/udev#$ROOT/usr/lib/udev#g" \
        -e "s#\[ -f /lib/udev#[ -f $ROOT/lib/udev#g" \
        -e "s#\[ -d /run/systemd/system \]#[ -d $ROOT/run/systemd/system ]#g" \
  > "$ROOT/run.sh"

bash -n "$ROOT/run.sh" || { echo "extracted block does not parse"; exit 1; }
PATH=/usr/bin:/bin bash "$ROOT/run.sh" >/dev/null 2>&1

fail=0
for u in brltty.service brltty-udev.service; do
    if [ "$(readlink "$ROOT/etc/systemd/system/$u" 2>/dev/null)" = "/dev/null" ]; then
        echo "  masked            $u"
    else
        echo "  NOT MASKED        $u"; fail=1
    fi
done
if [ -s "$ROOT/etc/udev/rules.d/85-brltty.rules" ]; then
    echo "  udev rule shadowed"
else
    echo "  UDEV RULE MISSING"; fail=1
fi
rm -rf "$ROOT"
[ "$fail" = 0 ] && echo "PASS: an image built in a chroot ships with brltty disarmed" \
                || echo "FAIL"
exit $fail
