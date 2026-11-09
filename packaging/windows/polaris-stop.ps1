# Stops every Polaris running from one install folder, so the installer and the
# uninstaller can replace or remove its files.
#
# Asks first over the loopback API (the same /api/system/stop-app the update
# worker uses, which lets Polaris save its state), then force-stops whatever is
# still running from that folder after the grace period. Polaris processes that
# run from any other folder are never touched.
# Windows PowerShell 5.1 compatible.
param(
    [Parameter(Mandatory = $true)][string]$InstallDir,
    [int]$GraceSeconds = 30
)

$ErrorActionPreference = 'SilentlyContinue'
$root = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\') + '\'

function Get-Ours {
    @(Get-CimInstance Win32_Process -Filter "Name='NINA.Polaris.exe'" | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)
    })
}

if ((Get-Ours).Count -eq 0) { exit 0 }

$port = 5080
try {
    $cfg = Get-Content -Raw -LiteralPath (Join-Path $root 'app\appsettings.json') | ConvertFrom-Json
    if ($cfg.Server.Http.Port) { $port = [int]$cfg.Server.Http.Port }
} catch { }
try { Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$port/api/system/stop-app" -TimeoutSec 10 | Out-Null } catch { }

$deadline = (Get-Date).AddSeconds($GraceSeconds)
while ((Get-Ours).Count -gt 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
foreach ($p in Get-Ours) { Stop-Process -Id $p.ProcessId -Force }
exit 0
