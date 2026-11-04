#!/usr/bin/env bash
# The persistent journal on images whose /var/log is a RAM disk.
#
# On the Orange Pi images /var/log is a 47 MB zram (orangepi-ramlog) that is
# rsynced to and from the card's /var/log.hdd. The persistent journal Polaris
# turns on lived inside it, so at every boot orangepi-ramlog copied the card's
# journal back into RAM: 44 MB of it on 2026-11-04, and rsyslog failed "No
# space left on device" from boot until the ramlog truncate job ran.
#
# Three parts, all in a temp directory, nothing on the host touched:
#   1. the postinst's journal block against a root shaped like an image build
#      (chroot: no /run/systemd/system, systemctl that cannot reach a bus);
#   2. orangepi-ramlog's own rsync lines with the defaults file the postinst
#      leaves behind: the journal stays out of RAM at boot and is not touched
#      on the card at sync;
#   3. polaris-journal-to-card.sh deciding what to do, with findmnt, mount
#      and friends stubbed.
#
#   bash packaging/deb/tests/journal-ramlog-test.sh
set -u

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
POSTINST="$HERE/../DEBIAN/postinst"
POSTRM="$HERE/../DEBIAN/postrm"
SCRIPT="$HERE/../opt/polaris/bin/polaris-journal-to-card.sh"
for f in "$POSTINST" "$POSTRM" "$SCRIPT"; do
    [ -r "$f" ] || { echo "cannot read $f"; exit 2; }
done

W=$(mktemp -d)
trap 'rm -rf "$W"' EXIT
fails=0
ok()  { echo "  ok   $1"; }
bad() { echo "  FAIL $1"; fails=$((fails + 1)); }

# A systemctl that fails like one in a chroot with no bus, and records calls.
STUBS="$W/stubs"
mkdir -p "$STUBS"
cat > "$STUBS/systemctl" <<'EOF'
#!/bin/bash
echo "systemctl $*" >> "$FAKE/calls"
exit 1
EOF
chmod +x "$STUBS/systemctl"

# ---- 1. the postinst block, as in an image build --------------------------
echo "--- postinst in a chroot ---"
ROOT="$W/root"
mkdir -p "$ROOT/etc/default"
export FAKE="$W/fake1"; mkdir -p "$FAKE"; : > "$FAKE/calls"
printf 'ENABLED=true\nSIZE=50M\nUSE_RSYNC=true\n' > "$ROOT/etc/default/orangepi-ramlog"

start=$(grep -n '# JOURNAL: the persistent-journal drop-in' "$POSTINST" | cut -d: -f1)
end=$(grep -n '# REBOOT ON HANG:' "$POSTINST" | cut -d: -f1)
[ -n "$start" ] && [ -n "$end" ] || { echo "cannot find the journal block in postinst"; exit 2; }
sed -n "${start},$((end - 1))p" "$POSTINST" \
  | sed -e "s#/etc/#$ROOT/etc/#g" \
        -e "s#/opt/polaris/#$ROOT/opt/polaris/#g" \
        -e "s#\[ -d /run/systemd/system \]#[ -d $ROOT/run/systemd/system ]#g" \
  > "$W/postinst-block.sh"
bash -n "$W/postinst-block.sh" || { echo "extracted postinst block does not parse"; exit 1; }
PATH="$STUBS:/usr/bin:/bin" bash "$W/postinst-block.sh" > "$W/out" 2>&1
PATH="$STUBS:/usr/bin:/bin" bash "$W/postinst-block.sh" >> "$W/out" 2>&1

D="$ROOT/etc/default/orangepi-ramlog"
[ "$(grep -c '^XTRA_RSYNC_FROM+=(--exclude=/journal)$' "$D")" = 1 ] \
    && ok "orangepi-ramlog defaults exclude the journal, once after two runs" \
    || bad "orangepi-ramlog defaults: $(cat "$D")"
grep -q '^ENABLED=true$' "$D" && ok "the operator's settings are kept" || bad "ENABLED line lost"
[ ! -e "$ROOT/etc/default/armbian-ramlog" ] && ok "no armbian-ramlog defaults invented" \
    || bad "created /etc/default/armbian-ramlog on an image without it"
L="$ROOT/etc/systemd/system/sysinit.target.wants/polaris-journal-to-card.service"
[ "$(readlink "$L" 2>/dev/null)" = "/lib/systemd/system/polaris-journal-to-card.service" ] \
    && ok "unit enabled without a running systemd" || bad "unit not enabled: $(ls -l "$L" 2>&1)"
grep -q 'restart systemd-journald\|start polaris-journal-to-card' "$FAKE/calls" \
    && bad "touched live services in a chroot: $(cat "$FAKE/calls")" || ok "no live service touched in a chroot"
( set -e; . "$D"; [ "${#XTRA_RSYNC_TO[@]}" = 1 ] && [ "${XTRA_RSYNC_FROM[0]}" = "--exclude=/journal" ] ) 2>/dev/null \
    && ok "the defaults file still sources, arrays as ramlog reads them" || bad "defaults file does not source cleanly"

# postrm remove hands the journal back to the sync.
start=$(grep -n '# Hand the journal back to the RAM log sync' "$POSTRM" | cut -d: -f1)
[ -n "$start" ] || { echo "cannot find the journal block in postrm"; exit 2; }
awk -v s="$start" 'NR >= s { print; if (/^        done$/) exit }' "$POSTRM" \
  | sed -e "s#/etc/#$ROOT/etc/#g" > "$W/postrm-block.sh"
bash -n "$W/postrm-block.sh" || { echo "extracted postrm block does not parse"; exit 1; }
bash "$W/postrm-block.sh"
grep -q 'journal' "$D" && bad "postrm left the excludes: $(cat "$D")" || ok "postrm removes the excludes"
grep -q '^ENABLED=true$' "$D" && ok "postrm keeps the rest" || bad "postrm damaged the defaults file"

# ---- 2. orangepi-ramlog's rsync with those defaults ------------------------
echo "--- orangepi-ramlog sync ---"
if ! command -v rsync >/dev/null 2>&1; then
    echo "  skip rsync not installed"
else
    # syncFromDisk / syncToDisk from orangepi-build's orangepi-ramlog
    # (external/packages/bsp/common/usr/lib/orangepi/orangepi-ramlog), paths
    # and logging aside.
    from_disk() {
        rsync -aXWv --delete --exclude "lost+found" --exclude orangepi-ramlog.log \
            --exclude '*.gz' --exclude '*.xz' --exclude='*.[0-9]' --links \
            ${XTRA_RSYNC_FROM[@]+"${XTRA_RSYNC_FROM[@]}"} "$HDD/" "$RAM/" >/dev/null 2>&1
    }
    to_disk() {
        rsync -aXWv --exclude "lost+found" --exclude orangepi-ramlog.log --delete --links \
            ${XTRA_RSYNC_TO[@]+"${XTRA_RSYNC_TO[@]}"} "$RAM/" "$HDD/" >/dev/null 2>&1
    }
    card_state() {
        HDD="$W/sync/hdd"; RAM="$W/sync/ram"
        rm -rf "$W/sync"; mkdir -p "$HDD/journal/abc" "$RAM"
        echo log > "$HDD/syslog"
        head -c 4096 /dev/zero > "$HDD/journal/abc/system.journal"
    }

    # Without the excludes: what the rig did.
    card_state
    unset XTRA_RSYNC_FROM XTRA_RSYNC_TO
    from_disk
    [ -f "$RAM/journal/abc/system.journal" ] \
        && ok "baseline: without the excludes the card journal is copied into RAM (the bug)" \
        || bad "baseline did not reproduce the copy; the test proves nothing"

    printf 'ENABLED=true\n' > "$D"
    PATH="$STUBS:/usr/bin:/bin" bash "$W/postinst-block.sh" > /dev/null 2>&1
    card_state
    . "$D"
    from_disk
    [ -f "$RAM/syslog" ] && ok "boot sync still loads the other logs" || bad "syslog not loaded"
    [ ! -e "$RAM/journal" ] && ok "boot sync leaves the journal on the card" || bad "journal copied into RAM: $(ls -R "$RAM")"
    to_disk
    [ -f "$HDD/journal/abc/system.journal" ] && ok "sync to card (with --delete) keeps the card journal" \
        || bad "card journal deleted by the sync"
fi

# ---- 3. polaris-journal-to-card.sh ----------------------------------------
echo "--- polaris-journal-to-card.sh ---"
cat > "$STUBS/findmnt" <<'EOF'
#!/bin/bash
# Longest-prefix match of --target against "$FAKE/mounts" (path fstype source).
t=; while [ $# -gt 0 ]; do [ "$1" = --target ] && t=$2; shift; done
best=; bestlen=-1
while read -r p fs src; do
    case "$t/" in "$p"/*|"$p"/) [ ${#p} -gt $bestlen ] && { best="$fs $src"; bestlen=${#p}; } ;; esac
done < "$FAKE/mounts"
[ -n "$best" ] && echo "$best"
EOF
cat > "$STUBS/mountpoint" <<'EOF'
#!/bin/bash
grep -qxF -- "${@: -1}" "$FAKE/mountpoints"
EOF
cat > "$STUBS/mount" <<'EOF'
#!/bin/bash
echo "mount $*" >> "$FAKE/calls"
[ "$1" = --bind ] && echo "$3" >> "$FAKE/mountpoints"
exit 0
EOF
cat > "$STUBS/journalctl" <<'EOF'
#!/bin/bash
echo "journalctl $*" >> "$FAKE/calls"
EOF
cat > "$STUBS/df" <<'EOF'
#!/bin/bash
echo "Filesystem 1024-blocks Used Available Capacity Mounted"
echo "/dev/zram1 $(cat "$FAKE/df_size") 1000 1000 2% ${@: -1}"
EOF
chmod +x "$STUBS"/*

R="$W/sys"
setup() {
    rm -rf "$R" "$W/fake3"
    export FAKE="$W/fake3"; mkdir -p "$FAKE" "$R/var/log" "$R/run/systemd/journal"
    : > "$FAKE/calls"; : > "$FAKE/mountpoints"; echo 48128 > "$FAKE/df_size"
    echo "/ ext4 /dev/mmcblk0p1" > "$FAKE/mounts"
}
run_script() {
    POLARIS_VARLOG="$R/var/log" \
    POLARIS_CARD_LOG_DIRS="$R/var/log.hdd $R/var/hdd.log" \
    POLARIS_JOURNAL_CAP_DROPIN="$R/run/systemd/journald.conf.d/zz-polaris-ramlog-cap.conf" \
    POLARIS_JOURNAL_FLUSHED="$R/run/systemd/journal/flushed" \
    PATH="$STUBS:/usr/bin:/bin" bash "$SCRIPT" > "$W/out" 2>&1
}

# a. /var/log on the card: nothing to do.
setup
run_script; rc=$?
[ "$rc" = 0 ] && [ ! -s "$FAKE/calls" ] && [ ! -e "$R/run/systemd/journald.conf.d" ] \
    && ok "/var/log on disk: no mount, no cap" || bad "acted on a disk /var/log (rc=$rc): $(cat "$FAKE/calls" "$W/out")"

# b. orangepi-ramlog on a running system: journal in the zram, journald flushed.
setup
echo "$R/var/log ext4 /dev/zram1" >> "$FAKE/mounts"
echo "$R/var/log.hdd ext4 /dev/mmcblk0p1[/var/log]" >> "$FAKE/mounts"
mkdir -p "$R/var/log.hdd/journal/abc" "$R/var/log/journal/abc"
echo old > "$R/var/log.hdd/journal/abc/system.journal"
echo new > "$R/var/log/journal/abc/system.journal"
echo arc > "$R/var/log/journal/abc/system@1.journal"
touch "$R/run/systemd/journal/flushed"
run_script; rc=$?
[ "$rc" = 0 ] && ok "exit 0" || bad "rc=$rc: $(cat "$W/out")"
grep -qxF "mount --bind $R/var/log.hdd/journal $R/var/log/journal" "$FAKE/calls" \
    && ok "card journal bind-mounted over /var/log/journal" || bad "no bind: $(cat "$FAKE/calls")"
[ "$(cat "$R/var/log.hdd/journal/abc/system.journal")" = new ] && [ -f "$R/var/log.hdd/journal/abc/system@1.journal" ] \
    && ok "the newer RAM journal moved to the card" || bad "card journal: $(ls -R "$R/var/log.hdd/journal")"
[ -z "$(ls -A "$R/var/log/journal")" ] && ok "the RAM copy is freed" || bad "RAM still holds: $(ls -R "$R/var/log/journal")"
grep -q 'journalctl --relinquish-var' "$FAKE/calls" && grep -q 'journalctl --flush' "$FAKE/calls" \
    && [ "$(grep -n relinquish "$FAKE/calls" | cut -d: -f1)" -lt "$(grep -n 'mount --bind' "$FAKE/calls" | cut -d: -f1)" ] \
    && ok "journald sent to /run during the move and flushed back after" || bad "journald handling: $(cat "$FAKE/calls")"
[ ! -e "$R/run/systemd/journald.conf.d" ] && ok "no cap when the card copy exists" || bad "cap written alongside the bind"

# c. run again: already mounted.
: > "$FAKE/calls"
run_script
[ ! -s "$FAKE/calls" ] && ok "second run is a no-op" || bad "second run acted: $(cat "$FAKE/calls")"

# d. at boot, before the flush: empty RAM dir, journald not told anything.
setup
echo "$R/var/log ext4 /dev/zram1" >> "$FAKE/mounts"
echo "$R/var/log.hdd ext4 /dev/mmcblk0p1[/var/log]" >> "$FAKE/mounts"
mkdir -p "$R/var/log.hdd"
run_script
grep -qxF "mount --bind $R/var/log.hdd/journal $R/var/log/journal" "$FAKE/calls" && [ -d "$R/var/log.hdd/journal" ] \
    && ok "boot: card journal dir created and mounted" || bad "boot: $(cat "$FAKE/calls" "$W/out")"
grep -q journalctl "$FAKE/calls" && bad "boot: journald poked before the flush" || ok "boot: journald left to its flush"

# e. log2ram on tmpfs keeps the card copy at /var/hdd.log.
setup
echo "$R/var/log tmpfs log2ram" >> "$FAKE/mounts"
mkdir -p "$R/var/hdd.log"
run_script
grep -qxF "mount --bind $R/var/hdd.log/journal $R/var/log/journal" "$FAKE/calls" \
    && ok "log2ram: /var/hdd.log/journal used" || bad "log2ram: $(cat "$FAKE/calls" "$W/out")"

# f. current armbian-ramlog already links the journal to the card.
setup
echo "$R/var/log ext4 /dev/zram1" >> "$FAKE/mounts"
echo "$R/var/log.hdd ext4 /dev/mmcblk0p1[/var/log]" >> "$FAKE/mounts"
mkdir -p "$R/var/log.hdd/journal"
ln -s "$R/var/log.hdd/journal" "$R/var/log/journal"
run_script
[ ! -s "$FAKE/calls" ] && [ -L "$R/var/log/journal" ] && ok "armbian symlink to the card left alone" \
    || bad "armbian symlink: $(cat "$FAKE/calls" "$W/out")"

# g. RAM /var/log and no card copy anywhere: cap at a fifth.
setup
echo "$R/var/log ext4 /dev/zram1" >> "$FAKE/mounts"
run_script
C="$R/run/systemd/journald.conf.d/zz-polaris-ramlog-cap.conf"
grep -qx 'SystemMaxUse=9625K' "$C" 2>/dev/null && ok "no card copy: SystemMaxUse capped at a fifth of 47 MB" \
    || bad "cap: $(cat "$C" 2>&1)"
grep -q 'mount' "$FAKE/calls" && bad "bound something with no card copy" || ok "no card copy: nothing mounted"
grep -q 'try-restart systemd-journald' "$FAKE/calls" && ok "journald restarted to read the cap" || bad "journald not restarted"
# The cap must win over polaris-persistent.conf: journald applies drop-ins in
# filename order across /etc and /run, the last one wins.
[ "$(printf '%s\n' polaris-persistent.conf zz-polaris-ramlog-cap.conf | sort | tail -1)" = zz-polaris-ramlog-cap.conf ] \
    && ok "cap drop-in sorts after polaris-persistent.conf" || bad "cap drop-in would be overridden"

# h. a /var/log.hdd that is itself in RAM is not "the card".
setup
echo "$R/var/log ext4 /dev/zram1" >> "$FAKE/mounts"
echo "$R/var/log.hdd tmpfs tmpfs" >> "$FAKE/mounts"
mkdir -p "$R/var/log.hdd"
run_script
grep -q 'mount --bind' "$FAKE/calls" && bad "bound a RAM directory: $(cat "$FAKE/calls")" || ok "a RAM /var/log.hdd is not used"

echo
[ "$fails" -eq 0 ] && { echo "all passed"; exit 0; } || { echo "$fails failed"; exit 1; }
