<#
.SYNOPSIS
  Stamp source-file license headers for the AGPL-3.0 relicensing proposal.

.DESCRIPTION
  Two header templates are applied:
    * MPL Exhibit A (with the N.I.N.A. copyright) -> files DERIVED from N.I.N.A.
    * AGPL-3.0                                     -> files ORIGINAL to Polaris.

  Idempotent: any file that already contains "Mozilla Public License" or
  "GNU Affero" in its first lines is skipped. Generated/designer files are
  skipped.

  IMPORTANT: the DERIVED vs ORIGINAL split below is a DIRECTORY-LEVEL DEFAULT
  and a guess. You MUST verify provenance per file before trusting it. Removing
  or omitting the MPL notice on N.I.N.A.-derived code is an MPL violation.

.PARAMETER Apply
  Actually write the headers. Without it, runs as a dry-run (lists files only).
#>
param([switch]$Apply)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Directories whose .cs files are (assumed) derived from N.I.N.A. -> MPL header.
# VERIFY THIS LIST. Anything not listed here under src/ gets the AGPL header.
$derivedDirs = @(
  'src\NINA.Core.Portable',
  'src\NINA.Image.Portable',
  'src\NINA.Guider.Portable'
)

$mplHeader = @'
// Copyright (C) 2016-2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This file is derived from N.I.N.A. - Nighttime Imaging 'N' Astronomy.
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// As part of N.I.N.A. Polaris this file is additionally available under the
// GNU Affero General Public License v3.0 (see LICENSE.txt and NOTICE), at the
// recipient's option, pursuant to MPL-2.0 section 3.3.

'@

$agplHeader = @'
// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
// for more details. You should have received a copy of the license along with
// this program. If not, see <https://www.gnu.org/licenses/>.

'@

function Is-Derived($relPath) {
  foreach ($d in $derivedDirs) {
    if ($relPath -like "$d\*") { return $true }
  }
  return $false
}

$scanDirs = @((Join-Path $root 'src'), (Join-Path $root 'tests')) | Where-Object { Test-Path $_ }
$files = Get-ChildItem -Path $scanDirs -Recurse -Filter *.cs -File |
  Where-Object {
    $_.FullName -notmatch '\\(obj|bin)\\' -and
    $_.Name -notmatch '\.(g|Designer|AssemblyInfo|GlobalUsings)\.cs$' -and
    $_.Name -notmatch 'AssemblyInfo\.cs$'
  }

$stampedMpl = 0; $stampedAgpl = 0; $skipped = 0
foreach ($f in $files) {
  $rel = $f.FullName.Substring($root.Length + 1)
  $head = (Get-Content -LiteralPath $f.FullName -TotalCount 6 -ErrorAction SilentlyContinue) -join "`n"
  if ($head -match 'Mozilla Public License' -or $head -match 'GNU Affero') {
    $skipped++; continue
  }
  $derived = Is-Derived $rel
  $hdr = if ($derived) { $mplHeader } else { $agplHeader }
  $tag = if ($derived) { 'MPL ' } else { 'AGPL' }
  Write-Host "[$tag] $rel"
  if ($Apply) {
    $body = Get-Content -LiteralPath $f.FullName -Raw
    Set-Content -LiteralPath $f.FullName -Value ($hdr + $body) -NoNewline -Encoding utf8
  }
  if ($derived) { $stampedMpl++ } else { $stampedAgpl++ }
}

Write-Host ""
Write-Host ("{0}: MPL={1}  AGPL={2}  already-headed(skipped)={3}" -f `
  ($(if ($Apply) { 'APPLIED' } else { 'DRY-RUN' })), $stampedMpl, $stampedAgpl, $skipped)
if (-not $Apply) { Write-Host "Re-run with -Apply to write headers." }
