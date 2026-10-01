#!/usr/bin/env bash
# Test for polaris-indi-install.sh with stub apt-get / dpkg / dpkg-deb on
# PATH. Nothing outside a temp directory is touched, and no package is really
# downloaded.
#
# The script is two guards wrapped around one apt call, so the guards are what
# this tests:
#
#   1. an install that would REMOVE something is refused (the night a driver
#      from the wrong INDI source tore out indi-bin, libindi1 and PHD2),
#   2. an install that would OVERWRITE files no package owns is refused (the
#      SBC images build INDI from source, so apt plans no removal, dpkg
#      overwrites in silence, and an older archive driver lands on top of a
#      newer one; the failure then looks like an undefined symbol with no
#      connection to the install).
#
#   bash packaging/deb/tests/indi-install-test.sh
set -u

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="$HERE/../opt/polaris/bin/polaris-indi-install.sh"
[ -r "$SRC" ] || { echo "cannot read $SRC"; exit 2; }

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
BIN="$WORK/bin"; mkdir -p "$BIN" "$WORK/run" "$WORK/sys"
PATH="$BIN:$PATH"

fails=0
ok()  { echo "  ok   $1"; }
bad() { echo "  FAIL $1"; fails=$((fails + 1)); }

# The script writes to /run/polaris; point it at the temp dir.
SCRIPT="$WORK/indi-install.sh"
sed -e "s|^OUT=.*|OUT=$WORK/run/indi-install.json|" \
    -e "s|mkdir -p /run/polaris|mkdir -p $WORK/run|" "$SRC" > "$SCRIPT"
chmod +x "$SCRIPT"

# ---- stubs ---------------------------------------------------------------
# apt-get: PLAN comes from $PLAN_FILE; download drops a prepared .deb listing.
cat > "$BIN/apt-get" <<STUB
#!/usr/bin/env bash
case "\$*" in
  *"install -s"*) cat "\$PLAN_FILE"; exit 0 ;;
  *download*)     [ -n "\${DOWNLOAD_OK:-}" ] && { touch ./fake.deb; exit 0; } || exit 1 ;;
  *install*)      echo "\$*" >> "$WORK/installed"; exit 0 ;;
esac
exit 0
STUB

# dpkg-deb -c: the contents of the package being installed.
cat > "$BIN/dpkg-deb" <<STUB
#!/usr/bin/env bash
[ "\$1" = "-c" ] && cat "\$CONTENTS_FILE"
exit 0
STUB

# dpkg -S: a path is owned only if it is listed in \$OWNED_FILE.
cat > "$BIN/dpkg" <<STUB
#!/usr/bin/env bash
if [ "\$1" = "-S" ]; then
    grep -qxF "\$2" "\$OWNED_FILE" 2>/dev/null && { echo "somepkg: \$2"; exit 0; }
    echo "dpkg-query: no path found matching pattern \$2" >&2; exit 1
fi
[ "\$1" = "-l" ] && exit 0
exit 0
STUB

cat > "$BIN/dpkg-query" <<'STUB'
#!/usr/bin/env bash
echo "9.9.9"
STUB
chmod +x "$BIN"/*

export PLAN_FILE="$WORK/plan" CONTENTS_FILE="$WORK/contents" OWNED_FILE="$WORK/owned"
: > "$OWNED_FILE"

run() { : > "$WORK/installed"; "$SCRIPT" "$1" >"$WORK/out" 2>"$WORK/err"; echo $?; }

# ---- 1. a plain install goes through -------------------------------------
echo "--- a clean install ---"
cat > "$PLAN_FILE" <<'EOF'
Inst indi-asi (2.2 Debian:13/trixie [arm64])
EOF
: > "$CONTENTS_FILE"      # the package brings nothing that already exists
export DOWNLOAD_OK=1
rc=$(run indi-asi)
[ "$rc" -eq 0 ] && ok "a package that collides with nothing installs" || bad "clean install refused: $(cat "$WORK/err")"
grep -q "install -y --no-install-recommends indi-asi" "$WORK/installed" \
    && ok "apt was actually called" || bad "apt was not called"

# ---- 2. a removal is refused ---------------------------------------------
echo "--- it would remove the stack in use ---"
cat > "$PLAN_FILE" <<'EOF'
Inst indi-asi (2.2 Debian:13/trixie [arm64])
Remv indi-bin [1.9.9]
Remv phd2 [2.6.14]
EOF
rc=$(run indi-asi)
[ "$rc" -ne 0 ] && ok "refused" || bad "a removal was allowed through"
grep -q "indi-bin" "$WORK/run/indi-install.json" && ok "it names what would have gone" || bad "the refusal does not say what"
[ -s "$WORK/installed" ] && bad "apt ran anyway" || ok "apt never ran"

# ---- 3. it would overwrite source-built files ----------------------------
echo "--- it would overwrite files built from source ---"
cat > "$PLAN_FILE" <<'EOF'
Inst libasi (1.27 Debian:12/bookworm [arm64])
Inst indi-asi (2.2 Debian:12/bookworm [arm64])
EOF
# The package ships these; they exist here and no package owns them, which is
# exactly the state of a Pi image built from source.
mkdir -p "$WORK/sys/usr/lib" "$WORK/sys/usr/bin"
touch "$WORK/sys/usr/lib/libEAFFocuser.so.1.7.7" "$WORK/sys/usr/bin/indi_asi_focuser"
cat > "$CONTENTS_FILE" <<EOF
-rw-r--r-- root/root 766816 2026-01-01 00:00 .$WORK/sys/usr/lib/libEAFFocuser.so.1.7.7
-rwxr-xr-x root/root 120000 2026-01-01 00:00 .$WORK/sys/usr/bin/indi_asi_focuser
EOF
: > "$OWNED_FILE"
rc=$(run indi-asi)
[ "$rc" -ne 0 ] && ok "refused" || bad "an overwrite of unowned files was allowed"
grep -q "libEAFFocuser" "$WORK/run/indi-install.json" && ok "it names a file that would be replaced" || bad "the refusal does not name the files"
grep -q "apt-get install" "$WORK/run/indi-install.json" && ok "it offers the manual way out" || bad "no escape hatch offered"
[ -s "$WORK/installed" ] && bad "apt ran anyway" || ok "apt never ran"

# ---- 4. the same files, but a package owns them --------------------------
echo "--- the same files, already owned by a package ---"
printf '%s\n' "$WORK/sys/usr/lib/libEAFFocuser.so.1.7.7" "$WORK/sys/usr/bin/indi_asi_focuser" > "$OWNED_FILE"
rc=$(run indi-asi)
[ "$rc" -eq 0 ] && ok "a normal package upgrade is not blocked" \
    || bad "an ordinary upgrade was refused: $(cat "$WORK/run/indi-install.json")"

# ---- 5. the check itself could not run -----------------------------------
echo "--- the download failed, so nothing is known ---"
: > "$OWNED_FILE"
unset DOWNLOAD_OK
rc=$(run indi-asi)
[ "$rc" -ne 0 ] && ok "refused rather than installed on an assumption" \
    || bad "installed without being able to check"
[ -s "$WORK/installed" ] && bad "apt ran anyway" || ok "apt never ran"

# ---- 6. the name is still validated --------------------------------------
echo "--- a name that is not an INDI package ---"
export DOWNLOAD_OK=1
rc=$(run "wget; rm -rf /")
[ "$rc" -ne 0 ] && ok "refused" || bad "accepted a name that is not a package"

echo
[ "$fails" -eq 0 ] && echo "all passed" || echo "$fails failed"
exit $((fails > 0))
