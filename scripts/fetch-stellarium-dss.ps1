<#
.SYNOPSIS
  Fetch the DSS Color HiPS survey for OFFLINE sky imagery (Windows mirror
  of fetch-stellarium-dss.sh).

.DESCRIPTION
  Downloads the DSS Color HiPS pyramid into the bundled skydata dir so the
  Stellarium Web Engine sky map shows real nebulae/galaxies with NO network
  at use time (ASIAIR style). sky-bridge.js auto-detects the local bundle
  and prefers it over the remote CDS Strasbourg URL.

  Size scales ~4x per order. Pick the ceiling that fits your SBC card:
    order 3  ~1020 tiles  ~30 MB   big objects recognisable
    order 4  ~4100 tiles  ~110 MB  most DSOs recognisable (good value)
    order 5  ~16400 tiles ~400 MB  detailed, ASIAIR-like
    order 6  ~65500 tiles ~1.5 GB  overkill for framing

  Resumable: existing tiles are skipped.

  Attribution: DSS Color, STScI/NASA, HEALPixed by CDS Strasbourg.

.PARAMETER MaxOrder
  Highest HEALPix order to fetch (default 4).

.PARAMETER Parallel
  Concurrent downloads (default 8). Requires PowerShell 7+ for
  ForEach-Object -Parallel; on Windows PowerShell 5.1 it runs serially.

.EXAMPLE
  pwsh scripts/fetch-stellarium-dss.ps1 -MaxOrder 4
#>
param(
    [int]$MaxOrder = 4,
    [int]$Parallel = 8
)
$ErrorActionPreference = 'Stop'
$remote = 'https://alasky.cds.unistra.fr/DSS/DSSColor'
$dest = Join-Path $PSScriptRoot '..\src\NINA.Polaris\wwwroot\sky\data\skydata\surveys\dss'
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Write-Host "DSS Color -> $dest  (max order $MaxOrder)"

function Get-Tile($url, $out) {
    if ((Test-Path $out) -and (Get-Item $out).Length -gt 0) { return }
    New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
    try {
        Invoke-WebRequest -Uri $url -OutFile $out -TimeoutSec 60 -MaximumRetryCount 3 `
            -RetryIntervalSec 1 -ErrorAction Stop
    } catch {
        # A missing tile in a sparse survey is normal; skip soft.
        if (Test-Path $out) { Remove-Item $out -Force -ErrorAction SilentlyContinue }
    }
}

foreach ($meta in @('properties', 'Moc.fits')) {
    Get-Tile "$remote/$meta" (Join-Path $dest $meta)
}

for ($order = 0; $order -le $MaxOrder; $order++) {
    $ntiles = 12 * [math]::Pow(4, $order)
    Write-Host ("  order {0}: {1} tiles" -f $order, $ntiles)
    if ($order -le 3) {
        Get-Tile "$remote/Norder$order/Allsky.jpg" (Join-Path $dest "Norder$order\Allsky.jpg")
    }
    $jobs = for ($npix = 0; $npix -lt $ntiles; $npix++) {
        $dir = [math]::Floor($npix / 10000) * 10000
        [pscustomobject]@{
            Url = "$remote/Norder$order/Dir$dir/Npix$npix.jpg"
            Out = (Join-Path $dest "Norder$order\Dir$dir\Npix$npix.jpg")
        }
    }
    if ($PSVersionTable.PSVersion.Major -ge 7) {
        $jobs | ForEach-Object -ThrottleLimit $Parallel -Parallel {
            $f = ${function:Get-Tile}
            # Re-declare in the parallel runspace.
            function Get-Tile($url, $out) {
                if ((Test-Path $out) -and (Get-Item $out).Length -gt 0) { return }
                New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
                try { Invoke-WebRequest -Uri $url -OutFile $out -TimeoutSec 60 -ErrorAction Stop }
                catch { if (Test-Path $out) { Remove-Item $out -Force -ErrorAction SilentlyContinue } }
            }
            Get-Tile $_.Url $_.Out
        }
    } else {
        foreach ($j in $jobs) { Get-Tile $j.Url $j.Out }
    }
}

$total = (Get-ChildItem -Recurse -File $dest | Measure-Object).Count
Write-Host "Done. $total files in $dest"
Write-Host "Commit with Git LFS (already tracked in .gitattributes):"
Write-Host "  git add $dest; git commit -m 'skydata: bundle DSS Color HiPS (order $MaxOrder)'"
