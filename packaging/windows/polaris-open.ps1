# Starts the installed Polaris (in its own console window) unless it is
# already running from this folder, then opens it in the browser once it
# answers. Any HTTP reply counts, a 401 from a password-protected Polaris
# included. The first start takes a few seconds, and a browser opened before
# that lands on a connection error.
# Windows PowerShell 5.1 compatible.
param(
    [Parameter(Mandatory = $true)][string]$InstallDir,
    [int]$WaitSeconds = 90
)

$ErrorActionPreference = 'SilentlyContinue'
$root = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
$app = Join-Path $root 'app'
$exe = Join-Path $app 'NINA.Polaris.exe'

$httpPort = 5080; $httpsPort = 5000
try {
    $cfg = Get-Content -Raw -LiteralPath (Join-Path $app 'appsettings.json') | ConvertFrom-Json
    if ($cfg.Server.Http.Port) { $httpPort = [int]$cfg.Server.Http.Port }
    if ($cfg.Server.Https.Port) { $httpsPort = [int]$cfg.Server.Https.Port }
} catch { }

$running = @(Get-CimInstance Win32_Process -Filter "Name='NINA.Polaris.exe'" | Where-Object {
    [string]::Equals([string]$_.ExecutablePath, $exe, [StringComparison]::OrdinalIgnoreCase)
}).Count -gt 0
if (-not $running) { Start-Process -FilePath $exe -WorkingDirectory $app }

for ($i = 0; $i -lt $WaitSeconds; $i++) {
    try {
        Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$httpPort/api/system/status" -TimeoutSec 2 | Out-Null
        break
    } catch {
        if ($_.Exception.Response) { break }
        Start-Sleep -Seconds 1
    }
}
Start-Process "https://localhost:$httpsPort/"
