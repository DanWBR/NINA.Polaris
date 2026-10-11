#!/usr/bin/env bash
# Test for polaris-self-update.sh with stub apt-get / dpkg-query on PATH: the
# staged package installs without Recommends, the missing Recommends are then
# tried one by one (alternatives in order, a package the distro lacks only
# skips itself), and a package staged during that pass is installed too.
# Nothing outside a temp directory is touched.
#
#   bash packaging/deb/tests/self-update-test.sh
set -u

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="$HERE/../opt/polaris/bin/polaris-self-update.sh"
[ -r "$SRC" ] || { echo "cannot read $SRC"; exit 2; }

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
BIN="$WORK/bin"; mkdir -p "$BIN" "$WORK/cache"
PATH="$BIN:$PATH"
CALLS="$WORK/calls"

fails=0
ok()  { echo "  ok   $1"; }
bad() { echo "  FAIL $1"; fails=$((fails + 1)); }

# The script has fixed paths; run a copy with them pointed at the temp dir.
SCRIPT="$WORK/self-update.sh"
sed -e "s|^DEB=.*|DEB=$WORK/cache/polaris-update.deb|" \
    -e "s|^LOG=.*|LOG=$WORK/update.log|" "$SRC" > "$SCRIPT"
chmod +x "$SCRIPT"

# ---- stubs ---------------------------------------------------------------
# dpkg-query: the polaris package and a fixed set of installed packages.
cat > "$BIN/dpkg-query" <<'STUB'
#!/usr/bin/env bash
fmt=""; pkg=""
while [ $# -gt 0 ]; do case "$1" in -W) ;; -f=*) fmt="${1#-f=}";; -f) shift; fmt="$1";; *) pkg="$1";; esac; shift; done
case "$fmt" in
  *Recommends*) [ "$pkg" = polaris ] && printf 'ffmpeg, libraw23 | libraw20, lsof, libasi (>= 1.0)\n' ;;
  *Status*)
    case "$pkg" in
      lsof|libraw20) printf 'install ok installed' ;;
      *) exit 1 ;;
    esac ;;
esac
STUB
# apt-get: records every call; the staged deb and ffmpeg install, libasi does
# not exist here. Installing the deb stages a second package the first time,
# the way a rollback issued during the extras pass would.
cat > "$BIN/apt-get" <<STUB
#!/usr/bin/env bash
echo "\$*" >> "$CALLS"
last="\${@: -1}"
case "\$last" in
  *.deb) exit 0 ;;
  # Stage a second package while the extras pass is running, which is what a
  # rollback issued mid-update does. Hung off the ffmpeg call rather than off
  # a timer: the old version raced the script's own rm of the staged file and
  # failed on a fast machine, which read as a product bug and was not one.
  ffmpeg) [ -f "$WORK/staged-once" ] || { touch "$WORK/staged-once"; touch "$WORK/cache/polaris-update.deb"; }; exit 0 ;;
  libasi) echo "E: Unable to locate package libasi"; exit 100 ;;
  *) exit 100 ;;
esac
STUB
# No vendor SDK for scenario one: point the file scan at an empty directory
# and stub ldconfig. Both are needed. Leaving the scan on the real filesystem
# made this test pass or fail depending on what the build host had installed,
# and the machine it was written on did have libASICamera2.
mkdir -p "$WORK/nolibs"
export POLARIS_SDK_LIBDIRS="$WORK/nolibs"

# ldconfig: this scenario is the one where no vendor SDK is on the machine.
# It has to be stubbed rather than inherited, or the result depends on what
# the build host happens to have installed: the machine this was written on
# had libASICamera2 in its ldconfig cache from unrelated work, and the test
# silently started exercising the other path.
cat > "$BIN/ldconfig" <<'STUB'
#!/usr/bin/env bash
exit 0
STUB
chmod +x "$BIN/dpkg-query" "$BIN/apt-get" "$BIN/ldconfig"

# ---- run -------------------------------------------------------------------
touch "$WORK/cache/polaris-update.deb"
"$SCRIPT"; rc=$?
[ "$rc" -eq 0 ] && ok "script exits 0" || bad "script exit $rc"

first=$(head -1 "$CALLS")
case "$first" in
  *--no-install-recommends*polaris-update.deb) ok "staged package installs without Recommends" ;;
  *) bad "first apt call: $first" ;;
esac
grep -q "install -y --no-install-recommends ffmpeg" "$CALLS" && ok "missing ffmpeg is tried" || bad "ffmpeg not tried"
grep -q " lsof$" "$CALLS" && bad "installed lsof was tried again" || ok "installed package skipped"
grep -q "libraw" "$CALLS" && bad "satisfied alternative group was tried" || ok "alternative group satisfied by libraw20"
grep -q "libasi" "$CALLS" && ok "libasi tried" || bad "libasi not tried"
grep -q "recommends: libasi not installed" "$WORK/update.log" && ok "unavailable package logged, run continues" || bad "libasi failure not logged"
[ "$(grep -c 'polaris-update.deb' "$CALLS")" -eq 2 ] && ok "package staged during the extras pass is installed" || bad "second staged package not installed"
[ -f "$WORK/cache/polaris-update.deb" ] && bad "staged package left behind" || ok "staged package removed"

# ---- second run: the SDK is already there, outside dpkg --------------------
# The published Pi images build the whole INDI stack from source, so the ZWO
# SDK is a real working file that dpkg has never heard of. Installing the
# distribution's libasi over it is a downgrade: Debian bookworm's 1.27 ships
# libEAFFocuser.so.1.4, which does not export EAFStepRange, and the focuser
# driver then dies at load with an undefined symbol. The newer file stays on
# disk unused, so the failure does not look like a downgrade at all.
echo
echo "--- with the ZWO SDK already present outside dpkg ---"
: > "$CALLS"
: > "$WORK/update.log"
rm -f "$WORK/staged-once"
touch "$WORK/cache/polaris-update.deb"

# Now the SDK is there: a real file in the scanned directory, which is how it
# looks on a Pi image, plus an ldconfig that knows about it.
touch "$WORK/nolibs/libASICamera2.so"

# ldconfig reports the SDK, which is what the guard asks.
cat > "$BIN/ldconfig" <<'STUB'
#!/usr/bin/env bash
[ "$1" = "-p" ] && echo "	libASICamera2.so.1 (libc6,AArch64) => /usr/lib/aarch64-linux-gnu/libASICamera2.so.1"
exit 0
STUB
chmod +x "$BIN/ldconfig"

"$SCRIPT" >/dev/null 2>&1; rc=$?
[ "$rc" -eq 0 ] && ok "script still exits 0" || bad "script exit $rc"

grep -q "libasi" "$CALLS"     && bad "libasi was installed over an SDK that was already there"     || ok "present SDK left alone"
grep -q "recommends: libasi already present outside dpkg" "$WORK/update.log"     && ok "the reason is written to the log"     || bad "nothing in the log explains the skip"
# The guard must not stop the rest of the extras: ffmpeg has no SDK and is
# still wanted.
grep -q "install -y --no-install-recommends ffmpeg" "$CALLS"     && ok "other recommends still install"     || bad "the guard swallowed the whole extras pass"

# leave the stub in place: the harness owns PATH for the whole run.

echo
[ "$fails" -eq 0 ] && { echo "all passed"; exit 0; } || { echo "$fails failed"; exit 1; }
