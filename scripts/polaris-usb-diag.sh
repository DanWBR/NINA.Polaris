#!/bin/bash
# =============================================================================
# N.I.N.A. Polaris - USB / stability diagnostics
# =============================================================================
# Collects what is needed to tell a USB link problem from a power problem, a
# thermal problem or a process holding a camera, on any Linux host running
# Polaris (written for the Radxa Dragon Q6A, works on any board):
#
#   - system: board, kernel, kernel command line, Polaris / INDI / SDK versions
#   - USB: the device tree with each device's negotiated speed, power and link
#     power-management state, usbcore parameters, which processes hold which
#     USB device nodes
#   - kernel log: the whole dmesg plus the USB / power lines filtered out
#   - thermal, CPU frequency, power supply, memory, load
#   - the Polaris and INDI journals of the last hours
#
# Usage (as root, so dmesg, the journal and the device nodes are readable):
#
#   sudo bash polaris-usb-diag.sh               # one snapshot
#   sudo bash polaris-usb-diag.sh --watch 15    # snapshot, then 15 minutes of
#                                               # timestamped USB / kernel events
#                                               # and a state sample every 10 s
#
# Start --watch, then make the camera capture until it drops. The result is a
# single .tar.gz, also copied to the Polaris files folder so it can be
# downloaded from the FILES tab.
#
# Nothing on the system is changed. Profiles (which hold passwords) are not
# collected.
# =============================================================================
set -u

WATCH_MIN=0
if [ "${1:-}" = "--watch" ]; then
    WATCH_MIN="${2:-15}"
    case "$WATCH_MIN" in ''|*[!0-9]*) echo "--watch needs a number of minutes"; exit 2;; esac
fi

if [ "$(id -u)" -ne 0 ]; then
    echo "Run as root (sudo), or dmesg, the journal and the USB device nodes are unreadable."
    exit 1
fi

HOST=$(hostname 2>/dev/null || echo host)
STAMP=$(date +%Y%m%d-%H%M%S)
NAME="polaris-diag-${HOST}-${STAMP}"
OUT="/tmp/${NAME}"
mkdir -p "$OUT"

have() { command -v "$1" >/dev/null 2>&1; }
section() { echo; echo "===== $* ====="; }
# run <file> <command...>: append the command and its output to a file
run() {
    local f="$OUT/$1"; shift
    { echo "\$ $*"; "$@" 2>&1; echo; } >> "$f"
}
readf() { [ -r "$1" ] && tr -d '\0' < "$1" 2>/dev/null; }

echo "Collecting into $OUT ..."

# ---------------------------------------------------------------- system ----
{
    section "date"; date -Is; date -u -Is
    section "board"; readf /proc/device-tree/model; echo
    readf /sys/firmware/devicetree/base/compatible | tr '\0' ' '; echo
    section "kernel"; uname -a
    section "kernel command line"; cat /proc/cmdline
    section "os"; cat /etc/os-release 2>/dev/null
    section "uptime / load"; uptime; cat /proc/loadavg
    section "memory"; free -m
    section "disk"; df -h / /home 2>/dev/null
    section "packages"
    dpkg-query -W -f='${Package} ${Version} ${Status}\n' 2>/dev/null \
        | grep -E '^(polaris|indi-bin|indi-full|libindi1|indi-toupbase|libtoupcam|libaltaircam|indi-asi|libasi|indi-svbony|libsvbony|indi-playerone|libplayerone|indi-qhy|libqhy|indi-gphoto|libusb-1.0-0|linux-image)' \
        | grep 'install ok installed' | sed 's/ install ok installed//'
} > "$OUT/system.txt" 2>&1

# ------------------------------------------------------------------- USB ----
{
    section "usbcore parameters"
    for p in autosuspend usbfs_memory_mb quirks use_both_schemes old_scheme_first; do
        printf '%-20s %s\n' "$p" "$(readf /sys/module/usbcore/parameters/$p)"
    done
    section "lsusb"; have lsusb && lsusb || echo "lsusb not installed (usbutils)"
    section "lsusb -t (tree, negotiated speed per device)"; have lsusb && lsusb -t
    section "per device (sysfs)"
    printf '%-10s %-9s %-6s %-7s %-6s %-9s %-6s %-6s %-6s %s\n' \
        dev vid:pid speed maxpwr ctrl autosusp lpm_u1 lpm_u2 state product
    for d in /sys/bus/usb/devices/*; do
        [ -r "$d/idVendor" ] || continue
        vid=$(readf "$d/idVendor"); pid=$(readf "$d/idProduct")
        printf '%-10s %-9s %-6s %-7s %-6s %-9s %-6s %-6s %-6s %s\n' \
            "$(basename "$d")" "$vid:$pid" "$(readf "$d/speed")" "$(readf "$d/bMaxPower")" \
            "$(readf "$d/power/control")" "$(readf "$d/power/autosuspend_delay_ms")" \
            "$(readf "$d/power/usb3_hardware_lpm_u1")" "$(readf "$d/power/usb3_hardware_lpm_u2")" \
            "$(readf "$d/power/runtime_status")" \
            "$(readf "$d/manufacturer") $(readf "$d/product")"
    done
    section "host controllers"
    for c in /sys/bus/platform/drivers/xhci-hcd/* /sys/bus/platform/drivers/dwc3/*; do
        [ -e "$c" ] && echo "$c"
    done
    have lspci && lspci -nn 2>/dev/null | grep -i usb
    section "processes holding USB device nodes"
    for n in /dev/bus/usb/*/*; do
        [ -e "$n" ] || continue
        holders=""
        for fd in /proc/[0-9]*/fd/*; do
            [ "$(readlink "$fd" 2>/dev/null)" = "$n" ] || continue
            pid=${fd#/proc/}; pid=${pid%%/*}
            holders="$holders $pid($(readf /proc/$pid/comm))"
        done
        [ -n "$holders" ] && echo "$n ($(stat -c '%U:%G %a' "$n")) held by:$holders"
    done
    section "device node permissions"
    ls -l /dev/bus/usb/*/* 2>/dev/null
    section "INDI processes"
    pgrep -a indi || echo "(none)"
    section "udev rules touching cameras / usbfs"
    grep -l -E "usbfs_memory_mb|0547|04b4|03c3|a0a0|f266" /lib/udev/rules.d/* /etc/udev/rules.d/* 2>/dev/null
} > "$OUT/usb.txt" 2>&1

# ----------------------------------------------------------- kernel log -----
dmesg -T > "$OUT/dmesg-full.txt" 2>&1 || dmesg > "$OUT/dmesg-full.txt" 2>&1
grep -i -E "usb|xhci|dwc3|over-?current|disconnect|reset .*speed|lpm|enumerat|babble|transfer|toup|cypress|power|thermal|throttl|undervolt|oom|hung task|blocked for" \
    "$OUT/dmesg-full.txt" > "$OUT/dmesg-usb-power.txt" 2>/dev/null

# ------------------------------------------------- thermal / cpu / power ----
snapshot_state() {
    echo "--- $(date -Is)"
    for z in /sys/class/thermal/thermal_zone*; do
        [ -r "$z/temp" ] || continue
        t=$(readf "$z/temp"); t=${t:-0}; printf '%s=%s.%sC ' "$(readf "$z/type")" "$((t / 1000))" "$(( (t % 1000) / 100 ))"
    done
    echo
    for c in /sys/devices/system/cpu/cpufreq/policy*; do
        [ -r "$c/scaling_cur_freq" ] || continue
        cur=$(readf "$c/scaling_cur_freq"); max=$(readf "$c/scaling_max_freq")
        printf '%s:%sMHz/%sMHz(%s) ' "$(basename "$c")" "$(( ${cur:-0} / 1000 ))" \
            "$(( ${max:-0} / 1000 ))" "$(readf "$c/scaling_governor")"
    done
    echo
    for s in /sys/class/power_supply/*; do
        [ -d "$s" ] || continue
        printf '%s: online=%s voltage_now=%s current_now=%s status=%s\n' "$(basename "$s")" \
            "$(readf "$s/online")" "$(readf "$s/voltage_now")" "$(readf "$s/current_now")" "$(readf "$s/status")"
    done
    have vcgencmd && echo "vcgencmd throttled: $(vcgencmd get_throttled 2>/dev/null)"
    echo "load: $(cat /proc/loadavg)  mem: $(free -m | awk '/Mem:/{print $3"/"$2" MB used"}')"
}
{
    section "thermal, cpu frequency, power supply"
    snapshot_state
    section "top processes by cpu"
    ps -eo pid,comm,%cpu,%mem,rss --sort=-%cpu 2>/dev/null | head -15
} > "$OUT/state.txt" 2>&1

# -------------------------------------------------------------- journals ----
if have journalctl; then
    journalctl -u polaris --since "-3h" --no-pager > "$OUT/journal-polaris.txt" 2>&1
    journalctl --since "-3h" --no-pager -t indiserver -t indi-web > "$OUT/journal-indi.txt" 2>&1
    journalctl -k --since "-3h" --no-pager > "$OUT/journal-kernel.txt" 2>&1
    journalctl -b -1 -k --no-pager 2>/dev/null | tail -300 > "$OUT/journal-kernel-previous-boot-tail.txt"
fi
# Polaris' own log files of the last hours (logs only: never the profiles).
for base in /home/polaris/.local/share/NINA.Polaris /var/lib/polaris /opt/polaris; do
    [ -d "$base" ] || continue
    find "$base" -maxdepth 3 -type f -name "*.log" -mmin -240 2>/dev/null | while read -r f; do
        tail -n 4000 "$f" > "$OUT/polaris-log-$(basename "$f")" 2>/dev/null
    done
done

# ----------------------------------------------------------------- watch ----
if [ "$WATCH_MIN" -gt 0 ]; then
    echo "Watching for ${WATCH_MIN} min: make the camera capture now. Ctrl+C ends it early (the bundle is still written)."
    EVENTS="$OUT/watch-kernel-events.txt"
    SAMPLES="$OUT/watch-state-samples.txt"
    lsusb > "$OUT/watch-lsusb-start.txt" 2>&1
    dmesg -w -T 2>/dev/null | grep --line-buffered -i -E "usb|xhci|dwc3|over-?current|disconnect|reset|lpm|babble|thermal|throttl|undervolt|oom" > "$EVENTS" &
    DPID=$!
    stop_watch() { kill "$DPID" 2>/dev/null; }
    trap 'stop_watch' INT TERM
    end=$(( $(date +%s) + WATCH_MIN * 60 ))
    while [ "$(date +%s)" -lt "$end" ]; do
        {
            snapshot_state
            for d in /sys/bus/usb/devices/*; do
                [ -r "$d/idVendor" ] || continue
                case "$(readf "$d/idVendor")" in 0547|04b4|03c3|a0a0|f266|1618)
                    echo "camera $(readf "$d/idVendor"):$(readf "$d/idProduct") at $(basename "$d") speed=$(readf "$d/speed") state=$(readf "$d/power/runtime_status")";;
                esac
            done
        } >> "$SAMPLES" 2>&1
        sleep 10 || break
    done
    stop_watch
    trap - INT TERM
    lsusb > "$OUT/watch-lsusb-end.txt" 2>&1
    n=$(grep -c -i -E 'disconnect|reset' "$EVENTS" 2>/dev/null)
    echo "Watch finished: ${n:-0} disconnect/reset lines recorded."
fi

# ------------------------------------------------------------------ pack ----
TAR="/tmp/${NAME}.tar.gz"
tar -czf "$TAR" -C /tmp "$NAME" 2>/dev/null
DEST=""
for d in /home/polaris/files /home/polaris; do
    [ -d "$d" ] || continue
    if mkdir -p "$d/diagnostics" 2>/dev/null && cp "$TAR" "$d/diagnostics/" 2>/dev/null; then
        chown -R polaris: "$d/diagnostics" 2>/dev/null
        DEST="$d/diagnostics/${NAME}.tar.gz"
        break
    fi
done
echo
echo "Done: $TAR"
[ -n "$DEST" ] && echo "Copy for the FILES tab: $DEST"
