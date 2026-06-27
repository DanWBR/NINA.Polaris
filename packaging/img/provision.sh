#!/bin/bash
# =============================================================================
# N.I.N.A. Polaris - in-target provisioning for the x64 bare-metal image
# =============================================================================
# Runs INSIDE the freshly installed Ubuntu system (via curtin in-target during
# autoinstall, or by hand inside a chroot / on a fresh netinst). It encodes the
# full "image ready for Polaris" recipe:
#
#   - INDI (+ 3rd party drivers) and PHD2 from their PPAs
#   - astrometry.net + index databases
#   - ASTAP (GUI + CLI) + the d80 star database
#   - default user (polaris/polaris) with passwordless sudo + autologin
#   - SSH enabled, hostname polaris-linux
#   - all suspend/hibernate disabled
#   - finally, the prebuilt polaris_amd64.deb from GitHub Releases
#
# It is idempotent enough to re-run. Tweak the variables below or override them
# via the environment (build-img.sh injects POLARIS_VERSION / POLARIS_USER /
# POLARIS_PASS / TARGET_HOSTNAME).
# =============================================================================
set -euxo pipefail
export DEBIAN_FRONTEND=noninteractive

# ---------------------------------------------------------------------------
# Tunables (env overrides win)
# ---------------------------------------------------------------------------
POLARIS_VERSION="${POLARIS_VERSION:-latest}"   # "latest" or e.g. 1.0.0
POLARIS_USER="${POLARIS_USER:-polaris}"
POLARIS_PASS="${POLARIS_PASS:-polaris}"
TARGET_HOSTNAME="${TARGET_HOSTNAME:-polaris-linux}"
POLARIS_REPO="${POLARIS_REPO:-DanWBR/NINA.Polaris}"

# ASTAP downloads - CONFIRM the current URLs on hnsky.org / sourceforge before
# relying on them; the project renames assets occasionally.
ASTAP_GUI_URL="${ASTAP_GUI_URL:-https://www.hnsky.org/astap_amd64.deb}"
ASTAP_CLI_URL="${ASTAP_CLI_URL:-https://downloads.sourceforge.net/project/astap-program/linux_installer/astap_command_line_version_Linux_amd64.deb}"
ASTAP_D80_URL="${ASTAP_D80_URL:-https://downloads.sourceforge.net/project/astap-program/star_databases/d80_star_database.deb}"

# ---------------------------------------------------------------------------
# 0. Base tools
# ---------------------------------------------------------------------------
apt-get update
apt-get install -y software-properties-common wget ca-certificates curl gnupg

# ---------------------------------------------------------------------------
# 1. PPAs: INDI (+ 3rd party drivers) and PHD2
# ---------------------------------------------------------------------------
add-apt-repository -y ppa:mutlaqja/ppa     # indi-full + indi 3rd party drivers
add-apt-repository -y ppa:pch/phd2         # PHD2
apt-get update

# ---------------------------------------------------------------------------
# 2. INDI + PHD2
# ---------------------------------------------------------------------------
apt-get install -y indi-full phd2

# ---------------------------------------------------------------------------
# 3. astrometry.net + index databases
# ---------------------------------------------------------------------------
apt-get install -y astrometry.net
# Wide-field indexes available straight from the Ubuntu archive. Narrow-field
# series (4200/4100) are large - pull the ones matching your FOV from
# data.astrometry.net into /usr/share/astrometry afterwards if you need them.
apt-get install -y astrometry-data-tycho2 astrometry-data-2mass || true

# ---------------------------------------------------------------------------
# 4. ASTAP (GUI + CLI) + d80 star database
# ---------------------------------------------------------------------------
cd /tmp
wget -O astap.deb     "$ASTAP_GUI_URL"
wget -O astap_cli.deb "$ASTAP_CLI_URL"
wget -O astap_d80.deb "$ASTAP_D80_URL"
apt-get install -y ./astap.deb ./astap_cli.deb ./astap_d80.deb
rm -f /tmp/astap.deb /tmp/astap_cli.deb /tmp/astap_d80.deb

# ---------------------------------------------------------------------------
# 5. Default user: polaris / polaris, passwordless sudo, hardware groups
# ---------------------------------------------------------------------------
if ! id "$POLARIS_USER" &>/dev/null; then
    useradd -m -s /bin/bash "$POLARIS_USER"
fi
usermod -aG sudo,dialout,video,plugdev "$POLARIS_USER"
echo "${POLARIS_USER}:${POLARIS_PASS}" | chpasswd
echo "${POLARIS_USER} ALL=(ALL) NOPASSWD:ALL" > "/etc/sudoers.d/${POLARIS_USER}"
chmod 440 "/etc/sudoers.d/${POLARIS_USER}"

# ---------------------------------------------------------------------------
# 6. Console autologin on tty1
# ---------------------------------------------------------------------------
mkdir -p /etc/systemd/system/getty@tty1.service.d
cat >/etc/systemd/system/getty@tty1.service.d/autologin.conf <<EOF
[Service]
ExecStart=
ExecStart=-/sbin/agetty --autologin ${POLARIS_USER} --noclear %I \$TERM
EOF

# ---------------------------------------------------------------------------
# 7. SSH + hostname
# ---------------------------------------------------------------------------
apt-get install -y openssh-server
systemctl enable ssh
hostnamectl set-hostname "$TARGET_HOSTNAME" 2>/dev/null || echo "$TARGET_HOSTNAME" > /etc/hostname
# Keep /etc/hosts consistent so sudo/SSH name resolution stays quiet.
if ! grep -q "$TARGET_HOSTNAME" /etc/hosts 2>/dev/null; then
    echo "127.0.1.1 ${TARGET_HOSTNAME}" >> /etc/hosts
fi

# ---------------------------------------------------------------------------
# 8. Kill suspend / hibernate for good
# ---------------------------------------------------------------------------
systemctl mask sleep.target suspend.target hibernate.target hybrid-sleep.target
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
# 9. Polaris: prebuilt amd64 .deb straight from GitHub Releases
# ---------------------------------------------------------------------------
cd /tmp
if [ "$POLARIS_VERSION" = "latest" ]; then
    POLARIS_URL="https://github.com/${POLARIS_REPO}/releases/latest/download/polaris_amd64.deb"
else
    POLARIS_URL="https://github.com/${POLARIS_REPO}/releases/download/v${POLARIS_VERSION}/polaris_${POLARIS_VERSION}_amd64.deb"
fi
wget -O polaris.deb "$POLARIS_URL"
apt-get install -y --install-recommends ./polaris.deb
rm -f /tmp/polaris.deb
systemctl enable polaris

# ---------------------------------------------------------------------------
# Cleanup
# ---------------------------------------------------------------------------
apt-get clean
rm -rf /var/lib/apt/lists/*

echo "==> Polaris image provisioning complete."
