$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = @(
    (Join-Path $root 'release\WindowsBridge\CodexLanBridge.exe')
    (Join-Path $root 'WindowsBridge\CodexLanBridge.exe')
    (Join-Path $root 'bridge\bin\Release\net8.0\CodexLanBridge.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $exe -or -not (Test-Path -LiteralPath $exe)) {
    throw "Bridge executable was not found under: $root"
}
$running = Get-Process -Name 'CodexLanBridge' -ErrorAction SilentlyContinue
if (-not $running) {
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -WindowStyle Hidden
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
