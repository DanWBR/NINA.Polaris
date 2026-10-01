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

# A vendor camera SDK that is already on the system, whether or not dpkg
# knows about it.
#
# The published Pi images carry the whole INDI stack built from source, so
# indiserver, every indi_* driver AND the vendor SDKs are orphan files: real,
# working, and invisible to dpkg. Asking dpkg whether libasi is installed
# therefore answers "no" on a machine that has a perfectly good ZWO SDK, and
# apt then installs the distribution's copy over it.
#
# On an old distribution that copy is older than the drivers. Debian
# bookworm's libasi 1.27 ships libEAFFocuser.so.1.4, which exports 23 EAF
# symbols and not EAFStepRange; installing it repoints libEAFFocuser.so.1
# away from the 1.7.7 the drivers were built against, and indi_asi_focuser
# then dies at load with "undefined symbol: EAFStepRange". The newer 1.7.7
# file is still sitting there unused, which makes the failure read like
# anything but a downgrade.
#
# So: if the library is already loadable, leave it alone. A missing SDK is
# one camera brand unavailable; a downgraded one is a working focuser that
# stops at the first move.
sdk_present() {
    case "$1" in
        libasi)       lib=libASICamera2 ;;
        libsvbony)    lib=libSVBCameraSDK ;;
        libplayerone) lib=libPlayerOneCamera ;;
        libtoupcam)   lib=libtoupcam ;;
        libaltaircam) lib=libaltaircam ;;
        # The INDI stack itself is source-built on these images, and the
        # archive's indi-bin lands indiserver and ~280 drivers over it. A
        # field report had both: indi-bin and libasi installed by this loop
        # on one card, and the archive's libasi then repointed all four ZWO
        # sonames at older builds. The focuser failed loudly on a missing
        # symbol; the camera just quietly dropped from SDK 1.41 to 1.27.
        indi-bin|indi-full)
            [ -e /usr/bin/indiserver ] && ! dpkg -S /usr/bin/indiserver >/dev/null 2>&1 && return 0
            return 1 ;;
        *) return 1 ;;
    esac
    ldconfig -p 2>/dev/null | grep -q "$lib" && return 0
    # ldconfig misses a library installed outside the cache paths.
    # POLARIS_SDK_LIBDIRS exists so the test can point the scan at an empty
    # directory: without it the result depends on whether the machine running
    # the test happens to have a vendor SDK, which is exactly how this check
    # first went wrong.
    for d in ${POLARIS_SDK_LIBDIRS:-/usr/lib /usr/lib/*-linux-gnu /usr/local/lib /lib/*-linux-gnu}; do
        [ -e "$d/$lib.so" ] && return 0
    done
    return 1
}

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
        # Already there as an untracked build: installing the archive's copy
        # over it is a downgrade, not an improvement.
        skip=0
        for p in $alts; do
            if sdk_present "$p"; then
                echo "recommends: $p already present outside dpkg, left alone" >> "$LOG"
                skip=1; break
            fi
        done
        [ "$skip" -eq 1 ] && continue
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
