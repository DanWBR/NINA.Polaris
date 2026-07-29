#!/bin/bash
# Read-only inspection of an image before it is compressed and published.
#
#   image-verify.sh <image.img> <root-offset-sectors> [boot-offset-sectors] [boot-mount]
#
# Everything here is something that has shipped broken at least once. Run it
# before compressing: a bad image costs minutes to catch now and a user's night
# to catch later.
set -euo pipefail

IMG="$1"; ROOT_SECT="$2"; BOOT_SECT="${3:-}"; BOOT_MNT="${4:-}"
MNT=/mnt/polaris-verify

cleanup() {
    set +e
    [ -n "$BOOT_MNT" ] && umount -l "$MNT$BOOT_MNT" 2>/dev/null
    umount "$MNT" 2>/dev/null
    [ -n "${LB:-}" ] && losetup -d "$LB" 2>/dev/null
    [ -n "${LR:-}" ] && losetup -d "$LR" 2>/dev/null
}
trap cleanup EXIT

mkdir -p "$MNT"
LR=$(losetup --find --show --offset $((ROOT_SECT * 512)) "$IMG")
mount -o ro "$LR" "$MNT"
if [ -n "$BOOT_SECT" ]; then
    LB=$(losetup --find --show --offset $((BOOT_SECT * 512)) "$IMG")
    mkdir -p "$MNT$BOOT_MNT" 2>/dev/null || true
    mount -o ro "$LB" "$MNT$BOOT_MNT" 2>/dev/null || echo "note: could not mount boot partition read-only"
fi

echo "== package"
chroot "$MNT" dpkg-query -W polaris 2>/dev/null || echo "  polaris NOT installed"
echo "   service links: $(ls "$MNT/etc/systemd/system/multi-user.target.wants/" 2>/dev/null | grep -c polaris)"

echo "== residue (every number should be 0)"
echo "   machine-id bytes: $(stat -c %s "$MNT/etc/machine-id" 2>/dev/null)"
echo "   TLS cert dirs:    $(find "$MNT" -maxdepth 7 -type d -path '*NINA.Polaris*' -name cert 2>/dev/null | wc -l)"
echo "   growroot markers: $(find "$MNT/var/lib" -name 'polaris-growroot*' -o -path '*/polaris/growroot.done' 2>/dev/null | wc -l)"
echo "   journal files:    $(find "$MNT/var/log/journal" -type f 2>/dev/null | wc -l)"
echo "   leftover profile: $(find "$MNT" -maxdepth 7 -name active.json -path '*NINA.Polaris*' 2>/dev/null | wc -l)"

# The thing that does the growing has to BE there. The Orange Pi 4 Pro image
# shipped with no grow-root at all, so its root stayed at the image size on a
# 64 GB card, and the marker check above reported a clean 0 the whole time.
echo "== grow-root (a shipped image needs the unit, enabled)"
echo "   unit present: $(ls "$MNT/lib/systemd/system/polaris-growroot.service" \
                            "$MNT/etc/systemd/system/polaris-growroot.service" 2>/dev/null | wc -l) (want >= 1)"
echo "   enabled:      $(ls "$MNT/etc/systemd/system/multi-user.target.wants/polaris-growroot.service" 2>/dev/null | wc -l) (want 1)"
echo "   growpart:     $([ -x "$MNT/usr/bin/growpart" ] && echo present || echo 'MISSING (script falls back to sfdisk)')"

# SSH must be BOTH keyless (shared keys across every flashed card would be a
# real vulnerability) and able to make its own keys on first boot. Ship only
# the first half and sshd fails ExecStartPre=sshd -t forever: port 22 answers
# "connection refused" and a headless board is unreachable.
# A published image must not carry the build board's WiFi key. NetworkManager
# writes the PSK into the .nmconnection AND (on Ubuntu) exports it to
# /etc/netplan/90-NM-*.yaml as a 64-hex pre-shared key, which is enough to join
# the network on its own. Two shipped images had the maintainer's home network
# in them. polaris-hotspot is deliberate: its password is public.
echo "== wifi credentials (want 0 everywhere)"
# find, not `ls | grep`: this script runs under `set -euo pipefail`, and a glob
# that matches nothing makes ls exit non-zero, which killed the script HERE --
# on a clean image, i.e. exactly the passing case. The report simply stopped
# after the grow-root section and read like a pass. find exits 0 with no hits,
# and the grep is wrapped so an empty result is not a pipeline failure.
nm=$(find "$MNT/etc/NetworkManager/system-connections" -maxdepth 1 -name '*.nmconnection' \
        ! -name 'polaris-hotspot.nmconnection' 2>/dev/null | wc -l)
np=$( { grep -rlisE '^[[:space:]]*(password|psk)[[:space:]]*:|wifis:' "$MNT/etc/netplan/" 2>/dev/null || true; } | wc -l)
ws=$(find "$MNT/etc/wpa_supplicant" -maxdepth 1 -name 'wpa_supplicant*.conf' 2>/dev/null | wc -l)
echo "   foreign NM connections : $nm"
echo "   netplan files with keys : $np"
echo "   wpa_supplicant configs  : $ws"
if [ "$nm" -ne 0 ] || [ "$np" -ne 0 ] || [ "$ws" -ne 0 ]; then
    echo "   !! DO NOT PUBLISH: this image carries someone's WiFi credentials"
    { grep -rlisE 'psk|password' "$MNT/etc/netplan/" \
        "$MNT/etc/NetworkManager/system-connections/" 2>/dev/null || true; } \
        | grep -v 'polaris-hotspot' | sed "s|$MNT|      |" || true
fi

echo "== ssh (want: 0 host keys AND the keygen unit enabled)"
echo "   host keys:    $(ls "$MNT/etc/ssh/ssh_host_"* 2>/dev/null | wc -l) (want 0)"
echo "   keygen unit:  $(ls "$MNT/lib/systemd/system/polaris-sshkeys.service" 2>/dev/null | wc -l) (want 1)"
echo "   keygen on:    $(ls "$MNT/etc/systemd/system/multi-user.target.wants/polaris-sshkeys.service" 2>/dev/null | wc -l) (want 1)"
echo "   sshd enabled: $(ls "$MNT/etc/systemd/system/multi-user.target.wants/ssh.service" 2>/dev/null | wc -l)"

echo "== kernel console (the LAST console= must be a screen: tty0 / tty1)"
found=0
for f in "$MNT/boot/firmware/cmdline.txt" "$MNT/boot/cmdline.txt"; do
    # Raspberry Pi OS leaves a stub at /boot/cmdline.txt pointing at the real
    # one under /boot/firmware; skip it so it does not read like a second
    # configuration.
    [ -f "$f" ] || continue
    grep -q 'DO NOT EDIT THIS FILE' "$f" && continue
    echo "   $f:"; sed 's/^/     /' "$f"; echo; found=1
done
if [ -f "$MNT/boot/grub/grub.cfg" ]; then
    echo "   grub.cfg (normal entries):"
    grep -E '^\s+linux\s' "$MNT/boot/grub/grub.cfg" | head -2 | sed 's/^/     /'
    found=1
fi
for f in "$MNT/boot/orangepiEnv.txt" "$MNT/boot/armbianEnv.txt"; do
    [ -f "$f" ] && { echo "   $f: $(grep -E '^console=' "$f" || echo '(console= not set, boot.cmd default applies)')"; found=1; }
done
for f in "$MNT$BOOT_MNT"/loader/entries/*.conf; do
    [ -f "$f" ] && { echo "   $(basename "$f"):"; grep -E '^options' "$f" | sed 's/^/     /'; found=1; }
done
[ "$found" = 0 ] && echo "   (no known boot config found -- check by hand)"

echo "== free space in the image"
df -h "$MNT" | tail -1
