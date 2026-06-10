# N.I.N.A. Polaris
# Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
# Licensed under the GNU Affero General Public License v3.0 or later.
#
# Fetch the Rockchip RKNPU2 runtime (librknnrt.so, aarch64) into
# external/rknpu/aarch64/ so the linux-arm64 publish bundles it for
# NPU-accelerated GraXpert AI on RK3588 boards. The library is proprietary
# Rockchip vendor code (see licenses/RKNPU-LICENSE.txt) and is NOT committed.
param([string]$Version = "v2.3.2")

$ErrorActionPreference = "Stop"
$src = "https://github.com/airockchip/rknn-toolkit2/raw/$Version/rknpu2/runtime/Linux/librknn_api/aarch64/librknnrt.so"
$destDir = Join-Path $PSScriptRoot "..\external\rknpu\aarch64"
$dest = Join-Path $destDir "librknnrt.so"

New-Item -ItemType Directory -Force -Path $destDir | Out-Null
Write-Host "Fetching librknnrt.so ($Version) -> $dest"
Invoke-WebRequest -Uri $src -OutFile $dest
Get-Item $dest | Select-Object FullName, Length
Write-Host "Done. The linux-arm64 publish will now bundle librknnrt.so."
