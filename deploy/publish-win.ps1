<#
.SYNOPSIS
    N.I.N.A. Polaris - Publish for Windows x64
.DESCRIPTION
    Builds a self-contained deployment for Windows mini PCs and desktops.
#>

param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $PSCommandPath
$projectPath = Join-Path $scriptDir "..\src\NINA.Polaris\NINA.Polaris.csproj"
$outputDir = Join-Path $scriptDir "..\publish\$Runtime"

Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  N.I.N.A. Polaris - Publish $Runtime" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    Write-Host "[ERROR] dotnet CLI not found. Install .NET SDK first." -ForegroundColor Red
    exit 1
}

Write-Host "[INFO] Cleaning previous build..." -ForegroundColor Green
if (Test-Path $outputDir) {
    Remove-Item -Path $outputDir -Recurse -Force
}

Write-Host "[INFO] Publishing for $Runtime..." -ForegroundColor Green
dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $outputDir

$size = "{0:N1} MB" -f ((Get-ChildItem -Path $outputDir -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB)

Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  Publish Complete!" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "[INFO] Output: $outputDir" -ForegroundColor Green
Write-Host "[INFO] Size: $size" -ForegroundColor Green
Write-Host "[INFO] Runtime: $Runtime (self-contained)" -ForegroundColor Green
Write-Host ""
Write-Host "[INFO] Run:" -ForegroundColor Green
Write-Host "  & '$outputDir\NINA.Polaris.exe' --urls=http://0.0.0.0:5000" -ForegroundColor Green
Write-Host ""
