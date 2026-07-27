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
#                    libarchive-tools genisoimage openssl wget
#   ( libarchive-tools provides bsdtar; genisoimage builds the payload iso )
#
# The big/critical artifacts (Polaris + ASTAP debs + d80 database) are
# pre-downloaded HERE on the host, where the network is reliable, and handed to
# the guest as an iso labelled POLARIS. The guest installs them from that local
# mount instead of fighting QEMU's emulated NAT for gigabytes (github/fastly +
# sourceforge time out badly under TCG). Cached under ./payload/ between runs.
#
# Usage:
#   ./build-img.sh
#
# Environment overrides:
#   ISO            path to an Ubuntu live-server ISO (downloaded if missing)
#   ISO_URL        where to fetch the ISO when ISO is absent
#   UBUNTU_RELEASE point release series to auto-discover (default 24.04)
#   OUTPUT         output image path           (default polaris-linux-x64.img)
#   DISK_SIZE      virtual disk size           (default 40G)
#   POLARIS_VERSION  "latest" or e.g. 0.89.6   (default latest)
#   POLARIS_USER / POLARIS_PASS / HOSTNAME_NAME
#   SKIP_PAYLOAD=1 don't pre-download/inject; let the guest fetch everything
#   QEMU_MEM / QEMU_CPUS
#
# Distribution: keep DISK_SIZE modest and just compress the raw .img (7z / xz /
# zstd) - the unused space is zeros and compresses away to a few GB. Do NOT use
# PiShrink: it truncates this GPT/UEFI image too tightly to leave room for the
# 33-sector GPT backup table, producing a non-bootable, unrepairable image. The
# polaris-growroot.service (baked in by provision.sh) expands the root fs to
# fill the real disk on first boot.
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
DISK_SIZE="${DISK_SIZE:-20G}"
POLARIS_VERSION="${POLARIS_VERSION:-latest}"
POLARIS_USER="${POLARIS_USER:-polaris}"
POLARIS_PASS="${POLARIS_PASS:-polaris}"
HOSTNAME_NAME="${HOSTNAME_NAME:-polaris-linux}"
POLARIS_REPO="${POLARIS_REPO:-DanWBR/NINA.Polaris}"
SKIP_PAYLOAD="${SKIP_PAYLOAD:-}"
PAYLOAD_DIR="${PAYLOAD_DIR:-$SCRIPT_DIR/payload}"
QEMU_MEM="${QEMU_MEM:-4096}"
QEMU_CPUS="${QEMU_CPUS:-4}"

SF="https://downloads.sourceforge.net/project/astap-program"

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
need genisoimage         "apt install genisoimage"
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

# Embed the installer (with the chosen knobs exported) as base64 in
# late-commands. The script is scripts/install-polaris-linux.sh, the same one
# users run by hand on a fresh Ubuntu -- one recipe, so the image and the
# documented install cannot drift.
PROVISION_RENDERED="$WORK/provision.sh"
{
    echo "#!/bin/bash"
    # An image IS the appliance case: dedicated user, autologin, hostname,
    # no-suspend, grow-root. Explicit so a future default flip cannot
    # silently produce a half-configured image.
    echo "export POLARIS_SETUP_MODE=appliance"
    echo "export POLARIS_VERSION='${POLARIS_VERSION}'"
    echo "export POLARIS_USER='${POLARIS_USER}'"
    echo "export POLARIS_PASS='${POLARIS_PASS}'"
    echo "export TARGET_HOSTNAME='${HOSTNAME_NAME}'"
    cat "$SCRIPT_DIR/../../scripts/install-polaris-linux.sh"
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
# Pre-download the heavy/critical artifacts on the host and pack them into an
# iso labelled POLARIS. provision.sh installs from this mount first, falling
# back to in-guest downloads only if a file is missing.
# ---------------------------------------------------------------------------
PAYLOAD_ARGS=()
if [ -z "$SKIP_PAYLOAD" ]; then
    mkdir -p "$PAYLOAD_DIR"
    # dl URL OUTNAME REQUIRED  -> cached, IPv4, .part-then-promote
    dl() {
        local url="$1" out="$2" required="${3:-0}"
        if [ -s "$PAYLOAD_DIR/$out" ]; then info "payload cached: $out"; return 0; fi
        info "payload download: $out"
        if wget -4 --tries=3 --retry-connrefused -O "$PAYLOAD_DIR/$out.part" "$url"; then
            mv "$PAYLOAD_DIR/$out.part" "$PAYLOAD_DIR/$out"
        else
            rm -f "$PAYLOAD_DIR/$out.part"
            if [ "$required" = "1" ]; then
                die "could not download required artifact $out from $url"
            fi
            warn "optional artifact $out failed to download - guest will retry it"
        fi
    }

    if [ "$POLARIS_VERSION" = "latest" ]; then
        P_URL="https://github.com/${POLARIS_REPO}/releases/latest/download/polaris_amd64.deb"
    else
        P_URL="https://github.com/${POLARIS_REPO}/releases/download/v${POLARIS_VERSION}/polaris_${POLARIS_VERSION}_amd64.deb"
    fi
    dl "$P_URL" "polaris_amd64.deb" 1
    dl "${SF}/linux_installer/astap_amd64.deb" "astap_amd64.deb" 0
    dl "${SF}/linux_installer/astap_command-line_version_Linux_amd64.zip" "astap_cli.zip" 0
    dl "${SF}/star_databases/d80_star_database.deb" "d80_star_database.deb" 0

    # Ship the self-install tool in the payload so the image gets it even
    # when the guest network is unusable (the whole reason the payload
    # exists). The installer copies it to /usr/local/sbin.
    cp -f "$SCRIPT_DIR/polaris-install-to-disk.sh" "$PAYLOAD_DIR/" 2>/dev/null || true

    info "Building payload iso (label POLARIS)"
    genisoimage -quiet -V POLARIS -J -r -o "$WORK/payload.iso" "$PAYLOAD_DIR"
    PAYLOAD_ARGS=(-drive file="$WORK/payload.iso",media=cdrom)
fi

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
    "${PAYLOAD_ARGS[@]}" \
    -kernel "$WORK/casper/vmlinuz" \
    -initrd "$WORK/casper/initrd" \
    -append "autoinstall ds=nocloud console=ttyS0,115200n8 ---" \
    -netdev user,id=n0 -device virtio-net-pci,netdev=n0 \
    -no-reboot -nographic

# ---------------------------------------------------------------------------
# Done
# ---------------------------------------------------------------------------
echo ""
info "Image built: $OUTPUT ($(du -h "$OUTPUT" | cut -f1))"
echo ""
echo "  To distribute, compress the raw image (free space is zeros):"
echo "    7z a polaris.7z $OUTPUT      # or: zstd -T0 $OUTPUT   /   xz -T0 $OUTPUT"
echo "  Root grows to fill the disk on first boot (polaris-growroot.service)."
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
