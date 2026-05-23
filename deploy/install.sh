#!/bin/bash
# =============================================================================
# N.I.N.A. Polaris - Installation Script for RPi / Linux ARM64
# =============================================================================
# Usage:
#   sudo ./install.sh [path-to-published-files]
#
# If no path is given, defaults to ../publish/linux-arm64
# Supports both fresh install and upgrade (stops service, copies, restarts).
# =============================================================================

set -euo pipefail

INSTALL_DIR="/opt/nina-polaris"
SERVICE_NAME="nina-polaris"
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PUBLISH_DIR="${1:-${SCRIPT_DIR}/../publish/linux-arm64}"
NINA_USER="nina"

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
info()  { echo -e "\e[32m[INFO]\e[0m  $*"; }
warn()  { echo -e "\e[33m[WARN]\e[0m  $*"; }
error() { echo -e "\e[31m[ERROR]\e[0m $*"; exit 1; }

# ---------------------------------------------------------------------------
# Pre-flight checks
# ---------------------------------------------------------------------------
if [[ $EUID -ne 0 ]]; then
    error "This script must be run as root (use sudo)."
fi

if [[ ! -d "$PUBLISH_DIR" ]]; then
    error "Published files not found at: $PUBLISH_DIR\n       Run publish-linux-arm64.sh first, or pass the publish directory as an argument."
fi

if [[ ! -f "$PUBLISH_DIR/NINA.Polaris" ]]; then
    error "NINA.Polaris binary not found in $PUBLISH_DIR. Is this the correct publish output?"
fi

# ---------------------------------------------------------------------------
# Create nina user if it does not exist
# ---------------------------------------------------------------------------
if ! id -u "$NINA_USER" &>/dev/null; then
    info "Creating system user '$NINA_USER' ..."
    useradd --system --no-create-home --shell /usr/sbin/nologin "$NINA_USER"
    info "User '$NINA_USER' created."
else
    info "User '$NINA_USER' already exists."
fi

# Add nina user to dialout group (serial/USB device access)
if getent group dialout &>/dev/null; then
    usermod -aG dialout "$NINA_USER" 2>/dev/null || true
    info "Added '$NINA_USER' to dialout group for device access."
fi

# Add nina user to video group (camera access)
if getent group video &>/dev/null; then
    usermod -aG video "$NINA_USER" 2>/dev/null || true
    info "Added '$NINA_USER' to video group for camera access."
fi

# ---------------------------------------------------------------------------
# Stop existing service (upgrade path)
# ---------------------------------------------------------------------------
if systemctl is-active --quiet "$SERVICE_NAME" 2>/dev/null; then
    info "Stopping existing $SERVICE_NAME service ..."
    systemctl stop "$SERVICE_NAME"
    UPGRADE=true
else
    UPGRADE=false
fi

# ---------------------------------------------------------------------------
# Create install directory and copy files
# ---------------------------------------------------------------------------
info "Creating install directory: $INSTALL_DIR"
mkdir -p "$INSTALL_DIR"

info "Copying published files from $PUBLISH_DIR ..."
cp -a "$PUBLISH_DIR/." "$INSTALL_DIR/"

# ---------------------------------------------------------------------------
# Set permissions
# ---------------------------------------------------------------------------
info "Setting ownership and permissions ..."
chown -R "$NINA_USER":"$NINA_USER" "$INSTALL_DIR"
chmod 755 "$INSTALL_DIR/NINA.Polaris"

# ---------------------------------------------------------------------------
# Install systemd service
# ---------------------------------------------------------------------------
info "Installing systemd service unit ..."
cp "${SCRIPT_DIR}/nina-polaris.service" "$SERVICE_FILE"
chmod 644 "$SERVICE_FILE"

systemctl daemon-reload
systemctl enable "$SERVICE_NAME"

# ---------------------------------------------------------------------------
# Start the service
# ---------------------------------------------------------------------------
info "Starting $SERVICE_NAME ..."
systemctl start "$SERVICE_NAME"

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
echo ""
echo "============================================================================="
if [[ "$UPGRADE" == true ]]; then
    info "Upgrade complete!"
else
    info "Installation complete!"
fi
echo "============================================================================="
echo ""
echo "  Install location:  $INSTALL_DIR"
echo "  Service name:      $SERVICE_NAME"
echo "  Running as user:   $NINA_USER"
echo ""
echo "  Access the web UI at:"
echo "    http://$(hostname -I | awk '{print $1}'):5000"
echo ""
echo "  Useful commands:"
echo "    sudo systemctl status  $SERVICE_NAME   # Check status"
echo "    sudo systemctl restart $SERVICE_NAME   # Restart"
echo "    sudo systemctl stop    $SERVICE_NAME   # Stop"
echo "    sudo journalctl -u $SERVICE_NAME -f    # Follow logs"
echo ""
echo "============================================================================="
