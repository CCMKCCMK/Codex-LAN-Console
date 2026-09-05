param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    # Internal marker used only after this script relaunches itself through UAC.
    [switch]$ElevatedRelaunch,

    [string]$ExpectedUserSid
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $root 'Protected-Release.ps1')
$currentUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if ($ExpectedUserSid -and $ExpectedUserSid -ne $currentUserSid) {
    throw 'Administrator deployment must be approved with the same Windows account that owns the Console task.'
}

function Invoke-ElevatedDeployment {
    $quotedScript = '"' + $PSCommandPath.Replace('"', '""') + '"'
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File $quotedScript -Version $Version -ElevatedRelaunch -ExpectedUserSid $currentUserSid"
    try {
        $process = Start-Process `
            -FilePath (Join-Path $PSHOME 'powershell.exe') `
            -Verb RunAs `
            -WindowStyle Hidden `
            -ArgumentList $arguments `
            -Wait `
            -PassThru
        exit $process.ExitCode
    }
    catch {
        Write-Error "Administrator deployment was cancelled or could not start: $($_.Exception.Message)"
        exit 5
    }
}

$project = Join-Path $root 'backend\bridge\CodexLanBridge.csproj'
$target = Join-Path $root "release\WindowsBridge-$Version"
$exe = Join-Path $target 'CodexLanBridge.exe'
$dataDirectory = Join-Path $env:LOCALAPPDATA 'CodexLanConsole'
$pointer = Join-Path $dataDirectory 'current-executable.txt'
$pointerTemporary = "$pointer.new"
$pauseFile = Join-Path $dataDirectory 'manual-stop.flag'
$taskName = 'Codex LAN Console'
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if (-not $task) { throw "Always-on task is missing. Run Install-Autostart.ps1 first." }
$runLevelBeforeDeploy = [string]$task.Principal.RunLevel
if ($runLevelBeforeDeploy -notin @('Limited', 'Highest')) {
    throw "Unexpected scheduled-task run level: $runLevelBeforeDeploy"
}
if ($runLevelBeforeDeploy -eq 'Highest' -and -not (Test-CodexElevated)) {
    if ($ElevatedRelaunch) {
        throw 'The elevated deployment helper did not receive an elevated token.'
    }
    Invoke-ElevatedDeployment
}

if (Test-Path -LiteralPath $target) {
    $releaseRoot = [IO.Path]::GetFullPath((Join-Path $root 'release')) + [IO.Path]::DirectorySeparatorChar
    if (-not [IO.Path]::GetFullPath($target).StartsWith($releaseRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to replace a path outside this repository release directory.'
    }
    $runningFromTarget = Get-Process -Name CodexLanBridge -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($target, [StringComparison]::OrdinalIgnoreCase) }
    if ($runningFromTarget) { throw "Version $Version is already running from $target" }
    Remove-Item -LiteralPath $target -Recurse -Force
}

# The current Bridge remains online for the entire build. Nothing below changes
# the scheduled task until the new executable is complete and verified.
dotnet publish $project -c Release -r win-x64 --self-contained true -o $target
if (-not (Test-Path -LiteralPath $exe)) { throw "Published executable is missing: $exe" }
$publishedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
if (-not $publishedVersion -or
    ($publishedVersion -ne $Version -and
        -not $publishedVersion.StartsWith("$Version+", [StringComparison]::Ordinal))) {
    throw "Published version '$publishedVersion' does not match requested version '$Version'."
}

$taskTarget = $target
$taskExe = $exe
if ($runLevelBeforeDeploy -eq 'Highest') {
    $taskTarget = Install-CodexProtectedRelease -SourceDirectory $target -Version $Version
    $taskExe = Join-Path $taskTarget 'CodexLanBridge.exe'
    $protectedPrefix = (Get-CodexProtectedBridgeRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if (-not ([IO.Path]::GetFullPath($taskExe)).StartsWith(
        $protectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Highest task executable is outside the protected release root: $taskExe"
    }
    Set-CodexAdministratorFirewallRule -ExecutablePath $taskExe
}
else {
    $releasePrefix = ([IO.Path]::GetFullPath((Join-Path $root 'release'))).TrimEnd(
        [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not ([IO.Path]::GetFullPath($taskExe)).StartsWith(
        $releasePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Limited task executable is outside the repository release root: $taskExe"
    }
}

$action = New-ScheduledTaskAction -Execute $taskExe -WorkingDirectory $taskTarget
Set-ScheduledTask -TaskName $taskName -Action $action | Out-Null
$runLevelAfterDeploy = [string](Get-ScheduledTask -TaskName $taskName).Principal.RunLevel
if ($runLevelAfterDeploy -ne $runLevelBeforeDeploy) {
    throw "Deployment unexpectedly changed the scheduled task run level from '$runLevelBeforeDeploy' to '$runLevelAfterDeploy'."
}

[void][IO.Directory]::CreateDirectory($dataDirectory)
# Keep the user-profile pointer on the repository copy even in Highest mode. It is the
# rollback/Standard-mode source; the Highest task action points only at Program Files.
[IO.File]::WriteAllText($pointerTemporary, $exe, [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $pointerTemporary -Destination $pointer -Force
Enable-ScheduledTask -TaskName $taskName | Out-Null

if (-not (Test-Path -LiteralPath $pauseFile)) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Get-Process -Name CodexLanBridge -ErrorAction SilentlyContinue |
        Where-Object { -not $_.Path -or -not $_.Path.StartsWith($taskTarget, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    for ($attempt = 0; $attempt -lt 50 -and (Get-Process -Name CodexLanBridge -ErrorAction SilentlyContinue); $attempt++) {
        Start-Sleep -Milliseconds 100
    }
    Start-ScheduledTask -TaskName $taskName

    $healthy = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Milliseconds 500
        try {
            $health = Invoke-RestMethod -Uri 'http://127.0.0.1:8787/api/health' -TimeoutSec 2
            if ($health.ok -and $health.version -eq $Version) { $healthy = $true; break }
        }
        catch { }
    }
    if (-not $healthy) {
        throw "Version $Version was installed and the recovery task remains enabled, but its health check did not become ready in time."
    }
}

Write-Host "Codex LAN Console $Version installed from: $target"
if ($runLevelAfterDeploy -eq 'Highest') {
    Write-Host "Protected administrator release: $taskTarget"
}
Write-Host 'The scheduled task remained enabled throughout the deployment.'
Write-Host "Administrator mode was preserved: $runLevelAfterDeploy"
