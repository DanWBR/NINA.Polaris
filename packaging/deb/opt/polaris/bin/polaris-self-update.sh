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
# through the browser relay), so a Recommends that cannot be fetched, such as
# ffmpeg, must not abort the Polaris install itself.
apt-get install -y --allow-downgrades --fix-missing "$DEB" >> "$LOG" 2>&1
RC=$?
echo "apt exit=$RC" >> "$LOG"

# ffmpeg is what MP4 output (time-lapse, SER to MP4) needs. Best effort: the
# published images were installed with dpkg, which never pulls Recommends, so
# pick it up here whenever the SBC happens to be online.
if [ "$RC" -eq 0 ] && ! command -v ffmpeg >/dev/null 2>&1; then
    if apt-get install -y ffmpeg >> "$LOG" 2>&1; then
        echo "ffmpeg installed" >> "$LOG"
    else
        echo "ffmpeg not installed (no network?), MP4 output stays unavailable until it is" >> "$LOG"
    fi
fi

rm -f "$DEB"
exit $RC
