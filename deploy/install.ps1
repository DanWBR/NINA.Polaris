#Requires -RunAsAdministrator
<#
.SYNOPSIS
    N.I.N.A. Polaris - Windows Setup Script
.DESCRIPTION
    Installs .NET runtime, checks for ASCOM Platform, configures firewall,
    and optionally creates a Windows Service for N.I.N.A. Polaris.
#>

param(
    [string]$InstallDir = "C:\Program Files\N.I.N.A. Polaris",
    [string]$DotNetVersion = "10.0",
    [int]$Port = 5000,
    [switch]$CreateService
)

$ErrorActionPreference = "Stop"

function Write-Info  { param([string]$Msg) Write-Host "[INFO] $Msg" -ForegroundColor Green }
function Write-Warn  { param([string]$Msg) Write-Host "[WARN] $Msg" -ForegroundColor Yellow }
function Write-Err   { param([string]$Msg) Write-Host "[ERROR] $Msg" -ForegroundColor Red }

Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  N.I.N.A. Polaris - Windows Setup" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
Write-Info "Architecture: $arch"
Write-Info "Windows: $([System.Environment]::OSVersion.VersionString)"

# Step 1: Check ASCOM Platform
function Install-AscomCheck {
    Write-Info "Checking ASCOM Platform..."

    $ascomKey = "HKLM:\SOFTWARE\ASCOM"
    if (Test-Path $ascomKey) {
        try {
            $ver = (Get-ItemProperty $ascomKey -ErrorAction Stop).PlatformVersion
            Write-Info "ASCOM Platform $ver detected"
        } catch {
            Write-Info "ASCOM Platform detected (version unknown)"
        }
    } else {
        $ascomWow = "HKLM:\SOFTWARE\WOW6432Node\ASCOM"
        if (Test-Path $ascomWow) {
            Write-Info "ASCOM Platform detected (32-bit registry)"
        } else {
            Write-Warn "ASCOM Platform not found."
            Write-Warn "For ASCOM device support, install from: https://ascom-standards.org/Downloads/Index.htm"
            Write-Warn "N.I.N.A. Polaris can still use Alpaca devices without ASCOM Platform."
        }
    }

    # Check Alpaca discovery
    try {
        $alpacaKey = "HKLM:\SOFTWARE\ASCOM\Alpaca"
        if (Test-Path $alpacaKey) {
            Write-Info "ASCOM Alpaca support detected"
        }
    } catch { }
}

# Step 2: Install .NET Runtime
function Install-DotNet {
    Write-Info "Checking .NET runtime..."

    $dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnetCmd) {
        $currentVer = & dotnet --version 2>$null
        if ($currentVer -and $currentVer.StartsWith($DotNetVersion)) {
            Write-Info ".NET $currentVer already installed"
            return
        }
    }

    Write-Info "Installing .NET $DotNetVersion runtime..."

    $installerUrl = "https://dot.net/v1/dotnet-install.ps1"
    $installerPath = Join-Path $env:TEMP "dotnet-install.ps1"

    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $installerUrl -OutFile $installerPath -UseBasicParsing

        & $installerPath -Channel $DotNetVersion -Runtime aspnetcore -InstallDir "C:\Program Files\dotnet"

        $dotnetPath = "C:\Program Files\dotnet"
        $machinePath = [System.Environment]::GetEnvironmentVariable("Path", "Machine")
        if ($machinePath -notlike "*$dotnetPath*") {
            [System.Environment]::SetEnvironmentVariable("Path", "$machinePath;$dotnetPath", "Machine")
            $env:Path = "$env:Path;$dotnetPath"
            Write-Info "Added dotnet to system PATH"
        }

        $verCheck = & "C:\Program Files\dotnet\dotnet.exe" --version 2>$null
        if ($verCheck) {
            Write-Info ".NET installed: $verCheck"
        } else {
            Write-Err "Failed to verify .NET installation"
            exit 1
        }
    } catch {
        Write-Err "Failed to install .NET runtime: $_"
        Write-Warn "Download manually from: https://dotnet.microsoft.com/download"
        exit 1
    } finally {
        if (Test-Path $installerPath) { Remove-Item $installerPath -Force }
    }
}

# Step 3: Check ASTAP
function Install-AstapCheck {
    Write-Info "Checking ASTAP plate solver..."

    $astapPaths = @(
        "C:\Program Files\astap",
        "C:\Program Files (x86)\astap",
        "${env:LOCALAPPDATA}\astap"
    )

    $found = $false
    foreach ($p in $astapPaths) {
        if (Test-Path (Join-Path $p "astap.exe")) {
            Write-Info "ASTAP found at: $p"
            $found = $true
            break
        }
    }

    if (-not $found) {
        $astapCmd = Get-Command astap -ErrorAction SilentlyContinue
        if ($astapCmd) {
            Write-Info "ASTAP found in PATH: $($astapCmd.Source)"
            $found = $true
        }
    }

    if (-not $found) {
        Write-Warn "ASTAP not found. For plate solving, install from:"
        Write-Warn "  https://www.hnsky.org/astap.htm"
        Write-Warn "  Also download a star database (H17 recommended)"
    }
}

# Step 4: Install application files
function Install-Application {
    Write-Info "Installing N.I.N.A. Polaris to $InstallDir..."

    if (-not (Test-Path $InstallDir)) {
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    }

    $scriptDir = Split-Path -Parent $PSCommandPath
    $publishDir = Join-Path (Split-Path -Parent $scriptDir) "publish"

    if ((Test-Path $publishDir) -and (Test-Path (Join-Path $publishDir "NINA.Polaris.exe"))) {
        Write-Info "Copying published binaries..."
        Copy-Item -Path "$publishDir\*" -Destination $InstallDir -Recurse -Force
        Write-Info "Application files installed"
    } else {
        Write-Warn "No published binaries found at $publishDir"
        Write-Warn "Run 'dotnet publish' first, or copy binaries to $InstallDir manually"
    }
}

# Step 5: Configure firewall
function Set-FirewallRule {
    Write-Info "Configuring firewall for port $Port..."

    $ruleName = "N.I.N.A. Polaris (TCP $Port)"

    $existing = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Info "Firewall rule already exists"
        return
    }

    try {
        New-NetFirewallRule `
            -DisplayName $ruleName `
            -Direction Inbound `
            -Protocol TCP `
            -LocalPort $Port `
            -Action Allow `
            -Profile Private `
            -Description "Allow N.I.N.A. Polaris web UI access on LAN" | Out-Null

        Write-Info "Firewall rule created (Private network only)"
    } catch {
        Write-Warn "Could not create firewall rule: $_"
        Write-Warn "You may need to manually allow TCP port $Port"
    }
}

# Step 6: Create Windows Service (optional)
function Install-NinaService {
    if (-not $CreateService) {
        Write-Info "Skipping Windows Service creation (use -CreateService to enable)"
        return
    }

    Write-Info "Creating Windows Service..."

    $serviceName = "NINAHeadless"
    $exePath = Join-Path $InstallDir "NINA.Polaris.exe"

    if (-not (Test-Path $exePath)) {
        Write-Warn "Cannot create service: $exePath not found"
        Write-Warn "Install application files first, then re-run with -CreateService"
        return
    }

    $existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($existing) {
        if ($existing.Status -eq "Running") {
            Write-Info "Stopping existing service..."
            Stop-Service -Name $serviceName -Force
        }
        Write-Info "Removing existing service..."
        sc.exe delete $serviceName | Out-Null
        Start-Sleep -Seconds 2
    }

    $binPath = "`"$exePath`" --urls=http://0.0.0.0:$Port"

    sc.exe create $serviceName `
        binPath= $binPath `
        start= delayed-auto `
        DisplayName= "N.I.N.A. Polaris Astronomy Controller" | Out-Null

    sc.exe description $serviceName "N.I.N.A. Polaris - Web-based astronomy camera and telescope controller" | Out-Null

    sc.exe failure $serviceName reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null

    Write-Info "Windows Service '$serviceName' created"
    Write-Info "Commands:"
    Write-Info "  Start-Service $serviceName"
    Write-Info "  Stop-Service $serviceName"
    Write-Info "  Get-Service $serviceName"
}

# Step 7: Create startup shortcut (alternative to service)
function Install-StartupShortcut {
    if ($CreateService) { return }

    Write-Info "Creating startup shortcut..."

    $exePath = Join-Path $InstallDir "NINA.Polaris.exe"
    if (-not (Test-Path $exePath)) {
        Write-Warn "Skipping shortcut: application not installed yet"
        return
    }

    $startupFolder = [System.Environment]::GetFolderPath("CommonStartup")
    $shortcutPath = Join-Path $startupFolder "N.I.N.A. Polaris.lnk"

    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $exePath
        $shortcut.Arguments = "--urls=http://0.0.0.0:$Port"
        $shortcut.WorkingDirectory = $InstallDir
        $shortcut.Description = "N.I.N.A. Polaris Astronomy Controller"
        $shortcut.WindowStyle = 7  # minimized
        $shortcut.Save()
        Write-Info "Startup shortcut created (runs minimized on login)"
    } catch {
        Write-Warn "Could not create startup shortcut: $_"
    }
}

# Run all steps
Install-AscomCheck
Install-DotNet
Install-AstapCheck
Install-Application
Set-FirewallRule
Install-NinaService
Install-StartupShortcut

# Summary
$ip = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -notlike "*Loopback*" -and $_.PrefixOrigin -ne "WellKnown" } | Select-Object -First 1).IPAddress

Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  Setup Complete!" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""
Write-Info "Access N.I.N.A. Polaris at:"
if ($ip) {
    Write-Info "  http://${ip}:$Port"
}
Write-Info "  http://localhost:$Port"
Write-Host ""
if ($CreateService) {
    Write-Info "Start the service:"
    Write-Info "  Start-Service NINAHeadless"
} else {
    Write-Info "Run manually:"
    Write-Info "  & '$InstallDir\NINA.Polaris.exe' --urls=http://0.0.0.0:$Port"
}
Write-Host ""
