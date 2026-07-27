#!/bin/bash
# =============================================================================
# polaris-install-to-disk - move a running Polaris USB onto an internal disk
# =============================================================================
# Writing the raw image straight to a mini PC's SSD is the awkward part of the
# whole setup: you cannot write to the disk you booted from, so it means
# booting some other Linux and dd'ing 13 GB at it. This flips it around. Write
# the image to a USB STICK, which everyone already knows how to do with
# balenaEtcher or Rufus, boot the mini PC from it, and let the running system
# install ITSELF to the internal disk.
#
#   sudo polaris-install-to-disk            # pick from a menu
#   sudo polaris-install-to-disk /dev/nvme0n1 --yes
#
# What it does: partitions the target GPT (ESP + ext4 root filling the disk),
# copies the running root filesystem, rewrites fstab with the new UUIDs,
# installs GRUB into the target's own ESP, and gives the clone a fresh
# machine-id. Then you power off, pull the stick, and boot from the SSD.
#
# It refuses to touch the disk it is running from, and it asks before erasing
# anything. Everything on the target is destroyed.
# =============================================================================
set -uo pipefail

TARGET="${1:-}"
ASSUME_YES=0
for a in "$@"; do
    case "$a" in
        --yes|-y) ASSUME_YES=1 ;;
        -h|--help) sed -n '2,22p' "$0" | sed 's/^# \?//'; exit 0 ;;
        /dev/*) TARGET="$a" ;;
        *) ;;
    esac
done
[ "${TARGET:-}" = "--yes" ] && TARGET=""
[ "${TARGET:-}" = "-y" ] && TARGET=""

die()  { echo -e "\e[31merror:\e[0m $*" >&2; exit 1; }
info() { echo -e "\e[36m==>\e[0m $*"; }

[ "$(id -u)" -eq 0 ] || die "run me with sudo"
for t in sgdisk partprobe mkfs.ext4 mkfs.vfat rsync grub-install blkid lsblk findmnt; do
    command -v "$t" >/dev/null || die "missing tool: $t (apt install gdisk rsync dosfstools grub-efi-amd64)"
done
[ -d /sys/firmware/efi ] || die "this installs a UEFI system; the machine booted in legacy BIOS mode"

# ---- what are we running from? Never offer or accept it as the target -------
ROOT_SRC="$(findmnt -no SOURCE /)" || die "cannot resolve the root device"
BOOT_DISK="/dev/$(lsblk -no PKNAME "$ROOT_SRC" 2>/dev/null)"
[ -b "$BOOT_DISK" ] || die "cannot resolve the boot disk of $ROOT_SRC"

candidates() {
    # Whole disks only (TYPE=disk), excluding the one we booted from and any
    # zram/loop/rom device.
    lsblk -dn -o NAME,SIZE,MODEL,TYPE | awk '$4=="disk"{print}' | while read -r n s m t; do
        [ "/dev/$n" = "$BOOT_DISK" ] && continue
        case "$n" in loop*|sr*|zram*) continue ;; esac
        printf '/dev/%s\t%s\t%s\n' "$n" "$s" "${m:-}"
    done
}

if [ -z "${TARGET:-}" ]; then
    mapfile -t DISKS < <(candidates)
    [ "${#DISKS[@]}" -gt 0 ] || die "no target disk found (only the boot device $BOOT_DISK is present)"
    echo "Install Polaris to which disk? (everything on it is destroyed)"
    i=1
    for d in "${DISKS[@]}"; do printf '  %d) %s\n' "$i" "$d"; i=$((i+1)); done
    printf '  q) cancel\n> '
    read -r choice
    [ "$choice" = "q" ] && exit 0
    TARGET="$(echo "${DISKS[$((choice-1))]:-}" | cut -f1)"
    [ -n "$TARGET" ] || die "nothing selected"
fi

[ -b "$TARGET" ] || die "$TARGET is not a block device"
[ "$TARGET" != "$BOOT_DISK" ] || die "$TARGET is the disk you booted from"
case "$(lsblk -dn -o TYPE "$TARGET")" in
    disk) ;;
    *) die "$TARGET is not a whole disk (pass /dev/sda, not /dev/sda1)" ;;
esac
# A partition of the boot disk is equally fatal.
findmnt -no SOURCE / | grep -q "^${TARGET}" && die "$TARGET holds the running root"

echo
info "Target: $TARGET"
lsblk -o NAME,SIZE,FSTYPE,LABEL,MOUNTPOINT "$TARGET" || true
echo
echo -e "\e[31mEverything on $TARGET will be erased.\e[0m"
if [ "$ASSUME_YES" -ne 1 ]; then
    printf 'Type ERASE to continue: '
    read -r confirm
    [ "$confirm" = "ERASE" ] || { echo "cancelled"; exit 0; }
fi

# ---- partition ------------------------------------------------------------
info "Partitioning $TARGET (GPT: 512M ESP + ext4 root filling the disk)"
umount -q "${TARGET}"* 2>/dev/null || true
wipefs -a "$TARGET" >/dev/null 2>&1 || true
sgdisk --zap-all "$TARGET" >/dev/null || die "sgdisk zap failed"
sgdisk -n1:0:+512M -t1:ef00 -c1:"EFI System" \
       -n2:0:0     -t2:8300 -c2:"polaris-root" "$TARGET" >/dev/null \
    || die "sgdisk partitioning failed"
partprobe "$TARGET"; sleep 2

# nvme/mmc name partitions p1/p2, sd* name them 1/2.
case "$TARGET" in
    *nvme*|*mmcblk*) P1="${TARGET}p1"; P2="${TARGET}p2" ;;
    *) P1="${TARGET}1"; P2="${TARGET}2" ;;
esac
[ -b "$P1" ] && [ -b "$P2" ] || die "partitions did not appear ($P1, $P2)"

info "Formatting"
mkfs.vfat -F32 -n POLARIS-ESP "$P1" >/dev/null || die "mkfs.vfat failed"
mkfs.ext4 -F -L polaris-root "$P2" >/dev/null  || die "mkfs.ext4 failed"

# ---- copy ------------------------------------------------------------------
MNT="$(mktemp -d /tmp/polaris-target.XXXXXX)"
cleanup() {
    for m in "$MNT/boot/efi" "$MNT/dev/pts" "$MNT/dev" "$MNT/proc" "$MNT/sys" "$MNT/run" "$MNT"; do
        umount -q "$m" 2>/dev/null || true
    done
    rmdir "$MNT" 2>/dev/null || true
}
trap cleanup EXIT
mount "$P2" "$MNT" || die "cannot mount the new root"
mkdir -p "$MNT/boot/efi"
mount "$P1" "$MNT/boot/efi" || die "cannot mount the new ESP"

info "Copying the running system (this is the long part)"
# --one-file-system keeps us on / even if something new gets mounted midway;
# the pseudo filesystems and the target itself are excluded explicitly.
rsync -aHAXx --info=progress2 --numeric-ids \
      --exclude='/dev/*' --exclude='/proc/*' --exclude='/sys/*' \
      --exclude='/tmp/*' --exclude='/run/*' --exclude='/mnt/*' \
      --exclude='/media/*' --exclude='/lost+found' \
      --exclude="$MNT" \
      / "$MNT/" || die "rsync failed"

# ---- make it its own system ------------------------------------------------
ROOT_UUID="$(blkid -s UUID -o value "$P2")"
ESP_UUID="$(blkid -s UUID -o value "$P1")"
[ -n "$ROOT_UUID" ] && [ -n "$ESP_UUID" ] || die "cannot read the new UUIDs"

info "Writing fstab"
cat >"$MNT/etc/fstab" <<EOF
# Written by polaris-install-to-disk
UUID=$ROOT_UUID  /          ext4  defaults,noatime  0 1
UUID=$ESP_UUID   /boot/efi  vfat  umask=0077        0 1
EOF

# A clone that keeps the source machine-id gets the same DHCP lease and the
# same systemd identity as the USB stick. Let first boot mint a new one.
rm -f "$MNT/etc/machine-id"
: > "$MNT/etc/machine-id"
rm -f "$MNT/var/lib/dbus/machine-id" 2>/dev/null || true

# The root partition now fills the disk, so the first-boot grow is done.
mkdir -p "$MNT/var/lib" && touch "$MNT/var/lib/polaris-growroot.done"

info "Installing GRUB into the target ESP"
for d in dev dev/pts proc sys run; do
    mount --rbind "/$d" "$MNT/$d" 2>/dev/null || mount --bind "/$d" "$MNT/$d"
done
chroot "$MNT" grub-install --target=x86_64-efi --efi-directory=/boot/efi \
        --bootloader-id=polaris --recheck >/dev/null 2>&1 \
    || die "grub-install failed"
# Some firmwares only boot the fallback path; copy the loader there too.
mkdir -p "$MNT/boot/efi/EFI/BOOT"
cp -f "$MNT/boot/efi/EFI/polaris/grubx64.efi" "$MNT/boot/efi/EFI/BOOT/BOOTX64.EFI" 2>/dev/null || true
chroot "$MNT" update-grub >/dev/null 2>&1 || die "update-grub failed"

sync
info "Done."
echo
echo "  Power off, remove the USB stick, and boot from $TARGET."
echo "  Polaris comes up on https://<hostname>.local:5000 as before; the"
echo "  profiles and settings on the stick came along with the copy."
