#!/bin/bash
# Keep the persistent journal on the card when /var/log is a RAM disk.
#
# Several board images run /var/log from a small zram or tmpfs to spare the
# card (orangepi-ramlog, armbian-ramlog, log2ram) and keep the card's own
# /var/log reachable at /var/log.hdd (/var/hdd.log for log2ram). Polaris turns
# on a persistent journal (journald.conf.d/polaris-persistent.conf), and on
# those images it landed in /var/log/journal, i.e. in RAM. orangepi-ramlog
# then synced it to the card and copied it back into RAM at every boot: on an
# Orange Pi 4 Pro (2026-11-04) 44 MB of journal went into a 47 MB zram, and
# rsyslog failed "No space left on device" for hours, until the ramlog
# truncate job ran.
#
# What this does, run by polaris-journal-to-card.service after the ramlog
# service and before journald flushes to /var/log/journal:
#   - /var/log on disk: nothing. The 300M cap in the drop-in applies.
#   - /var/log in RAM, card copy found: bind-mount <card>/journal over
#     /var/log/journal, so the journal is written to the card directly.
#     Anything already in the RAM copy is moved to the card first.
#   - /var/log in RAM, no card copy: cap the journal at a fifth of the RAM
#     disk with a drop-in under /run, so it cannot crowd out the other logs.
#
# The ramlog rsync excludes for the journal are written by the postinst into
# /etc/default/{orangepi,armbian}-ramlog; they keep a stale copy out of RAM if
# this unit ever fails.
set -u

VARLOG=${POLARIS_VARLOG:-/var/log}
CARD_DIRS=${POLARIS_CARD_LOG_DIRS:-/var/log.hdd /var/hdd.log}
CAP_DROPIN=${POLARIS_JOURNAL_CAP_DROPIN:-/run/systemd/journald.conf.d/zz-polaris-ramlog-cap.conf}
FLUSHED=${POLARIS_JOURNAL_FLUSHED:-/run/systemd/journal/flushed}
JOURNAL=$VARLOG/journal

log() { echo "polaris-journal: $*"; }

# True when the filesystem holding $1 lives in memory: tmpfs/ramfs, or any
# filesystem on a zram device (orangepi-ramlog formats its zram as ext4).
in_ram() {
    local fstype src
    read -r fstype src < <(findmnt -n -r -o FSTYPE,SOURCE --target "$1" 2>/dev/null) || return 1
    case "$fstype" in tmpfs|ramfs) return 0 ;; esac
    case "$src" in /dev/zram*) return 0 ;; esac
    return 1
}

in_ram "$VARLOG" || exit 0

if mountpoint -q "$JOURNAL"; then
    log "$JOURNAL is already a mount of its own, leaving it alone"
    exit 0
fi
# Current armbian-ramlog links /var/log/journal to /var/log.hdd/journal itself.
if [ -L "$JOURNAL" ] && ! in_ram "$(readlink -f "$JOURNAL")"; then
    log "$JOURNAL already points at $(readlink -f "$JOURNAL"), on disk"
    exit 0
fi

card=
for d in $CARD_DIRS; do
    [ -d "$d" ] || continue
    in_ram "$d" && continue
    card=$d
    break
done

if [ -z "$card" ]; then
    size_k=$(df -Pk "$VARLOG" 2>/dev/null | awk 'NR == 2 { print $2 }')
    case "$size_k" in
        ''|*[!0-9]*|0) log "cannot size $VARLOG, journal left as configured"; exit 0 ;;
    esac
    cap=$((size_k / 5))
    mkdir -p "$(dirname "$CAP_DROPIN")"
    cat > "$CAP_DROPIN" <<EOF
# Written at boot by polaris-journal-to-card.sh: $VARLOG is a ${size_k}K RAM
# disk with no copy on the card, so the persistent journal lives in RAM.
[Journal]
SystemMaxUse=${cap}K
SystemMaxFileSize=$((cap / 4))K
RuntimeMaxUse=${cap}K
EOF
    log "$VARLOG is in RAM and has no card copy; journal capped at ${cap}K"
    # journald reads its configuration only when it starts.
    systemctl --no-block try-restart systemd-journald.service >/dev/null 2>&1 || true
    exit 0
fi

dst=$card/journal
mkdir -p "$dst" "$JOURNAL"
# What systemd-tmpfiles gives the journal directory.
chgrp systemd-journal "$dst" 2>/dev/null || true
chmod 2755 "$dst" 2>/dev/null || true

relinquished=
if [ -n "$(ls -A "$JOURNAL" 2>/dev/null)" ]; then
    # On a running system journald writes here right now: send it back to /run
    # while the files move, and flush it to the card once they are there.
    if [ -e "$FLUSHED" ] && journalctl --relinquish-var >/dev/null 2>&1; then
        relinquished=1
    fi
    # The RAM copy is the newer one: ramlog loaded it from the card at boot
    # and journald has only appended to it since.
    if cp -a "$JOURNAL/." "$dst/"; then
        find "$JOURNAL" -mindepth 1 -delete
        log "moved the journal out of the RAM disk into $dst"
    else
        log "could not copy the RAM journal to $dst, it stays in RAM until the next boot"
    fi
fi

if mount --bind "$dst" "$JOURNAL"; then
    log "persistent journal on the card: $dst mounted on $JOURNAL"
    rc=0
else
    log "bind mount of $dst on $JOURNAL failed"
    rc=1
fi
if [ -n "$relinquished" ]; then
    journalctl --flush >/dev/null 2>&1 || true
fi
exit $rc
