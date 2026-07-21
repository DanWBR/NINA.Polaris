#!/bin/sh
# Polaris headless USB auto-mount helper.
#
# Invoked by udev (via systemd-run, detached) on add/remove of a USB
# mass-storage filesystem. Mounts it under /media/polaris so the Polaris app
# (UsbDriveWatcherService) can detect it and offer it as the capture home.
# Best-effort: every failure is swallowed, a headless host must never block or
# error out on a plugged-in stick.
set -u

ACTION="${1:-}"
KDEV="${2:-}"
[ -n "$KDEV" ] || exit 0
DEV="/dev/$KDEV"
BASE="/media/polaris"

POLUID="$(id -u polaris 2>/dev/null || echo 0)"
POLGID="$(id -g polaris 2>/dev/null || echo 0)"

# Keep mount-dir names filesystem-safe and short.
sanitize() { printf '%s' "$1" | tr -c 'A-Za-z0-9._-' '_' | cut -c1-64; }

case "$ACTION" in
  add)
    # Already mounted (the system disk, an fstab entry, or a duplicate add
    # event): leave it alone.
    grep -qs "^$DEV " /proc/mounts && exit 0

    FSTYPE="$(blkid -o value -s TYPE "$DEV" 2>/dev/null || true)"
    [ -n "$FSTYPE" ] || exit 0
    case "$FSTYPE" in
      swap|crypto_LUKS|linux_raid_member|LVM2_member) exit 0 ;;
    esac

    # Friendly mount-dir name: the filesystem label, else the partition label,
    # else the drive model from sysfs (e.g. "Cruzer Blade"), else a plain "usb".
    # Never the UUID (an unlabelled stick would mount as a huge GUID).
    LABEL="$(blkid -o value -s LABEL "$DEV" 2>/dev/null || true)"
    PARTLABEL="$(blkid -o value -s PARTLABEL "$DEV" 2>/dev/null || true)"
    DISK="$(echo "$KDEV" | sed 's/[0-9]*$//')"   # sdb1 -> sdb
    MODEL=""
    if [ -r "/sys/block/$DISK/device/model" ]; then
        MODEL="$(cat "/sys/block/$DISK/device/model" 2>/dev/null | sed 's/[[:space:]]\{1,\}$//;s/^[[:space:]]\{1,\}//')"
    fi
    NAME="$(sanitize "${LABEL:-${PARTLABEL:-${MODEL:-usb}}}")"
    [ -n "$NAME" ] || NAME="usb"
    TARGET="$BASE/$NAME"
    # Disambiguate if a drive with the same name is already mounted (two
    # unlabelled sticks both fall back to "usb").
    if mountpoint -q "$TARGET" 2>/dev/null; then TARGET="$BASE/${NAME}-${KDEV}"; fi
    mkdir -p "$TARGET" || exit 0

    case "$FSTYPE" in
      vfat|exfat|ntfs)
        # No Unix ownership on these; hand the whole tree to the polaris user.
        mount -t "$FSTYPE" -o "rw,nosuid,nodev,noatime,uid=$POLUID,gid=$POLGID,umask=0022" \
              "$DEV" "$TARGET" || { rmdir "$TARGET" 2>/dev/null; exit 0; }
        ;;
      *)
        mount -o "rw,nosuid,nodev,noatime" "$DEV" "$TARGET" \
              || { rmdir "$TARGET" 2>/dev/null; exit 0; }
        # Native Linux filesystems carry their own permissions; make the mount
        # root writable by the service user.
        chown "$POLUID:$POLGID" "$TARGET" 2>/dev/null || true
        ;;
    esac
    ;;

  remove)
    # The node is gone. Unmount any /media/polaris mount whose backing device
    # has disappeared, then prune the empty mount directories.
    awk '$2 ~ "^/media/polaris/" {print $2}' /proc/mounts 2>/dev/null | while read -r MP; do
      SRC="$(findmnt -n -o SOURCE --mountpoint "$MP" 2>/dev/null || true)"
      if [ -z "$SRC" ] || [ ! -e "$SRC" ]; then
        umount -l "$MP" 2>/dev/null || true
      fi
    done
    for d in "$BASE"/*; do
      [ -d "$d" ] || continue
      mountpoint -q "$d" 2>/dev/null || rmdir "$d" 2>/dev/null || true
    done
    ;;
esac
exit 0
