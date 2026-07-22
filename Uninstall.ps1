$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Get-Process -Name 'CodexLanBridge' -ErrorAction SilentlyContinue | Stop-Process
$shortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'Codex LAN Console.lnk'
if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force }
$data = Join-Path $env:LOCALAPPDATA 'CodexLanConsole'
if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }
Write-Host 'Codex LAN Console autostart and local device tokens were removed.'
Write-Host 'You may now delete the CodexLanConsole project directory.'
