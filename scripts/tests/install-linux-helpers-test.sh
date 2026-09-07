#!/usr/bin/env bash
# Regression test for install-polaris-linux.sh, against the failure reported on
# Kubuntu 24.04 (Discord, 2026-09-06):
#
#   E: Package 'indi-full' has no installation candidate
#   [FAIL] apt install indi-full phd2 openssh-server astrometry.net astrometry-data-tycho2
#   Failed to enable unit: Unit file ssh.service does not exist.
#
# One name with no candidate aborts the whole apt command, so a package that is
# simply not in this distribution took SSH down with it. The helpers are
# exercised here against stub apt tooling; no packages are installed and
# nothing outside a temp directory is touched.
#
#   bash scripts/tests/install-linux-helpers-test.sh
set -u

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="$HERE/../install-polaris-linux.sh"
[ -r "$SRC" ] || { echo "cannot read $SRC"; exit 2; }

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
BIN="$WORK/bin"; mkdir -p "$BIN"
PATH="$BIN:$PATH"

fails=0
ok()  { echo "  ok   $1"; }
bad() { echo "  FAIL $1"; fails=$((fails + 1)); }

# ---- the helpers, lifted out of the real script ----------------------------
for fn in apt_recover apt_try_each has_candidate d80_installed d80_on_disk; do
    sed -n "/^${fn}()[ {]/,/^}/p" "$SRC" >> "$WORK/helpers.sh"
done
sed -n '/^apt_recover(){/p' "$SRC" >> "$WORK/helpers.sh"
note_fail() { NOTED+=("$*"); }
# shellcheck disable=SC1090,SC1091
. "$WORK/helpers.sh"

# ---- stubs: every package installs except the ones named in MISSING --------
cat > "$BIN/apt-get" <<'STUB'
#!/usr/bin/env bash
[ "$1" = install ] || exit 0
shift
for a in "$@"; do
    case "$a" in -*) continue;; esac
    for m in $MISSING; do
        if [ "$a" = "$m" ]; then
            echo "E: Package '$a' has no installation candidate" >&2
            exit 100
        fi
    done
    echo "$a" >> "$INSTALLED_LOG"
done
exit 0
STUB
cat > "$BIN/apt-cache" <<'STUB'
#!/usr/bin/env bash
pkg="$2"
for m in $MISSING; do
    if [ "$pkg" = "$m" ]; then
        printf '%s:\n  Installed: (none)\n  Candidate: (none)\n' "$pkg"; exit 0
    fi
done
printf '%s:\n  Installed: (none)\n  Candidate: 2.1.4\n' "$pkg"
STUB
printf '#!/usr/bin/env bash\nexit 1\n' > "$BIN/dpkg-query"
chmod +x "$BIN"/*
export MISSING INSTALLED_LOG

echo "== has_candidate =="
MISSING="indi-full"
has_candidate indi-bin  && ok "indi-bin has a candidate"      || bad "indi-bin has a candidate"
has_candidate indi-full && bad "indi-full must have none"     || ok  "indi-full has no candidate"

echo "== apt_try_each: the reported line, with indi-full missing =="
INSTALLED_LOG="$WORK/installed.txt"; : > "$INSTALLED_LOG"
NOTED=()
apt_try_each indi-full phd2 openssh-server astrometry.net astrometry-data-tycho2
for p in phd2 openssh-server astrometry.net astrometry-data-tycho2; do
    grep -qx "$p" "$INSTALLED_LOG" && ok  "$p survived a missing indi-full" \
                                   || bad "$p was taken down with indi-full"
done
grep -qx indi-full "$INSTALLED_LOG" && bad "indi-full should not install" \
                                    || ok  "indi-full correctly skipped"
[ "${#NOTED[@]}" = 1 ] && [ "${NOTED[0]}" = "apt install indi-full" ] \
    && ok  "the summary names the one package that failed" \
    || bad "summary should name only indi-full, got: ${NOTED[*]-}"

echo "== apt_try_each: nothing missing =="
INSTALLED_LOG="$WORK/installed2.txt"; : > "$INSTALLED_LOG"
NOTED=(); MISSING=""
apt_try_each indi-full phd2 openssh-server && ok "clean run returns 0" || bad "clean run returns 0"
[ "${#NOTED[@]}" = 0 ] && ok "no failures noted" || bad "noted ${NOTED[*]-}"

echo "== d80_installed is not fooled by an unmatched glob =="
# The trap that once failed every SD image: with nullglob set, `ls <glob>`
# with nothing matching becomes a bare `ls`, which succeeds.
cd "$WORK" || exit 2
shopt -s nullglob
d80_installed && bad "nullglob on: reported present with nothing there" \
              || ok  "nullglob on: correctly absent"
shopt -u nullglob
d80_installed && bad "nullglob off: reported present with nothing there" \
              || ok  "nullglob off: correctly absent"

echo "== d80_on_disk =="
PAYLOAD="$WORK/payload"; mkdir -p "$PAYLOAD"
d80_on_disk >/dev/null && bad "found a copy that does not exist" || ok "absent when absent"
echo x > "$PAYLOAD/d80_star_database.deb"
found=$(d80_on_disk) && [ "$found" = "$PAYLOAD/d80_star_database.deb" ] \
    && ok  "finds the copy already on disk" \
    || bad "did not find the payload copy (got '${found:-}')"

echo
[ "$fails" = 0 ] && echo "all checks passed" || echo "$fails check(s) failed"
exit "$fails"
