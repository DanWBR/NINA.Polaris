# =============================================================================
# N.I.N.A. Polaris - Publish Script for Windows x64 (Mini PCs)
# =============================================================================
# Builds a self-contained deployment for win-x64.
# Run from any directory; paths are resolved relative to this script.
# =============================================================================

[CmdletBinding()]
param(
    [switch]$InstallService
)

$ErrorActionPreference = 'Stop'

$ScriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Definition
$RepoRoot   = Split-Path -Parent $ScriptDir
$Project    = Join-Path $RepoRoot 'src\NINA.Polaris\NINA.Polaris.csproj'
$OutputDir  = Join-Path $RepoRoot 'publish\win-x64'
$RID        = 'win-x64'

Write-Host '=============================================================================' -ForegroundColor Cyan
Write-Host "  N.I.N.A. Polaris - Publishing for $RID" -ForegroundColor Cyan
Write-Host '=============================================================================' -ForegroundColor Cyan
Write-Host ''

# ---------------------------------------------------------------------------
# Verify project file exists
# ---------------------------------------------------------------------------
if (-not (Test-Path $Project)) {
    Write-Error "Project file not found: $Project"
    exit 1
}

# ---------------------------------------------------------------------------
# Clean previous output
# ---------------------------------------------------------------------------
if (Test-Path $OutputDir) {
    Write-Host 'Cleaning previous publish output ...'
    Remove-Item -Recurse -Force $OutputDir
}

# ---------------------------------------------------------------------------
# Publish
# ---------------------------------------------------------------------------
Write-Host 'Building and publishing ...'
Write-Host ''

dotnet publish $Project `
    -c Release `
    -r $RID `
    --self-contained true `
    -o $OutputDir

if ($LASTEXITCODE -ne 0) {
    Write-Error 'dotnet publish failed.'
    exit $LASTEXITCODE
}

Write-Host ''

# ---------------------------------------------------------------------------
# Report output size
# ---------------------------------------------------------------------------
$files = Get-ChildItem -Path $OutputDir -Recurse -File
$totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
$fileCount  = $files.Count

if ($totalBytes -gt 1MB) {
    $sizeDisplay = '{0:N1} MB' -f ($totalBytes / 1MB)
} else {
    $sizeDisplay = '{0:N0} KB' -f ($totalBytes / 1KB)
}

Write-Host '=============================================================================' -ForegroundColor Green
Write-Host "  Publish complete!" -ForegroundColor Green
Write-Host "  Output:     $OutputDir"
Write-Host "  Total size: $sizeDisplay  ($fileCount files)"
Write-Host '=============================================================================' -ForegroundColor Green
Write-Host ''

# ---------------------------------------------------------------------------
# Windows Service installation (optional)
# ---------------------------------------------------------------------------
if ($InstallService) {
    Write-Host '=============================================================================' -ForegroundColor Yellow
    Write-Host '  Installing as Windows Service' -ForegroundColor Yellow
    Write-Host '=============================================================================' -ForegroundColor Yellow
    Write-Host ''

    $ServiceName = 'NINAHeadless'
    $ExePath     = Join-Path $OutputDir 'NINA.Polaris.exe'

    # Check for administrator privileges
    $isAdmin = ([Security.Principal.WindowsPrincipal] `
                [Security.Principal.WindowsIdentity]::GetCurrent() `
               ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

    if (-not $isAdmin) {
        Write-Warning 'Administrator privileges required to install a Windows Service.'
        Write-Warning 'Please re-run this script in an elevated PowerShell prompt with -InstallService.'
        exit 1
    }

    # Stop existing service if running
    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
        Write-Host "Stopping existing $ServiceName service ..."
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Write-Host "Removing existing $ServiceName service ..."
        sc.exe delete $ServiceName | Out-Null
        Start-Sleep -Seconds 2
    }

    # Create the service
    Write-Host "Creating Windows Service '$ServiceName' ..."
    sc.exe create $ServiceName `
        binPath= "`"$ExePath`"" `
        start= delayed-auto `
        DisplayName= "N.I.N.A. Polaris Astronomy Controller" | Out-Null

    sc.exe description $ServiceName "N.I.N.A. Polaris astronomy equipment controller and web API" | Out-Null
    sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null

    Write-Host "Starting $ServiceName ..."
    Start-Service -Name $ServiceName

    Write-Host ''
    Write-Host "  Service '$ServiceName' installed and started." -ForegroundColor Green
    Write-Host ''
} else {
    # Print manual instructions
    Write-Host 'To run as a Windows Service, you have two options:'
    Write-Host ''
    Write-Host '  Option 1: Re-run this script with -InstallService (requires admin):'
    Write-Host "    .\publish-win-x64.ps1 -InstallService" -ForegroundColor White
    Write-Host ''
    Write-Host '  Option 2: Use sc.exe manually (requires admin):'
    Write-Host "    sc.exe create NINAHeadless binPath= `"$OutputDir\NINA.Polaris.exe`" start= delayed-auto" -ForegroundColor White
    Write-Host "    sc.exe start NINAHeadless" -ForegroundColor White
    Write-Host ''
    Write-Host '  Option 3: Use NSSM (Non-Sucking Service Manager) for more control:'
    Write-Host '    choco install nssm   # or download from https://nssm.cc/' -ForegroundColor White
    Write-Host "    nssm install NINAHeadless `"$OutputDir\NINA.Polaris.exe`"" -ForegroundColor White
    Write-Host '    nssm set NINAHeadless AppDirectory' "`"$OutputDir`"" -ForegroundColor White
    Write-Host '    nssm set NINAHeadless AppEnvironmentExtra ASPNETCORE_URLS=http://0.0.0.0:5000' -ForegroundColor White
    Write-Host '    nssm start NINAHeadless' -ForegroundColor White
    Write-Host ''
    Write-Host '  Or just run it directly:'
    Write-Host "    cd $OutputDir" -ForegroundColor White
    Write-Host '    .\NINA.Polaris.exe' -ForegroundColor White
    Write-Host ''
}

Write-Host "  Access the web UI at: http://localhost:5000" -ForegroundColor Cyan
Write-Host ''
