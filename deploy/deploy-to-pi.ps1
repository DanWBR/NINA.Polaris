# Build → publish → ship → restart, one command.
#
# Defaults target a Pi reachable as `polaris.local` over SSH key auth.
# Use linux-arm for Pi 2/3 (ARMv7 32-bit), linux-arm64 for Pi 4/5 on a
# 64-bit OS, linux-x64 for an Intel mini PC.
#
# Prereq on the Pi (one-time):
#   - SSH key from this machine in ~/.ssh/authorized_keys
#   - .NET 10 ASP.NET runtime installed (see docs/user-guide/installation.md)
#   - Either: systemd unit polaris.service running (deploy/nina-polaris.service)
#     OR: the script's fallback "pkill + nohup" path is fine for ad-hoc testing
#
# Usage:
#   .\deploy\deploy-to-pi.ps1                          # uses defaults below
#   .\deploy\deploy-to-pi.ps1 -PiHost 192.168.1.50     # explicit IP
#   .\deploy\deploy-to-pi.ps1 -Rid linux-arm64         # Pi 4/5 64-bit
#   .\deploy\deploy-to-pi.ps1 -NoRestart               # just copy, don't bounce
#
# After it finishes the UI is at http://<pi>:5000.

param(
    # Hostname or IP. polaris.local works when the Pi advertises mDNS;
    # otherwise put the LAN IP. Don't include user@ here.
    [string]$PiHost = "polaris.local",

    # User on the Pi. Pi OS default user is `pi`; raspi-config lets you
    # change it at first boot.
    [string]$PiUser = "pi",

    # Where to deploy on the Pi. Default lives under the user's home
    # so no sudo is needed.
    [string]$RemotePath = "~/polaris",

    # Runtime identifier. Pi 2/3 with 32-bit Raspbian → linux-arm.
    # Pi 4/5 with 64-bit Pi OS or Ubuntu → linux-arm64.
    [ValidateSet("linux-arm", "linux-arm64", "linux-x64")]
    [string]$Rid = "linux-arm",

    # Skip the restart step. Useful when you've stopped the service
    # by hand and want to inspect the binaries before bringing it up.
    [switch]$NoRestart,

    # Skip the actual file transfer. Use after a NoRestart pass when
    # you just want to bounce the service on the Pi.
    [switch]$NoCopy,

    # Build in Debug instead of Release. Bigger output, faster builds,
    # symbol-rich logs — for active troubleshooting.
    [switch]$Debug
)

$ErrorActionPreference = "Stop"

$config = if ($Debug) { "Debug" } else { "Release" }
$repo = Split-Path -Parent $PSScriptRoot
$projPath = Join-Path $repo "src\NINA.Polaris\NINA.Polaris.csproj"
$publishOut = Join-Path $repo "build-output\$Rid-$config"

Write-Host "==> Pi: $PiUser@$PiHost  Path: $RemotePath  RID: $Rid  Config: $config" -ForegroundColor Cyan

if (-not $NoCopy) {
    # Self-contained=false means the .NET runtime must already be on
    # the Pi. Keeps the deploy small (~5 MB instead of ~80 MB) and
    # makes runtime upgrades a separate concern.
    Write-Host "==> Publishing..." -ForegroundColor Cyan
    dotnet publish $projPath `
        -c $config `
        -r $Rid `
        --self-contained false `
        -o $publishOut `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

    Write-Host "==> Ensuring remote path exists..." -ForegroundColor Cyan
    ssh "$PiUser@$PiHost" "mkdir -p $RemotePath"
    if ($LASTEXITCODE -ne 0) { throw "ssh mkdir failed — check SSH key + host" }

    Write-Host "==> Copying $publishOut/* → $PiUser@${PiHost}:$RemotePath/" -ForegroundColor Cyan
    # scp -C compresses on the fly — meaningful for slow Pi 2 Wi-Fi.
    # -p preserves mtimes so the Pi-side "is this file newer" check is
    # reliable.
    scp -C -p -r "$publishOut\*" "$PiUser@${PiHost}:$RemotePath/"
    if ($LASTEXITCODE -ne 0) { throw "scp failed" }
}

if ($NoRestart) {
    Write-Host "==> Skipping restart (--NoRestart)." -ForegroundColor Yellow
    Write-Host "    Start manually: ssh $PiUser@$PiHost 'cd $RemotePath && ASPNETCORE_URLS=http://0.0.0.0:5000 dotnet NINA.Polaris.dll'"
    return
}

Write-Host "==> Restarting Polaris on the Pi..." -ForegroundColor Cyan
# Two restart strategies, tried in order:
# 1. systemctl --user (or system) — if the user installed
#    deploy/nina-polaris.service. Production-correct: auto-restart
#    on crash, logs to journalctl, runs at boot.
# 2. Fallback: pkill + nohup. Quick + dirty, fine for ad-hoc test
#    sessions. Logs go to ~/polaris/polaris.log.
$restartScript = @'
set -e
if systemctl --user is-active --quiet polaris 2>/dev/null; then
  systemctl --user restart polaris
  echo "Restarted via systemctl --user."
elif sudo -n systemctl is-active --quiet polaris 2>/dev/null; then
  sudo systemctl restart polaris
  echo "Restarted via systemctl (system)."
else
  pkill -f "dotnet.*NINA.Polaris" || true
  sleep 1
  cd REMOTE_PATH
  ASPNETCORE_URLS=http://0.0.0.0:5000 nohup dotnet NINA.Polaris.dll > polaris.log 2>&1 &
  sleep 2
  if pgrep -f "dotnet.*NINA.Polaris" > /dev/null; then
    echo "Restarted via nohup. Logs: ~/polaris/polaris.log"
  else
    echo "Process didn't stay up — check ~/polaris/polaris.log"
    tail -20 polaris.log
    exit 1
  fi
fi
'@
$restartScript = $restartScript.Replace("REMOTE_PATH", $RemotePath)
ssh "$PiUser@$PiHost" $restartScript
if ($LASTEXITCODE -ne 0) { throw "Restart failed" }

Write-Host ""
Write-Host "==> Done. UI: http://${PiHost}:5000" -ForegroundColor Green
