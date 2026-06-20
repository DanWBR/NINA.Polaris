<#
.SYNOPSIS
    Convert the DanWBR/starnet TensorFlow-1 checkpoint to ONNX for Polaris.

.DESCRIPTION
    Orchestrates the one-time, offline SN-1 conversion on Windows:
      1. creates a Python 3.7 venv (TF 1.15 only supports 3.7),
      2. installs a pinned, known-good TF1 + tf2onnx stack,
      3. runs the fork's export.py  -> starnet_generator.pb,
      4. runs tf2onnx              -> model.onnx (fixed 256x256x3, RGB),
      5. copies model.onnx into Polaris' bundled models tree.

    The model facts (input X:0 [None,256,256,3], output
    generator/g_deconv7/Sub:0, [0,1] in/out, tile 256) are documented in
    scripts/convert-starnet-onnx.md. Weights are CC BY-NC-SA 4.0
    (NonCommercial) -- attribute when bundling.

.PARAMETER StarnetDir
    Path to the DanWBR/starnet checkout (has model.ckpt.*, export.py, gen_sub.txt).

.PARAMETER Version
    Version folder under starnet-ai-models (default 1.0.0).

.PARAMETER Python
    A Python 3.7 launcher/exe. Default tries the py launcher: "py -3.7".

.PARAMETER SkipInstall
    Reuse an existing venv without re-running pip (faster on re-runs).

.EXAMPLE
    pwsh -File scripts/convert-starnet-onnx.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\convert-starnet-onnx.ps1 -StarnetDir D:\src\starnet
#>
[CmdletBinding()]
param(
    [string]$StarnetDir = "C:\Users\danie\source\repos\DanWBR\starnet",
    [string]$Version    = "1.0.0",
    [string]$ModelsDir  = "",
    [string]$Python     = "",
    [string]$InputName  = "X:0",
    [string]$OutputName = "generator/g_deconv7/Sub:0",
    [int]   $Opset      = 13,
    [switch]$SkipInstall,
    [switch]$Docker,
    [string]$Image      = "python:3.7-slim"
)

$ErrorActionPreference = "Stop"
function Step($m) { Write-Host "`n==== $m ====" -ForegroundColor Cyan }
function Fail($m) { Write-Error $m; exit 1 }

# Resolve the default models dir relative to this script. Done in the body
# (not the param default) because $PSScriptRoot is empty when the script is
# dot-sourced or pasted into the console.
if ([string]::IsNullOrWhiteSpace($ModelsDir)) {
    $scriptDir = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($scriptDir) -and $PSCommandPath) {
        $scriptDir = Split-Path -Parent $PSCommandPath
    }
    if ([string]::IsNullOrWhiteSpace($scriptDir)) { $scriptDir = (Get-Location).Path }
    $ModelsDir = Join-Path $scriptDir "..\src\NINA.Polaris\wwwroot\graxpert\models\starnet-ai-models"
}

# --- validate inputs --------------------------------------------------------
Step "Checking the StarNet checkout"
if (-not (Test-Path $StarnetDir))                       { Fail "StarnetDir not found: $StarnetDir" }
if (-not (Test-Path (Join-Path $StarnetDir "export.py"))) { Fail "export.py not found in $StarnetDir" }
if (-not (Test-Path (Join-Path $StarnetDir "model.ckpt.index"))) {
    Fail "model.ckpt.* not found in $StarnetDir (download the weights first; see wherearemyweights.txt)"
}
Write-Host "  OK: $StarnetDir"

# --- Docker path (no local Python 3.7 needed) ------------------------------
# Runs the whole convert inside a throwaway python:3.7-slim container. The
# StarNet checkout is mounted at /work and the models tree at /out, so the
# resulting model.onnx lands directly in the Polaris bundle.
if ($Docker) {
    Step "Converting via Docker ($Image)"
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        Fail "docker not found on PATH. Install Docker Desktop, or drop -Docker and use a local Python 3.7."
    }
    # The engine must actually be running (and in Linux-containers mode).
    & docker info 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Fail "Docker is installed but the engine isn't reachable. Start Docker Desktop (wait for the whale icon to settle), make sure it's in LINUX containers mode, then re-run with -Docker."
    }
    $starAbs   = (Resolve-Path $StarnetDir).Path
    New-Item -ItemType Directory -Force -Path $ModelsDir | Out-Null
    $modelsAbs = (Resolve-Path $ModelsDir).Path

    # Single-line && chain (no newlines/set-e -- avoids CRLF + errexit
    # fragility): each step must succeed before the next runs, so a failure
    # stops here and surfaces its real error instead of cascading into
    # "starnet_generator.pb not found".
    $inner = "set -ex && " +
        "pip install --no-cache-dir 'tensorflow==1.15.*' 'numpy==1.18.5' 'protobuf==3.19.6' 'onnx==1.10.2' 'tf2onnx==1.9.3' && " +
        "python export.py && " +
        "ls -la starnet_generator.pb && " +
        "python -m tf2onnx.convert --graphdef starnet_generator.pb --inputs $InputName --outputs $OutputName --opset $Opset --output model.onnx && " +
        "mkdir -p '/out/$Version' && " +
        "cp model.onnx '/out/$Version/model.onnx' && " +
        "echo container-wrote-/out/$Version/model.onnx"

    & docker run --rm `
        -v "${starAbs}:/work" `
        -v "${modelsAbs}:/out" `
        -w /work `
        $Image `
        sh -c $inner
    if ($LASTEXITCODE -ne 0) { Fail "Docker conversion failed (exit $LASTEXITCODE)." }

    $destFile = Join-Path (Join-Path $ModelsDir $Version) "model.onnx"
    if (-not (Test-Path $destFile)) { Fail "Expected $destFile not produced." }
    $sizeMB = [math]::Round((Get-Item $destFile).Length / 1048576.0, 1)
    Write-Host "`nDone. StarNet ONNX in place:" -ForegroundColor Green
    Write-Host "  $destFile ($sizeMB MB)" -ForegroundColor Green
    Write-Host "Next: start Polaris, POST /api/onnx/rescan, check GET /api/onnx/manifest for 'starnet'/$Version." -ForegroundColor Green
    exit 0
}

# --- locate / create the venv ----------------------------------------------
$venv   = Join-Path $StarnetDir ".onnxvenv"
$venvPy = Join-Path $venv "Scripts\python.exe"

if (-not (Test-Path $venvPy)) {
    Step "Creating Python 3.7 venv at $venv"
    if ($Python -ne "") {
        & $Python -m venv $venv
    } else {
        # py launcher; TF 1.15 needs CPython 3.7
        & py -3.7 -m venv $venv
    }
    if (-not (Test-Path $venvPy)) {
        Fail "venv creation failed: no CPython 3.7 (TF 1.15 requires 3.7). Easiest fix: re-run with -Docker (uses python:3.7-slim, no local Python). Or install CPython 3.7 / pass -Python <path to python3.7.exe>."
    }
} else {
    Write-Host "  Reusing existing venv: $venv"
}

# --- install the pinned TF1 + tf2onnx stack --------------------------------
if (-not $SkipInstall) {
    Step "Installing TF1 + tf2onnx (pinned; adjust here if pip resolves a conflict)"
    & $venvPy -m pip install --upgrade "pip<24" "setuptools<66" "wheel"
    # tensorflow 1.15 needs old numpy/protobuf; tf2onnx 1.9.x still supports tf1.
    & $venvPy -m pip install `
        "tensorflow==1.15.0" `
        "numpy==1.18.5" `
        "protobuf==3.19.6" `
        "onnx==1.10.2" `
        "tf2onnx==1.9.3" `
        "onnxruntime" `
        "Pillow" "tifffile"
    if ($LASTEXITCODE -ne 0) { Fail "pip install failed -- tweak the pins in this script and re-run with -SkipInstall off." }
} else {
    Write-Host "  -SkipInstall: using whatever is already in the venv"
}

# --- freeze the generator subgraph (export.py runs from the repo) -----------
Step "Freezing the generator graph (export.py)"
$pb = Join-Path $StarnetDir "starnet_generator.pb"
Push-Location $StarnetDir
try {
    & $venvPy "export.py"
    if ($LASTEXITCODE -ne 0) { Fail "export.py failed." }
} finally { Pop-Location }
if (-not (Test-Path $pb)) { Fail "Expected $pb was not produced by export.py." }
Write-Host "  OK: $pb"

# --- GraphDef -> ONNX -------------------------------------------------------
Step "Converting to ONNX (tf2onnx)"
$onnx = Join-Path $StarnetDir "model.onnx"
& $venvPy -m tf2onnx.convert `
    --graphdef $pb `
    --inputs   $InputName `
    --outputs  $OutputName `
    --opset    $Opset `
    --output   $onnx
if ($LASTEXITCODE -ne 0) { Fail "tf2onnx conversion failed." }
if (-not (Test-Path $onnx)) { Fail "tf2onnx did not produce $onnx." }
Write-Host "  OK: $onnx"

# --- copy into the Polaris bundled models tree ------------------------------
Step "Installing into Polaris"
$dest = Join-Path $ModelsDir $Version
New-Item -ItemType Directory -Force -Path $dest | Out-Null
$destFile = Join-Path $dest "model.onnx"
Copy-Item $onnx $destFile -Force
$sizeMB = [math]::Round((Get-Item $destFile).Length / 1048576.0, 1)
Write-Host "  Copied -> $destFile ($sizeMB MB)" -ForegroundColor Green

Step "Done"
Write-Host @"
StarNet ONNX is in place:
  $destFile

Next:
  1. Start Polaris, then POST /api/onnx/rescan and check GET /api/onnx/manifest
     for the 'starnet' family / $Version entry.
  2. Sanity-check against the fork's rgb_test5.tif_starless.tif (see the .md).
  3. Weights are CC BY-NC-SA 4.0 (NonCommercial) -- add attribution to
     3rd-party-licenses.txt + the in-app About list before bundling.
  4. Tell Claude 'model is in place' to wire SN-2 (StarRemovalPipeline) + SN-3.
"@ -ForegroundColor Green
