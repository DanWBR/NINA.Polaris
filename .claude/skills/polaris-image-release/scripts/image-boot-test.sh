#!/bin/bash
# Boot an x86-64 image under QEMU + OVMF and wait for the guest to answer.
#
#   image-boot-test.sh <copy-of-image.img> [minutes]
#
# Pass a COPY. Booting an image writes a machine-id, a TLS keypair and the
# growroot marker into it, so the artifact you ship must never be booted.
#
# OVMF means this exercises the real path: firmware -> GPT -> ESP -> GRUB ->
# kernel. Without KVM (WSL2 usually has no /dev/kvm) it runs under emulation,
# so give it a few minutes.
set -euo pipefail

IMG="$1"
MINUTES="${2:-12}"
VARS=/root/OVMF_VARS_boottest.fd
LOG=/root/boottest-serial.log

case "$IMG" in
    */polaris-linux-x64.img) echo "refusing: that is the artifact, boot a copy" >&2; exit 2 ;;
esac

cp /usr/share/OVMF/OVMF_VARS_4M.fd "$VARS"

setsid nohup qemu-system-x86_64 \
    -machine q35 -cpu max -smp 4 -m 4096 \
    -drive if=pflash,format=raw,unit=0,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd \
    -drive if=pflash,format=raw,unit=1,file="$VARS" \
    -drive file="$IMG",format=raw,if=virtio \
    -netdev user,id=n0,hostfwd=tcp::2222-:22,hostfwd=tcp::5555-:5000 \
    -device virtio-net-pci,netdev=n0 \
    -display none -serial file:"$LOG" \
    < /dev/null > /root/boottest-qemu.out 2>&1 &
disown

echo "== qemu started, waiting up to ${MINUTES}m for the guest"

# A port check is NOT enough: QEMU's hostfwd listens the moment QEMU starts, so
# 127.0.0.1:2222 accepts connections while the guest is still in the firmware.
# Only a protocol answer proves the guest is up.
deadline=$(( $(date +%s) + MINUTES * 60 ))
up=""
while [ "$(date +%s)" -lt "$deadline" ]; do
    banner=$(timeout 5 bash -c 'exec 3<>/dev/tcp/127.0.0.1/2222; head -c 20 <&3' 2>/dev/null || true)
    case "$banner" in
        SSH-*) up="ssh"; break ;;
    esac
    code=$(curl -sk --max-time 5 -o /dev/null -w '%{http_code}' https://127.0.0.1:5555/ 2>/dev/null || echo 000)
    [ "$code" != "000" ] && { up="https:$code"; break; }
    sleep 15
done

if [ -z "$up" ]; then
    echo "== guest did not answer within ${MINUTES}m"
    echo "== serial tail:"; tail -20 "$LOG" 2>/dev/null | tr -d '\r'
    echo "== leaving qemu running for inspection (pkill -f boottest to stop)"
    exit 1
fi

echo "== guest answered ($up)"
echo "== serial log: $LOG ($(stat -c %s "$LOG") bytes)"
cat <<'EOF'

Check from inside, then stop qemu:

  sshpass -p polaris ssh -p 2222 -o StrictHostKeyChecking=no \
    -o UserKnownHostsFile=/dev/null polaris@127.0.0.1 \
    'dpkg-query -W polaris; systemctl is-active polaris.service; cat /proc/cmdline'

  curl -sk https://127.0.0.1:5555/api/system/status
  # a sanitized first boot answers: {"error":"auth required","authConfigured":false}

  pkill -f "$(basename "$IMG")"
EOF
