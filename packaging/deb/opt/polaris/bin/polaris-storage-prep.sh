#!/bin/bash
# Mounts a data disk (typically an NVMe SSD) for Polaris captures and makes the
# mount survive a reboot.
#
# Started on demand as root via `systemctl start polaris-storage-prep.service`,
# which the unprivileged polaris service user may trigger through the SAME
# manage-units PolicyKit rule the self-update and the solver-database install
# already use. No new policy, no password.
#
# NON-DESTRUCTIVE BY DESIGN. This script mounts a filesystem that ALREADY
# EXISTS and never partitions, formats or erases anything. A wrong click here
# must cost a mount, not a night of data. If the target has no filesystem the
# script refuses and says so.
#
# The app stages a single line in /home/polaris/.cache/polaris-storage/request:
#     UUID=<filesystem uuid>  MOUNT=<absolute path>
# and reads back /tmp/polaris-storage.log.
set -o pipefail

STAGE=/home/polaris/.cache/polaris-storage
REQ="$STAGE/request"
LOG=/tmp/polaris-storage.log
OWNER=polaris

echo "polaris storage prep starting $(date -u)" > "$LOG"

if [ ! -f "$REQ" ]; then
    echo "nothing staged at $REQ" >> "$LOG"
    exit 1
fi

UUID=$(sed -n 's/.*UUID=\([^ ]*\).*/\1/p' "$REQ")
MOUNT=$(sed -n 's/.*MOUNT=\([^ ]*\).*/\1/p' "$REQ")

if [ -z "$UUID" ] || [ -z "$MOUNT" ]; then
    echo "request must carry UUID= and MOUNT=; got: $(cat "$REQ")" >> "$LOG"
    exit 1
fi

# The mount point is ours to create, so it lives under a fixed prefix. Anything
# else is a request to mount a disk over some existing part of the system.
case "$MOUNT" in
    /mnt/polaris-*) ;;
    *) echo "refusing a mount point outside /mnt/polaris-*: $MOUNT" >> "$LOG"; exit 1 ;;
esac

DEV=$(blkid -U "$UUID" 2>/dev/null)
if [ -z "$DEV" ]; then
    echo "no filesystem with UUID=$UUID is present" >> "$LOG"
    exit 1
fi
echo "target: $DEV (UUID=$UUID) -> $MOUNT" >> "$LOG"

# Never touch the disk we are running from. findmnt answers from the kernel,
# which is the only source that cannot be fooled by a name that looks safe.
ROOT_SRC=$(findmnt -no SOURCE / 2>/dev/null)
ROOT_DISK=$(lsblk -no PKNAME "$ROOT_SRC" 2>/dev/null)
TARGET_DISK=$(lsblk -no PKNAME "$DEV" 2>/dev/null)
if [ -n "$ROOT_DISK" ] && [ "$ROOT_DISK" = "$TARGET_DISK" ]; then
    echo "refusing: $DEV lives on the boot disk ($ROOT_DISK)" >> "$LOG"
    exit 1
fi

FSTYPE=$(blkid -o value -s TYPE "$DEV" 2>/dev/null)
if [ -z "$FSTYPE" ]; then
    echo "no filesystem on $DEV; format it first (this tool never formats)" >> "$LOG"
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
