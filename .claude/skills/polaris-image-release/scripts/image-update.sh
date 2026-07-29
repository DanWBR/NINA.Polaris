#!/bin/bash
# Install a Polaris .deb into a flashable image, then sanitize it.
#
#   image-update.sh <image.img> <root-offset-sectors> --deb <file.deb>
#                   [--boot-offset <sectors> --boot-mount <path>]
#
# Run as root inside WSL (wsl -u root). For an arm64 image, register the
# qemu-aarch64 binfmt handler first (see SKILL.md) so the package's own
# postinst can run under emulation.
#
# The image is never booted here, and must not be booted before shipping:
# booting writes a machine-id, a TLS keypair and the growroot marker into it,
# and every device flashed from that image then shares them.
set -euo pipefail

IMG=""; ROOT_SECT=""; DEB=""; BOOT_SECT=""; BOOT_MNT=""
MNT=/mnt/polaris-img

while [ $# -gt 0 ]; do
    case "$1" in
        --deb)          DEB="$2"; shift 2 ;;
        --boot-offset)  BOOT_SECT="$2"; shift 2 ;;
        --boot-mount)   BOOT_MNT="$2"; shift 2 ;;
        --mnt)          MNT="$2"; shift 2 ;;
        -*)             echo "unknown option: $1" >&2; exit 2 ;;
        *)              if [ -z "$IMG" ]; then IMG="$1"; else ROOT_SECT="$1"; fi; shift ;;
    esac
done

[ -f "${IMG:-}" ]      || { echo "usage: image-update.sh <image.img> <root-sectors> --deb <file.deb>" >&2; exit 2; }
[ -n "${ROOT_SECT:-}" ]|| { echo "missing root offset in sectors" >&2; exit 2; }
[ -f "${DEB:-}" ]      || { echo "missing --deb <file.deb>" >&2; exit 2; }

say() { echo "== $*"; }

cleanup() {
    set +e
    umount -l "$MNT/dev/pts" "$MNT/dev" "$MNT/proc" "$MNT/sys" 2>/dev/null
    [ -n "$BOOT_MNT" ] && umount -l "$MNT$BOOT_MNT" 2>/dev/null
    umount "$MNT" 2>/dev/null
    [ -n "${LB:-}" ] && losetup -d "$LB" 2>/dev/null
    [ -n "${LR:-}" ] && losetup -d "$LR" 2>/dev/null
}
trap cleanup EXIT

mkdir -p "$MNT"
LR=$(losetup --find --show --offset $((ROOT_SECT * 512)) "$IMG")
mount "$LR" "$MNT"
say "root from $LR"

if [ -n "$BOOT_SECT" ]; then
    LB=$(losetup --find --show --offset $((BOOT_SECT * 512)) "$IMG")
    mkdir -p "$MNT$BOOT_MNT"
    mount "$LB" "$MNT$BOOT_MNT"
    say "boot at $BOOT_MNT"
fi

# A foreign-architecture rootfs needs a binfmt handler, and WSL forgets its
# registrations whenever the VM shuts down (which it does after a few minutes
# idle). Without this guard the failure surfaces as "chroot: failed to run
# command 'env': Exec format error", which reads like a corrupt image.
host_arch=$(uname -m)
img_arch=$(od -An -tx1 -j18 -N2 "$MNT/bin/true" 2>/dev/null | tr -d ' ')   # ELF e_machine, little endian
case "$img_arch" in
    b700) img_arch=aarch64 ;;
    3e00) img_arch=x86_64 ;;
    *)    img_arch=unknown ;;
esac
if [ "$img_arch" = aarch64 ] && [ "$host_arch" != aarch64 ]; then
    if [ ! -e /proc/sys/fs/binfmt_misc/qemu-aarch64 ]; then
        mount | grep -q binfmt_misc || mount -t binfmt_misc none /proc/sys/fs/binfmt_misc
        conf=/usr/lib/binfmt.d/qemu-aarch64.conf
        [ -f "$conf" ] || { echo "install qemu-user-static first (apt install qemu-user-static)" >&2; exit 3; }
        # Verbatim: the kernel parses the \xHH escapes. Expanding them here
        # once made every exec on the distro fail with ELOOP.
        grep '^:qemu-aarch64:' "$conf" | while IFS= read -r l; do
            printf '%s' "$l" > /proc/sys/fs/binfmt_misc/register
        done
        [ -e /proc/sys/fs/binfmt_misc/qemu-aarch64 ] || { echo "could not register the aarch64 binfmt handler" >&2; exit 3; }
    fi
    say "arm64 image on $host_arch: qemu-aarch64 binfmt handler active"
fi

say "before: $(chroot "$MNT" dpkg-query -W -f='${Version}' polaris 2>/dev/null || echo none)"

mount --bind /proc "$MNT/proc"
mount --bind /sys  "$MNT/sys"
mount --bind /dev  "$MNT/dev"
mount --bind /dev/pts "$MNT/dev/pts"

# There is no systemd in a chroot. Without this a postinst either fails trying
# to start the service or leaves half-started state behind.
printf '#!/bin/sh\nexit 101\n' > "$MNT/usr/sbin/policy-rc.d"
chmod +x "$MNT/usr/sbin/policy-rc.d"

cp "$DEB" "$MNT/tmp/polaris-install.deb"
say "dpkg -i"
chroot "$MNT" env DEBIAN_FRONTEND=noninteractive dpkg -i /tmp/polaris-install.deb
rm -f "$MNT/tmp/polaris-install.deb" "$MNT/usr/sbin/policy-rc.d"

say "after: $(chroot "$MNT" dpkg-query -W -f='${Version}' polaris)"

# ---- enable the units the postinst could not ------------------------------
# The postinst gates its whole systemd block on `[ -d /run/systemd/system ]`,
# which is FALSE inside a chroot, so `systemctl enable` never runs and a unit
# the package introduced arrives on the card DISABLED. The units that are
# enabled in these images got that way during the original build on a live
# system, which is why the gap went unnoticed: polaris.service worked, and
# only a newly added unit was missing.
#
# Symptom if this is skipped: grow-root and ssh-keygen ship present but inert,
# so the card never grows and sshd never gets host keys. image-verify.sh
# checks "enabled", not just "present", for exactly this reason.
say "enable packaged units"
for name in polaris-growroot.service polaris-sshkeys.service; do
    unit="$MNT/lib/systemd/system/$name"
    [ -f "$unit" ] || { echo "   $name not in this package, skipping"; continue; }
    want=$(sed -n 's/^WantedBy=//p' "$unit" | head -1 | awk '{print $1}')
    [ -n "$want" ] || { echo "   $name has no WantedBy, skipping"; continue; }
    mkdir -p "$MNT/etc/systemd/system/$want.wants"
    # Target is the path as the BOOTED system sees it, not the chroot path.
    ln -sf "/lib/systemd/system/$name" "$MNT/etc/systemd/system/$want.wants/$name"
    echo "   enabled $name -> $want"
done
# A unit the image build hand-wrote into /etc SHADOWS the packaged one, so the
# packaged fix would never take effect. The postinst removes these on a live
# host; do the same here.
for stale in polaris-growroot.service polaris-sshkeys.service; do
    if [ -e "$MNT/etc/systemd/system/$stale" ] && [ ! -L "$MNT/etc/systemd/system/$stale" ]; then
        rm -f "$MNT/etc/systemd/system/$stale"
        echo "   removed the legacy /etc copy of $stale"
    fi
done
rm -f "$MNT/usr/local/sbin/polaris-growroot.sh"

say "service links: $(ls "$MNT/etc/systemd/system/multi-user.target.wants/" 2>/dev/null | grep -c polaris)"

umount -l "$MNT/dev/pts" "$MNT/dev" "$MNT/proc" "$MNT/sys"

# ---- sanitize -------------------------------------------------------------
# Anything below identifies THIS build. Shipping it makes every flashed device
# claim the same identity, or skip a first-boot step it still needs.
say "sanitize"
: > "$MNT/etc/machine-id"
rm -f "$MNT/var/lib/dbus/machine-id"
find "$MNT" -maxdepth 7 -type d -path '*NINA.Polaris*' -name cert -exec rm -rf {} + 2>/dev/null || true
# Grow-root markers, all three spellings: the legacy one the image-build
# script wrote, and the packaged one (/var/lib/polaris/growroot.done). Ship
# either and the flashed card never grows past the image size.
rm -f "$MNT/var/lib/polaris-growroot.done" "$MNT/var/lib/misc/polaris-growroot.done" \
      "$MNT/var/lib/polaris/growroot.done" 2>/dev/null || true
# WiFi credentials. The build board is connected to somebody's home network,
# and NetworkManager writes that connection to disk: the .nmconnection carries
# the PSK, and on Ubuntu NM also EXPORTS it to /etc/netplan/90-NM-*.yaml, where
# the 64-hex pre-shared key is enough to join the network without the
# passphrase. Both shipped inside published archives (field report: "acho que
# as imagens estao indo com a senha da minha rede wifi") -- anyone who
# downloaded an image got the maintainer's WiFi key.
#
# polaris-hotspot is the exception: its password is documented (polaris1234)
# and the whole point is that a headless board comes up reachable.
say "strip WiFi credentials"
for f in "$MNT"/etc/NetworkManager/system-connections/*.nmconnection; do
    [ -f "$f" ] || continue
    case "$(basename "$f")" in
        polaris-hotspot.nmconnection) continue ;;
    esac
    echo "   removed $(basename "$f")"
    rm -f "$f"
done
# NM's netplan exports: one file per connection, regenerated from the
# connection at runtime, so removing them loses nothing.
for f in "$MNT"/etc/netplan/*.yaml; do
    [ -f "$f" ] || continue
    if grep -qiE '^[[:space:]]*(password|psk)[[:space:]]*:|wifis:' "$f"; then
        echo "   removed $(basename "$f") (carried a wifi key)"
        rm -f "$f"
    fi
done
rm -f "$MNT"/etc/wpa_supplicant/wpa_supplicant*.conf 2>/dev/null || true
# Which networks this board has seen, and the key NM uses to derive per-device
# secrets. Not credentials, but not ours to publish either.
rm -f "$MNT"/var/lib/NetworkManager/seen-bssids \
      "$MNT"/var/lib/NetworkManager/timestamps \
      "$MNT"/var/lib/NetworkManager/secret_key \
      "$MNT"/var/lib/NetworkManager/*.lease 2>/dev/null || true

# SSH host keys: every card flashed from this image would otherwise share one
# identity, and anyone holding the image could impersonate all of them.
# Removing them is only safe because polaris-sshkeys.service regenerates them
# before sshd on first boot -- the two halves have to ship together. Without
# that unit, deleting these means no SSH at all (sshd -t fails), which is
# exactly how the Orange Pi 4 Pro image went out.
rm -f "$MNT/etc/ssh/ssh_host_"* 2>/dev/null || true
rm -rf "$MNT/var/log/journal/"* 2>/dev/null || true
find "$MNT/var/log" -type f -name '*.log' -exec truncate -s 0 {} \; 2>/dev/null || true
rm -f "$MNT/root/.bash_history" "$MNT/home/polaris/.bash_history" 2>/dev/null || true
rm -rf "$MNT/var/lib/apt/lists/"* "$MNT/var/cache/apt/archives/"*.deb 2>/dev/null || true

say "residue: machine-id=$(stat -c %s "$MNT/etc/machine-id")B" \
    "certs=$(find "$MNT" -maxdepth 7 -type d -path '*NINA.Polaris*' -name cert 2>/dev/null | wc -l)" \
    "growroot=$(find "$MNT/var/lib" -name 'polaris-growroot*' -o -path '*/polaris/growroot.done' 2>/dev/null | wc -l)" \
    "journal=$(find "$MNT/var/log/journal" -type f 2>/dev/null | wc -l)" \
    "sshhostkeys=$(ls "$MNT/etc/ssh/ssh_host_"* 2>/dev/null | wc -l)"

# Free space full of old data compresses; zeroed free space disappears.
# The "No space left on device" at the end of the fill is the expected finish.
say "zero free space"
dd if=/dev/zero of="$MNT/ZEROFILL" bs=16M status=none || true
sync; rm -f "$MNT/ZEROFILL"; sync

df -h "$MNT" | tail -1
say "done"
