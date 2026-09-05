$ErrorActionPreference = 'SilentlyContinue'

if (Get-Process -Name chrome -ErrorAction SilentlyContinue) { exit 0 }

$dataDirectory = Join-Path $env:LOCALAPPDATA 'CodexLanConsole'
$settingsFile = Join-Path $dataDirectory 'chrome-bootstrap.json'
$remembered = $null
if (Test-Path -LiteralPath $settingsFile) {
    try { $remembered = (Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).ExecutablePath } catch { }
}

$candidates = @(
    $env:CODEX_LAN_CHROME_PATH,
    $remembered,
    (Join-Path $env:ProgramFiles 'Qoom Chrome\chrome.exe'),
    (Join-Path $env:ProgramFiles 'Google\Chrome\Application\chrome.exe'),
    $(if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} 'Google\Chrome\Application\chrome.exe' }),
    (Join-Path $env:LOCALAPPDATA 'Google\Chrome\Application\chrome.exe'),
    (Join-Path $env:LOCALAPPDATA 'Google\Chrome SxS\Application\chrome.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -Unique

$chrome = $candidates | Select-Object -First 1
if (-not $chrome) { exit 2 }

Start-Process -FilePath $chrome -ArgumentList '--no-startup-window'
exit 0
