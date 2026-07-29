#!/bin/bash
# Grow the root partition + filesystem to fill the card/disk it was flashed
# onto. A shipped image keeps the root partition at its build size, so
# without this a 64 GB card carries a 8 GB filesystem and Polaris runs out of
# room on the first night.
#
# Runs on every boot until it SUCCEEDS. The "done" marker is written only
# after the filesystem is verified to cover its partition — an earlier
# version touched the marker unconditionally, so a host where growpart was
# missing recorded "done" on the first boot and never tried again. That is
# exactly how the Orange Pi 4 Pro image shipped a root that never grew, with
# nothing in the log to say so.
#
# Idempotent, and safe on a normal install where the partition already fills
# the disk: growpart reports there is nothing to do and the size check then
# writes the marker.
#
# Usage: polaris-growroot.sh [device]
#   device  a partition to grow instead of "/". Only for testing the logic
#           against a loop device; the marker is not written in that mode.
set -u

marker=/var/lib/polaris/growroot.done
log() { echo "polaris-growroot: $*"; }

testing=0
[ $# -gt 0 ] && [ -n "${1:-}" ] && testing=1

[ "$testing" = 0 ] && [ -f "$marker" ] && exit 0
# The pre-.deb marker (/var/lib/polaris-growroot.done) is deliberately NOT
# honoured: the old script wrote it whether or not the resize worked, so on
# the hosts that need this most it means "we gave up", not "we are done".
# Running again costs one no-op growpart on a host that is already full.

if [ "$testing" = 1 ]; then
    rootpart="$1"
else
    rootpart="$(findmnt -no SOURCE / 2>/dev/null || true)"
fi
[ -n "$rootpart" ] || { log "cannot determine the root device"; exit 0; }

# A root on LVM/overlay/NFS has no single parent disk to grow. Nothing to do,
# and nothing wrong either.
parent="$(lsblk -no PKNAME "$rootpart" 2>/dev/null | head -1 || true)"
if [ -z "$parent" ]; then
    log "$rootpart has no parent disk, nothing to grow"
    [ "$testing" = 0 ] && mkdir -p "$(dirname "$marker")" && touch "$marker"
    exit 0
fi
disk="/dev/$parent"
partnum="$(basename "$rootpart" | grep -o '[0-9]*$' || true)"
[ -n "$partnum" ] || { log "cannot parse a partition number from $rootpart"; exit 0; }

if command -v growpart >/dev/null 2>&1; then
    # Non-zero when the partition already fills the disk, which is fine.
    growpart "$disk" "$partnum" || log "growpart made no change to $rootpart"
else
    # growpart lives in cloud-guest-utils, which is only a Recommends and is
    # absent from at least one shipped image. sfdisk is util-linux, i.e.
    # always there, and "size = rest of the disk" is precisely what growpart
    # does internally. It refuses to overlap a following partition, so this
    # is a no-op rather than a hazard when root is not the last partition.
    log "growpart missing, extending $disk partition $partnum with sfdisk"
    echo ', +' | sfdisk --no-reread --force -N "$partnum" "$disk" \
        || log "sfdisk made no change (already full, or root is not the last partition)"
fi
command -v partprobe >/dev/null 2>&1 && partprobe "$disk" >/dev/null 2>&1 || true

resize2fs "$rootpart" || log "resize2fs failed on $rootpart"

# Only stop retrying once the filesystem really covers its partition. If it
# does not, the next boot tries again instead of leaving the operator with a
# full card and no explanation.
fs_blocks="$(dumpe2fs -h "$rootpart" 2>/dev/null | awk -F: '/^Block count/ { gsub(/ /,"",$2); print $2 }')"
fs_bsize="$(dumpe2fs -h "$rootpart" 2>/dev/null | awk -F: '/^Block size/ { gsub(/ /,"",$2); print $2 }')"
part_bytes="$(blockdev --getsize64 "$rootpart" 2>/dev/null || echo 0)"

if [ -n "$fs_blocks" ] && [ -n "$fs_bsize" ] && [ "$part_bytes" -gt 0 ] 2>/dev/null; then
    fs_bytes=$(( fs_blocks * fs_bsize ))
    # Within 1% counts as full: ext4 leaves a little slack and the exact
    # figure depends on the block group layout.
    if [ "$fs_bytes" -ge $(( part_bytes - part_bytes / 100 )) ]; then
        if [ "$testing" = 0 ]; then
            mkdir -p "$(dirname "$marker")" && touch "$marker"
        fi
        log "root filesystem fills its partition ($fs_bytes of $part_bytes bytes)"
        exit 0
    fi
    log "filesystem is $fs_bytes of $part_bytes bytes, retrying on the next boot"
    exit 0
fi

log "could not verify the filesystem size, retrying on the next boot"
exit 0
