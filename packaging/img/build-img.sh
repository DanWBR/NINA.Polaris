#!/bin/bash
# =============================================================================
# Build a flashable x64 bare-metal Polaris image (Ubuntu autoinstall in QEMU)
# =============================================================================
# Produces a raw .img with Ubuntu 22.04/24.04 installed and the full Polaris
# stack provisioned (see provision.sh). Boots on real UEFI mini PCs because the
# stock subiquity installer lays down the ESP + GRUB exactly like a USB install.
#
# Runs fine inside WSL2 - WSL2 exposes /dev/kvm, so QEMU is hardware accelerated
# and you can even boot-test the result before flashing.
#
# Prerequisites (Debian/Ubuntu host or WSL2):
#   sudo apt install qemu-system-x86 ovmf cloud-image-utils \
#                    libarchive-tools whois openssl wget
#   ( libarchive-tools provides bsdtar; whois provides mkpasswd as a fallback )
#
# Usage:
#   ./build-img.sh
#
# Environment overrides:
#   ISO            path to an Ubuntu live-server ISO (downloaded if missing)
#   ISO_URL        where to fetch the ISO when ISO is absent
#   OUTPUT         output image path           (default polaris-linux-x64.img)
#   DISK_SIZE      virtual disk size           (default 24G)
#   POLARIS_VERSION  "latest" or e.g. 1.0.0    (default latest)
#   POLARIS_USER / POLARIS_PASS / HOSTNAME_NAME
#   QEMU_MEM / QEMU_CPUS
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# ---------------------------------------------------------------------------
# Config
# ---------------------------------------------------------------------------
ISO="${ISO:-ubuntu-live-server-amd64.iso}"
# Ubuntu only keeps the newest point release on the mirror, so a hard-coded
# filename eventually 404s. Discover the current one unless ISO_URL is set.
UBUNTU_RELEASE="${UBUNTU_RELEASE:-24.04}"
ISO_URL="${ISO_URL:-}"
OUTPUT="${OUTPUT:-polaris-linux-x64.img}"
DISK_SIZE="${DISK_SIZE:-24G}"
POLARIS_VERSION="${POLARIS_VERSION:-latest}"
POLARIS_USER="${POLARIS_USER:-polaris}"
POLARIS_PASS="${POLARIS_PASS:-polaris}"
HOSTNAME_NAME="${HOSTNAME_NAME:-polaris-linux}"
QEMU_MEM="${QEMU_MEM:-4096}"
QEMU_CPUS="${QEMU_CPUS:-4}"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

info()  { echo -e "\e[32m[INFO]\e[0m  $*"; }
warn()  { echo -e "\e[33m[WARN]\e[0m  $*"; }
die()   { echo -e "\e[31m[ERROR]\e[0m $*" >&2; exit 1; }

# ---------------------------------------------------------------------------
# Dependency check
# ---------------------------------------------------------------------------
need() { command -v "$1" >/dev/null 2>&1 || die "missing '$1' - $2"; }
need qemu-system-x86_64 "apt install qemu-system-x86"
need cloud-localds       "apt install cloud-image-utils"
need bsdtar              "apt install libarchive-tools"
need openssl             "apt install openssl"
need wget                "apt install wget"

[ -e /dev/kvm ] || warn "/dev/kvm not present - QEMU will fall back to slow TCG emulation. \
On WSL2 enable nested virtualization, or run this on a Linux host with KVM."

# ---------------------------------------------------------------------------
# Locate OVMF (UEFI firmware) - name varies across distros
# ---------------------------------------------------------------------------
OVMF_CODE=""
OVMF_VARS=""
for c in /usr/share/OVMF/OVMF_CODE_4M.fd /usr/share/OVMF/OVMF_CODE.fd \
         /usr/share/ovmf/OVMF.fd; do
    [ -f "$c" ] && OVMF_CODE="$c" && break
done
for v in /usr/share/OVMF/OVMF_VARS_4M.fd /usr/share/OVMF/OVMF_VARS.fd; do
    [ -f "$v" ] && OVMF_VARS="$v" && break
done
[ -n "$OVMF_CODE" ] || die "OVMF firmware not found - apt install ovmf"
cp "$OVMF_VARS" "$WORK/OVMF_VARS.fd" 2>/dev/null || : > "$WORK/OVMF_VARS.fd"

# ---------------------------------------------------------------------------
# Fetch the Ubuntu live-server ISO if needed
# ---------------------------------------------------------------------------
# A failed/404 download via 'wget -O' leaves a 0-byte file behind that would
# poison later runs (bsdtar then fails on the empty "ISO"). Treat anything
# implausibly small as missing so a botched download self-heals.
if [ -f "$ISO" ] && [ "$(stat -c%s "$ISO" 2>/dev/null || echo 0)" -lt 500000000 ]; then
    warn "Existing '$ISO' is too small to be a real ISO - re-downloading"
    rm -f "$ISO"
fi
if [ ! -f "$ISO" ]; then
    if [ -z "$ISO_URL" ]; then
        BASE="https://releases.ubuntu.com/${UBUNTU_RELEASE}/"
        info "Discovering the current live-server ISO under $BASE"
        NAME="$(wget -qO- "$BASE" \
            | grep -oE "ubuntu-${UBUNTU_RELEASE}(\.[0-9]+)?-live-server-amd64\.iso" \
            | sort -V | tail -1)"
        [ -n "$NAME" ] || die "could not find a live-server ISO at $BASE - set ISO_URL=... manually"
        ISO_URL="${BASE}${NAME}"
    fi
    info "Downloading ISO from $ISO_URL"
    # Download to .part and only promote on success, so an interrupted/404
    # fetch never leaves a half/empty file in place.
    wget -O "$ISO.part" "$ISO_URL"
    mv "$ISO.part" "$ISO"
fi

# Extract kernel + initrd so we can inject 'autoinstall' on the cmdline without
# touching the ISO's bootloader (the reliable headless path in QEMU).
info "Extracting kernel + initrd from the ISO"
bsdtar -C "$WORK" -xf "$ISO" casper/vmlinuz casper/initrd \
    || die "could not extract casper/vmlinuz + casper/initrd from $ISO"

# ---------------------------------------------------------------------------
# Render user-data from the template + build the NoCloud seed
# ---------------------------------------------------------------------------
info "Rendering autoinstall seed (user: $POLARIS_USER, host: $HOSTNAME_NAME, polaris: $POLARIS_VERSION)"
PWHASH="$(openssl passwd -6 "$POLARIS_PASS")"

# Embed provision.sh (with the chosen knobs exported) as base64 in late-commands.
PROVISION_RENDERED="$WORK/provision.sh"
{
    echo "#!/bin/bash"
    echo "export POLARIS_VERSION='${POLARIS_VERSION}'"
    echo "export POLARIS_USER='${POLARIS_USER}'"
    echo "export POLARIS_PASS='${POLARIS_PASS}'"
    echo "export TARGET_HOSTNAME='${HOSTNAME_NAME}'"
    cat "$SCRIPT_DIR/provision.sh"
} > "$PROVISION_RENDERED"
B64="$(base64 -w0 "$PROVISION_RENDERED")"

# '|' is safe as a sed delimiter: absent from crypt hashes and base64 alphabets.
sed -e "s|@@PWHASH@@|${PWHASH}|" \
    -e "s|@@USERNAME@@|${POLARIS_USER}|" \
    -e "s|@@HOSTNAME@@|${HOSTNAME_NAME}|" \
    -e "s|@@PROVISION_B64@@|${B64}|" \
    "$SCRIPT_DIR/user-data" > "$WORK/user-data"
cp "$SCRIPT_DIR/meta-data" "$WORK/meta-data"

cloud-localds "$WORK/seed.iso" "$WORK/user-data" "$WORK/meta-data"

# ---------------------------------------------------------------------------
# Create the target disk
# ---------------------------------------------------------------------------
info "Creating target disk $OUTPUT ($DISK_SIZE)"
qemu-img create -f raw "$OUTPUT" "$DISK_SIZE" >/dev/null

# ---------------------------------------------------------------------------
# Run the unattended install
# ---------------------------------------------------------------------------
ACCEL=()
[ -e /dev/kvm ] && ACCEL=(-enable-kvm -cpu host)

info "Booting installer in QEMU - this runs unattended and powers off when done."
info "(Watch the serial console below; it downloads PPAs + ASTAP + Polaris.)"
qemu-system-x86_64 \
    "${ACCEL[@]}" \
    -m "$QEMU_MEM" -smp "$QEMU_CPUS" \
    -machine q35 \
    -drive if=pflash,format=raw,readonly=on,file="$OVMF_CODE" \
    -drive if=pflash,format=raw,file="$WORK/OVMF_VARS.fd" \
    -drive file="$OUTPUT",format=raw,if=virtio,cache=writeback \
    -drive file="$ISO",media=cdrom \
    -drive file="$WORK/seed.iso",media=cdrom \
    -kernel "$WORK/casper/vmlinuz" \
    -initrd "$WORK/casper/initrd" \
    -append "autoinstall ds=nocloud console=ttyS0,115200n8 ---" \
    -netdev user,id=n0 -device virtio-net-pci,netdev=n0 \
    -no-reboot -nographic

# ---------------------------------------------------------------------------
# Done
# ---------------------------------------------------------------------------
echo ""
info "Image built: $OUTPUT"
echo ""
echo "  Boot-test it (no install media this time):"
echo "    qemu-system-x86_64 -enable-kvm -m 4096 -machine q35 \\"
echo "      -drive if=pflash,format=raw,readonly=on,file=$OVMF_CODE \\"
echo "      -drive if=pflash,format=raw,file=OVMF_VARS.fd \\"
echo "      -drive file=$OUTPUT,format=raw,if=virtio -nographic"
echo ""
echo "  Flash it to the mini PC's SSD/USB:"
echo "    Windows:  Rufus or balenaEtcher  ->  select $OUTPUT"
echo "    Linux:    sudo dd if=$OUTPUT of=/dev/sdX bs=4M status=progress conv=fsync"
echo ""
echo "  First boot: ssh ${POLARIS_USER}@${HOSTNAME_NAME}.local  (pw: ${POLARIS_PASS})"
echo "              UI at  https://${HOSTNAME_NAME}.local:5000"
