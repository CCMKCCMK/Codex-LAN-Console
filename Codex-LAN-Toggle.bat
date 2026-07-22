@echo off
setlocal EnableExtensions
title Codex LAN Console Manager
set "ROOT=%~dp0"
if exist "%ROOT%release\WindowsBridge\CodexLanBridge.exe" (
  set "BRIDGE=%ROOT%release\WindowsBridge\CodexLanBridge.exe"
  set "WORKDIR=%ROOT%release\WindowsBridge"
) else if exist "%ROOT%WindowsBridge\CodexLanBridge.exe" (
  set "BRIDGE=%ROOT%WindowsBridge\CodexLanBridge.exe"
  set "WORKDIR=%ROOT%WindowsBridge"
) else if exist "%ROOT%backend\bridge\bin\Release\net8.0\CodexLanBridge.exe" (
  set "BRIDGE=%ROOT%backend\bridge\bin\Release\net8.0\CodexLanBridge.exe"
  set "WORKDIR=%ROOT%backend\bridge\bin\Release\net8.0"
) else (
  set "BRIDGE=%ROOT%backend\bridge\bin\Release\net8.0\CodexLanBridge.exe"
  set "WORKDIR=%ROOT%backend\bridge\bin\Release\net8.0"
)
set "PAIRING_FILE=%LOCALAPPDATA%\CodexLanConsole\pairing.txt"
if /I "%~1"=="status" goto status

:menu
cls
echo ==========================================
echo          Codex LAN Console Manager
echo ==========================================
call :status
echo.
echo [1] Start bridge
echo [2] Stop bridge
echo [3] Show pairing code and address
echo [4] Refresh status
echo [0] Exit
echo.
set /p "ACTION=Choose an option: "
if "%ACTION%"=="1" goto start_bridge
if "%ACTION%"=="2" goto stop_bridge
if "%ACTION%"=="3" goto show_pairing
if "%ACTION%"=="4" goto menu
if "%ACTION%"=="0" exit /b 0
goto menu

:status
set "TAILSCALE_IP="
set "LAN_IP="
for /f "usebackq delims=" %%I in (`powershell.exe -NoProfile -Command "Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceAlias -eq 'Tailscale' -and $_.AddressState -eq 'Preferred' } | Select-Object -First 1 -ExpandProperty IPAddress"`) do set "TAILSCALE_IP=%%I"
for /f "usebackq delims=" %%I in (`powershell.exe -NoProfile -Command "Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.AddressState -eq 'Preferred' -and $_.InterfaceAlias -notlike '*Tailscale*' -and $_.InterfaceAlias -notlike 'vEthernet*' -and $_.InterfaceAlias -notlike '*Loopback*' -and $_.InterfaceAlias -ne 'Meta' -and ($_.IPAddress -like '192.168.*' -or $_.IPAddress -like '10.*' -or $_.IPAddress -like '172.*') } | Select-Object -First 1 -ExpandProperty IPAddress"`) do set "LAN_IP=%%I"
tasklist /FI "IMAGENAME eq CodexLanBridge.exe" 2>NUL | find /I "CodexLanBridge.exe" >NUL
if errorlevel 1 (
  echo Status: STOPPED
) else (
  echo Status: RUNNING
)
if defined TAILSCALE_IP (
  echo Tailscale address: http://%TAILSCALE_IP%:8787
) else (
  echo Tailscale address: unavailable
)
if defined LAN_IP (
  echo LAN address:       http://%LAN_IP%:8787
) else (
  echo LAN address:       unavailable
)
exit /b 0

:start_bridge
tasklist /FI "IMAGENAME eq CodexLanBridge.exe" 2>NUL | find /I "CodexLanBridge.exe" >NUL
if not errorlevel 1 (
  echo Bridge is already running.
  timeout /t 2 /nobreak >NUL
  goto menu
)
if not exist "%BRIDGE%" (
  echo Bridge executable was not found:
  echo %BRIDGE%
  pause
  goto menu
)
powershell.exe -NoProfile -WindowStyle Hidden -Command "Start-Process -FilePath '%BRIDGE%' -WorkingDirectory '%WORKDIR%' -WindowStyle Hidden"
timeout /t 2 /nobreak >NUL
echo Bridge started.
goto show_pairing

:stop_bridge
tasklist /FI "IMAGENAME eq CodexLanBridge.exe" 2>NUL | find /I "CodexLanBridge.exe" >NUL
if errorlevel 1 (
  echo Bridge is already stopped.
) else (
  powershell.exe -NoProfile -Command "$items=Get-Process -Name CodexLanBridge -ErrorAction SilentlyContinue; foreach($item in $items){taskkill.exe /PID $item.Id /T /F ^| Out-Null}"
  echo Bridge stopped.
)
timeout /t 2 /nobreak >NUL
goto menu

:show_pairing
cls
echo ==========================================
echo          Codex LAN Console Pairing
echo ==========================================
call :status
echo.
if exist "%PAIRING_FILE%" (
  type "%PAIRING_FILE%"
) else (
  echo No active pairing code yet. Start the bridge first.
)
echo.
pause
goto menu
