# Polaris self-update worker for the portable Windows build.
#
# Polaris (UpdateService.Windows.cs) downloads the release zip, checks it
# against GitHub's SHA-256, copies this script out of its own folder and starts
# it. This script then:
#   1. checks the zip again and extracts it next to the Polaris folder, while
#      Polaris keeps running (a failure here changes nothing);
#   2. asks Polaris to stop over the loopback API and waits for it to exit;
#   3. carries the operator's settings over and swaps the folders, keeping the
#      old one as <folder>.previous;
#   4. starts the new Polaris and waits for it to answer with the new version,
#      putting the old folder back and restarting it if it does not.
#
# Based on matteoconti92's community prototype (DanWBR/NINA.Polaris#63).
# Windows PowerShell 5.1 compatible. Never run it from the folder it replaces.
param(
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$Sha256,
    [Parameter(Mandatory = $true)][string]$AppDir,
    [Parameter(Mandatory = $true)][int]$ParentPid,
    [Parameter(Mandatory = $true)][int]$Port,
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+(\.\d+){1,3}$')][string]$Version,
    [Parameter(Mandatory = $true)][string]$StatusPath,
    [string]$MergedSettings,
    [string]$RestartArgs,
    [string]$WorkDir,
    [switch]$Hidden
)

$ErrorActionPreference = 'Stop'
# Expand-Archive's progress bar slows a 180 MB extraction several times over.
$ProgressPreference = 'SilentlyContinue'

$AppDir = $AppDir.TrimEnd('\')
$exeName = 'NINA.Polaris.exe'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$updateDir = Split-Path -Parent $StatusPath
$log = Join-Path $updateDir 'update.log'
$staging = "$AppDir.new-$stamp"
$previous = "$AppDir.previous"
$failedDir = "$AppDir.failed-$stamp"
$base = "http://127.0.0.1:$Port"

function Write-Log([string]$message) {
    Add-Content -LiteralPath $log -Value ('{0} {1}' -f (Get-Date -Format 's'), $message)
}

function Set-Status([string]$phase, [string]$message) {
    $json = [ordered]@{
        phase = $phase; message = $message; version = $Version
        at = (Get-Date).ToUniversalTime().ToString('o')
    } | ConvertTo-Json -Compress
    $tmp = "$StatusPath.tmp"
    [IO.File]::WriteAllText($tmp, $json, (New-Object Text.UTF8Encoding $false))
    Move-Item -LiteralPath $tmp -Destination $StatusPath -Force
    Write-Log "[$phase] $message"
}

# Polaris's own command line, so it comes back started the same way.
$argLine = $null
if ($RestartArgs) {
    $list = @(ConvertFrom-Json ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($RestartArgs))))
    if ($list.Count -gt 0) {
        $argLine = ($list | ForEach-Object { '"' + ([string]$_ -replace '"', '\"') + '"' }) -join ' '
    }
}

function Start-Polaris {
    $wd = if ($WorkDir -and (Test-Path -LiteralPath $WorkDir)) { $WorkDir } else { $AppDir }
    $p = @{ FilePath = (Join-Path $AppDir $exeName); WorkingDirectory = $wd; PassThru = $true }
    if ($argLine) { $p.ArgumentList = $argLine }
    if ($Hidden) { $p.WindowStyle = 'Hidden' }
    return Start-Process @p
}

function Test-PolarisFromAppDir {
    $target = Join-Path $AppDir $exeName
    foreach ($p in @(Get-CimInstance Win32_Process -Filter "Name='$exeName'" -ErrorAction SilentlyContinue)) {
        if ([string]::Equals([string]$p.ExecutablePath, $target, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

# Numeric match, component by component, so 0.98.23 never passes for 0.98.235
# and 0.98.235.0 (the assembly form) passes for 0.98.235.
function Test-Version([string]$found) {
    $m = [regex]::Match([string]$found, '^\d+(\.\d+)*').Value
    if (-not $m) { return $false }
    $a = $m.Split('.'); $b = $Version.Split('.')
    for ($i = 0; $i -lt [Math]::Max($a.Count, $b.Count); $i++) {
        $x = if ($i -lt $a.Count) { [int]$a[$i] } else { 0 }
        $y = if ($i -lt $b.Count) { [int]$b[$i] } else { 0 }
        if ($x -ne $y) { return $false }
    }
    return $true
}

function Wait-Healthy($proc) {
    $deadline = (Get-Date).AddSeconds(180)
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) { return $false }
        try {
            $s = Invoke-RestMethod -Uri "$base/api/system/status" -TimeoutSec 5
            if (Test-Version ([string]$s.version)) { return $true }
        } catch { }
        Start-Sleep -Seconds 3
    }
    return $false
}

# Rename and move can fail for a few seconds after a process exits, while an
# antivirus scanner or the search indexer still holds a file.
function Invoke-WithRetry([scriptblock]$action) {
    for ($i = 1; ; $i++) {
        try { & $action; return }
        catch {
            if ($i -ge 15) { throw }
            Start-Sleep -Seconds 2
        }
    }
}

$stopped = $false
$swapped = $false
try {
    New-Item -ItemType Directory -Path $updateDir -Force | Out-Null
    Write-Log "Update to $Version, Polaris PID $ParentPid, folder $AppDir"

    Set-Status 'verifying' 'Checking the package...'
    $hash = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash
    if ($hash -ne $Sha256.ToUpperInvariant()) { throw 'Checksum mismatch. Nothing was changed.' }

    Set-Status 'extracting' "Unpacking $Version..."
    Expand-Archive -LiteralPath $ZipPath -DestinationPath $staging -Force
    $found = @(Get-ChildItem -LiteralPath $staging -Recurse -File -Filter $exeName)
    if ($found.Count -ne 1) { throw 'Unexpected package layout. Nothing was changed.' }
    $newApp = $found[0].Directory.FullName
    $dll = Join-Path $newApp 'NINA.Polaris.dll'
    if (-not (Test-Path -LiteralPath $dll)) { throw 'NINA.Polaris.dll missing from the package. Nothing was changed.' }
    $shipped = (Get-Item -LiteralPath $dll).VersionInfo.ProductVersion
    if (-not (Test-Version ([string]$shipped))) {
        throw "The package is version $shipped, expected $Version. Nothing was changed."
    }

    # Carry the operator's configuration: the merged appsettings.json, plus any
    # appsettings.*.json they added that the release does not ship.
    if ($MergedSettings -and (Test-Path -LiteralPath $MergedSettings)) {
        Copy-Item -LiteralPath $MergedSettings -Destination (Join-Path $newApp 'appsettings.json') -Force
    }
    Get-ChildItem -LiteralPath $AppDir -Filter 'appsettings*.json' -File | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $newApp $_.Name))
    } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $newApp }

    Set-Status 'stopping' 'Stopping Polaris...'
    $r = Invoke-RestMethod -Method Post -Uri "$base/api/system/stop-app" -TimeoutSec 15
    if ($r.ok -ne $true) { throw 'Polaris refused to stop. Nothing was changed.' }
    $deadline = (Get-Date).AddSeconds(120)
    while (Get-Process -Id $ParentPid -ErrorAction SilentlyContinue) {
        if ((Get-Date) -gt $deadline) { throw 'Polaris did not stop within 2 minutes. Nothing was changed.' }
        Start-Sleep -Milliseconds 500
    }
    $stopped = $true
    if (Test-PolarisFromAppDir) { throw 'Another Polaris is running from this folder. Nothing was changed.' }

    Set-Status 'installing' "Installing $Version..."
    if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Recurse -Force }
    Invoke-WithRetry { Rename-Item -LiteralPath $AppDir -NewName (Split-Path -Leaf $previous) }
    try {
        Invoke-WithRetry { Move-Item -LiteralPath $newApp -Destination $AppDir }
    } catch {
        Invoke-WithRetry { Rename-Item -LiteralPath $previous -NewName (Split-Path -Leaf $AppDir) }
        throw
    }
    $swapped = $true

    Set-Status 'restarting' "Starting Polaris $Version..."
    $proc = Start-Polaris
    if (-not (Wait-Healthy $proc)) {
        # Only the process this script started is ever stopped.
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force; $proc.WaitForExit(30000) | Out-Null }
        Invoke-WithRetry { Move-Item -LiteralPath $AppDir -Destination $failedDir }
        Invoke-WithRetry { Rename-Item -LiteralPath $previous -NewName (Split-Path -Leaf $AppDir) }
        Start-Polaris | Out-Null
        Set-Status 'rolled-back' "Polaris $Version did not start, so the previous version was restored. The new files are in $failedDir."
        exit 1
    }

    # Installed with polaris-setup-win-<arch>.exe: keep the version Windows
    # shows under Installed apps in step with the one now running. The key is
    # per user (the setup never needs administrator rights) and is only touched
    # when it names this install's folder.
    try {
        $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{f4228c17-673e-4a65-8628-416979934996}_is1'
        if (Test-Path -LiteralPath $key) {
            $loc = [string](Get-ItemProperty -LiteralPath $key).InstallLocation
            if ($loc.TrimEnd('\') -ieq (Split-Path -Parent $AppDir)) {
                Set-ItemProperty -LiteralPath $key -Name DisplayVersion -Value $Version
            }
        }
    } catch { Write-Log ('Could not update the installed-apps entry: ' + $_.Exception.Message) }

    Set-Status 'complete' "Polaris $Version is running. The previous version is kept in $previous."
    exit 0
}
catch {
    $cause = $_.Exception.Message
    Write-Log "Error: $cause"
    try {
        # Polaris was stopped but never swapped (or the swap was undone): bring
        # the version that was running back.
        if ($stopped -and -not $swapped -and -not (Test-PolarisFromAppDir) -and
                (Test-Path -LiteralPath (Join-Path $AppDir $exeName))) {
            Start-Polaris | Out-Null
            $cause += ' Polaris was restarted on the version that was installed.'
        }
    } catch { Write-Log ('Restart after failure failed: ' + $_.Exception.Message) }
    try { Set-Status 'failed' $cause } catch { }
    exit 1
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $ZipPath -Force -ErrorAction SilentlyContinue
}
