# build-wasm.ps1 - build the NINA.Polaris.Wasm AOT bundle and copy
# it into the static-assets tree the ASP.NET host serves.
#
# Run manually after editing C# code in src/NINA.Polaris.Wasm/. The
# wwwroot/js/wasm/ output is .gitignored - it's a derived artifact,
# not source. CI should call this before publish.
#
# First run takes a few minutes because Emscripten + Mono cross
# tooling has to warm caches. Subsequent runs ~30-60s.
#
# Usage:
#   .\deploy\build-wasm.ps1                  # Release (default)
#   .\deploy\build-wasm.ps1 -Configuration Debug
#   .\deploy\build-wasm.ps1 -SkipBuild       # just re-copy existing AppBundle

param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$wasmProj = Join-Path $repo "src\NINA.Polaris.Wasm\NINA.Polaris.Wasm.csproj"
$appBundle = Join-Path $repo "src\NINA.Polaris.Wasm\bin\$Configuration\net10.0\browser-wasm\AppBundle"
$wwwroot = Join-Path $repo "src\NINA.Polaris\wwwroot\js\wasm"

if (-not $SkipBuild) {
    Write-Host "==> Publishing NINA.Polaris.Wasm ($Configuration)..." -ForegroundColor Cyan
    dotnet publish $wasmProj -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
}

if (-not (Test-Path $appBundle)) {
    throw "AppBundle not found at $appBundle - did publish succeed?"
}

Write-Host "==> Mirroring AppBundle -> $wwwroot" -ForegroundColor Cyan
if (Test-Path $wwwroot) { Remove-Item -Recurse -Force $wwwroot }
New-Item -ItemType Directory -Force -Path $wwwroot | Out-Null

# Robocopy gives us mirror semantics + skip-newer-on-target heuristics
# that plain Copy-Item lacks. Exit codes 0-7 are success; 8+ are real
# errors. Don't propagate the (non-zero) success codes as failures.
robocopy $appBundle $wwwroot /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with code $LASTEXITCODE" }
$global:LASTEXITCODE = 0

$wasmSize = (Get-Item (Join-Path $wwwroot '_framework\dotnet.native.wasm')).Length
$totalSize = (Get-ChildItem $wwwroot -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ""
Write-Host "==> Done. dotnet.native.wasm: $([math]::Round($wasmSize/1MB, 1)) MB ; total bundle: $([math]::Round($totalSize/1MB, 1)) MB" -ForegroundColor Green
Write-Host "    Served from: /js/wasm/main.js"
