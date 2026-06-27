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
# Resilience: this script deliberately does NOT use 'set -e'. A single failing
# optional component (e.g. an ASTAP mirror 404) must not nuke an hour-long
# image build. Failures are collected and reported at the end; only a failure
# to install Polaris itself makes the script exit non-zero (which fails the
# autoinstall). Re-run it on the booted system to retry anything that failed.
#
# Tweak the variables below or override them via the environment (build-img.sh
# injects POLARIS_VERSION / POLARIS_USER / POLARIS_PASS / TARGET_HOSTNAME).
# =============================================================================
set -uo pipefail
export DEBIAN_FRONTEND=noninteractive

# ---------------------------------------------------------------------------
# Tunables (env overrides win)
# ---------------------------------------------------------------------------
POLARIS_VERSION="${POLARIS_VERSION:-latest}"   # "latest" or e.g. 0.89.6
POLARIS_USER="${POLARIS_USER:-polaris}"
POLARIS_PASS="${POLARIS_PASS:-polaris}"
TARGET_HOSTNAME="${TARGET_HOSTNAME:-polaris-linux}"
POLARIS_REPO="${POLARIS_REPO:-DanWBR/NINA.Polaris}"

# ASTAP downloads (exact SourceForge asset names; case-sensitive).
SF="https://downloads.sourceforge.net/project/astap-program"
ASTAP_GUI_URL="${ASTAP_GUI_URL:-${SF}/linux_installer/astap_amd64.deb}"
ASTAP_CLI_URL="${ASTAP_CLI_URL:-${SF}/linux_installer/astap_command-line_version_Linux_amd64.zip}"
ASTAP_D80_URL="${ASTAP_D80_URL:-${SF}/star_databases/d80_star_database.deb}"

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
FAILED=()
banner()   { echo -e "\n\e[36m==> $*\e[0m"; }
note_fail(){ FAILED+=("$1"); echo -e "\e[31m[FAIL]\e[0m $1"; }

# fetch URL OUTFILE  -> retries, requires a non-empty result
fetch() {
    local url="$1" out="$2" i
    for i in 1 2 3; do
        if wget --tries=2 --timeout=60 -O "$out" "$url" && [ -s "$out" ]; then
            return 0
        fi
        echo "  retry $i/3 for $url"; sleep 3
    done
    return 1
}

apt_try() { apt-get install -y "$@" || note_fail "apt install $*"; }

# ---------------------------------------------------------------------------
# 0. Base tools
# ---------------------------------------------------------------------------
banner "Base tools"
apt-get update || note_fail "apt update (base)"
apt_try software-properties-common wget ca-certificates curl gnupg unzip

# ---------------------------------------------------------------------------
# 1. PPAs: INDI (+ 3rd party drivers) and PHD2
# ---------------------------------------------------------------------------
banner "PPAs (INDI + PHD2)"
add-apt-repository -y ppa:mutlaqja/ppa || note_fail "add-apt-repository indi"
add-apt-repository -y ppa:pch/phd2     || note_fail "add-apt-repository phd2"
apt-get update || note_fail "apt update (ppa)"

# ---------------------------------------------------------------------------
# 2. INDI + PHD2
# ---------------------------------------------------------------------------
banner "INDI + PHD2"
apt_try indi-full phd2

# ---------------------------------------------------------------------------
# 3. astrometry.net + index databases
# ---------------------------------------------------------------------------
banner "astrometry.net"
apt_try astrometry.net
# Wide-field indexes from the Ubuntu archive. Narrow-field series (4200/4100)
# are large - pull the ones matching your FOV from data.astrometry.net into
# /usr/share/astrometry afterwards if you need them. Non-fatal either way.
apt-get install -y astrometry-data-tycho2 astrometry-data-2mass \
    || note_fail "astrometry index packages"

# ---------------------------------------------------------------------------
# 4. ASTAP (GUI + CLI) + d80 star database  (all non-fatal)
# ---------------------------------------------------------------------------
banner "ASTAP (GUI + CLI + d80)"
cd /tmp

# GUI .deb
if fetch "$ASTAP_GUI_URL" astap.deb; then
    apt-get install -y ./astap.deb || note_fail "install astap GUI deb"
else
    note_fail "download astap GUI ($ASTAP_GUI_URL)"
fi

# CLI ships as a zip containing the 'astap_cli' binary.
if fetch "$ASTAP_CLI_URL" astap_cli.zip; then
    rm -rf /tmp/astapcli && mkdir -p /tmp/astapcli
    if unzip -o astap_cli.zip -d /tmp/astapcli >/dev/null; then
        CLI_BIN="$(find /tmp/astapcli -type f -name 'astap_cli' | head -1)"
        [ -z "$CLI_BIN" ] && CLI_BIN="$(find /tmp/astapcli -type f -iname 'astap*' ! -iname '*.txt' | head -1)"
        if [ -n "$CLI_BIN" ]; then
            install -m 0755 "$CLI_BIN" /usr/local/bin/astap_cli
        else
            note_fail "astap_cli binary not found inside zip"
        fi
    else
        note_fail "unzip astap_cli"
    fi
else
    note_fail "download astap_cli ($ASTAP_CLI_URL)"
fi

# d80 star database (~1.25 GB)
if fetch "$ASTAP_D80_URL" astap_d80.deb; then
    apt-get install -y ./astap_d80.deb || note_fail "install astap d80 db"
else
    note_fail "download astap d80 ($ASTAP_D80_URL)"
fi
rm -f /tmp/astap.deb /tmp/astap_cli.zip /tmp/astap_d80.deb
rm -rf /tmp/astapcli

# ---------------------------------------------------------------------------
# 5. Default user: polaris / polaris, passwordless sudo, hardware groups
# ---------------------------------------------------------------------------
banner "User $POLARIS_USER"
if ! id "$POLARIS_USER" &>/dev/null; then
    useradd -m -s /bin/bash "$POLARIS_USER" || note_fail "useradd $POLARIS_USER"
fi
usermod -aG sudo,dialout,video,plugdev "$POLARIS_USER" || note_fail "usermod groups"
echo "${POLARIS_USER}:${POLARIS_PASS}" | chpasswd || note_fail "set password"
echo "${POLARIS_USER} ALL=(ALL) NOPASSWD:ALL" > "/etc/sudoers.d/${POLARIS_USER}"
chmod 440 "/etc/sudoers.d/${POLARIS_USER}"

# ---------------------------------------------------------------------------
# 6. Console autologin on tty1
# ---------------------------------------------------------------------------
banner "Console autologin"
mkdir -p /etc/systemd/system/getty@tty1.service.d
cat >/etc/systemd/system/getty@tty1.service.d/autologin.conf <<EOF
[Service]
ExecStart=
ExecStart=-/sbin/agetty --autologin ${POLARIS_USER} --noclear %I \$TERM
EOF

# ---------------------------------------------------------------------------
# 7. SSH + hostname
# ---------------------------------------------------------------------------
banner "SSH + hostname"
apt_try openssh-server
systemctl enable ssh || note_fail "enable ssh"
hostnamectl set-hostname "$TARGET_HOSTNAME" 2>/dev/null || echo "$TARGET_HOSTNAME" > /etc/hostname
if ! grep -q "$TARGET_HOSTNAME" /etc/hosts 2>/dev/null; then
    echo "127.0.1.1 ${TARGET_HOSTNAME}" >> /etc/hosts
fi

# ---------------------------------------------------------------------------
# 8. Kill suspend / hibernate for good
# ---------------------------------------------------------------------------
banner "Disable suspend/hibernate"
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
# 9. Polaris: prebuilt amd64 .deb from GitHub Releases  (ESSENTIAL)
# ---------------------------------------------------------------------------
banner "Polaris ($POLARIS_VERSION)"
POLARIS_OK=0
cd /tmp
if [ "$POLARIS_VERSION" = "latest" ]; then
    POLARIS_URL="https://github.com/${POLARIS_REPO}/releases/latest/download/polaris_amd64.deb"
else
    POLARIS_URL="https://github.com/${POLARIS_REPO}/releases/download/v${POLARIS_VERSION}/polaris_${POLARIS_VERSION}_amd64.deb"
fi
if fetch "$POLARIS_URL" polaris.deb; then
    if apt-get install -y --install-recommends ./polaris.deb; then
        systemctl enable polaris || note_fail "enable polaris service"
        POLARIS_OK=1
    else
        note_fail "install polaris.deb"
    fi
else
    note_fail "download polaris.deb ($POLARIS_URL)"
fi
rm -f /tmp/polaris.deb

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
