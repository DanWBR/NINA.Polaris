#!/bin/bash
# Write the diagnostics report where it can be read WITHOUT the UI and without
# SSH: the boot partition, which is FAT32 on every board that has one, so the
# operator can pull the card, put it in any laptop and open a text file.
#
# This is the case that left us blind in the field: a board that booted, had no
# grow-root, no SSH host keys (port 22 refused) and no way in. Everything we
# needed to know was on that filesystem and unreachable.
#
# Runs as root from polaris-diagnostics.service, so it can write to /boot.
# Talks to the LOOPBACK HTTP port, which AuthMiddleware exempts (127.0.0.1 is
# already inside the box), so no token handling here.
set -u

PORT="${POLARIS_HTTP_PORT:-5080}"
URL="http://127.0.0.1:${PORT}/api/system/diagnostics?format=text"
NAME=polaris-diagnostics.txt

log() { echo "polaris-diagnostics: $*"; }

# Wait for the service to answer: this unit starts right after polaris.service,
# which needs a few seconds to bind. 60s total, then give up quietly.
for i in $(seq 1 30); do
    body=$(curl -fsS --max-time 5 "$URL" 2>/dev/null) && break
    sleep 2
done
if [ -z "${body:-}" ]; then
    log "no answer from $URL after 60s, nothing written"
    exit 0
fi

# First writable boot partition wins; fall back to /var/log so the report
# always lands somewhere.
target=""
for d in /boot/firmware /boot/efi /boot; do
    # Must be a real mount point, otherwise /boot is just a directory on the
    # root filesystem and offers no advantage over /var/log.
    if mountpoint -q "$d" 2>/dev/null && [ -w "$d" ]; then target="$d"; break; fi
done
[ -z "$target" ] && [ -w /boot ] && target=/boot
[ -z "$target" ] && target=/var/log

printf '%s\n' "$body" > "$target/$NAME.tmp" && mv "$target/$NAME.tmp" "$target/$NAME"
sync
log "wrote $target/$NAME ($(wc -l < "$target/$NAME") lines)"

# A copy under /var/log too, so `journalctl`-minded people and the log panel
# have it without mounting anything.
[ "$target" != /var/log ] && cp -f "$target/$NAME" "/var/log/$NAME" 2>/dev/null || true
exit 0
