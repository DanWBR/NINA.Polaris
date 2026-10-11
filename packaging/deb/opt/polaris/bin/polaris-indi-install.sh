#!/bin/bash
# Install ONE INDI driver package, as root, for the Polaris UI.
#
# Started as polaris-indi-install@<package>.service. The package name arrives
# as the systemd instance argument, so it is validated here too: Polaris
# already checked it, and a root process does not take someone else's word.
#
# Two guards, and they are the reason this script exists rather than a plain
# `apt install` behind a button.
#
# 1. REFUSE IF APT WOULD REMOVE ANYTHING. INDI ships either from the mutlaqja
#    PPA (libindi1) or from the distribution archive (libindidriver1), and
#    installing a driver from the wrong one makes apt tear out the stack in
#    use, PHD2 included. That happened on a working rig, at night, from one
#    command.
#
# 2. REFUSE IF IT WOULD OVERWRITE FILES NOBODY OWNS. The published SBC images
#    build the whole INDI stack from source, so indiserver, every indi_*
#    driver and the vendor SDKs are real working files that dpkg has never
#    heard of. apt therefore plans no removal at all, guard 1 is satisfied,
#    and dpkg silently writes over them, because it only refuses when another
#    *package* owns the file. If the archive version is older than the source
#    build, the result is a driver that loads and then dies on an undefined
#    symbol, with the newer file still sitting on disk unused. Nothing in that
#    failure points back at the install that caused it.
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

# --- guard 2: would this write over something built from source? ---------
# Ask the actual packages, not a heuristic: download what apt intends to
# install, list its contents, and look for files that are already on this
# system and owned by nobody.
INCOMING=$(echo "$PLAN" | awk '/^Inst /{print $2}')
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
CLASH=""
CLASH_N=0

if ( cd "$STAGE" && apt-get download $INCOMING >/dev/null 2>&1 ); then
    for deb in "$STAGE"/*.deb; do
        [ -e "$deb" ] || continue
        # dpkg-deb -c prints a tar listing; take the regular files only, and
        # turn "./usr/bin/x" into "/usr/bin/x".
        while read -r path; do
            [ -e "$path" ] || continue
            [ -d "$path" ] && continue
            if ! dpkg -S "$path" >/dev/null 2>&1; then
                CLASH_N=$((CLASH_N + 1))
                [ "$CLASH_N" -le 6 ] && CLASH="$CLASH $path"
            fi
        done <<EOF
$(dpkg-deb -c "$deb" 2>/dev/null | awk '$1 ~ /^-/ {print substr($NF, 2)}')
EOF
    done
else
    # Could not check. Say so rather than installing on the assumption that
    # it is fine: this guard exists because the failure it prevents is
    # invisible afterwards.
    fail "could not download the package to check what it would replace; nothing was installed"
fi

if [ "$CLASH_N" -gt 0 ]; then
    MORE=""
    [ "$CLASH_N" -gt 6 ] && MORE=" and $((CLASH_N - 6)) more"
    fail "refused: this would overwrite $CLASH_N file(s) built from source on this image, including$CLASH$MORE. Installing the archive version over them can replace a newer driver or SDK with an older one. If you want it anyway, run it by hand: sudo apt-get install $PKG"
fi

apt-get install -y --no-install-recommends "$PKG" >/tmp/polaris-indi-install.log 2>&1 \
    || fail "apt failed, see /tmp/polaris-indi-install.log"

INSTALLED=$(dpkg-query -W -f='${Version}' "$PKG" 2>/dev/null || echo "")
printf '{"ok":true,"package":"%s","version":"%s"}' "$PKG" "$INSTALLED" > "$OUT"
chmod 0644 "$OUT"
cat "$OUT"
