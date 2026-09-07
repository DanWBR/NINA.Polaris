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
for fn in apt_recover apt_try_each has_candidate d80_installed d80_on_disk \n          system_codename ubuntu_codename ppa_retarget phd2_unsatisfiable; do
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

# ---------------------------------------------------------------------------
# Linux Mint (Discord, 2026-09-07):
#   "phd2 has unmet dependencies ... depends on libindi1 but that is not
#    installable. E: Unable to correct problems, you have held broken packages."
#
# Two distinct causes, both checked here:
#   * Mint reports its own codename, so a PPA added under it points at a suite
#     Launchpad has never published, and everything in that PPA vanishes.
#   * phd2 is in no Ubuntu release. It exists only in ppa:pch/phd2 and links
#     against libindi1, which exists only in the INDI PPA; Ubuntu's own INDI
#     ships libindidriver1 instead. Without that PPA phd2 is unsatisfiable by
#     construction, and apt's answer reads like a broken system.
# ---------------------------------------------------------------------------
echo "== ubuntu_codename: the base series wins over the derivative's own =="
OS_RELEASE="$WORK/os-release"
UPSTREAM_RELEASE="$WORK/upstream-lsb"
cat > "$BIN/lsb_release" <<'STUB'
#!/usr/bin/env bash
[ "${1:-}" = -cs ] && echo "${SYS_CODENAME:-noble}"
exit 0
STUB
chmod +x "$BIN/lsb_release"

export SYS_CODENAME=xia
printf 'ID=linuxmint\nUBUNTU_CODENAME=noble\n' > "$OS_RELEASE"
printf 'DISTRIB_CODENAME=noble\n' > "$UPSTREAM_RELEASE"
[ "$(ubuntu_codename)" = noble ] && ok "Mint resolves to the Ubuntu series" \
                                 || bad "Mint resolved to '$(ubuntu_codename)'"
[ "$(system_codename)" = xia ] && ok "and still reports its own name" \
                               || bad "system_codename was '$(system_codename)'"

rm -f "$UPSTREAM_RELEASE"
[ "$(ubuntu_codename)" = noble ] && ok "os-release alone is enough" \
                                 || bad "without upstream-release: '$(ubuntu_codename)'"

export SYS_CODENAME=noble
printf 'ID=ubuntu\nUBUNTU_CODENAME=noble\n' > "$OS_RELEASE"
[ "$(ubuntu_codename)" = "$(system_codename)" ] \
    && ok "plain Ubuntu: the two agree" || bad "plain Ubuntu disagreed"

echo "== ppa_retarget: rewrites the suite, and only for the named PPA =="
APT_SOURCES_DIR="$WORK/sources.list.d"; mkdir -p "$APT_SOURCES_DIR"
printf 'deb https://ppa.launchpadcontent.net/mutlaqja/ppa/ubuntu xia main\n' \
    > "$APT_SOURCES_DIR/indi.list"
printf 'Types: deb\nURIs: https://ppa.launchpadcontent.net/pch/phd2/ubuntu\nSuites: xia\nComponents: main\n' \
    > "$APT_SOURCES_DIR/phd2.sources"
printf 'deb https://example.org/other/ubuntu xia main\n' \
    > "$APT_SOURCES_DIR/unrelated.list"

ppa_retarget mutlaqja/ppa noble xia >/dev/null
ppa_retarget pch/phd2     noble xia >/dev/null
grep -q 'mutlaqja/ppa/ubuntu noble main' "$APT_SOURCES_DIR/indi.list" \
    && ok "one-line .list retargeted" || bad "list: $(cat "$APT_SOURCES_DIR/indi.list")"
grep -q '^Suites: noble$' "$APT_SOURCES_DIR/phd2.sources" \
    && ok "deb822 .sources retargeted" || bad "sources: $(cat "$APT_SOURCES_DIR/phd2.sources")"
grep -q 'example.org/other/ubuntu xia main' "$APT_SOURCES_DIR/unrelated.list" \
    && ok "an unrelated repository is untouched" || bad "unrelated file was rewritten"

echo "== ppa_retarget: a no-op on plain Ubuntu =="
before=$(cat "$APT_SOURCES_DIR/indi.list")
ppa_retarget mutlaqja/ppa noble noble >/dev/null
[ "$(cat "$APT_SOURCES_DIR/indi.list")" = "$before" ] \
    && ok "same codename changes nothing" || bad "rewrote a file it should not have"

echo "== phd2_unsatisfiable =="
cat > "$BIN/apt-cache" <<'STUB'
#!/usr/bin/env bash
case "$1" in
  policy)
    for m in $MISSING; do
        [ "$2" = "$m" ] && { echo "  Candidate: (none)"; exit 0; }
    done
    echo "  Candidate: 1.0"
    ;;
  depends)
    [ "$2" = phd2 ] && echo "  Depends: libindi1"
    ;;
esac
exit 0
STUB
chmod +x "$BIN/apt-cache"

MISSING="libindi1"
phd2_unsatisfiable && ok  "phd2 skipped when libindi1 is unavailable" \
                   || bad "phd2 would still be attempted without libindi1"
MISSING=""
phd2_unsatisfiable && bad "phd2 skipped even though libindi1 is there" \
                   || ok  "phd2 attempted when the INDI PPA is present"
MISSING="phd2 libindi1"
phd2_unsatisfiable && bad "reported unsatisfiable for an absent phd2" \
                   || ok  "an absent phd2 is left to the normal apt path"


echo
[ "$fails" = 0 ] && echo "all checks passed" || echo "$fails check(s) failed"
exit "$fails"
