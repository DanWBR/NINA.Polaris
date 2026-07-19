<#
  Stage the llama.cpp Android arm64 release into the plugin's jniLibs so the app
  packages an executable llama-server (+ its .so deps). Run before
  `npx cap sync android` / the Android build; the binaries are never committed.

    ./fetch-llama.ps1 [-Tag b10058]

  The eval (canopus-eval/MOBILE.md) validated b10058. Bump the tag deliberately.
#>
param(
  [string]$Tag = "b10058",
  [string]$OutDir = (Join-Path $PSScriptRoot "..\android\src\main\jniLibs\arm64-v8a")
)
$ErrorActionPreference = "Stop"

$asset = "llama-$Tag-bin-android-arm64.zip"
$url = "https://github.com/ggml-org/llama.cpp/releases/download/$Tag/$asset"
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "polaris-llama-$Tag"
$ex = Join-Path $tmp "extracted"
New-Item -ItemType Directory -Force -Path $ex, $OutDir | Out-Null
$zip = Join-Path $tmp $asset

Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile $zip
Expand-Archive -Path $zip -DestinationPath $ex -Force

# All shared libs ship as-is; the server executable is renamed lib*.so so
# Android puts it in nativeLibraryDir (the only exec-allowed place, W^X).
Get-ChildItem -Path $ex -Recurse -Filter "*.so" |
  ForEach-Object { Copy-Item $_.FullName -Destination $OutDir -Force }
$server = Get-ChildItem -Path $ex -Recurse -Filter "llama-server" | Select-Object -First 1
if (-not $server) { throw "llama-server not found in $asset" }
Copy-Item $server.FullName -Destination (Join-Path $OutDir "libllamaserver.so") -Force

Write-Host "Staged into ${OutDir}:"
Get-ChildItem $OutDir | ForEach-Object { Write-Host "  $($_.Name)" }
