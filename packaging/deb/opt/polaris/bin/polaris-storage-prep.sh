#!/bin/bash
# Prepares a data disk (typically an NVMe SSD) for Polaris captures and makes
# the mount survive a reboot.
#
# Started on demand as root via `systemctl start polaris-storage-prep.service`,
# which the unprivileged polaris service user may trigger through the SAME
# manage-units PolicyKit rule the self-update and the solver-database install
# already use. No new policy, no password.
#
# Two actions, staged by the app in
# /home/polaris/.cache/polaris-storage/request:
#
#     ACTION=mount   UUID=<filesystem uuid>  MOUNT=<absolute path>
#     ACTION=format  DEV=<whole disk>        MOUNT=<absolute path>  CONFIRM=ERASE
#
# ACTION=mount is non-destructive: it mounts a filesystem that already exists.
# ACTION=format ERASES THE WHOLE DISK and is the reason every guard below is
# repeated here rather than trusted from the caller. The app checks the same
# things, but the app is the unprivileged half; this script is what actually
# holds the erase, so it re-derives its own answers from the kernel.
#
# The result is read back from /tmp/polaris-storage.log.
set -o pipefail

STAGE=/home/polaris/.cache/polaris-storage
REQ="$STAGE/request"
LOG=/tmp/polaris-storage.log
OWNER=polaris
# A blank disk smaller than this is far more likely to be a card reader, a
# bootloader partition or something plugged in by accident than a data disk
# somebody means to erase.
MIN_FORMAT_BYTES=$((8 * 1024 * 1024 * 1024))

echo "polaris storage prep starting $(date -u)" > "$LOG"

if [ ! -f "$REQ" ]; then
    echo "nothing staged at $REQ" >> "$LOG"
    exit 1
fi

field() { sed -n "s/.*\\b$1=\\([^ ]*\\).*/\\1/p" "$REQ"; }

ACTION=$(field ACTION)
MOUNT=$(field MOUNT)
UUID=$(field UUID)
DEV=$(field DEV)
CONFIRM=$(field CONFIRM)
[ -z "$ACTION" ] && ACTION=mount

if [ -z "$MOUNT" ]; then
    echo "request must carry MOUNT=; got: $(cat "$REQ")" >> "$LOG"
    exit 1
fi

# The mount point is ours to create, so it lives under a fixed prefix. Anything
# else is a request to mount a disk over some existing part of the system.
case "$MOUNT" in
    /mnt/polaris-*) ;;
    *) echo "refusing a mount point outside /mnt/polaris-*: $MOUNT" >> "$LOG"; exit 1 ;;
esac

# Which disk the system runs from. findmnt answers from the kernel, which is
# the only source that cannot be fooled by a name that looks safe.
ROOT_SRC=$(findmnt -no SOURCE / 2>/dev/null)
ROOT_DISK=$(lsblk -no PKNAME "$ROOT_SRC" 2>/dev/null)
[ -z "$ROOT_DISK" ] && ROOT_DISK=$(basename "$ROOT_SRC" 2>/dev/null)

# ---------------------------------------------------------------- format ----
if [ "$ACTION" = "format" ]; then
    if [ "$CONFIRM" != "ERASE" ]; then
        echo "refusing to format without CONFIRM=ERASE" >> "$LOG"
        exit 1
    fi

    # A whole disk, spelled exactly. No partition suffix (formatting a disk
    # means repartitioning it), no path traversal, no symlink games.
    case "$DEV" in
        /dev/nvme[0-9]*n[0-9] | /dev/sd[a-z] | /dev/sd[a-z][a-z] | /dev/mmcblk[0-9]) ;;
        *) echo "refusing to format: $DEV is not a whole-disk device node" >> "$LOG"; exit 1 ;;
    esac
    if [ ! -b "$DEV" ]; then
        echo "refusing to format: $DEV is not a block device" >> "$LOG"
        exit 1
    fi

    DEV_NAME=$(basename "$DEV")
    if [ "$DEV_NAME" = "$ROOT_DISK" ]; then
        echo "refusing to format the boot disk ($ROOT_DISK)" >> "$LOG"
        exit 1
    fi

    # Anything mounted anywhere on this disk means it is in use. Refusing is
    # cheap; erasing a disk somebody is reading from is not.
    INUSE=$(lsblk -ln -o MOUNTPOINT "$DEV" 2>/dev/null | grep -c '[^[:space:]]')
    if [ "$INUSE" != "0" ]; then
        echo "refusing to format $DEV: it has $INUSE mounted filesystem(s)" >> "$LOG"
        lsblk -o NAME,SIZE,FSTYPE,LABEL,MOUNTPOINT "$DEV" >> "$LOG" 2>&1
        exit 1
    fi
    if grep -q "^/dev/$DEV_NAME" /proc/swaps 2>/dev/null; then
        echo "refusing to format $DEV: swap is active on it" >> "$LOG"
        exit 1
    fi

    SIZE_BYTES=$(blockdev --getsize64 "$DEV" 2>/dev/null || echo 0)
    if [ "$SIZE_BYTES" -lt "$MIN_FORMAT_BYTES" ]; then
        echo "refusing to format $DEV: $SIZE_BYTES bytes is below the $MIN_FORMAT_BYTES floor" >> "$LOG"
        exit 1
    fi

    echo "formatting $DEV ($SIZE_BYTES bytes). Contents before:" >> "$LOG"
    lsblk -o NAME,SIZE,FSTYPE,LABEL "$DEV" >> "$LOG" 2>&1

    # GPT + one Linux partition spanning the disk, via sfdisk (util-linux, so
    # no new package dependency; parted and sgdisk are not guaranteed present).
    wipefs -a "$DEV" >> "$LOG" 2>&1
    if ! printf 'label: gpt\n,,L\n' | sfdisk --quiet --wipe always "$DEV" >> "$LOG" 2>&1; then
        echo "RESULT=failed (partitioning $DEV)" >> "$LOG"
        exit 1
    fi
    partprobe "$DEV" >> "$LOG" 2>&1
    udevadm settle --timeout=15 >> "$LOG" 2>&1

    # Ask the kernel for the partition name rather than guessing at suffixes:
    # nvme0n1 -> nvme0n1p1 but sda -> sda1, and the rule is not worth encoding.
    PART=$(lsblk -ln -o NAME,TYPE "$DEV" 2>/dev/null | awk '$2=="part"{print "/dev/"$1; exit}')
    if [ -z "$PART" ] || [ ! -b "$PART" ]; then
        echo "RESULT=failed (no partition appeared on $DEV)" >> "$LOG"
        exit 1
    fi
    echo "partition: $PART" >> "$LOG"

    # ext4: journaled, native ownership and permissions, and the same fstab
    # path the mount action already uses.
    #   -m 1  reserve 1% for root instead of the default 5%. On a 1 TB data
    #         disk the default would set aside 50 GB for nothing.
    #   lazy_* let mkfs return in seconds on a large disk; the kernel finishes
    #         initialising the tables in the background.
    if ! mkfs.ext4 -F -L POLARIS -m 1 -E lazy_itable_init=1,lazy_journal_init=1 \
            "$PART" >> "$LOG" 2>&1; then
        echo "RESULT=failed (mkfs.ext4 on $PART)" >> "$LOG"
        exit 1
    fi
    udevadm settle --timeout=15 >> "$LOG" 2>&1

    UUID=$(blkid -o value -s UUID "$PART" 2>/dev/null)
    if [ -z "$UUID" ]; then
        echo "RESULT=failed (the new filesystem has no UUID)" >> "$LOG"
        exit 1
    fi
    echo "formatted: $PART UUID=$UUID" >> "$LOG"
    # Fall through into the mount path below with the freshly made filesystem.
fi

# ----------------------------------------------------------------- mount ----
if [ -z "$UUID" ]; then
    echo "request must carry UUID= (or ACTION=format); got: $(cat "$REQ")" >> "$LOG"
    exit 1
fi

TARGET=$(blkid -U "$UUID" 2>/dev/null)
if [ -z "$TARGET" ]; then
    echo "no filesystem with UUID=$UUID is present" >> "$LOG"
    exit 1
fi
echo "target: $TARGET (UUID=$UUID) -> $MOUNT" >> "$LOG"

# Never touch the disk we are running from, whichever action got us here.
TARGET_DISK=$(lsblk -no PKNAME "$TARGET" 2>/dev/null)
if [ -n "$ROOT_DISK" ] && [ "$ROOT_DISK" = "$TARGET_DISK" ]; then
    echo "refusing: $TARGET lives on the boot disk ($ROOT_DISK)" >> "$LOG"
    exit 1
fi

FSTYPE=$(blkid -o value -s TYPE "$TARGET" 2>/dev/null)
if [ -z "$FSTYPE" ]; then
    echo "no filesystem on $TARGET; nothing to mount" >> "$LOG"
    exit 1
fi
echo "filesystem: $FSTYPE" >> "$LOG"

mkdir -p "$MOUNT" || { echo "could not create $MOUNT" >> "$LOG"; exit 1; }

# nofail is not optional: without it a disk that is missing, dead or simply
# unplugged at boot drops the machine into the emergency shell, and a headless
# observatory host that will not boot is a far worse failure than no captures.
FSTAB_LINE="UUID=$UUID $MOUNT $FSTYPE defaults,nofail,x-systemd.device-timeout=10 0 2"
if grep -q "UUID=$UUID" /etc/fstab; then
    echo "fstab already carries UUID=$UUID, leaving it alone" >> "$LOG"
else
    cp /etc/fstab "/etc/fstab.polaris-backup-$(date -u +%Y%m%dT%H%M%S)" 2>/dev/null
    echo "$FSTAB_LINE" >> /etc/fstab
    echo "fstab += $FSTAB_LINE" >> "$LOG"
fi

if mountpoint -q "$MOUNT"; then
    echo "$MOUNT is already mounted" >> "$LOG"
else
    mount "$MOUNT" >> "$LOG" 2>&1 || {
        echo "mount failed; rolling the fstab line back" >> "$LOG"
        grep -v "UUID=$UUID $MOUNT " /etc/fstab > /etc/fstab.new && mv /etc/fstab.new /etc/fstab
        exit 1
    }
fi

# The app runs as the polaris user and has to be able to write here. Only the
# capture directory is chowned, not the whole volume: a disk that already
# carries someone's data should not have its ownership rewritten.
CAPTURE="$MOUNT/files"
mkdir -p "$CAPTURE" || { echo "could not create $CAPTURE" >> "$LOG"; exit 1; }
chown "$OWNER":"$OWNER" "$CAPTURE" 2>/dev/null
chmod 0775 "$CAPTURE" 2>/dev/null

# Prove it: the app reads this back before it points the capture root at the
# new location, so a mount that silently did not happen cannot end up with
# frames being written into an empty directory on the ROOT filesystem.
if mountpoint -q "$MOUNT"; then
    echo "mounted OK, capture directory ready: $CAPTURE" >> "$LOG"
    echo "RESULT=ok CAPTURE=$CAPTURE" >> "$LOG"
    exit 0
fi

echo "RESULT=failed (not mounted after the attempt)" >> "$LOG"
exit 1
