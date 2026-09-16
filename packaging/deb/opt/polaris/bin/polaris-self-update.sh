#!/bin/bash
# Installs the .deb that NINA.Polaris downloaded into the polaris cache.
#
# Started on demand as root via `systemctl start polaris-self-update.service`,
# which the unprivileged polaris service user is allowed to trigger by the
# 50-polaris-update.rules PolicyKit rule (no password — same passwordless
# pattern as the power / clock / NetworkManager actions).
#
# Why a dedicated unit instead of running apt as a child of the app: the new
# package's postinst restarts polaris.service, which kills that unit's whole
# cgroup. apt running here lives in polaris-self-update.service's own cgroup,
# so it survives the restart and finishes the install.
set -o pipefail

DEB=/home/polaris/.cache/polaris-update.deb
LOG=/tmp/polaris-update.log

echo "polaris self-update starting $(date -u)" > "$LOG"
if [ ! -f "$DEB" ]; then
    echo "no update package found at $DEB" >> "$LOG"
    exit 1
fi

export DEBIAN_FRONTEND=noninteractive

# --allow-downgrades so a user can also pin to an older release if needed.
# --fix-missing: the SBC is often offline while updating (the .deb arrives
# through the browser relay), so a dependency that cannot be fetched must not
# abort the Polaris install itself.
# --no-install-recommends: the Recommends list is long (siril, phd2, xpra, an
# X server, the python stack) and the published images were installed with
# dpkg, which never pulled it. Letting apt fetch all of it here put hundreds
# of MB of downloads in front of the Polaris upgrade, and the browser gave up
# waiting long before dpkg ran. The upgrade itself goes first; the extras are
# picked up afterwards, best effort, once the new version is already up.
install_staged() {
    apt-get install -y --allow-downgrades --fix-missing --no-install-recommends "$DEB" >> "$LOG" 2>&1
    RC=$?
    echo "apt exit=$RC" >> "$LOG"
    rm -f "$DEB"
}

installed() { dpkg-query -W -f='${Status}' "$1" 2>/dev/null | grep -q 'install ok installed'; }

# Missing Recommends, one apt run each (bounded to 5 minutes) so a package
# this distro does not carry, or a mirror that is down, only skips that one.
# An entry with alternatives ("libraw23 | libraw20") is satisfied by any of
# them installed, and tried in order otherwise. ffmpeg is the one that
# matters most: MP4 output (time-lapse, SER to MP4) needs it.
install_recommends() {
    dpkg-query -W -f='${Recommends}
' polaris 2>/dev/null | tr ',' '
' | sed 's/([^)]*)//g'     | while read -r entry; do
        alts=$(echo "$entry" | tr '|' ' ')
        [ -z "$alts" ] && continue
        have=0
        for p in $alts; do installed "$p" && have=1 && break; done
        [ "$have" -eq 1 ] && continue
        done_one=0
        for p in $alts; do
            if timeout 300 apt-get install -y --no-install-recommends "$p" >> "$LOG" 2>&1; then
                echo "recommends: $p installed" >> "$LOG"; done_one=1; break
            fi
        done
        [ "$done_one" -eq 0 ] && echo "recommends: $entry not installed (not available or no network)" >> "$LOG"
    done
}

install_staged
if [ "$RC" -eq 0 ]; then
    install_recommends
    # The unit stays active while the extras install. A `systemctl start`
    # issued meanwhile (a second update, or a rollback right after) joins
    # this run instead of starting a new one, so pick up what it staged.
    if [ -f "$DEB" ]; then
        echo "another package was staged while the extras installed; installing it" >> "$LOG"
        install_staged
    fi
fi

exit $RC
