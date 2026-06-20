<#
.SYNOPSIS
    Produce the starrem2k13 (U2NETP) star-removal ONNX model for Polaris.

.DESCRIPTION
    starrem2k13 (https://github.com/code2k13/starrem2k13) is an MIT-licensed
    U2NETP star-removal model. Unlike StarNet it ships a first-class ONNX
    exporter (export_to_onnx.py), so this is a thin orchestrator:

      1. clone (or reuse) the code2k13/starrem2k13 checkout,
      2. fetch the trained weights from the repo's GitHub Releases,
      3. run export_to_onnx.py inside a TensorFlow Docker image
         (opset 13, fixed 512x512 single-channel input),
      4. copy the resulting model.onnx into Polaris' bundled models tree at
         wwwroot/graxpert/models/starrem2k13-ai-models/<Version>/model.onnx

    Model facts the Polaris pipeline relies on (see
    scripts/convert-starrem2k13-onnx.md):
      - input  : float32 [1,512,512] (ONE channel; RGB is run per-channel)
      - tile   : 512, normalization divide-by-382 on 8-bit
                 (Polaris feeds stretched*255/382, reads output*382/255)
      - output : starless image directly (not a mask)
      - license: MIT (code AND weights) -> may be bundled by default.

.PARAMETER Source
    Path to a code2k13/starrem2k13 checkout. If empty, the script clones it
    into a temp dir.

.PARAMETER WeightsUrl
    Direct URL to the trained weights archive/file in the repo's Releases.
    If empty, the script prints the releases page and stops so you can pick
    the current asset URL.

.PARAMETER Version
    Version folder under starrem2k13-ai-models (default 1.0.0).

.PARAMETER ModelsDir
    Override the destination models dir (default: the repo's bundled tree).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\convert-starrem2k13-onnx.ps1 -WeightsUrl https://github.com/code2k13/starrem2k13/releases/download/v1.0/weights.zip
#>
[CmdletBinding()]
param(
    [string]$Source     = "",
    [string]$WeightsUrl = "",
    [string]$Version    = "1.0.0",
    [string]$ModelsDir  = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ModelsDir) {
    $ModelsDir = Join-Path $repoRoot "src\NINA.Polaris\wwwroot\graxpert\models\starrem2k13-ai-models\$Version"
}

# 1) Get the source checkout.
$cleanupSource = $false
if (-not $Source) {
    $Source = Join-Path $env:TEMP "starrem2k13-src"
    if (-not (Test-Path $Source)) {
        Write-Host "Cloning code2k13/starrem2k13 into $Source ..."
        git clone --depth 1 https://github.com/code2k13/starrem2k13.git $Source
        $cleanupSource = $true
    } else {
        Write-Host "Reusing existing checkout at $Source"
    }
}
if (-not (Test-Path (Join-Path $Source "export_to_onnx.py"))) {
    throw "export_to_onnx.py not found under $Source -- is this a starrem2k13 checkout?"
}

# FAST PATH: the upstream repo ships a prebuilt weights/model.onnx (U2NETP is
# tiny, ~2.6 MB). If it's there, just copy it -- no Docker / export needed.
$prebuilt = Join-Path $Source "weights\model.onnx"
if (Test-Path $prebuilt) {
    New-Item -ItemType Directory -Force -Path $ModelsDir | Out-Null
    $target = Join-Path $ModelsDir "model.onnx"
    Copy-Item -Path $prebuilt -Destination $target -Force
    Write-Host "Copied prebuilt model -> $target" -ForegroundColor Green
    Write-Host "Restart Polaris (or POST /api/onnx/rescan) to pick up the starrem2k13 family."
    return
}

# 2) Otherwise (re-)export from weights. The repo keeps the trained weights out
#    of git and publishes them as a Release asset; we can't guess the asset URL,
#    so require it explicitly the first time.
$weightsDir = Join-Path $Source "weights"
$haveWeights = (Test-Path $weightsDir) -and `
    ((Get-ChildItem -Path $weightsDir -File -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0)
if (-not $haveWeights) {
    if (-not $WeightsUrl) {
        Write-Host ""
        Write-Host "No weights found under $weightsDir." -ForegroundColor Yellow
        Write-Host "Download the trained weights from the releases page and re-run with -WeightsUrl:"
        Write-Host "  https://github.com/code2k13/starrem2k13/releases"
        throw "weights missing"
    }
    New-Item -ItemType Directory -Force -Path $weightsDir | Out-Null
    $dest = Join-Path $weightsDir ([System.IO.Path]::GetFileName($WeightsUrl.Split('?')[0]))
    Write-Host "Downloading weights -> $dest"
    Invoke-WebRequest -Uri $WeightsUrl -OutFile $dest
    if ($dest -match '\.zip$') {
        Write-Host "Expanding $dest"
        Expand-Archive -Path $dest -DestinationPath $weightsDir -Force
    }
}

# 3) Run the exporter inside a TF Docker image (no local Python needed).
$srcUnix = ($Source -replace '\\','/') -replace '^([A-Za-z]):','/$1'.ToLower()
Write-Host "Running export_to_onnx.py in Docker (tensorflow/tensorflow:2.13.0) ..."
$pip = "pip install --quiet tf2onnx onnx pillow numpy"
$run = "$pip; python export_to_onnx.py"
docker run --rm -v "${Source}:/work" -w /work tensorflow/tensorflow:2.13.0 bash -lc $run

# 4) Locate the produced model.onnx and copy it into the bundled tree.
$produced = Get-ChildItem -Path $Source -Filter "*.onnx" -File -Recurse |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $produced) { throw "export_to_onnx.py did not produce a .onnx file under $Source" }

New-Item -ItemType Directory -Force -Path $ModelsDir | Out-Null
$target = Join-Path $ModelsDir "model.onnx"
Copy-Item -Path $produced.FullName -Destination $target -Force
Write-Host ""
Write-Host "Done. Wrote $target" -ForegroundColor Green
Write-Host "Restart Polaris (or POST /api/onnx/rescan) to pick up the starrem2k13 family."

if ($cleanupSource) {
    Write-Host "Source checkout left at $Source (delete it manually if you don't need it)."
}
