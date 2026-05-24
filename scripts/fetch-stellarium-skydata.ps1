# fetch-stellarium-skydata.ps1 — Windows wrapper around
# fetch-stellarium-skydata.sh. Same CRLF-strip pattern as
# build-stellarium-web.ps1 — see that file for the rationale.

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$shScript = Join-Path $scriptDir 'fetch-stellarium-skydata.sh'

if (-not (Test-Path $shScript)) {
    Write-Error "Cannot find $shScript"
    exit 1
}

Write-Host "→ Normalising line endings on fetch-stellarium-skydata.sh"
$content = [System.IO.File]::ReadAllText($shScript)
$content = $content -replace "`r", ""
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($shScript, $content, $utf8NoBom)

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
    Write-Error "bash not found. Install Git for Windows."
    exit 1
}

Write-Host "→ Invoking $bash $shScript"
& $bash $shScript
exit $LASTEXITCODE
