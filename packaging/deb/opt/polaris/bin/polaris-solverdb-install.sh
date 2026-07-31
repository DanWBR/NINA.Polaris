#!/bin/bash
# Installs the plate-solver star database / index files that NINA.Polaris
# downloaded into the polaris cache.
#
# Started on demand as root via `systemctl start polaris-solverdb.service`,
# which the unprivileged polaris service user may trigger through the SAME
# manage-units PolicyKit rule the self-update already relies on
# (50-polaris-update.rules / .pkla). No new policy is needed, and no password.
#
# Why root at all: /opt/astap and the astrometry.net index directory are
# root-owned system paths. Downloading is unprivileged; only the move into
# place is not.
#
# The app stages:
#   /home/polaris/.cache/polaris-solverdb/payload      one archive or one or more .fits
#   /home/polaris/.cache/polaris-solverdb/target       "astap" | "astrometry"
# and reads back /tmp/polaris-solverdb.log.
set -o pipefail

STAGE=/home/polaris/.cache/polaris-solverdb
LOG=/tmp/polaris-solverdb.log
ASTAP_DIR=/opt/astap

echo "polaris solver-database install starting $(date -u)" > "$LOG"

if [ ! -d "$STAGE" ]; then
    echo "nothing staged at $STAGE" >> "$LOG"
    exit 1
fi

TARGET=$(cat "$STAGE/target" 2>/dev/null)
case "$TARGET" in
    astap|astrometry) ;;
    *) echo "unknown or missing target: '$TARGET'" >> "$LOG"; exit 1 ;;
esac

install_astap() {
    local archive
    archive=$(find "$STAGE" -maxdepth 1 -type f \( -name '*.zip' -o -name '*.7z' \) | head -1)
    if [ -z "$archive" ]; then
        echo "no .zip/.7z payload in $STAGE" >> "$LOG"
        return 1
    fi
    mkdir -p "$ASTAP_DIR" || return 1
    echo "unpacking $(basename "$archive") into $ASTAP_DIR" >> "$LOG"
    case "$archive" in
        *.zip)
            command -v unzip >/dev/null || { echo "unzip not installed" >> "$LOG"; return 1; }
            # -o: a re-download of the same database replaces it rather than
            # prompting on a pipe with no tty, which would hang the unit.
            unzip -o "$archive" -d "$ASTAP_DIR" >> "$LOG" 2>&1 || return 1
            ;;
        *.7z)
            command -v 7zr >/dev/null && SEVEN=7zr || SEVEN=7z
            command -v "$SEVEN" >/dev/null || { echo "7z not installed" >> "$LOG"; return 1; }
            "$SEVEN" x -y -o"$ASTAP_DIR" "$archive" >> "$LOG" 2>&1 || return 1
            ;;
    esac
    # ASTAP runs as the polaris service user and only ever READS these.
    chmod -R a+rX "$ASTAP_DIR" >> "$LOG" 2>&1
    echo "astap database files now: $(ls -1 "$ASTAP_DIR" | wc -l)" >> "$LOG"
}

install_astrometry() {
    # Where astrometry.net looks. The Debian package uses /usr/share/astrometry;
    # a source build usually uses /usr/local/astrometry/data. Install into the
    # one that already exists, preferring a directory that already holds
    # indexes, so the solver's configured path does not have to change.
    local dir=""
    for cand in /usr/share/astrometry /usr/local/astrometry/data /usr/share/astrometry/data; do
        if [ -d "$cand" ]; then dir="$cand"; break; fi
    done
    if [ -z "$dir" ]; then
        dir=/usr/share/astrometry
        mkdir -p "$dir" || return 1
    fi
    local n=0
    shopt -s nullglob
    for f in "$STAGE"/*.fits; do
        install -m 0644 "$f" "$dir/" >> "$LOG" 2>&1 || return 1
        n=$((n+1))
    done
    shopt -u nullglob
    if [ "$n" -eq 0 ]; then
        echo "no .fits payload in $STAGE" >> "$LOG"
        return 1
    fi
    echo "installed $n index file(s) into $dir" >> "$LOG"
    # astrometry.net reads its index list from a cfg; make sure the directory
    # we just filled is actually listed, otherwise the files sit there unused.
    for cfg in /etc/astrometry.cfg /usr/local/astrometry/etc/astrometry.cfg; do
        [ -f "$cfg" ] || continue
        if ! grep -qE "^\s*add_path\s+$dir\s*$" "$cfg"; then
            echo "add_path $dir" >> "$cfg"
            echo "added '$dir' to $cfg" >> "$LOG"
        fi
    done
}

RC=0
if [ "$TARGET" = "astap" ]; then
    install_astap || RC=$?
else
    install_astrometry || RC=$?
fi

echo "install exit=$RC" >> "$LOG"
# Free the card either way: a failed payload is re-downloaded, never resumed.
rm -rf "$STAGE"
exit $RC
