#!/bin/bash
# =============================================================================
# N.I.N.A. Polaris - in-target provisioning for the x64 bare-metal image
# =============================================================================
# Runs INSIDE the freshly installed Ubuntu system (via curtin in-target during
# autoinstall, or by hand inside a chroot / on a fresh netinst). Encodes the
# full "image ready for Polaris" recipe:
#
#   - INDI (+ 3rd party drivers) and PHD2 from their PPAs
#   - astrometry.net + a light index database (tycho2)
#   - ASTAP (GUI + CLI) + the d80 star database
#   - default user (polaris/polaris) with passwordless sudo + autologin
#   - SSH enabled, hostname polaris-linux
#   - all suspend/hibernate disabled
#   - the prebuilt polaris_amd64.deb (the one essential step)
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
#
# Override anything via the environment (build-img.sh injects POLARIS_VERSION /
# POLARIS_USER / POLARIS_PASS / TARGET_HOSTNAME).
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

SF="https://downloads.sourceforge.net/project/astap-program"
ASTAP_GUI_URL="${ASTAP_GUI_URL:-${SF}/linux_installer/astap_amd64.deb}"
ASTAP_CLI_URL="${ASTAP_CLI_URL:-${SF}/linux_installer/astap_command-line_version_Linux_amd64.zip}"
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
apt_try software-properties-common wget ca-certificates curl gnupg unzip

# ---------------------------------------------------------------------------
# 1. Local config first (no network - always succeeds)
# ---------------------------------------------------------------------------
banner "User $POLARIS_USER + autologin + hostname + no-suspend"
if ! id "$POLARIS_USER" &>/dev/null; then
    useradd -m -s /bin/bash "$POLARIS_USER" || note_fail "useradd $POLARIS_USER"
fi
usermod -aG sudo,dialout,video,plugdev "$POLARIS_USER" || note_fail "usermod groups"
echo "${POLARIS_USER}:${POLARIS_PASS}" | chpasswd || note_fail "set password"
echo "${POLARIS_USER} ALL=(ALL) NOPASSWD:ALL" > "/etc/sudoers.d/${POLARIS_USER}"
chmod 440 "/etc/sudoers.d/${POLARIS_USER}"

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
    POLARIS_URL="https://github.com/${POLARIS_REPO}/releases/latest/download/polaris_amd64.deb"
else
    POLARIS_URL="https://github.com/${POLARIS_REPO}/releases/download/v${POLARIS_VERSION}/polaris_${POLARIS_VERSION}_amd64.deb"
fi
POLARIS_OK=0
if install_deb "polaris_amd64.deb" "$POLARIS_URL" "polaris.deb"; then
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

rm -rf /tmp/astap*.deb /tmp/astap_cli.zip /tmp/astapcli /tmp/d80*.deb /tmp/polaris_amd64.deb 2>/dev/null || true
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
