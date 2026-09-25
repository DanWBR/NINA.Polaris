#!/bin/bash
# Re-enumerate the USB devices, the software equivalent of unplugging and
# replugging everything on the bus.
#
# Why this exists: a camera or a focuser that drops off the bus mid session is
# routine on an SBC, and the usual cure is a trip to the telescope to replug a
# cable. Unbinding a hub from the usb driver and binding it back makes the
# kernel re-enumerate everything beneath it, which recovers a device whose
# descriptor got lost, a serial adapter that renumbered, and a driver that is
# holding a file descriptor for a port that no longer exists.
#
# It does NOT fix a device that is electrically absent: a USB-C cable with no
# data pairs comes back as a Billboard device however many times it is
# re-enumerated. The summary says what came back so the operator can tell the
# two apart without reading dmesg.
#
# Runs as root from polaris-usb-reset.service, because /sys/bus/usb/drivers/usb
# is root-only. The polaris user starts that unit through polkit.
set -u

OUT=/run/polaris/usb-reset.json
mkdir -p /run/polaris

SYS=/sys/bus/usb/devices
DRV=/sys/bus/usb/drivers/usb

# ---- what must not be touched -------------------------------------------
#
# The root filesystem is the obvious one: plenty of these boards boot from a
# USB SSD, and re-enumerating the disk under a running system is how you get a
# read-only rootfs and a corrupted database. Anything in that device's chain,
# hub included, is off limits.
protected=""
root_src=$(findmnt -no SOURCE / 2>/dev/null || true)
if [ -n "$root_src" ]; then
    root_dev=$(lsblk -no PKNAME "$root_src" 2>/dev/null | head -1)
    [ -z "$root_dev" ] && root_dev=$(basename "$root_src" | sed 's/[0-9]*$//')
    if [ -n "$root_dev" ] && [ -e "/sys/block/$root_dev" ]; then
        # Walk up from the block device to its USB device, if it has one.
        p=$(readlink -f "/sys/block/$root_dev/device" 2>/dev/null || true)
        while [ -n "$p" ] && [ "$p" != "/" ]; do
            if [ -f "$p/idVendor" ]; then
                protected="$protected $(basename "$p")"
            fi
            p=$(dirname "$p")
        done
    fi
fi

is_protected() {
    local id="$1" pr
    for pr in $protected; do
        # The whole chain: 1-1 protects 1-1.4 and 1-1.4.2 as well.
        case "$pr" in "$id"|"$id".*) return 0 ;; esac
        case "$id" in "$pr"|"$pr".*) return 0 ;; esac
    done
    return 1
}

describe() {
    local d="$SYS/$1"
    local v p n
    v=$(cat "$d/idVendor" 2>/dev/null || echo "????")
    p=$(cat "$d/idProduct" 2>/dev/null || echo "????")
    n=$(cat "$d/product" 2>/dev/null || echo "")
    printf '%s %s:%s %s' "$1" "$v" "$p" "$n"
}

snapshot() {
    local d id
    for d in "$SYS"/*; do
        id=$(basename "$d")
        case "$id" in usb[0-9]*) continue ;; esac      # root hubs
        [ -f "$d/idVendor" ] || continue               # interfaces, not devices
        describe "$id"
        echo
    done
}

json_array() {
    # stdin: one entry per line -> a JSON array of strings
    local first=1
    printf '['
    while IFS= read -r line; do
        [ -z "$line" ] && continue
        line=${line//\\/\\\\}; line=${line//\"/\\\"}
        [ $first -eq 1 ] || printf ','
        printf '"%s"' "$line"
        first=0
    done
    printf ']'
}

before=$(snapshot)

# ---- pick what to re-enumerate ------------------------------------------
#
# Hubs first: rebinding a hub re-enumerates everything under it in one go,
# which is what recovers a device the kernel lost track of. When there is no
# external hub, fall back to re-authorizing the individual devices.
hubs=""
for d in "$SYS"/*; do
    id=$(basename "$d")
    case "$id" in usb[0-9]*) continue ;; esac
    [ -f "$d/bDeviceClass" ] || continue
    [ "$(cat "$d/bDeviceClass")" = "09" ] || continue
    is_protected "$id" && continue
    hubs="$hubs $id"
done

reset_ids=""
skipped=""
for pr in $protected; do skipped="$skipped $pr"; done

# A device plugged straight into the board, with no hub in between, is not
# covered by any hub rebind: it hangs off a root hub, and root hubs are not
# ours to unbind. Those get the authorize toggle instead, which is the closest
# thing to a replug the kernel offers for a single device.
loose=""
for d in "$SYS"/*; do
    id=$(basename "$d")
    case "$id" in usb[0-9]*) continue ;; esac
    [ -f "$d/idVendor" ] || continue
    [ "$(cat "$d/bDeviceClass" 2>/dev/null)" = "09" ] && continue   # hubs handled above
    is_protected "$id" && continue
    # Under a hub we are about to rebind? Then it comes back with the hub.
    covered=0
    for h in $hubs; do
        case "$id" in "$h".*) covered=1; break ;; esac
    done
    [ "$covered" = "1" ] && continue
    loose="$loose $id"
done

for id in $hubs; do
    echo "$id" > "$DRV/unbind" 2>/dev/null || continue
    reset_ids="$reset_ids $id"
done
for id in $loose; do
    [ -w "$SYS/$id/authorized" ] || continue
    echo 0 > "$SYS/$id/authorized" 2>/dev/null || continue
    reset_ids="$reset_ids $id"
done

sleep 2

for id in $hubs; do
    case " $reset_ids " in *" $id "*) echo "$id" > "$DRV/bind" 2>/dev/null || true ;; esac
done
for id in $loose; do
    case " $reset_ids " in *" $id "*) echo 1 > "$SYS/$id/authorized" 2>/dev/null || true ;; esac
done

# Give the kernel time to enumerate what came back before reporting.
sleep 5
after=$(snapshot)

{
    method="none"
    [ -n "$hubs" ] && method="hub-rebind"
    [ -n "$loose" ] && method="$method+reauthorize"
    printf '{"ok":true,"method":"%s",' "${method#none+}"
    printf '"reset":'; printf '%s\n' $reset_ids | json_array
    printf ',"skipped":'; printf '%s\n' $skipped | json_array
    printf ',"before":'; printf '%s\n' "$before" | json_array
    printf ',"after":'; printf '%s\n' "$after" | json_array
    printf '}'
} > "$OUT"
chmod 0644 "$OUT"
cat "$OUT"
