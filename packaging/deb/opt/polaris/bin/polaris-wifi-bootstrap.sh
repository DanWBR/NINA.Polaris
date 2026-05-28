#!/bin/bash
# polaris-wifi-bootstrap: idempotent first-boot helper that creates
# a NetworkManager 'polaris-hotspot' connection so the Pi comes up
# as a WiFi access point even before the user opens Polaris.
#
# Run by polaris-wifi-bootstrap.service the first time the system
# boots after the .deb is installed. Sentinel file
# /var/lib/polaris/wifi-bootstrap.done prevents re-execution on
# subsequent boots (the service ConditionPathExists=! checks it).
#
# Re-running manually is also safe: the 'nmcli connection show'
# guard short-circuits when the connection already exists, and
# the WiFi-interface detection bails out cleanly on hosts without
# WiFi (mini PCs, ethernet-only setups).
#
# Defaults baked into the image: SSID 'Polaris-Hotspot' / PSK
# 'polaris1234'. Users change these via Polaris UI (Settings →
# Network → Edit hotspot credentials).
set -euo pipefail

CONN=polaris-hotspot
SSID="${POLARIS_HOTSPOT_SSID:-Polaris-Hotspot}"
PSK="${POLARIS_HOTSPOT_PSK:-polaris1234}"

# Pick the first WiFi interface NetworkManager reports. We do not
# hardcode wlan0 because Pi 5 sometimes presents the onboard radio
# as wlx<MAC> when USB WiFi adapters are also plugged in.
IFACE=$(nmcli -t -f DEVICE,TYPE device status \
    | awk -F: '$2=="wifi"{print $1; exit}' \
    || true)

if [ -z "${IFACE:-}" ]; then
    echo "polaris-wifi-bootstrap: no WiFi interface detected, skipping" >&2
    exit 0
fi

if nmcli -t connection show | grep -q "^${CONN}:"; then
    echo "polaris-wifi-bootstrap: connection ${CONN} already exists, skipping" >&2
    exit 0
fi

# 2.4 GHz band (b/g) for maximum client compatibility. 5 GHz works
# on Pi 5 but the regulatory-domain handling can refuse to start the
# AP when the country code is unset; b/g is universally OK.
# ipv4.method shared = NM acts as DHCP + DNS + NAT for clients,
# which is what gives the connected phone an IP automatically.
nmcli connection add type wifi ifname "$IFACE" con-name "$CONN" \
    autoconnect yes ssid "$SSID" \
    802-11-wireless.mode ap 802-11-wireless.band bg \
    ipv4.method shared ipv6.method ignore \
    wifi-sec.key-mgmt wpa-psk wifi-sec.psk "$PSK"

nmcli connection up "$CONN" || true
echo "polaris-wifi-bootstrap: created ${CONN} on ${IFACE} (SSID ${SSID})"
