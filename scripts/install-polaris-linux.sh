#!/bin/bash
# =============================================================================
# N.I.N.A. Polaris - Linux installer
# =============================================================================
# Turns a fresh Debian/Ubuntu into a Polaris box, and is the SAME script the
# bare-metal image is built from (packaging/img/build-img.sh runs it inside the
# autoinstall). One recipe, so the documented install and the shipped image
# cannot drift apart.
#
#   curl -fsSL https://raw.githubusercontent.com/DanWBR/NINA.Polaris/master/scripts/install-polaris-linux.sh | sudo bash
#
# Why this exists: the .deb installs Polaris and nothing else. The surrounding
# stack is the actual work, and it is what the image was really for:
#
#   - INDI (+ 3rd party drivers) and PHD2 from their PPAs
#   - astrometry.net + a light index database (tycho2)
#   - ASTAP (GUI + CLI) + the d80 star database
#   - the Polaris package itself
#
# Modes (POLARIS_SETUP_MODE, or --appliance / --addon):
#
#   appliance (default)  A machine dedicated to Polaris. Also creates the
#                        polaris user with passwordless sudo, console
#                        autologin, sets the hostname, disables suspend, and
#                        installs the first-boot grow-root unit.
#   addon                Someone's existing machine. Installs the software and
#                        touches NOTHING else: no new user, no autologin, no
#                        hostname change, no sleep policy.
#
# Other knobs (all overridable from the environment):
#   POLARIS_VERSION   "latest" (default) or e.g. 0.96.10
#   POLARIS_USER      appliance mode only, default "polaris"
#   POLARIS_PASS      appliance mode only, default "polaris"
#   TARGET_HOSTNAME   appliance mode only, default "polaris-linux"
#
# Robustness (learned the hard way building under emulated QEMU networking):
#   * Big/critical artifacts (Polaris + ASTAP debs + d80) are read from a local
#     payload mount when present (built on the host where the network is sane),
#     and only downloaded as a fallback. build-img.sh attaches that payload as
#     an iso labelled POLARIS.
#   * Downloads force IPv4 (-4) - QEMU slirp routinely black-holes IPv6 to CDNs
#     like github/fastly and sourceforge, which manifests as connect timeouts.
#   * NOT 'set -e': an optional component failing must not nuke an hour-long
#     build. Failures are collected and summarised; apt state is repaired after
#     any failure so one bad package can't cascade. Only a missing Polaris
#     fails the whole install.
#   * astrometry-data-2mass is deliberately avoided: it downloads at dpkg
#     configure time and an interrupted fetch leaves dpkg in an unrecoverable
#     state. tycho2 is small and ships the data in the package.
# =============================================================================
set -uo pipefail
export DEBIAN_FRONTEND=noninteractive

# ---------------------------------------------------------------------------
# Tunables
# ---------------------------------------------------------------------------
POLARIS_VERSION="${POLARIS_VERSION:-latest}"   # "latest" or e.g. 0.89.6
POLARIS_USER="${POLARIS_USER:-polaris}"
POLARIS_PASS="${POLARIS_PASS:-polaris}"
TARGET_HOSTNAME="${TARGET_HOSTNAME:-polaris-linux}"
POLARIS_REPO="${POLARIS_REPO:-DanWBR/NINA.Polaris}"
POLARIS_SETUP_MODE="${POLARIS_SETUP_MODE:-appliance}"

for arg in "$@"; do
    case "$arg" in
        --appliance) POLARIS_SETUP_MODE=appliance ;;
        --addon)     POLARIS_SETUP_MODE=addon ;;
        -h|--help)
            sed -n '2,52p' "$0" | sed 's/^# \?//'
            exit 0 ;;
        *) echo "unknown option: $arg (try --help)" >&2; exit 2 ;;
    esac
done
case "$POLARIS_SETUP_MODE" in
    appliance|addon) ;;
    *) echo "POLARIS_SETUP_MODE must be 'appliance' or 'addon'" >&2; exit 2 ;;
esac

if [ "$(id -u)" -ne 0 ]; then
    echo "This installer needs root: re-run with sudo." >&2
    exit 1
fi

# The image build only ever targeted amd64, but the same recipe serves arm64
# boards that were installed from a plain Debian/Ubuntu rather than a Polaris
# image. Pick the package flavours from the running system.
DEB_ARCH="$(dpkg --print-architecture 2>/dev/null || echo amd64)"
case "$DEB_ARCH" in
    amd64) ASTAP_ARCH=amd64 ;;
    arm64) ASTAP_ARCH=aarch64 ;;
    *) echo "Unsupported architecture: $DEB_ARCH (amd64 and arm64 only)" >&2; exit 2 ;;
esac

SF="https://downloads.sourceforge.net/project/astap-program"
ASTAP_GUI_URL="${ASTAP_GUI_URL:-${SF}/linux_installer/astap_${ASTAP_ARCH}.deb}"
ASTAP_CLI_URL="${ASTAP_CLI_URL:-${SF}/linux_installer/astap_command-line_version_Linux_${ASTAP_ARCH}.zip}"
ASTAP_D80_URL="${ASTAP_D80_URL:-${SF}/star_databases/d80_star_database.deb}"

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
FAILED=()
banner()    { echo -e "\n\e[36m==> $*\e[0m"; }
note_fail() { FAILED+=("$1"); echo -e "\e[31m[FAIL]\e[0m $1"; }
apt_recover(){ dpkg --configure -a >/dev/null 2>&1 || true; apt-get -f install -y >/dev/null 2>&1 || true; }

# fetch URL OUTFILE  -> IPv4, retries, requires a non-empty result
fetch() {
    local url="$1" out="$2" i
    for i in 1 2 3 4 5; do
        if wget -4 --tries=2 --timeout=90 --retry-connrefused -O "$out" "$url" && [ -s "$out" ]; then
            return 0
        fi
        echo "  retry $i/5 for $url"; sleep 5
    done
    return 1
}

apt_try() { apt-get install -y "$@" || { note_fail "apt install $*"; apt_recover; }; }

# ---------------------------------------------------------------------------
# Locate the optional host-built payload (debs pre-downloaded on the host)
# ---------------------------------------------------------------------------
PAYLOAD=""
for d in /dev/sr0 /dev/sr1 /dev/sr2 /dev/sr3 /dev/vdb /dev/vdc; do
    [ -b "$d" ] || continue
    if blkid "$d" 2>/dev/null | grep -q 'LABEL="POLARIS"'; then
        mkdir -p /mnt/payload
        if mount -o ro "$d" /mnt/payload 2>/dev/null; then PAYLOAD=/mnt/payload; break; fi
    fi
done
[ -n "$PAYLOAD" ] && echo "Using local payload at $PAYLOAD ($(ls "$PAYLOAD"))" \
                  || echo "No local payload found - will download everything."

# local-first .deb install: install_deb PAYLOADNAME URL DESC
install_deb() {
    local name="$1" url="$2" desc="$3" f
    if [ -n "$PAYLOAD" ] && [ -s "$PAYLOAD/$name" ]; then
        f="$PAYLOAD/$name"
    else
        f="/tmp/$name"
        fetch "$url" "$f" || { note_fail "download $desc"; return 1; }
    fi
    apt-get install -y "$f" || { note_fail "install $desc"; apt_recover; return 1; }
    return 0
}

# ---------------------------------------------------------------------------
# 0. Base tools
# ---------------------------------------------------------------------------
banner "Base tools"
apt-get update || note_fail "apt update (base)"
apt_try software-properties-common wget ca-certificates curl gnupg unzip \
        cloud-guest-utils gdisk

# ---------------------------------------------------------------------------
# 1. Local config first (no network - always succeeds)
# ---------------------------------------------------------------------------
# Appliance only. On someone else's machine, creating a passwordless-sudo
# user, hijacking tty1 with an autologin, renaming the host and masking
# suspend would all be unwelcome surprises.
if [ "$POLARIS_SETUP_MODE" = appliance ]; then
banner "User $POLARIS_USER + autologin + hostname + no-suspend"
if ! id "$POLARIS_USER" &>/dev/null; then
    useradd -m -s /bin/bash "$POLARIS_USER" || note_fail "useradd $POLARIS_USER"
fi
usermod -aG sudo,dialout,video,plugdev "$POLARIS_USER" || note_fail "usermod groups"
echo "${POLARIS_USER}:${POLARIS_PASS}" | chpasswd || note_fail "set password"
echo "${POLARIS_USER} ALL=(ALL) NOPASSWD:ALL" > "/etc/sudoers.d/${POLARIS_USER}"
chmod 440 "/etc/sudoers.d/${POLARIS_USER}"

# CRITICAL: .NET's Environment.GetFolderPath(LocalApplicationData) returns an
# EMPTY string when ~/.local/share does not exist. Polaris then resolves its
# cert / profile / log / cache paths RELATIVE to its WorkingDirectory
# (/opt/polaris) and crash-loops (it tries to mkdir under the NINA.Polaris
# executable file). The .deb postinst creates ~/.config but NOT ~/.local/share,
# so create it here. (Also fixed upstream in the deb postinst.)
PHOME="$(getent passwd "$POLARIS_USER" | cut -d: -f6)"
PHOME="${PHOME:-/home/$POLARIS_USER}"
install -d -o "$POLARIS_USER" -g "$POLARIS_USER" "$PHOME/.local/share" "$PHOME/.config"
fi

# Needed in BOTH modes: .NET's GetFolderPath(LocalApplicationData) returns an
# empty string when ~/.local/share is missing, and Polaris then resolves its
# cert/profile/log paths relative to /opt/polaris and crash-loops. In addon
# mode the service runs as the user who invoked sudo, so fix that home too.
RUN_USER="${SUDO_USER:-}"
if [ -n "$RUN_USER" ] && id "$RUN_USER" &>/dev/null; then
    RHOME="$(getent passwd "$RUN_USER" | cut -d: -f6)"
    [ -n "$RHOME" ] && install -d -o "$RUN_USER" -g "$RUN_USER"         "$RHOME/.local/share" "$RHOME/.config" 2>/dev/null || true
fi
if [ "$POLARIS_SETUP_MODE" = appliance ]; then

mkdir -p /etc/systemd/system/getty@tty1.service.d
cat >/etc/systemd/system/getty@tty1.service.d/autologin.conf <<EOF
[Service]
ExecStart=
ExecStart=-/sbin/agetty --autologin ${POLARIS_USER} --noclear %I \$TERM
EOF

hostnamectl set-hostname "$TARGET_HOSTNAME" 2>/dev/null || echo "$TARGET_HOSTNAME" > /etc/hostname
grep -q "$TARGET_HOSTNAME" /etc/hosts 2>/dev/null || echo "127.0.1.1 ${TARGET_HOSTNAME}" >> /etc/hosts

systemctl mask sleep.target suspend.target hibernate.target hybrid-sleep.target \
    || note_fail "mask sleep targets"
mkdir -p /etc/systemd/logind.conf.d
cat >/etc/systemd/logind.conf.d/nosleep.conf <<'EOF'
[Login]
HandleLidSwitch=ignore
HandleLidSwitchDocked=ignore
HandleLidSwitchExternalPower=ignore
HandleSuspendKey=ignore
HandleHibernateKey=ignore
IdleAction=ignore
EOF

# ---------------------------------------------------------------------------
# 1b. First-boot grow-root
# ---------------------------------------------------------------------------
# The script + unit used to live here. They now ship IN THE .deb
# (/opt/polaris/bin/polaris-growroot.sh + polaris-growroot.service, enabled by
# its postinst), so a host flashed from an image built before this existed
# picks the feature up on the next Polaris update instead of never. A second
# copy written to /etc/systemd/system would SHADOW the packaged unit, so this
# section deliberately installs nothing.
#
# Why it moved: the Orange Pi 4 Pro image went out with no grow-root at all
# (this section never ran on that build), so its root stayed at the image size
# on a 64 GB card with nothing in the log to say why.

# ---------------------------------------------------------------------------
# 1c. Self-install tool (image -> internal disk)
# ---------------------------------------------------------------------------
# The point of the flashable image is that you never have to write 13 GB to a
# disk you cannot boot from: put the image on a USB stick, boot the mini PC
# from it, and run this to clone the running system onto the internal SSD.
# Payload first (the guest network is the unreliable part of an image build),
# repo as the fallback.
banner "Self-install tool"
ITD=/usr/local/sbin/polaris-install-to-disk
if [ -n "$PAYLOAD" ] && [ -s "$PAYLOAD/polaris-install-to-disk.sh" ]; then
    install -m0755 "$PAYLOAD/polaris-install-to-disk.sh" "$ITD" || note_fail "install self-install tool"
else
    RAW="https://raw.githubusercontent.com/${POLARIS_REPO}/master/packaging/img/polaris-install-to-disk.sh"
    if fetch "$RAW" /tmp/itd.sh; then
        install -m0755 /tmp/itd.sh "$ITD" || note_fail "install self-install tool"
    else
        note_fail "download self-install tool"
    fi
fi
# Its dependencies. grub-efi-amd64-bin carries grub-install for the target.
apt_try gdisk rsync dosfstools parted
[ "$DEB_ARCH" = amd64 ] && apt_try grub-efi-amd64-bin
fi   # end appliance-only section

# ---------------------------------------------------------------------------
# 2. PPAs: INDI (+ 3rd party drivers) and PHD2
# ---------------------------------------------------------------------------
banner "PPAs (INDI + PHD2)"
add-apt-repository -y ppa:mutlaqja/ppa || note_fail "add-apt-repository indi"
add-apt-repository -y ppa:pch/phd2     || note_fail "add-apt-repository phd2"
apt-get update || note_fail "apt update (ppa)"

# ---------------------------------------------------------------------------
# 3. INDI + PHD2 + SSH + astrometry (mirror packages)
# ---------------------------------------------------------------------------
banner "INDI + PHD2 + SSH + astrometry"
apt_try indi-full phd2 openssh-server astrometry.net astrometry-data-tycho2
systemctl enable ssh || note_fail "enable ssh"

# ---------------------------------------------------------------------------
# 4. Polaris - the one essential step (local payload first)
# ---------------------------------------------------------------------------
banner "Polaris ($POLARIS_VERSION)"
if [ "$POLARIS_VERSION" = "latest" ]; then
    POLARIS_URL="https://github.com/${POLARIS_REPO}/releases/latest/download/polaris_${DEB_ARCH}.deb"
else
    POLARIS_URL="https://github.com/${POLARIS_REPO}/releases/download/v${POLARIS_VERSION}/polaris_${POLARIS_VERSION}_${DEB_ARCH}.deb"
fi
POLARIS_OK=0
if install_deb "polaris_${DEB_ARCH}.deb" "$POLARIS_URL" "polaris.deb"; then
    systemctl enable polaris || note_fail "enable polaris service"
    POLARIS_OK=1
fi

# ---------------------------------------------------------------------------
# 5. ASTAP GUI + CLI + d80 (heavy, last, all non-fatal)
# ---------------------------------------------------------------------------
banner "ASTAP (GUI + CLI + d80)"
install_deb "astap_amd64.deb" "$ASTAP_GUI_URL" "astap GUI" || true

# CLI ships as a zip containing the 'astap_cli' binary.
CLI_ZIP=""
if [ -n "$PAYLOAD" ] && [ -s "$PAYLOAD/astap_cli.zip" ]; then
    CLI_ZIP="$PAYLOAD/astap_cli.zip"
elif fetch "$ASTAP_CLI_URL" /tmp/astap_cli.zip; then
    CLI_ZIP="/tmp/astap_cli.zip"
else
    note_fail "download astap_cli"
fi
if [ -n "$CLI_ZIP" ]; then
    rm -rf /tmp/astapcli && mkdir -p /tmp/astapcli
    if unzip -o "$CLI_ZIP" -d /tmp/astapcli >/dev/null; then
        BIN="$(find /tmp/astapcli -type f -name 'astap_cli' | head -1)"
        [ -z "$BIN" ] && BIN="$(find /tmp/astapcli -type f -iname 'astap*' ! -iname '*.txt' | head -1)"
        [ -n "$BIN" ] && install -m 0755 "$BIN" /usr/local/bin/astap_cli || note_fail "astap_cli binary not found"
    else
        note_fail "unzip astap_cli"
    fi
fi

install_deb "d80_star_database.deb" "$ASTAP_D80_URL" "astap d80 db" || true

rm -rf /tmp/astap*.deb /tmp/astap_cli.zip /tmp/astapcli /tmp/d80*.deb /tmp/polaris_*.deb 2>/dev/null || true
[ -n "$PAYLOAD" ] && umount /mnt/payload 2>/dev/null || true

# ---------------------------------------------------------------------------
# Cleanup + summary
# ---------------------------------------------------------------------------
apt-get clean
rm -rf /var/lib/apt/lists/*

echo ""
echo "============================================================================="
if [ "${#FAILED[@]}" -eq 0 ]; then
    echo -e "\e[32m==> Polaris image provisioning complete - everything installed.\e[0m"
else
    echo -e "\e[33m==> Provisioning finished with ${#FAILED[@]} non-fatal issue(s):\e[0m"
    for f in "${FAILED[@]}"; do echo "      - $f"; done
    echo "    Re-run this script on the booted system to retry them."
fi
echo "============================================================================="

# Only Polaris itself is allowed to fail the whole install.
if [ "$POLARIS_OK" -ne 1 ]; then
    echo -e "\e[31m[ERROR] Polaris install failed - aborting (this is the one fatal step).\e[0m" >&2
    exit 1
fi
exit 0
