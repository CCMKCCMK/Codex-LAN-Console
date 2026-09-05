[CmdletBinding()]
param(
    [ValidateSet('Preserve', 'Limited', 'Highest')]
    [string]$RunLevel = 'Preserve',

    [switch]$RestartBridge,

    [Alias('ResetAdministratorPairing')]
    [switch]$OpenAdministratorPairing,

    # Internal marker used only after the script relaunches itself through UAC.
    [switch]$ElevatedRelaunch,

    [string]$ExpectedUserSid
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptRoot 'Protected-Release.ps1')
$currentUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if ($ExpectedUserSid -and $ExpectedUserSid -ne $currentUserSid) {
    throw 'Administrator mode must be approved with the same Windows account that runs Codex LAN Console.'
}

function Invoke-ElevatedSelf {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('Limited', 'Highest')]
        [string]$RequestedRunLevel,

        [bool]$ShouldRestart,

        [bool]$ShouldOpenAdministratorPairing
    )

    # -File is deliberately passed as a single quoted command line. Start-Process otherwise
    # loses quoting around paths containing spaces before powershell.exe parses the arguments.
    $quotedScript = '"' + $PSCommandPath.Replace('"', '""') + '"'
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File $quotedScript -RunLevel $RequestedRunLevel -ElevatedRelaunch -ExpectedUserSid $currentUserSid"
    if ($ShouldRestart) { $arguments += ' -RestartBridge' }
    if ($ShouldOpenAdministratorPairing) { $arguments += ' -OpenAdministratorPairing' }

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
        Write-Error "Administrator-mode change was cancelled or could not start: $($_.Exception.Message)"
        exit 5
    }
}

$root = $scriptRoot
$dataDirectory = Join-Path $env:LOCALAPPDATA 'CodexLanConsole'
[void][IO.Directory]::CreateDirectory($dataDirectory)
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $root 'release'))
$releasePrefix = $releaseRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
# This pointer deliberately remains the latest repository release. Limited mode runs it
# directly; Highest mode treats it only as input for a hash-checked Program Files copy.
$currentExecutableFile = Join-Path $dataDirectory 'current-executable.txt'
$exe = $null
if (Test-Path -LiteralPath $currentExecutableFile) {
    $pointed = (Get-Content -LiteralPath $currentExecutableFile -Raw).Trim()
    if ($pointed) {
        $pointed = [IO.Path]::GetFullPath($pointed)
        if ($pointed.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $pointed -PathType Leaf)) {
            $exe = $pointed
        }
    }
}
if (-not $exe) {
    $latest = Get-ChildItem -LiteralPath $releaseRoot -Directory -Filter 'WindowsBridge-*' -ErrorAction SilentlyContinue |
        ForEach-Object {
            try {
                $version = [Version]$_.Name.Substring('WindowsBridge-'.Length)
                $candidate = Join-Path $_.FullName 'CodexLanBridge.exe'
                if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                    [PSCustomObject]@{ Version = $version; Exe = $candidate }
                }
            }
            catch { }
        } |
        Sort-Object Version -Descending |
        Select-Object -First 1
    if ($latest) { $exe = $latest.Exe }
}
if (-not $exe) { throw "No versioned Bridge executable was found under: $releaseRoot" }

$manualStopFile = Join-Path $dataDirectory 'manual-stop.flag'
$bridgeWasPaused = Test-Path -LiteralPath $manualStopFile

$taskName = 'Codex LAN Console'
$legacyTaskName = 'Codex LAN Console Standard'
$legacyTask = Get-ScheduledTask -TaskName $legacyTaskName -ErrorAction SilentlyContinue
if ($legacyTask) {
    Stop-ScheduledTask -TaskName $legacyTaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $legacyTaskName -Confirm:$false -ErrorAction SilentlyContinue
}
$existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
$existingRunLevel = if ($existingTask) { [string]$existingTask.Principal.RunLevel } else { $null }
$effectiveRunLevel = if ($RunLevel -eq 'Preserve') {
    if ($existingRunLevel -in @('Limited', 'Highest')) { $existingRunLevel } else { 'Limited' }
}
else {
    $RunLevel
}

# Creating a highest-privilege task, or replacing an existing highest-privilege task,
# requires an elevated registration process. The CLI therefore asks for UAC exactly once
# when the mode changes; routine starts and deployments do not prompt again.
$requiresElevation = $effectiveRunLevel -eq 'Highest' -or $existingRunLevel -eq 'Highest'
if ($requiresElevation -and -not (Test-CodexElevated)) {
    if ($ElevatedRelaunch) {
        throw 'The elevated Administrator-mode helper did not receive an elevated token.'
    }
    Invoke-ElevatedSelf `
        -RequestedRunLevel $effectiveRunLevel `
        -ShouldRestart $RestartBridge.IsPresent `
        -ShouldOpenAdministratorPairing $OpenAdministratorPairing.IsPresent
}

if ($OpenAdministratorPairing) {
    if ($existingRunLevel -ne 'Highest') {
        throw 'Administrator pairing can be opened only while the protected Administrator Mode task is installed.'
    }
    $administratorStatus = Get-CodexAdministratorTaskStatus -TaskName $taskName -RepositoryRoot $root
    if ($administratorStatus -ne 'ENABLED (PROTECTED)') {
        throw "Administrator pairing request refused because the task is not an ACL-protected installation: $administratorStatus"
    }

    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Get-Process -Name 'CodexLanBridge' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    for ($attempt = 0; $attempt -lt 50 -and (Get-Process -Name 'CodexLanBridge' -ErrorAction SilentlyContinue); $attempt++) {
        Start-Sleep -Milliseconds 100
    }

    $credentialRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'CodexLanConsole\AdminCredentials'))
    $credentialPrefix = $credentialRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    $credentialDirectory = [IO.Path]::GetFullPath((Join-Path $credentialRoot $currentUserSid))
    if (-not $credentialDirectory.StartsWith($credentialPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $credentialDirectory) -ne $currentUserSid) {
        throw "Administrator credential path escaped its protected root: $credentialDirectory"
    }
    if (-not (Test-Path -LiteralPath $credentialDirectory -PathType Container)) {
        throw 'The protected Administrator credential directory does not exist. Start Administrator Mode once before adding another device.'
    }
    $credentialDirectoryInfo = Get-Item -LiteralPath $credentialDirectory -Force
    if (($credentialDirectoryInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Administrator credential directory cannot be a reparse point: $credentialDirectory"
    }

    # The request contains no pairing code or device token. The elevated Bridge consumes
    # it once at startup, creates a fresh ten-minute code, and keeps every existing hash.
    $requestFile = Join-Path $credentialDirectory 'open-pairing.request'
    $temporaryRequest = $requestFile + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText(
            $temporaryRequest,
            "Requested: $([DateTimeOffset]::Now.ToString('O'))`r`n",
            [Text.UTF8Encoding]::new($false))
        $requestAcl = New-CodexProtectedAcl `
            -Directory $false `
            -UserSid ([Security.Principal.SecurityIdentifier]::new($currentUserSid))
        Set-Acl -LiteralPath $temporaryRequest -AclObject $requestAcl
        Move-Item -LiteralPath $temporaryRequest -Destination $requestFile -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryRequest) {
            Remove-Item -LiteralPath $temporaryRequest -Force -ErrorAction SilentlyContinue
        }
    }

    if ($RestartBridge -and -not $bridgeWasPaused) {
        Start-ScheduledTask -TaskName $taskName
    }
    Write-Host 'A time-limited Administrator pairing window was opened locally.'
    Write-Host 'Existing paired devices remain authorized. Use manager option 3 to read the new code.'
    exit 0
}

# Installation and mode changes never override an explicit user pause.
$installRootFile = Join-Path $dataDirectory 'install-root.txt'
[IO.File]::WriteAllText($installRootFile, $root, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($currentExecutableFile, $exe, [Text.UTF8Encoding]::new($false))

$taskExe = $exe
$workingDirectory = Split-Path -Parent $taskExe
if ($effectiveRunLevel -eq 'Highest') {
    $version = Get-CodexReleaseVersion -SourceDirectory $workingDirectory
    $workingDirectory = Install-CodexProtectedRelease `
        -SourceDirectory $workingDirectory `
        -Version $version
    $taskExe = Join-Path $workingDirectory 'CodexLanBridge.exe'
    $protectedPrefix = (Get-CodexProtectedBridgeRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if (-not ([IO.Path]::GetFullPath($taskExe)).StartsWith(
        $protectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Highest task executable is outside the protected release root: $taskExe"
    }
    Set-CodexAdministratorFirewallRule -ExecutablePath $taskExe
}
elseif ($existingRunLevel -eq 'Highest') {
    Remove-CodexAdministratorFirewallRule
}
$user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute $taskExe -WorkingDirectory $workingDirectory
$logonTrigger = New-ScheduledTaskTrigger -AtLogOn -User $user
# Task Scheduler does not classify every forced termination as a failure. This minute-level
# heartbeat is therefore the unconditional recovery path; IgnoreNew keeps a healthy instance
# untouched, and the bridge mutex supplies a second single-instance guard.
$heartbeatTrigger = New-ScheduledTaskTrigger `
    -Once `
    -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Minutes 1)
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel $effectiveRunLevel
$settings = New-ScheduledTaskSettingsSet `
    -Hidden `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 999 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew

Register-ScheduledTask `
    -TaskName $taskName `
    -Action $action `
    -Trigger @($logonTrigger, $heartbeatTrigger) `
    -Principal $principal `
    -Settings $settings `
    -Description 'Keeps the Codex LAN Console and quota widget available.' `
    -Force | Out-Null

# Browser UI must always start with the interactive user's limited token. In
# Administrator Mode the Bridge itself is elevated, so launching Chrome from the
# Bridge would create an elevated browser. This on-demand helper keeps that
# privilege boundary intact while still allowing the phone to wake Chrome.
$browserTaskName = 'Codex LAN Console Chrome Bootstrap'
$browserLauncher = Join-Path $root 'Start-CodexChrome.ps1'
if (-not (Test-Path -LiteralPath $browserLauncher -PathType Leaf)) {
    throw "Chrome bootstrap helper was not found: $browserLauncher"
}
$browserArguments = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $browserLauncher + '"'
$browserAction = New-ScheduledTaskAction `
    -Execute (Join-Path $PSHOME 'powershell.exe') `
    -Argument $browserArguments `
    -WorkingDirectory $root
$browserPrincipal = New-ScheduledTaskPrincipal `
    -UserId $user `
    -LogonType Interactive `
    -RunLevel Limited
$browserSettings = New-ScheduledTaskSettingsSet `
    -Hidden `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 2) `
    -MultipleInstances IgnoreNew
$browserTask = New-ScheduledTask `
    -Action $browserAction `
    -Principal $browserPrincipal `
    -Settings $browserSettings `
    -Description 'Starts the configured Chrome browser with standard interactive-user privileges.'
Register-ScheduledTask `
    -TaskName $browserTaskName `
    -InputObject $browserTask `
    -Force | Out-Null

# Remove the old one-shot Startup shortcut so there is only one launch owner.
$legacyShortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'Codex LAN Console.lnk'
if (Test-Path -LiteralPath $legacyShortcut) { Remove-Item -LiteralPath $legacyShortcut -Force }

if ($bridgeWasPaused) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Disable-ScheduledTask -TaskName $taskName | Out-Null
}
else {
    Enable-ScheduledTask -TaskName $taskName | Out-Null
}
if ($RestartBridge -and -not $bridgeWasPaused) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Get-Process -Name 'CodexLanBridge' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    for ($attempt = 0; $attempt -lt 50 -and (Get-Process -Name 'CodexLanBridge' -ErrorAction SilentlyContinue); $attempt++) {
        Start-Sleep -Milliseconds 100
    }
    Start-ScheduledTask -TaskName $taskName
}
elseif (-not $bridgeWasPaused -and -not (Get-Process -Name 'CodexLanBridge' -ErrorAction SilentlyContinue)) {
    Start-ScheduledTask -TaskName $taskName
}

Write-Host "Always-on task installed: $taskName"
if ($effectiveRunLevel -eq 'Highest') {
    Write-Host 'Administrator mode: ENABLED'
    Write-Host "Protected executable: $taskExe"
    Write-Host 'Phone-started Codex work inherits administrator rights without another UAC prompt.'
}
else {
    Write-Host 'Administrator mode: STANDARD'
}
Write-Host 'It starts at sign-in and is restarted automatically after an unexpected exit.'
Write-Host 'Chrome bootstrap: ENABLED (interactive standard-user task)'
