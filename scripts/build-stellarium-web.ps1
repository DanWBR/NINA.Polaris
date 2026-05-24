# build-stellarium-web.ps1 — Windows wrapper around build-stellarium-web.sh.
#
# Why this wrapper exists: Windows Git with autocrlf=true checks .sh
# files out with CRLF line endings. bash reads those as literal \r
# at the end of each command, which makes 'source /emsdk/emsdk_env.sh'
# look up '/emsdk/emsdk_env.sh\r' and fail with the misleading
# "No such file or directory". Repo .gitattributes pins *.sh to LF,
# but Git only re-checks-out files on a force-refresh, so an existing
# CRLF copy survives `git pull`.
#
# This wrapper normalises the .sh to LF on each invocation, then
# hands off to bash (Git Bash on Windows, /bin/bash on WSL/Linux).
# Idempotent — runs the strip every time, but the operation is cheap.
#
# Usage from PowerShell:
#   .\scripts\build-stellarium-web.ps1

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$shScript = Join-Path $scriptDir 'build-stellarium-web.sh'

if (-not (Test-Path $shScript)) {
    Write-Error "Cannot find $shScript"
    exit 1
}

# Read raw + strip CR. UTF-8 without BOM matches what Git writes.
Write-Host "→ Normalising line endings on build-stellarium-web.sh"
$content = [System.IO.File]::ReadAllText($shScript)
$content = $content -replace "`r", ""
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($shScript, $content, $utf8NoBom)

# Hand off to bash. Git Bash on Windows installs at one of these paths;
# fall back to PATH lookup.
$bashCandidates = @(
    'C:\Program Files\Git\bin\bash.exe',
    'C:\Program Files (x86)\Git\bin\bash.exe',
    'bash'
)
$bash = $null
foreach ($candidate in $bashCandidates) {
    if ($candidate -eq 'bash') {
        if (Get-Command bash -ErrorAction SilentlyContinue) { $bash = 'bash'; break }
    } elseif (Test-Path $candidate) {
        $bash = $candidate
        break
    }
}
if (-not $bash) {
    Write-Error "bash not found. Install Git for Windows (which ships Git Bash)."
    exit 1
}

Write-Host "→ Invoking $bash $shScript"
& $bash $shScript
exit $LASTEXITCODE
