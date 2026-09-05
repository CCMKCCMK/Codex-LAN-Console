[CmdletBinding()]
param(
    [switch]$ElevatedRelaunch,
    [string]$ExpectedUserSid
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$protectedHelpers = Join-Path $root 'Protected-Release.ps1'
if (Test-Path -LiteralPath $protectedHelpers) {
    . $protectedHelpers
}
elseif (-not (Get-Command Test-CodexElevated -ErrorAction SilentlyContinue)) {
    function Test-CodexElevated {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
}

$taskName = 'Codex LAN Console'
$browserTaskName = 'Codex LAN Console Chrome Bootstrap'
$currentUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if ($ExpectedUserSid -and $ExpectedUserSid -ne $currentUserSid) {
    throw 'Uninstall must be approved with the same Windows account that owns Codex LAN Console.'
}

$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
$protectedRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'Codex LAN Console\Bridge'))
$administratorCredentialRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'CodexLanConsole\AdminCredentials'))
$administratorCredentialDirectory = [IO.Path]::GetFullPath(
    (Join-Path $administratorCredentialRoot $currentUserSid))
$administratorFirewallExists = [bool](
    Get-NetFirewallRule -Name 'Codex-LAN-Console-Administrator' -ErrorAction SilentlyContinue)
$needsElevation = ($task -and [string]$task.Principal.RunLevel -eq 'Highest') -or
    (Test-Path -LiteralPath $protectedRoot) -or
    (Test-Path -LiteralPath $administratorCredentialDirectory) -or
    $administratorFirewallExists

if ($needsElevation -and -not (Test-CodexElevated)) {
    if ($ElevatedRelaunch) { throw 'The uninstall helper did not receive an elevated token.' }
    $quotedScript = '"' + $PSCommandPath.Replace('"', '""') + '"'
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File $quotedScript -ElevatedRelaunch -ExpectedUserSid $currentUserSid"
    try {
        $process = Start-Process `
            -FilePath (Join-Path $PSHOME 'powershell.exe') `
            -Verb RunAs `
            -ArgumentList $arguments `
            -Wait `
            -PassThru
        exit $process.ExitCode
    }
    catch {
        Write-Error "Uninstall was cancelled or could not start: $($_.Exception.Message)"
        exit 5
    }
}

if ($task) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}
$browserTask = Get-ScheduledTask -TaskName $browserTaskName -ErrorAction SilentlyContinue
if ($browserTask) {
    Stop-ScheduledTask -TaskName $browserTaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $browserTaskName -Confirm:$false
}
Get-Process -Name 'CodexLanBridge' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

if ($administratorFirewallExists) {
    Get-NetFirewallRule -Name 'Codex-LAN-Console-Administrator' -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction Stop
}

$programFilesPrefix = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd(
    [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $protectedRoot.StartsWith($programFilesPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    (Split-Path -Leaf $protectedRoot) -ne 'Bridge' -or
    (Split-Path -Leaf (Split-Path -Parent $protectedRoot)) -ne 'Codex LAN Console') {
    throw "Protected release cleanup path is invalid: $protectedRoot"
}
if (Test-Path -LiteralPath $protectedRoot) {
    Remove-Item -LiteralPath $protectedRoot -Recurse -Force
}

$administratorCredentialPrefix = $administratorCredentialRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $administratorCredentialDirectory.StartsWith(
        $administratorCredentialPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    (Split-Path -Leaf $administratorCredentialDirectory) -ne $currentUserSid) {
    throw "Administrator credential cleanup path is invalid: $administratorCredentialDirectory"
}
if (Test-Path -LiteralPath $administratorCredentialDirectory) {
    Remove-Item -LiteralPath $administratorCredentialDirectory -Recurse -Force
}

$shortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'Codex LAN Console.lnk'
if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force }
$data = Join-Path $env:LOCALAPPDATA 'CodexLanConsole'
if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }

Write-Host 'Codex LAN Console, local tokens, Administrator credentials, firewall rule, and protected releases were removed.'
Write-Host 'You may now delete the CodexLanConsole project directory.'
