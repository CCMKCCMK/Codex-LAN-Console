@echo off
setlocal EnableExtensions
title Codex LAN Console Manager
set "INSTALL_ROOT_FILE=%LOCALAPPDATA%\CodexLanConsole\install-root.txt"
if not exist "%INSTALL_ROOT_FILE%" goto install_missing
set /p "SAVED_ROOT="<"%INSTALL_ROOT_FILE%"
if not defined SAVED_ROOT goto install_missing
set "ROOT=%SAVED_ROOT%"
set "CURRENT_EXE_FILE=%LOCALAPPDATA%\CodexLanConsole\current-executable.txt"
if not exist "%CURRENT_EXE_FILE%" goto install_missing
set /p "BRIDGE="<"%CURRENT_EXE_FILE%"
for %%I in ("%BRIDGE%") do set "WORKDIR=%%~dpI"
if not exist "%BRIDGE%" goto release_missing
set "PAIRING_FILE=%LOCALAPPDATA%\CodexLanConsole\pairing.txt"
set "WIDGET_FILE=%LOCALAPPDATA%\CodexLanConsole\quota-widget-settings.json"
set "LOCAL_CONTROL_TOKEN_FILE=%LOCALAPPDATA%\CodexLanConsole\local-control-token.txt"
set "PAUSE_FILE=%LOCALAPPDATA%\CodexLanConsole\manual-stop.flag"
set "TASK_NAME=Codex LAN Console"
for /f "usebackq delims=" %%I in (`powershell.exe -NoProfile -Command "$p=Get-ScheduledTask -TaskName 'Codex LAN Console' -ErrorAction SilentlyContinue; if($p){'Codex LAN Console'}else{$s=Get-ScheduledTask -TaskName 'Codex LAN Console Standard' -ErrorAction SilentlyContinue; if($s){'Codex LAN Console Standard'}}"`) do set "TASK_NAME=%%I"
set "AUTOSTART_SCRIPT=%ROOT%\Install-Autostart.ps1"
set "DIRECT_COMMAND="
if /I "%~1"=="status" goto status
if /I "%~1"=="start" goto direct_start
if /I "%~1"=="stop" goto direct_stop
if /I "%~1"=="repair" goto direct_repair
if /I "%~1"=="cpu-status" goto direct_cpu_status
if /I "%~1"=="cpu-on" goto direct_cpu_on
if /I "%~1"=="cpu-monitor" goto direct_cpu_monitor
if /I "%~1"=="cpu-off" goto direct_cpu_off
if /I "%~1"=="cpu-repair" goto direct_cpu_repair
if /I "%~1"=="admin-status" goto direct_admin_status
if /I "%~1"=="admin-on" goto direct_admin_on
if /I "%~1"=="admin-off" goto direct_admin_off
if /I "%~1"=="admin-pair" goto direct_admin_pair
if /I "%~1"=="admin-reset" goto direct_admin_pair
goto menu

:install_missing
echo Current Codex LAN Console installation was not found.
echo Missing installation marker or current executable pointer.
pause
exit /b 2

:release_missing
echo Current release executable was not found:
echo %BRIDGE%
echo Rebuild or reinstall the latest release before using this manager.
pause
exit /b 2

:direct_start
set "DIRECT_COMMAND=1"
goto start_bridge

:direct_stop
if /I not "%~2"=="CONFIRM" (
  echo Direct stop was refused. Use: "%~nx0" stop CONFIRM
  exit /b 4
)
set "DIRECT_COMMAND=1"
goto stop_bridge

:direct_repair
set "DIRECT_COMMAND=1"
goto repair_widget

:direct_cpu_status
set "DIRECT_COMMAND=1"
goto cpu_status

:direct_cpu_on
set "DIRECT_COMMAND=1"
goto cpu_on

:direct_cpu_monitor
set "DIRECT_COMMAND=1"
goto cpu_monitor

:direct_cpu_off
set "DIRECT_COMMAND=1"
goto cpu_off

:direct_cpu_repair
set "DIRECT_COMMAND=1"
goto cpu_repair

:direct_admin_status
call :show_admin_status
exit /b 0

:direct_admin_on
if /I not "%~2"=="CONFIRM" (
  echo Direct Administrator-mode enable was refused. Use: "%~nx0" admin-on CONFIRM
  exit /b 4
)
set "DIRECT_COMMAND=1"
goto admin_enable_confirmed

:direct_admin_off
if /I not "%~2"=="CONFIRM" (
  echo Direct Administrator-mode disable was refused. Use: "%~nx0" admin-off CONFIRM
  exit /b 4
)
set "DIRECT_COMMAND=1"
goto admin_disable_confirmed

:direct_admin_pair
if /I not "%~2"=="ADD" (
  echo Direct Administrator pairing request was refused. Use: "%~nx0" admin-pair ADD
  exit /b 4
)
set "DIRECT_COMMAND=1"
goto admin_pair_confirmed

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
echo [5] Repair/restart Windows quota widget
echo [6] CPU health and frequency guard
echo [7] Administrator mode
echo [0] Exit
echo.
set /p "ACTION=Choose an option: "
if "%ACTION%"=="1" goto start_bridge
if "%ACTION%"=="2" goto stop_bridge
if "%ACTION%"=="3" goto show_pairing
if "%ACTION%"=="4" goto menu
if "%ACTION%"=="5" goto repair_widget
if "%ACTION%"=="6" goto cpu_menu
if "%ACTION%"=="7" goto admin_menu
if "%ACTION%"=="0" exit /b 0
goto menu

:status
set "TAILSCALE_IP="
set "LAN_IP="
set "WIDGET_STATE=ON"
set "KEEPALIVE_STATE=MISSING"
for /f "usebackq delims=" %%I in (`powershell.exe -NoProfile -Command "Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceAlias -eq 'Tailscale' -and $_.AddressState -eq 'Preferred' } | Select-Object -First 1 -ExpandProperty IPAddress"`) do set "TAILSCALE_IP=%%I"
for /f "usebackq delims=" %%I in (`powershell.exe -NoProfile -Command "Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.AddressState -eq 'Preferred' -and $_.InterfaceAlias -notlike '*Tailscale*' -and $_.InterfaceAlias -notlike 'vEthernet*' -and $_.InterfaceAlias -notlike '*Loopback*' -and $_.InterfaceAlias -ne 'Meta' -and ($_.IPAddress -like '192.168.*' -or $_.IPAddress -like '10.*' -or $_.IPAddress -like '172.*') } | Select-Object -First 1 -ExpandProperty IPAddress"`) do set "LAN_IP=%%I"
set "BRIDGE_STATE=PAUSED BY USER"
if not exist "%PAUSE_FILE%" (
  tasklist /FI "IMAGENAME eq CodexLanBridge.exe" 2>NUL | find /I "CodexLanBridge.exe" >NUL
  if errorlevel 1 (set "BRIDGE_STATE=STOPPED") else (set "BRIDGE_STATE=RUNNING")
)
call :load_admin_status
echo Status: %BRIDGE_STATE%
if defined TAILSCALE_IP (
  echo Tailscale address: http://%TAILSCALE_IP%:8787
) else (
  echo Tailscale address: unavailable
)
if /I "%ADMIN_MODE:~0,8%"=="STANDARD" (
  if defined LAN_IP (
    echo LAN address:       http://%LAN_IP%:8787
  ) else (
    echo LAN address:       unavailable
  )
) else (
  echo LAN address:       disabled in Administrator mode
)
for /f "usebackq delims=" %%I in (`powershell.exe -NoProfile -Command "$p='%WIDGET_FILE%'; if(Test-Path -LiteralPath $p){try{$v=ConvertFrom-Json -InputObject (Get-Content -LiteralPath $p -Raw); if($v.enabled){'ON'}else{'OFF'}}catch{'ON'}}else{'ON'}"`) do set "WIDGET_STATE=%%I"
echo Windows widget:     %WIDGET_STATE%
for /f "usebackq delims=" %%I in (`powershell.exe -NoProfile -Command "$t=Get-ScheduledTask -TaskName '%TASK_NAME%' -ErrorAction SilentlyContinue; if($null -eq $t){'MISSING'}elseif($t.Settings.Enabled){'ENABLED'}else{'DISABLED'}"`) do set "KEEPALIVE_STATE=%%I"
echo Always-on recovery: %KEEPALIVE_STATE%
echo Administrator mode: %ADMIN_MODE%
call :show_cpu_status
exit /b 0

:show_admin_status
call :load_admin_status
echo Administrator mode: %ADMIN_MODE%
exit /b 0

:load_admin_status
set "ADMIN_MODE=MISSING"
for /f "usebackq delims=" %%I in (`powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ". (Join-Path $env:ROOT 'Protected-Release.ps1'); Get-CodexAdministratorTaskStatus -TaskName '%TASK_NAME%' -RepositoryRoot $env:ROOT"`) do set "ADMIN_MODE=%%I"
exit /b 0

:admin_menu
cls
echo ==========================================
echo              Administrator Mode
echo ==========================================
call :show_admin_status
echo.
echo When enabled, work started from the phone inherits administrator rights.
echo A hash-checked Bridge copy is placed in an ACL-protected Program Files folder.
echo WARNING: Codex and project commands then run with full administrator rights.
echo This private build is not Authenticode-signed; enable it only from a trusted local copy.
echo Windows asks for permission once while enabling or disabling this mode.
echo The secure Windows permission screen still requires a local click that one time.
echo.
echo [1] Enable Administrator mode
echo [2] Return to Standard mode
echo [3] Add another Administrator phone
echo [0] Back
echo.
set /p "ADMIN_ACTION=Choose an option: "
if "%ADMIN_ACTION%"=="1" goto admin_enable
if "%ADMIN_ACTION%"=="2" goto admin_disable
if "%ADMIN_ACTION%"=="3" goto admin_pair
if "%ADMIN_ACTION%"=="0" goto menu
goto admin_menu

:admin_pair
echo.
echo This keeps every existing Administrator phone and opens a ten-minute pairing window.
echo The window closes immediately after one new phone is paired.
set /p "ADMIN_CONFIRM=Type ADD to continue: "
if /I not "%ADMIN_CONFIRM%"=="ADD" goto admin_menu
:admin_pair_confirmed
if not exist "%AUTOSTART_SCRIPT%" (
  echo Administrator-mode installer was not found:
  echo %AUTOSTART_SCRIPT%
  if /I "%~1"=="admin-pair" exit /b 3
  if /I "%~1"=="admin-reset" exit /b 3
  if defined DIRECT_COMMAND exit /b 3
  pause
  goto admin_menu
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%AUTOSTART_SCRIPT%" -RunLevel Preserve -OpenAdministratorPairing -RestartBridge
set "ADMIN_PAIR_EXIT=%ERRORLEVEL%"
if "%ADMIN_PAIR_EXIT%"=="0" goto admin_pair_succeeded
echo Administrator pairing window was not opened. The Windows confirmation may have been cancelled.
if /I "%~1"=="admin-pair" exit /b 5
if /I "%~1"=="admin-reset" exit /b 5
if defined DIRECT_COMMAND exit /b 5
pause
goto admin_menu
:admin_pair_succeeded
echo Administrator pairing window is open. Existing phones remain valid.
echo Return to the main menu and choose option 3 to view the new code and expiry.
if /I "%~1"=="admin-pair" exit /b 0
if /I "%~1"=="admin-reset" exit /b 0
if defined DIRECT_COMMAND exit /b 0
pause
goto admin_menu

:admin_enable
echo.
echo This installs a hash-checked, protected copy and grants administrator rights to
echo all work started through Codex LAN Console.
set /p "ADMIN_CONFIRM=Type ADMIN to continue: "
if /I not "%ADMIN_CONFIRM%"=="ADMIN" goto admin_menu
:admin_enable_confirmed
if not exist "%AUTOSTART_SCRIPT%" (
  echo Administrator-mode installer was not found:
  echo %AUTOSTART_SCRIPT%
  if defined DIRECT_COMMAND exit /b 3
  pause
  goto admin_menu
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%AUTOSTART_SCRIPT%" -RunLevel Highest -RestartBridge
if errorlevel 1 (
  echo Administrator mode was not changed. The Windows confirmation may have been cancelled.
  if defined DIRECT_COMMAND exit /b 5
  pause
  goto admin_menu
)
echo Administrator mode is enabled. Phone-started work can now perform elevated operations.
if defined DIRECT_COMMAND exit /b 0
pause
goto admin_menu

:admin_disable
echo.
echo Returning to Standard mode removes inherited administrator rights.
set /p "ADMIN_CONFIRM=Type STANDARD to continue: "
if /I not "%ADMIN_CONFIRM%"=="STANDARD" goto admin_menu
:admin_disable_confirmed
if not exist "%AUTOSTART_SCRIPT%" (
  echo Administrator-mode installer was not found:
  echo %AUTOSTART_SCRIPT%
  if defined DIRECT_COMMAND exit /b 3
  pause
  goto admin_menu
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%AUTOSTART_SCRIPT%" -RunLevel Limited -RestartBridge
if errorlevel 1 (
  echo Standard mode was not restored. The Windows confirmation may have been cancelled.
  if defined DIRECT_COMMAND exit /b 5
  pause
  goto admin_menu
)
echo Standard mode is active.
if defined DIRECT_COMMAND exit /b 0
pause
goto admin_menu

:show_cpu_status
set "CPU_STATUS_SEEN="
call :load_local_control_token
if not defined LOCAL_CONTROL_TOKEN goto cpu_status_unavailable
for /f "usebackq delims=" %%I in (`curl.exe -fsS --max-time 1 -H "X-Codex-Local-Control: %LOCAL_CONTROL_TOKEN%" http://127.0.0.1:8787/api/local/cpu/status 2^>NUL`) do (
  echo %%I
  set "CPU_STATUS_SEEN=1"
)
:cpu_status_unavailable
if not defined CPU_STATUS_SEEN echo CPU guard: unavailable
exit /b 0

:load_local_control_token
set "LOCAL_CONTROL_TOKEN="
if exist "%LOCAL_CONTROL_TOKEN_FILE%" set /p "LOCAL_CONTROL_TOKEN="<"%LOCAL_CONTROL_TOKEN_FILE%"
exit /b 0

:cpu_menu
cls
echo ==========================================
echo          CPU Health and Frequency Guard
echo ==========================================
call :show_cpu_status
echo.
echo [1] Refresh detailed status
echo [2] Enable automatic guard
echo [3] Monitor only
echo [4] Turn CPU guard off
echo [5] Repair Windows CPU policy now
echo [6] Save current healthy power policy as baseline
echo [0] Back
echo.
set /p "CPU_ACTION=Choose an option: "
if "%CPU_ACTION%"=="1" goto cpu_status
if "%CPU_ACTION%"=="2" goto cpu_on
if "%CPU_ACTION%"=="3" goto cpu_monitor
if "%CPU_ACTION%"=="4" goto cpu_off
if "%CPU_ACTION%"=="5" goto cpu_repair
if "%CPU_ACTION%"=="6" goto cpu_baseline
if "%CPU_ACTION%"=="0" goto menu
goto cpu_menu

:cpu_status
call :show_cpu_status
if defined DIRECT_COMMAND exit /b 0
echo.
pause
goto cpu_menu

:cpu_on
call :load_local_control_token
curl.exe -sS --max-time 5 -X POST -H "X-Codex-Local-Control: %LOCAL_CONTROL_TOKEN%" http://127.0.0.1:8787/api/local/cpu/mode/AutoGuard
if errorlevel 1 echo CPU guard could not reach the local bridge.
if defined DIRECT_COMMAND exit /b 0
echo.
pause
goto cpu_menu

:cpu_monitor
call :load_local_control_token
curl.exe -sS --max-time 5 -X POST -H "X-Codex-Local-Control: %LOCAL_CONTROL_TOKEN%" http://127.0.0.1:8787/api/local/cpu/mode/Monitor
if errorlevel 1 echo CPU guard could not reach the local bridge.
if defined DIRECT_COMMAND exit /b 0
echo.
pause
goto cpu_menu

:cpu_off
call :load_local_control_token
curl.exe -sS --max-time 5 -X POST -H "X-Codex-Local-Control: %LOCAL_CONTROL_TOKEN%" http://127.0.0.1:8787/api/local/cpu/mode/Off
if errorlevel 1 echo CPU guard could not reach the local bridge.
if defined DIRECT_COMMAND exit /b 0
echo.
pause
goto cpu_menu

:cpu_repair
call :load_local_control_token
curl.exe -sS --max-time 15 -X POST -H "X-Codex-Local-Control: %LOCAL_CONTROL_TOKEN%" http://127.0.0.1:8787/api/local/cpu/repair
if errorlevel 1 echo CPU policy repair could not reach the local bridge.
if defined DIRECT_COMMAND exit /b 0
echo.
pause
goto cpu_menu

:cpu_baseline
echo.
echo Only do this while CPU behavior and power settings are known to be healthy.
set /p "BASELINE_CONFIRM=Type YES to replace the saved baseline: "
if /I not "%BASELINE_CONFIRM%"=="YES" goto cpu_menu
call :load_local_control_token
curl.exe -sS --max-time 15 -X POST -H "X-Codex-Local-Control: %LOCAL_CONTROL_TOKEN%" http://127.0.0.1:8787/api/local/cpu/baseline
if errorlevel 1 echo CPU baseline capture could not reach the local bridge.
echo.
pause
goto cpu_menu

:start_bridge
powershell.exe -NoProfile -Command "Remove-Item -LiteralPath '%PAUSE_FILE%' -Force -ErrorAction SilentlyContinue"
powershell.exe -NoProfile -Command "$t=Get-ScheduledTask -TaskName '%TASK_NAME%' -ErrorAction SilentlyContinue; if($null -eq $t){exit 3}; Enable-ScheduledTask -TaskName '%TASK_NAME%' | Out-Null"
if errorlevel 1 (
  echo Recovery task is missing. Reinstall the latest Codex LAN Console release.
  if defined DIRECT_COMMAND exit /b 3
  pause
  goto menu
)
tasklist /FI "IMAGENAME eq CodexLanBridge.exe" 2>NUL | find /I "CodexLanBridge.exe" >NUL
if not errorlevel 1 (
  echo Bridge is already running.
  if defined DIRECT_COMMAND exit /b 0
  powershell.exe -NoProfile -Command "Start-Sleep -Seconds 2"
  goto menu
)
powershell.exe -NoProfile -Command "Start-ScheduledTask -TaskName '%TASK_NAME%'"
powershell.exe -NoProfile -Command "Start-Sleep -Seconds 2"
echo Bridge started.
if defined DIRECT_COMMAND (
  call :status
  exit /b 0
)
goto show_pairing

:stop_bridge
if defined DIRECT_COMMAND goto stop_confirmed
echo.
echo This pauses phone access and disables automatic recovery until you start it again.
set /p "STOP_CONFIRM=Type STOP to continue: "
if /I not "%STOP_CONFIRM%"=="STOP" goto menu
:stop_confirmed
powershell.exe -NoProfile -Command "$p='%PAUSE_FILE%'; [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($p)); [IO.File]::WriteAllText($p, [DateTimeOffset]::Now.ToString('O'), [Text.UTF8Encoding]::new($false)); $t=Get-ScheduledTask -TaskName '%TASK_NAME%' -ErrorAction SilentlyContinue; if($t){Stop-ScheduledTask -TaskName '%TASK_NAME%' -ErrorAction SilentlyContinue; Disable-ScheduledTask -TaskName '%TASK_NAME%' | Out-Null}; Get-Process -Name CodexLanBridge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; for($i=0;$i -lt 30 -and (Get-Process -Name CodexLanBridge -ErrorAction SilentlyContinue);$i++){Start-Sleep -Milliseconds 100}"
echo Bridge and widget paused explicitly. Automatic recovery is disabled until Start is selected.
if defined DIRECT_COMMAND exit /b 0
powershell.exe -NoProfile -Command "Start-Sleep -Seconds 2"
goto menu

:repair_widget
for /f "usebackq delims=" %%I in (`powershell.exe -NoProfile -Command "$p='%WIDGET_FILE%'; [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($p)); $v=$null; if(Test-Path -LiteralPath $p){try{$v=ConvertFrom-Json -InputObject (Get-Content -LiteralPath $p -Raw)}catch{}}; if($null -eq $v){$v=[pscustomobject]@{enabled=$true;left=$null;top=$null}}; $v.enabled=$true; $v.left=$null; $v.top=$null; Set-Content -LiteralPath $p -Value (ConvertTo-Json -InputObject $v) -Encoding utf8; 'ON'"`) do set "WIDGET_STATE=%%I"
powershell.exe -NoProfile -Command "Remove-Item -LiteralPath '%PAUSE_FILE%' -Force -ErrorAction SilentlyContinue; $t=Get-ScheduledTask -TaskName '%TASK_NAME%' -ErrorAction SilentlyContinue; if($null -eq $t){exit 3}; Enable-ScheduledTask -TaskName '%TASK_NAME%' | Out-Null; Get-Process -Name CodexLanBridge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; for($i=0;$i -lt 50 -and (Get-Process -Name CodexLanBridge -ErrorAction SilentlyContinue);$i++){Start-Sleep -Milliseconds 100}; Start-ScheduledTask -TaskName '%TASK_NAME%'"
if errorlevel 1 (
  echo Repair could not start the current recovery task.
  if defined DIRECT_COMMAND exit /b 3
  pause
  goto menu
)
goto start_bridge

:show_pairing
cls
echo ==========================================
echo          Codex LAN Console Pairing
echo ==========================================
call :status
echo.
set "ACTIVE_PAIRING_FILE=%PAIRING_FILE%"
for /f "usebackq delims=" %%I in (`powershell.exe -NoProfile -Command "$t=Get-ScheduledTask -TaskName '%TASK_NAME%' -ErrorAction SilentlyContinue; if($t -and [string]$t.Principal.RunLevel -eq 'Highest'){$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value; Join-Path $env:ProgramData ('CodexLanConsole\AdminCredentials\'+$sid+'\pairing.txt')}else{Join-Path $env:LOCALAPPDATA 'CodexLanConsole\pairing.txt'}"`) do set "ACTIVE_PAIRING_FILE=%%I"
if exist "%ACTIVE_PAIRING_FILE%" (
  type "%ACTIVE_PAIRING_FILE%"
) else (
  echo No active pairing code yet. Start the bridge first.
)
echo.
pause
goto menu
