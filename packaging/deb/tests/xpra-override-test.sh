#!/usr/bin/env bash
# The postinst's xpra section against a fake /etc: an override that names
# ${XPRA_SESSION_DIR} (unusable on xpra 3) is moved aside, one that does not
# is left alone, and a missing file is not an error.
#
#   bash packaging/deb/tests/xpra-override-test.sh
set -u

POSTINST="$(cd "$(dirname "$0")/.." && pwd)/DEBIAN/postinst"
ROOT=$(mktemp -d)
trap 'rm -rf "$ROOT"' EXIT
mkdir -p "$ROOT/etc/xpra/conf.d"

start=$(grep -n '# ---- 5d. xpra' "$POSTINST" | cut -d: -f1)
end=$(grep -n '# ---- 6. systemd' "$POSTINST" | cut -d: -f1)
[ -n "$start" ] && [ -n "$end" ] || { echo "cannot find the xpra block in postinst"; exit 2; }
sed -n "${start},$((end - 1))p" "$POSTINST" | sed -e "s#/etc/xpra#$ROOT/etc/xpra#g" > "$ROOT/run.sh"
bash -n "$ROOT/run.sh" || { echo "extracted block does not parse"; exit 1; }

fails=0
ok()  { echo "  ok   $1"; }
bad() { echo "  FAIL $1"; fails=$((fails + 1)); }
F="$ROOT/etc/xpra/conf.d/99-polaris-xorg-dummy.conf"

# 1. the broken override is retired
printf 'xvfb = /usr/lib/xorg/Xorg -logfile ${XPRA_SESSION_DIR}/Xorg.log\n' > "$F"
out=$(bash "$ROOT/run.sh" 2>&1)
[ ! -f "$F" ] && [ -f "$F.disabled" ] && ok "override with XPRA_SESSION_DIR moved to .disabled" || bad "override not retired: $(ls "$ROOT/etc/xpra/conf.d")"
grep -q "disabled" <<<"$out" && ok "says so" || bad "no message: $out"
rm -f "$F.disabled"

# 2. a working override stays
printf 'xvfb = Xvfb -screen 0 1920x1080x24\n' > "$F"
bash "$ROOT/run.sh" >/dev/null 2>&1
[ -f "$F" ] && [ ! -f "$F.disabled" ] && ok "override without the variable is left alone" || bad "working override was touched"
rm -f "$F"

# 3. nothing there
bash "$ROOT/run.sh" >/dev/null 2>&1 && ok "no override: no error" || bad "failed with no override present"

echo
[ "$fails" -eq 0 ] && { echo "all passed"; exit 0; } || { echo "$fails failed"; exit 1; }
