#!/bin/bash
# ExecStart for polaris-indiweb.service: resolves the indi-web binary, then
# execs it in the foreground so systemd keeps tracking the real process.
#
# It exists because systemd expands variables in ExecStart's ARGUMENTS but not
# in the executable itself, and the binary's location is not fixed: the .deb
# puts it in /opt/polaris-indiweb-venv, an operator who ran `pip install
# indiweb` by hand has it somewhere on PATH, and either has to work.
#
# Resolution order, first hit wins:
#   1. $INDIWEB_BIN            (the unit's default, or /etc/default/polaris-indiweb)
#   2. the packaged venv
#   3. indi-web on PATH
set -o pipefail

PORT=${INDIWEB_PORT:-8624}
HOST=${INDIWEB_HOST:-127.0.0.1}
VENV_BIN=/opt/polaris-indiweb-venv/bin/indi-web

BIN=""
if [ -n "$INDIWEB_BIN" ] && [ -x "$INDIWEB_BIN" ]; then
    BIN="$INDIWEB_BIN"
elif [ -x "$VENV_BIN" ]; then
    BIN="$VENV_BIN"
else
    BIN=$(command -v indi-web 2>/dev/null)
fi

if [ -z "$BIN" ]; then
    echo "polaris-indiweb: no indi-web binary found." >&2
    echo "polaris-indiweb: install it with" >&2
    echo "polaris-indiweb:   sudo python3 -m venv /opt/polaris-indiweb-venv" >&2
    echo "polaris-indiweb:   sudo /opt/polaris-indiweb-venv/bin/pip install indiweb legacy-cgi" >&2
    echo "polaris-indiweb: or point INDIWEB_BIN at your own copy in /etc/default/polaris-indiweb" >&2
    exit 1
fi

echo "polaris-indiweb: exec $BIN --port $PORT --host $HOST"
exec "$BIN" --port "$PORT" --host "$HOST"
