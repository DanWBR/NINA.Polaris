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
for fn in apt_recover apt_install apt_try_each has_candidate d80_installed d80_on_disk \
          system_codename ubuntu_codename ppa_retarget phd2_unsatisfiable \
          indi_ppa_covers indi_archive_drivers indi_fallback_hint \
          country_mirror_failed use_main_archive mirror_restore \
          apt_ubuntu_series check_archive_series; do
    sed -n "/^${fn}()[ {]/,/^}/p" "$SRC" >> "$WORK/helpers.sh"
done
sed -n '/^apt_recover(){/p' "$SRC" >> "$WORK/helpers.sh"
sed -n '/^INDI_PPA_SERIES=/p' "$SRC" >> "$WORK/helpers.sh"
sed -n '/^COUNTRY_MIRROR_RE=/p' "$SRC" >> "$WORK/helpers.sh"
MIRROR_BACKUP=""
sed -n '/^INDI_ARCHIVE_DRIVERS=/,/"$/p' "$SRC" >> "$WORK/helpers.sh"
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

echo "== indi_fallback_hint: names the release, what the archive gives instead, and the way out =="
# The INDI PPA dropped 22.04 and 24.04 on 2026-08-25 (Launchpad); only 26.04 is served.
indi_ppa_covers resolute && ok "resolute is covered"  || bad "resolute not covered"
indi_ppa_covers noble    && bad "noble must not be covered (PPA dropped it)" || ok "noble is not covered"
indi_ppa_covers jammy    && bad "jammy must not be covered (PPA dropped it)" || ok "jammy is not covered"
indi_ppa_covers ""       && bad "empty must not be covered"  || ok "empty is not covered"

# Ubuntu's own archive on 24.04: these third-party drivers exist, the rest do not.
MISSING="indi-full indi-3rdparty-drivers libindi1 indi-svbony indi-toupbase indi-qhy indi-atik indi-mi indi-qsi indi-webcam indi-celestronaux indi-avalon indi-gpsnmea"
drivers="$(indi_archive_drivers)"
[ "$drivers" = "indi-asi indi-eqmod indi-gphoto indi-playerone indi-sx indi-gpsd indi-aagcloudwatcher-ng indi-apogee indi-fli indi-sbig indi-dsi indi-duino" ]     && ok "archive drivers: only the ones with a candidate, in list order" || bad "archive drivers: $drivers"

export SYS_CODENAME=noble
printf 'ID=ubuntu
UBUNTU_CODENAME=noble
' > "$OS_RELEASE"
hint="$(indi_fallback_hint)"
grep -q "based on 'noble', which the PPA no longer serves" <<<"$hint" && ok "the machine's own series is named" || bad "hint: $hint"
grep -q "only" <<<"$hint" && grep -q "resolute (26.04)" <<<"$hint" && ok "the served series is listed" || bad "hint lacks the series"
grep -q "from the PPA on 2026-08-25" <<<"$hint" && ok "says why 24.04 gets nothing" || bad "hint lacks the removal note"
grep -q "indi-asi indi-eqmod indi-gphoto" <<<"$hint" && ok "lists the archive drivers it installs instead" || bad "hint lacks the driver list"
grep -q "native drivers" <<<"$hint" && ok "mentions the native camera drivers" || bad "hint lacks the native-driver note"
grep -q "Ubuntu 26.04 LTS" <<<"$hint" && ok "points at 26.04" || bad "hint lacks the 26.04 suggestion"

export SYS_CODENAME=resolute
printf 'ID=ubuntu
UBUNTU_CODENAME=resolute
' > "$OS_RELEASE"
hint="$(indi_fallback_hint)"
grep -q "was the PPA added above" <<<"$hint"     && ok "on a served series it blames the PPA step instead" || bad "hint: $hint"
MISSING=""

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
    # apt-cache prints an unresolvable dependency in angle brackets.
    if [ "$2" = phd2 ]; then
        for m in $MISSING; do [ "$m" = libindi1 ] && { echo "  Depends: <libindi1>"; exit 0; }; done
        echo "  Depends: libindi1"
    fi
    ;;
esac
exit 0
STUB
chmod +x "$BIN/apt-cache"

MISSING="libindi1"
phd2_unsatisfiable && ok  "phd2 skipped when libindi1 is unavailable (<libindi1> form)" \
                   || bad "phd2 would still be attempted without libindi1"
MISSING=""
phd2_unsatisfiable && bad "phd2 skipped even though libindi1 is there" \
                   || ok  "phd2 attempted when the INDI PPA is present"
MISSING="phd2 libindi1"
phd2_unsatisfiable && bad "reported unsatisfiable for an absent phd2" \
                   || ok  "an absent phd2 is left to the normal apt path"

# ---------------------------------------------------------------------------
# Ubuntu 26.04 server in Indonesia (Discord, 2026-10-08):
#   E: Failed to fetch http://id.archive.ubuntu.com/ubuntu/pool/main/g/gpgmepp/libgpgmepp7_2.0.0-2_amd64.deb  403  Forbidden
#   [FAIL] apt install phd2
#   [FAIL] install polaris.deb
# The country mirror refused one file that both depend on. The install must
# retry against the main archive, and leave the user's sources as they were.
# ---------------------------------------------------------------------------
echo "== a country mirror refusing a file falls back to the main archive =="
APT_SOURCES_DIR="$WORK/mirror/sources.list.d"; mkdir -p "$APT_SOURCES_DIR"
APT_SOURCES_LIST="$WORK/mirror/sources.list"
cat > "$APT_SOURCES_DIR/ubuntu.sources" <<'EOF'
Types: deb
URIs: http://id.archive.ubuntu.com/ubuntu/
Suites: resolute resolute-updates
Components: main universe

Types: deb
URIs: http://security.ubuntu.com/ubuntu/
Suites: resolute-security
Components: main universe
EOF
printf 'deb http://de.ports.ubuntu.com/ubuntu-ports resolute main\n' > "$APT_SOURCES_LIST"
printf 'deb https://ppa.launchpadcontent.net/pch/phd2/ubuntu resolute main\n' > "$APT_SOURCES_DIR/phd2.list"
orig_sources=$(cat "$APT_SOURCES_DIR/ubuntu.sources")
orig_list=$(cat "$APT_SOURCES_LIST")
cat > "$BIN/apt-get" <<'STUB'
#!/usr/bin/env bash
[ "$1" = install ] || exit 0
shift
if grep -q 'id\.archive\.ubuntu\.com' "$APT_SOURCES_DIR/ubuntu.sources"; then
    echo "E: Failed to fetch http://id.archive.ubuntu.com/ubuntu/pool/main/g/gpgmepp/libgpgmepp7_2.0.0-2_amd64.deb  403  Forbidden [IP: 202.79.180.254 80]" >&2
    echo "E: Unable to fetch some archives, maybe run apt update or try with --fix-missing?" >&2
    exit 100
fi
for a in "$@"; do case "$a" in -*) ;; *) echo "$a" >> "$INSTALLED_LOG";; esac; done
exit 0
STUB
chmod +x "$BIN/apt-get"
export APT_SOURCES_DIR
INSTALLED_LOG="$WORK/installed3.txt"; : > "$INSTALLED_LOG"
NOTED=()
apt_try_each phd2 openssh-server >/dev/null 2>&1
grep -qx phd2 "$INSTALLED_LOG" && grep -qx openssh-server "$INSTALLED_LOG" \
    && ok "installed through the main archive" || bad "installed: $(cat "$INSTALLED_LOG")"
[ "${#NOTED[@]}" = 0 ] && ok "no failure noted" || bad "noted ${NOTED[*]-}"
grep -q 'URIs: http://archive.ubuntu.com/ubuntu/' "$APT_SOURCES_DIR/ubuntu.sources" \
    && ok "deb822 country mirror pointed at archive.ubuntu.com" \
    || bad "sources: $(cat "$APT_SOURCES_DIR/ubuntu.sources")"
grep -q 'http://ports.ubuntu.com/ubuntu-ports' "$APT_SOURCES_LIST" \
    && ok "one-line ports mirror pointed at ports.ubuntu.com" || bad "list: $(cat "$APT_SOURCES_LIST")"
grep -q 'http://security.ubuntu.com/ubuntu/' "$APT_SOURCES_DIR/ubuntu.sources" \
    && ok "security.ubuntu.com left alone" || bad "security entry rewritten"
grep -q 'ppa.launchpadcontent.net/pch/phd2' "$APT_SOURCES_DIR/phd2.list" \
    && ok "PPA entry left alone" || bad "PPA entry rewritten"

use_main_archive >/dev/null 2>&1 && bad "switched a second time" || ok "switches only once per run"

mirror_restore >/dev/null
[ "$(cat "$APT_SOURCES_DIR/ubuntu.sources")" = "$orig_sources" ] \
    && [ "$(cat "$APT_SOURCES_LIST")" = "$orig_list" ] \
    && ok "original sources restored" \
    || bad "not restored: $(cat "$APT_SOURCES_DIR/ubuntu.sources" "$APT_SOURCES_LIST")"
[ -z "$MIRROR_BACKUP" ] && ok "backup cleaned up" || bad "backup left at $MIRROR_BACKUP"

echo "== a failure that is not a country mirror is not retried =="
MIRROR_BACKUP=""
printf 'Types: deb\nURIs: http://archive.ubuntu.com/ubuntu/\nSuites: resolute\nComponents: main\n' \
    > "$APT_SOURCES_DIR/ubuntu.sources"
cat > "$BIN/apt-get" <<'STUB'
#!/usr/bin/env bash
[ "$1" = install ] || exit 0
echo "E: Package '$3' has no installation candidate" >&2
exit 100
STUB
apt_install indi-full >/dev/null 2>&1 && bad "a missing package reported success" \
                                     || ok  "a missing package still fails"
[ -z "$MIRROR_BACKUP" ] && ok "sources untouched" || bad "switched mirrors for an unrelated failure"

# ---------------------------------------------------------------------------
# The same machine, an hour later (Discord, 2026-10-08): a 26.04 system whose
# Ubuntu archive entries now said noble. Every install failed on dependencies
# ("python3-venv ... Depends python3 (= 3.12.3-0ubuntu2.1)"), none of which
# named the cause. The script must say so and stop before installing.
# ---------------------------------------------------------------------------
echo "== check_archive_series =="
cat > "$BIN/apt-cache" <<'STUB'
#!/usr/bin/env bash
[ "$1" = policy ] || exit 0
cat <<EOF
Package files:
 100 /var/lib/dpkg/status
     release a=now
 500 https://ppa.launchpadcontent.net/pch/phd2/ubuntu resolute/main amd64 Packages
     release v=26.04,o=LP-PPA-pch-phd2,a=resolute,n=resolute,l=PHD2,c=main,b=amd64
 500 http://security.ubuntu.com/ubuntu ${ARCHIVE}-security/main amd64 Packages
     release v=xx,o=Ubuntu,a=${ARCHIVE}-security,n=${ARCHIVE},l=Ubuntu,c=main,b=amd64
 500 http://archive.ubuntu.com/ubuntu ${ARCHIVE}/main amd64 Packages
     release v=xx,o=Ubuntu,a=${ARCHIVE},n=${ARCHIVE},l=Ubuntu,c=main,b=amd64
EOF
STUB
chmod +x "$BIN/apt-cache"
export ARCHIVE SYS_CODENAME=resolute
printf 'ID=ubuntu\nUBUNTU_CODENAME=resolute\n' > "$OS_RELEASE"
rm -f "$UPSTREAM_RELEASE"

ARCHIVE=noble
[ "$(apt_ubuntu_series)" = noble ] && ok "reads the archive series, not the PPA's" \
                                   || bad "series: '$(apt_ubuntu_series)'"
msg="$(check_archive_series)" && bad "a resolute system on noble sources passed" \
                              || ok  "a resolute system on noble sources stops"
grep -q "Ubuntu 'resolute', but apt's Ubuntu sources are for 'noble'" <<<"$msg" \
    && ok "the message names both releases" || bad "message: $msg"

ARCHIVE=resolute
check_archive_series >/dev/null && ok "matching sources pass" || bad "matching sources were rejected"

# Linux Mint 22: its own codename is xia, the Ubuntu base and archive are noble.
ARCHIVE=noble; SYS_CODENAME=xia
printf 'ID=linuxmint\nUBUNTU_CODENAME=noble\n' > "$OS_RELEASE"
check_archive_series >/dev/null && ok "Mint on its Ubuntu base passes" || bad "Mint was rejected"

# Debian: no o=Ubuntu entries at all, nothing to compare.
printf '#!/usr/bin/env bash\necho "Package files:"\n' > "$BIN/apt-cache"
check_archive_series >/dev/null && ok "no Ubuntu archive: no verdict" || bad "Debian was rejected"

echo
[ "$fails" = 0 ] && echo "all checks passed" || echo "$fails check(s) failed"
exit "$fails"
