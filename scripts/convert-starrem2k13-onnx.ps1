<#
.SYNOPSIS
    Produce the starrem2k13 star-removal ONNX model for Polaris.

.DESCRIPTION
    starrem2k13 (https://github.com/code2k13/starrem2k13) is an MIT-licensed
    star-removal model. We use the LARGER pix2pix-style U-Net from the pinned
    commit 0398ce05 (~31M params, the version that matches the published
    trained weights) -- NOT the tiny 646k-param U2NETP the repo's main branch
    later switched to (that one removes stars poorly: rings + washed nebula).

    This script:
      1. fetches model.py from the pinned commit,
      2. loads the trained weights (a TensorFlow checkpoint, weights/weights.*),
      3. exports to ONNX (opset 13) inside a TensorFlow Docker image,
      4. copies model.onnx into Polaris' bundled models tree at
         wwwroot/graxpert/models/starrem2k13-ai-models/<Version>/model.onnx

    Model facts the Polaris pipeline relies on (see
    scripts/convert-starrem2k13-onnx.md), verified against the real ONNX:
      - input   : args_0, float32 [1,512,512] (ONE channel; RGB run per-channel)
      - output  : [1,512,512,1], relu (the starless image directly, not a mask)
      - tile    : 512, normalization = 8-bit /512
                  (Polaris feeds stretched*255/512, reads output*512/255)
      - license : MIT (code AND weights) -> may be bundled by default.

.PARAMETER WeightsDir
    Folder holding the trained TF checkpoint (checkpoint + weights.index +
    weights.data-*). Download it from the project's GitHub Releases.

.PARAMETER Version
    Version folder under starrem2k13-ai-models (default 1.0.0).

.PARAMETER ModelsDir
    Override the destination models dir (default: the repo's bundled tree).

.PARAMETER Commit
    Pinned upstream commit to take model.py from (default 0398ce05...).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\convert-starrem2k13-onnx.ps1 -WeightsDir C:\Users\me\Downloads\weights
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$WeightsDir,
    [string]$Version   = "1.0.0",
    [string]$ModelsDir = "",
    [string]$Commit    = "0398ce05bdd766e93ae5c728e6965ff8c5ce6c57"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ModelsDir) {
    $ModelsDir = Join-Path $repoRoot "src\NINA.Polaris\wwwroot\graxpert\models\starrem2k13-ai-models\$Version"
}

if (-not (Test-Path $WeightsDir)) { throw "WeightsDir not found: $WeightsDir" }
$idx = Get-ChildItem -Path $WeightsDir -Filter "weights.index" -ErrorAction SilentlyContinue
if (-not $idx) { throw "No 'weights.index' in $WeightsDir -- expected a TF checkpoint (checkpoint + weights.index + weights.data-*)." }

# Stage a build dir: model.py (pinned commit) + the checkpoint + export.py.
$build = Join-Path $env:TEMP "starrem2k13-build"
New-Item -ItemType Directory -Force -Path $build | Out-Null
$modelUrl = "https://raw.githubusercontent.com/code2k13/starrem2k13/$Commit/model.py"
Write-Host "Fetching model.py from $Commit ..."
Invoke-WebRequest -Uri $modelUrl -OutFile (Join-Path $build "model.py")

$buildWeights = Join-Path $build "weights"
New-Item -ItemType Directory -Force -Path $buildWeights | Out-Null
Copy-Item -Path (Join-Path $WeightsDir "*") -Destination $buildWeights -Force

@'
import tensorflow as tf, tf2onnx, model
G = model.Generator()
print("PARAMS", G.count_params())
G.load_weights("weights/weights")
print("weights loaded")
spec = (tf.TensorSpec((1, 512, 512), tf.float32),)
tf2onnx.convert.from_keras(G, input_signature=spec, opset=13, output_path="model.onnx")
print("exported model.onnx")
'@ | Set-Content -Encoding ascii (Join-Path $build "export.py")

Write-Host "Exporting ONNX in Docker (tensorflow/tensorflow:2.15.0) ..."
$buildUnix = $build  # docker on Windows accepts Windows paths in -v
docker run --rm -v "${buildUnix}:/work" -w /work tensorflow/tensorflow:2.15.0 `
    bash -lc "pip install --quiet tf2onnx onnx 2>/dev/null; python export.py"

$produced = Join-Path $build "model.onnx"
if (-not (Test-Path $produced)) { throw "export.py did not produce model.onnx" }

New-Item -ItemType Directory -Force -Path $ModelsDir | Out-Null
$target = Join-Path $ModelsDir "model.onnx"
Copy-Item -Path $produced -Destination $target -Force
Write-Host ""
Write-Host "Done. Wrote $target ($([math]::Round((Get-Item $target).Length/1MB,1)) MB)" -ForegroundColor Green
Write-Host "Restart Polaris (or POST /api/onnx/rescan) to pick up the starrem2k13 family."
