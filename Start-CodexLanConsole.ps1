$ErrorActionPreference = 'Stop'
$dataDirectory = Join-Path $env:LOCALAPPDATA 'CodexLanConsole'
$rootFile = Join-Path $dataDirectory 'install-root.txt'
if (-not (Test-Path -LiteralPath $rootFile)) { throw "Current installation marker was not found: $rootFile" }
$root = [IO.File]::ReadAllText($rootFile).Trim()
$currentExecutableFile = Join-Path $dataDirectory 'current-executable.txt'
if (-not (Test-Path -LiteralPath $currentExecutableFile)) { throw "Current executable pointer was not found: $currentExecutableFile" }
$exe = [IO.File]::ReadAllText($currentExecutableFile).Trim()
if (-not $exe -or -not (Test-Path -LiteralPath $exe)) {
    throw "Bridge executable was not found under: $root"
}
Remove-Item -LiteralPath (Join-Path $dataDirectory 'manual-stop.flag') -Force -ErrorAction SilentlyContinue
$taskName = 'Codex LAN Console'
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if (-not $task) { throw "Always-on task is missing. Run Install-Autostart.ps1 first." }
Enable-ScheduledTask -TaskName $taskName | Out-Null
if (-not (Get-Process -Name 'CodexLanBridge' -ErrorAction SilentlyContinue)) {
    Start-ScheduledTask -TaskName $taskName
    Start-Sleep -Seconds 2
}
$tailscale = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.InterfaceAlias -eq 'Tailscale' -and $_.AddressState -eq 'Preferred' } |
    Select-Object -First 1 -ExpandProperty IPAddress
$lan = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -like '192.168.*' -or $_.IPAddress -like '10.*' -or $_.IPAddress -like '172.1[6-9].*' -or $_.IPAddress -like '172.2?.*' -or $_.IPAddress -like '172.3[0-1].*' } |
    Where-Object { $_.InterfaceAlias -notlike 'vEthernet*' } |
    Select-Object -First 1 -ExpandProperty IPAddress
$address = if ($tailscale) { $tailscale } elseif ($lan) { $lan } else { '127.0.0.1' }
Start-Process "http://${address}:8787/"
Start-Process notepad.exe (Join-Path $env:LOCALAPPDATA 'CodexLanConsole\pairing.txt')
