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
  *.deb) if [ ! -f "$WORK/staged-once" ]; then touch "$WORK/staged-once"; ( sleep 0.2; touch "$WORK/cache/polaris-update.deb" ) & fi; exit 0 ;;
  ffmpeg) exit 0 ;;
  libasi) echo "E: Unable to locate package libasi"; exit 100 ;;
  *) exit 100 ;;
esac
STUB
chmod +x "$BIN/dpkg-query" "$BIN/apt-get"

# ---- run -------------------------------------------------------------------
touch "$WORK/cache/polaris-update.deb"
"$SCRIPT"; rc=$?
[ "$rc" -eq 0 ] && ok "script exits 0" || bad "script exit $rc"
sleep 0.5   # let the background staging settle before reading the calls

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

echo
[ "$fails" -eq 0 ] && { echo "all passed"; exit 0; } || { echo "$fails failed"; exit 1; }
