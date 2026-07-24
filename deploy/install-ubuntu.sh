#!/usr/bin/env bash
# =============================================================================
# Polaris one-shot installer for Ubuntu
# =============================================================================
# Installs everything Polaris uses, in one go, the way you would do it by
# hand on Ubuntu:
#
#   1. INDI drivers from the maintainer PPA (newer than Ubuntu's own)
#   2. PHD2 from its PPA
#   3. ASTAP + a star database (Polaris's primary plate solver)
#   4. astrometry.net + a starter index (secondary solver, optional)
#   5. Siril (post-processing CLI Polaris shells out to)
#   6. Polaris itself (the .deb, which auto-starts on boot)
#
# The script is idempotent: safe to re-run to add missing pieces or update.
#
# UBUNTU ONLY. The PPAs are built for Ubuntu release codenames; on Debian /
# Raspberry Pi OS `add-apt-repository ppa:...` does not resolve. On those,
# use the plain `sudo apt install ./polaris_arm64.deb` (it pulls Debian's
# own indi/phd2/astap) or compile the newer INDI/PHD2 from source.
#
# Usage:
#   sudo ./install-ubuntu.sh
#
# Tunables (override via environment):
#   STAR_DB=d50            ASTAP star database: d05 | d20 | d50 | d80 | w08
#                          (bigger = solves smaller fields; d50 fits most
#                          400-1500 mm setups. See docs/user-guide/benchmark
#                          and the ASTAP page for the FOV table.)
#   WITH_ASTROMETRY=1      set 0 to skip astrometry.net + its index
#   WITH_PPA=1             set 0 to use Ubuntu's own indi/phd2 (no PPAs)
#   DL_DIR=$HOME           where large downloads land (NEVER /tmp: on small
#                          SBCs /tmp is a RAM tmpfs and a ~1 GB DB fills it)
#   POLARIS_DEB_URL=...    override the Polaris .deb URL
# =============================================================================

set -euo pipefail

STAR_DB="${STAR_DB:-d50}"
WITH_ASTROMETRY="${WITH_ASTROMETRY:-1}"
WITH_PPA="${WITH_PPA:-1}"
# DL_DIR defaults to the invoking user's home, not root's, even under sudo.
DL_DIR="${DL_DIR:-$(getent passwd "${SUDO_USER:-$USER}" | cut -d: -f6)}"
DL_DIR="${DL_DIR:-$HOME}"

info()  { echo -e "\e[32m[INFO]\e[0m  $*"; }
warn()  { echo -e "\e[33m[WARN]\e[0m  $*"; }
error() { echo -e "\e[31m[ERROR]\e[0m $*"; exit 1; }
step()  { echo; echo -e "\e[36m==== $* ====\e[0m"; }

[ "$(id -u)" -eq 0 ] || error "Run as root: sudo ./install-ubuntu.sh"

# --- OS + architecture ---------------------------------------------------
. /etc/os-release 2>/dev/null || true
if [ "${ID:-}" != "ubuntu" ]; then
    warn "This script targets Ubuntu (the PPAs are Ubuntu-only)."
    warn "Detected: ${PRETTY_NAME:-unknown}. Continuing without PPAs."
    WITH_PPA=0
fi
ARCH="$(dpkg --print-architecture)"   # arm64 | amd64
case "$ARCH" in
    arm64|amd64) : ;;
    *) error "Unsupported architecture: $ARCH (need arm64 or amd64).";;
esac
POLARIS_DEB_URL="${POLARIS_DEB_URL:-https://github.com/DanWBR/NINA.Polaris/releases/latest/download/polaris_${ARCH}.deb}"

mkdir -p "$DL_DIR"
export DEBIAN_FRONTEND=noninteractive

# --- 1. PPAs -------------------------------------------------------------
step "1/6  APT repositories"
apt-get update -y
apt-get install -y software-properties-common curl wget ca-certificates
if [ "$WITH_PPA" = "1" ]; then
    # add-apt-repository is idempotent; -y avoids the interactive prompt.
    add-apt-repository -y ppa:mutlaqja/ppa    # INDI + 3rd-party drivers (KStars author)
    add-apt-repository -y ppa:pch/phd2        # PHD2
    apt-get update -y
else
    info "Skipping PPAs (using the distro's own indi/phd2)."
fi

# --- 2/3/5. INDI, PHD2, ASTAP, Siril, base deps --------------------------
step "2/6  INDI, PHD2, ASTAP, Siril + base dependencies"
# libfontconfig1 is what SkiaSharp needs to encode images headless; lsof and
# the malloc/icu bits are used by Polaris + INDI at runtime.
apt-get install -y \
    libicu-dev libssl-dev libfontconfig1 lsof \
    indi-full gsc \
    phd2 \
    astap \
    siril

# --- 4. ASTAP star database ---------------------------------------------
step "3/6  ASTAP star database ($STAR_DB)"
case "$STAR_DB" in
    w08) DB_FILE="w08_star_database_mag08_astap.deb" ;;
    d05|d20|d50|d80) DB_FILE="${STAR_DB}_star_database.deb" ;;
    *) error "Unknown STAR_DB '$STAR_DB' (use d05|d20|d50|d80|w08)." ;;
esac
if ls /opt/astap/ >/dev/null 2>&1 && [ -n "$(ls -A /opt/astap 2>/dev/null)" ]; then
    info "ASTAP database already present in /opt/astap, skipping download."
else
    info "Downloading $DB_FILE to $DL_DIR (not /tmp) ..."
    wget -O "$DL_DIR/$DB_FILE" \
        "https://downloads.sourceforge.net/project/astap-program/star_databases/$DB_FILE"
    apt-get install -y "$DL_DIR/$DB_FILE"
    rm -f "$DL_DIR/$DB_FILE"
fi

# --- astrometry.net (optional secondary solver) --------------------------
if [ "$WITH_ASTROMETRY" = "1" ]; then
    step "4/6  astrometry.net + starter index"
    apt-get install -y astrometry.net
    # Wide/medium-field index (Tycho-2). Package name varies by release, so
    # install it best-effort. For narrow fields (long focal length + small
    # sensor) add the matching index-42xx / index-52xx files from
    # http://data.astrometry.net by FOV. ASTAP is Polaris's primary solver,
    # so this is a convenience, not a requirement.
    if apt-get install -y astrometry-data-tycho2; then
        info "Installed the Tycho-2 index (wide/medium field)."
    else
        warn "astrometry-data-tycho2 not found in apt on this release."
        warn "Download index files by FOV from http://data.astrometry.net"
        warn "into /usr/share/astrometry/ if you need astrometry.net."
    fi
else
    step "4/6  astrometry.net  (skipped: WITH_ASTROMETRY=0)"
fi

# --- 6. Polaris ----------------------------------------------------------
step "5/6  Polaris (.deb)"
info "Downloading Polaris ($ARCH) to $DL_DIR ..."
if ! wget -O "$DL_DIR/polaris.deb" "$POLARIS_DEB_URL"; then
    error "Could not download the Polaris .deb for $ARCH from:
       $POLARIS_DEB_URL
       If you are on amd64 and no .deb is published, use the x64 tarball
       from the releases page instead."
fi
# apt install (not dpkg -i) so any remaining apt dependencies resolve.
apt-get install -y "$DL_DIR/polaris.deb"
rm -f "$DL_DIR/polaris.deb"

# --- verify --------------------------------------------------------------
step "6/6  Verify"
ok=1
command -v indiserver >/dev/null && info "indiserver: $(command -v indiserver)" || { warn "indiserver missing"; ok=0; }
command -v phd2        >/dev/null && info "phd2:       $(command -v phd2)"        || warn "phd2 missing"
command -v astap       >/dev/null && info "astap:      $(command -v astap)"       || warn "astap missing"
command -v solve-field >/dev/null && info "astrometry: $(command -v solve-field)" || { [ "$WITH_ASTROMETRY" = 1 ] && warn "solve-field missing"; }
command -v siril       >/dev/null && info "siril:      $(command -v siril)"       || warn "siril missing"
if systemctl is-active --quiet polaris 2>/dev/null; then
    info "polaris.service: active"
else
    warn "polaris.service is not active yet; check: journalctl -u polaris -e"
    ok=0
fi

echo
echo "============================================================================="
if [ "$ok" = 1 ]; then
    info "Done. Open Polaris at:  https://$(hostname -I | awk '{print $1}'):5000"
else
    warn "Finished with warnings, see above."
fi
echo "  Star DB:        /opt/astap  (STAR_DB=$STAR_DB)"
echo "  Update later:   the in-app update, or re-run this script"
echo "============================================================================="
