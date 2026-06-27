# N.I.N.A. Polaris
# Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
# Licensed under the GNU Affero General Public License v3.0 or later.
#
# Assemble the Qualcomm AI Runtime (QAIRT, formerly QNN) aarch64 runtime into
# external/qairt/aarch64/{bin,lib,dsp} so the linux-arm64 publish bundles it at
# /opt/polaris/qairt for NPU-accelerated GraXpert AI on Qualcomm SBCs (Radxa
# Dragon Q6A / QCS6490, Hexagon V68). The runtime is proprietary Qualcomm vendor
# code (see licenses/QAIRT-LICENSE.txt) and is NOT committed to the repo.
#
# There is no public download for the device-matched 2.45 runtime (the only
# public x86 SDK is 2.31, version-locked against the board's 2.45 firmware), so
# this COPIES from a source root you provide — typically the board's own QAIRT
# tree (scp it over) or the matching 2.45 SDK.
#
# Usage:
#   ./scripts/fetch-qairt.ps1 -Source C:\path\to\qairt
param([Parameter(Mandatory = $true)][string]$Source)

$ErrorActionPreference = "Stop"
if (-not (Test-Path -PathType Container $Source)) {
    throw "Source root '$Source' is not a directory."
}

$dest = Join-Path $PSScriptRoot "..\external\qairt\aarch64"
foreach ($sub in "bin", "lib", "dsp") {
    New-Item -ItemType Directory -Force -Path (Join-Path $dest $sub) | Out-Null
}

$plan = @(
    @{ Name = "qnn-net-run";                     Sub = "bin"; Prefer = "aarch64" },
    @{ Name = "libQnnHtp.so";                    Sub = "lib"; Prefer = "aarch64" },
    @{ Name = "libQnnSystem.so";                 Sub = "lib"; Prefer = "aarch64" },
    @{ Name = "libQnnHtpV68Stub.so";             Sub = "lib"; Prefer = "aarch64" },
    @{ Name = "libQnnHtpPrepare.so";             Sub = "lib"; Prefer = "aarch64" },
    @{ Name = "libQnnHtpNetRunExtensions.so";    Sub = "lib"; Prefer = "aarch64" },
    @{ Name = "libQnnHtpV68Skel.so";             Sub = "dsp"; Prefer = "hexagon-v68" }
)

$missing = 0
Write-Host "==> Assembling QAIRT aarch64 runtime into $dest"
foreach ($item in $plan) {
    $hits = Get-ChildItem -Path $Source -Recurse -File -Filter $item.Name -ErrorAction SilentlyContinue
    $pick = $hits | Where-Object { $_.FullName -match $item.Prefer } | Select-Object -First 1
    if (-not $pick) { $pick = $hits | Select-Object -First 1 }
    if (-not $pick) {
        Write-Warning "  MISSING: $($item.Name) (not found under $Source)"
        $missing++
        continue
    }
    Copy-Item -Force $pick.FullName (Join-Path (Join-Path $dest $item.Sub) $item.Name)
    Write-Host "  $($item.Sub)/$($item.Name)  <-  $($pick.FullName)"
}

if ($missing -ne 0) {
    Write-Warning "Some files were not found. The NPU path needs all of them; point -Source at a complete 2.45 aarch64 runtime."
    exit 2
}
Write-Host "Done. The linux-arm64 publish will now bundle /opt/polaris/qairt."
