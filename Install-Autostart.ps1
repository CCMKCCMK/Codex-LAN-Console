$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = @(
    (Join-Path $root 'release\WindowsBridge\CodexLanBridge.exe')
    (Join-Path $root 'WindowsBridge\CodexLanBridge.exe')
    (Join-Path $root 'bridge\bin\Release\net8.0\CodexLanBridge.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $exe -or -not (Test-Path -LiteralPath $exe)) { throw "Bridge executable was not found under: $root" }
$startup = [Environment]::GetFolderPath('Startup')
$shortcutPath = Join-Path $startup 'Codex LAN Console.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = Split-Path -Parent $exe
$shortcut.WindowStyle = 7
$shortcut.Description = 'Local phone bridge for Codex'
$shortcut.Save()
Write-Host "Autostart installed: $shortcutPath"
