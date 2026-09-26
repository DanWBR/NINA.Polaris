#!/bin/bash
# Install ONE INDI driver package, as root, for the Polaris UI.
#
# Started as polaris-indi-install@<package>.service. The package name arrives
# as the systemd instance argument, so it is validated here too: Polaris
# already checked it, and a root process does not take someone else's word.
#
# The simulation is re-run here and the install is REFUSED if apt would remove
# anything. That guard is the reason this script exists rather than a plain
# `apt install` behind a button: INDI ships either from the mutlaqja PPA
# (libindi1) or from the distribution archive (libindidriver1), and installing
# a driver from the wrong one makes apt tear out the stack in use, PHD2
# included. That happened on a working rig, at night, from one command.
set -u

PKG="${1:-}"
OUT=/run/polaris/indi-install.json
mkdir -p /run/polaris

fail() {
    printf '{"ok":false,"package":"%s","error":"%s"}' "$PKG" "$1" > "$OUT"
    chmod 0644 "$OUT"
    echo "$1" >&2
    exit 1
}

case "$PKG" in
    indi-*|libindi*) ;;
    *) fail "not an INDI driver package name" ;;
esac
case "$PKG" in
    *[!a-z0-9.+-]*) fail "illegal character in the package name" ;;
esac

export DEBIAN_FRONTEND=noninteractive
export LC_ALL=C

PLAN=$(apt-get install -s "$PKG" 2>&1) || fail "apt could not plan the install"

if echo "$PLAN" | grep -q '^Remv '; then
    REMOVED=$(echo "$PLAN" | awk '/^Remv /{printf "%s ", $2}')
    fail "refused: this would remove${REMOVED:+ $REMOVED}"
fi

if ! echo "$PLAN" | grep -q '^Inst '; then
    fail "nothing to install"
fi

apt-get install -y --no-install-recommends "$PKG" >/tmp/polaris-indi-install.log 2>&1 \
    || fail "apt failed, see /tmp/polaris-indi-install.log"

INSTALLED=$(dpkg-query -W -f='${Version}' "$PKG" 2>/dev/null || echo "")
printf '{"ok":true,"package":"%s","version":"%s"}' "$PKG" "$INSTALLED" > "$OUT"
chmod 0644 "$OUT"
cat "$OUT"
