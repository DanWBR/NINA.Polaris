<#
.SYNOPSIS
    Produce the nox star-removal ONNX models (colour + gray) for Polaris.

.DESCRIPTION
    nox (https://github.com/charvey2718/nox) is an MIT-licensed star-removal
    network (StarNet-like encoder/decoder, LayerNorm, GAN + perceptual losses,
    ~54M params). It ships trained Keras weights: generator_color.h5 and
    generator_gray.h5. This script rebuilds the generator from nox.py and
    exports each to a fixed 512x512 ONNX inside a TensorFlow Docker image.

    Model facts the Polaris pipeline relies on (verified against the ONNX):
      - input   : gen_input_image, float32 [1,512,512,C] (C=3 colour, 1 gray)
      - output  : [1,512,512,C], subtractive (input - relu(decode)), [-1,1] domain
      - tile    : 512; normalization = feed (2x-1), read (y+1)/2
      - license : MIT (code AND weights) -> may be bundled by default.

.PARAMETER WeightsDir
    Folder holding generator_color.h5 and generator_gray.h5 (from nox releases).

.PARAMETER Version
    Version folder under nox-*-ai-models (default 1.0.0).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\convert-nox-onnx.ps1 -WeightsDir C:\Users\me\Downloads\nox\v1.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$WeightsDir,
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$modelsRoot = Join-Path $repoRoot "src\NINA.Polaris\wwwroot\graxpert\models"

$color = Join-Path $WeightsDir "generator_color.h5"
$gray  = Join-Path $WeightsDir "generator_gray.h5"
if (-not (Test-Path $color)) { throw "generator_color.h5 not found in $WeightsDir" }
if (-not (Test-Path $gray))  { throw "generator_gray.h5 not found in $WeightsDir" }

$build = Join-Path $env:TEMP "nox-build"
New-Item -ItemType Directory -Force -Path $build | Out-Null
Copy-Item $color (Join-Path $build "generator_color.h5") -Force
Copy-Item $gray  (Join-Path $build "generator_gray.h5") -Force

# Inline generator (matches nox.py) + tf2onnx export, fixed 512.
@'
import os, sys
os.environ["TF_CPP_MIN_LOG_LEVEL"]="3"
import tensorflow as tf, tf2onnx
nch=int(sys.argv[1]); h5=sys.argv[2]; out=sys.argv[3]
def generator(n):
    tf.keras.backend.clear_session(); L=[]
    f=[64,128,256,512,512,512,512,512,512,512,512,512,256,128,64]
    inp=tf.keras.layers.Input(shape=(None,None,n),name="gen_input_image")
    for i in range(1+len(f)):
        if i==0:
            L.append(tf.keras.layers.Conv2D(f[0],4,strides=(2,2),padding="same")(inp))
        elif 1<=i<=7:
            r=tf.keras.layers.LeakyReLU(alpha=0.2)(L[-1])
            c=tf.keras.layers.Conv2D(f[i],4,strides=(2,2),padding="same")(r)
            L.append(tf.keras.layers.LayerNormalization()(c))
        elif 8<=i<=14:
            r=tf.keras.layers.ReLU()(L[-1] if i==8 else tf.concat([L[-1],L[15-i]],axis=3))
            d=tf.keras.layers.Conv2DTranspose(f[i],4,strides=(2,2),padding="same")(r)
            L.append(tf.keras.layers.LayerNormalization()(d))
        else:
            r=tf.keras.layers.ReLU()(tf.concat([L[-1],L[0]],axis=3))
            d=tf.keras.layers.Conv2DTranspose(n,4,strides=(2,2),padding="same")(r)
            o=tf.math.subtract(inp,tf.keras.layers.ReLU()(d))
    return tf.keras.Model(inputs=inp,outputs=o,name="generator")
m=generator(nch); m.load_weights(h5); print("PARAMS",m.count_params())
spec=(tf.TensorSpec((1,512,512,nch),tf.float32,name="gen_input_image"),)
tf2onnx.convert.from_keras(m,input_signature=spec,opset=13,output_path=out)
print("exported",out)
'@ | Set-Content -Encoding ascii (Join-Path $build "export_nox.py")

Write-Host "Exporting nox ONNX in Docker (tensorflow/tensorflow:2.15.0) ..."
docker run --rm -v "${build}:/work" -w /work tensorflow/tensorflow:2.15.0 bash -lc `
    "pip install --quiet tf2onnx onnx 2>/dev/null; python export_nox.py 3 generator_color.h5 nox_color.onnx; python export_nox.py 1 generator_gray.h5 nox_gray.onnx"

foreach ($pair in @(@("nox_color.onnx","nox-color-ai-models"), @("nox_gray.onnx","nox-gray-ai-models"))) {
    $src = Join-Path $build $pair[0]
    if (-not (Test-Path $src)) { throw "export did not produce $($pair[0])" }
    $dst = Join-Path $modelsRoot (Join-Path $pair[1] $Version)
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Copy-Item $src (Join-Path $dst "model.onnx") -Force
    Write-Host "Wrote $(Join-Path $dst 'model.onnx')" -ForegroundColor Green
}
Write-Host "Restart Polaris (or POST /api/onnx/rescan) to pick up the nox-color/nox-gray families."
