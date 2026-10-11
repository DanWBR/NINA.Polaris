#!/usr/bin/env bash
# Test for polaris-sdk-symlinks.sh. Everything happens in a temp directory
# pointed at by POLARIS_SDK_LIBDIRS; no system path is touched.
#
# The repair exists because a Raspberry Pi card was found running the ZWO SDK
# from December 2022 under drivers built against a 2026 one: the focuser died
# on a missing symbol and the camera silently lost two years of SDK. It must
# put the sonames back without inventing anything, and it must never move a
# soname backwards.
#
#   bash packaging/deb/tests/sdk-symlinks-test.sh
set -u

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$HERE/../opt/polaris/bin/polaris-sdk-symlinks.sh"
[ -r "$SCRIPT" ] || { echo "cannot read $SCRIPT"; exit 2; }

fails=0
ok()  { echo "  ok   $1"; }
bad() { echo "  FAIL $1"; fails=$((fails + 1)); }

run() { POLARIS_SDK_LIBDIRS="$D" bash "$SCRIPT" > "$D/../out" 2>&1; }
link() { readlink "$D/$1" 2>/dev/null || echo "<none>"; }

fresh() { rm -rf "$D"; mkdir -p "$D"; }
W=$(mktemp -d); trap 'rm -rf "$W"' EXIT; D="$W/lib"

# ---- 1. the state a broken Pi card is in ---------------------------------
echo "--- an archive package repointed the sonames at older builds ---"
fresh
touch "$D/libASICamera2.so.1.27"  "$D/libASICamera2.so.1.41"
touch "$D/libEAFFocuser.so.1.4"   "$D/libEAFFocuser.so.1.7.7"
touch "$D/libEFWFilter.so.1.7"    "$D/libEFWFilter.so.1.7.0"
touch "$D/libUSB2ST4Conv.so.1.0"
ln -sf libASICamera2.so.1.27 "$D/libASICamera2.so.1"
ln -sf libEAFFocuser.so.1.4  "$D/libEAFFocuser.so.1"
run
[ "$(link libASICamera2.so.1)" = "libASICamera2.so.1.41" ]  && ok "the camera SDK goes back to 1.41"   || bad "camera: $(link libASICamera2.so.1)"
[ "$(link libEAFFocuser.so.1)" = "libEAFFocuser.so.1.7.7" ] && ok "the focuser SDK goes back to 1.7.7" || bad "focuser: $(link libEAFFocuser.so.1)"
[ "$(link libEFWFilter.so.1)" = "libEFWFilter.so.1.7.0" ]   && ok "1.7.0 beats 1.7"                    || bad "filter wheel: $(link libEFWFilter.so.1)"
[ "$(link libASICamera2.so)" = "libASICamera2.so.1" ]       && ok "the development link follows"       || bad "libASICamera2.so: $(link libASICamera2.so)"
grep -q "1.27 -> libASICamera2.so.1.41" "$W/out" && ok "it says what it changed" || bad "the change is not logged"

# ---- 2. nothing to do ----------------------------------------------------
echo "--- already correct ---"
fresh
touch "$D/libASICamera2.so.1.41"
ln -sf libASICamera2.so.1.41 "$D/libASICamera2.so.1"
run
[ -s "$W/out" ] && bad "it reported work on a healthy system: $(cat "$W/out")" || ok "silent when there is nothing to fix"
[ "$(link libASICamera2.so.1)" = "libASICamera2.so.1.41" ] && ok "left alone" || bad "it changed a correct link"

# ---- 3. it must not invent anything --------------------------------------
echo "--- a brand that is not installed ---"
fresh
touch "$D/libASICamera2.so.1.41"
run
[ -e "$D/libSVBCameraSDK.so.1" ] && bad "created a link with no library behind it" || ok "no link invented"
[ -e "$D/libEAFFocuser.so.1" ]   && bad "created a focuser link out of nothing"    || ok "nothing for an absent focuser SDK"

# ---- 4. it must never move a soname backwards ----------------------------
echo "--- the soname already points at the newest ---"
fresh
touch "$D/libASICamera2.so.1.27" "$D/libASICamera2.so.1.41"
ln -sf libASICamera2.so.1.41 "$D/libASICamera2.so.1"
run
[ "$(link libASICamera2.so.1)" = "libASICamera2.so.1.41" ] && ok "stays on the newest" || bad "moved back to $(link libASICamera2.so.1)"

# ---- 5. version ordering that a plain sort gets wrong --------------------
echo "--- 1.7.7 against 1.10.0 and 1.4 ---"
fresh
touch "$D/libEAFFocuser.so.1.4" "$D/libEAFFocuser.so.1.7.7" "$D/libEAFFocuser.so.1.10.0"
ln -sf libEAFFocuser.so.1.4 "$D/libEAFFocuser.so.1"
run
[ "$(link libEAFFocuser.so.1)" = "libEAFFocuser.so.1.10.0" ] && ok "1.10.0 beats 1.7.7, which beats 1.4" || bad "picked $(link libEAFFocuser.so.1)"

# ---- 6. a library of another name is not touched -------------------------
echo "--- something that is not a vendor SDK ---"
fresh
touch "$D/libfoo.so.1.0" "$D/libfoo.so.2.0"
ln -sf libfoo.so.1.0 "$D/libfoo.so.1"
run
[ "$(link libfoo.so.1)" = "libfoo.so.1.0" ] && ok "an unrelated library is left alone" || bad "it touched libfoo"

# ---- 7. a directory it cannot write to is not fatal ----------------------
echo "--- no permission ---"
fresh
touch "$D/libASICamera2.so.1.27" "$D/libASICamera2.so.1.41"
ln -sf libASICamera2.so.1.27 "$D/libASICamera2.so.1"
chmod 500 "$D"
run; rc=$?
chmod 700 "$D"
[ "$rc" -eq 0 ] && ok "exits 0 so the service still starts" || bad "exit $rc would block the service"

echo
[ "$fails" -eq 0 ] && echo "all passed" || echo "$fails failed"
exit $((fails > 0))
