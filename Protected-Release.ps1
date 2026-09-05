$ErrorActionPreference = 'Stop'

function Test-CodexElevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-CodexProtectedBridgeRoot {
    if (-not $env:ProgramFiles) { throw 'Program Files is unavailable.' }
    return [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'Codex LAN Console\Bridge'))
}

$script:CodexAdministratorFirewallRuleName = 'Codex-LAN-Console-Administrator'

function Set-CodexAdministratorFirewallRule {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath
    )

    if (-not (Test-CodexElevated)) { throw 'Administrator firewall setup requires elevation.' }
    $executable = [IO.Path]::GetFullPath($ExecutablePath)
    $protectedPrefix = (Get-CodexProtectedBridgeRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if (-not $executable.StartsWith($protectedPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Administrator firewall executable is not a protected Bridge release: $executable"
    }

    Get-NetFirewallRule -Name $script:CodexAdministratorFirewallRuleName -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction Stop
    New-NetFirewallRule `
        -Name $script:CodexAdministratorFirewallRuleName `
        -DisplayName 'Codex LAN Console Administrator Mode' `
        -Description 'Allows the protected Administrator Mode Bridge only through the Tailscale IPv4 range.' `
        -Direction Inbound `
        -Action Allow `
        -Enabled True `
        -Profile Any `
        -Program $executable `
        -Protocol TCP `
        -LocalPort 8787 `
        -RemoteAddress '100.64.0.0/10' | Out-Null
}

function Remove-CodexAdministratorFirewallRule {
    if (-not (Test-CodexElevated)) { throw 'Removing the Administrator firewall rule requires elevation.' }
    Get-NetFirewallRule -Name $script:CodexAdministratorFirewallRuleName -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction Stop
}

function Get-CodexReleaseVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory
    )

    $leaf = Split-Path -Leaf ([IO.Path]::GetFullPath($SourceDirectory))
    if ($leaf -notmatch '^WindowsBridge-(\d+\.\d+\.\d+)$') {
        throw "Release directory is not versioned correctly: $SourceDirectory"
    }
    return $Matches[1]
}

function Get-CodexReleaseManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Directory
    )

    $root = [IO.Path]::GetFullPath($Directory).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $prefix = $root + [IO.Path]::DirectorySeparatorChar
    $manifest = [ordered]@{}
    $entries = @(Get-ChildItem -LiteralPath $root -Recurse -Force)
    foreach ($entry in $entries) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Protected releases cannot contain reparse points: $($entry.FullName)"
        }
    }
    foreach ($file in $entries | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName) {
        $relative = $file.FullName.Substring($prefix.Length)
        $manifest[$relative] = [PSCustomObject]@{
            Length = $file.Length
            Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
    if ($manifest.Count -eq 0) { throw "Release directory is empty: $root" }
    return $manifest
}

function Assert-CodexReleaseMatches {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,

        [Parameter(Mandatory = $true)]
        [string]$DestinationDirectory
    )

    $source = Get-CodexReleaseManifest -Directory $SourceDirectory
    $destination = Get-CodexReleaseManifest -Directory $DestinationDirectory
    if ($source.Count -ne $destination.Count) {
        throw "Protected release file count mismatch ($($source.Count) source, $($destination.Count) protected)."
    }
    foreach ($relative in $source.Keys) {
        if (-not $destination.Contains($relative)) {
            throw "Protected release is missing: $relative"
        }
        if ($source[$relative].Length -ne $destination[$relative].Length -or
            $source[$relative].Sha256 -ne $destination[$relative].Sha256) {
            throw "Protected release hash mismatch: $relative"
        }
    }
}

function New-CodexProtectedAcl {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Directory,

        [Parameter(Mandatory = $true)]
        [Security.Principal.SecurityIdentifier]$UserSid
    )

    $systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $administratorsSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $acl = if ($Directory) {
        [Security.AccessControl.DirectorySecurity]::new()
    }
    else {
        [Security.AccessControl.FileSecurity]::new()
    }
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner($administratorsSid)
    $inheritance = if ($Directory) {
        [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    }
    else {
        [Security.AccessControl.InheritanceFlags]::None
    }
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    [void]$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $systemSid, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, $propagation, $allow))
    [void]$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $administratorsSid, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, $propagation, $allow))
    [void]$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $UserSid, [Security.AccessControl.FileSystemRights]::ReadAndExecute, $inheritance, $propagation, $allow))
    return $acl
}

function Set-CodexProtectedAcl {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-CodexElevated)) { throw 'Protected release ACLs require an elevated process.' }
    $userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $root = [IO.Path]::GetFullPath($Path)
    $directories = @((Get-Item -LiteralPath $root -Force)) +
        @(Get-ChildItem -LiteralPath $root -Directory -Recurse -Force | Sort-Object FullName)
    foreach ($directory in $directories) {
        Set-Acl -LiteralPath $directory.FullName -AclObject (New-CodexProtectedAcl -Directory $true -UserSid $userSid)
    }
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse -Force | Sort-Object FullName) {
        Set-Acl -LiteralPath $file.FullName -AclObject (New-CodexProtectedAcl -Directory $false -UserSid $userSid)
    }
}

function Assert-CodexProtectedAcl {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $requiredFull = @('S-1-5-18', 'S-1-5-32-544')
    $items = @((Get-Item -LiteralPath $Path -Force)) +
        @(Get-ChildItem -LiteralPath $Path -Recurse -Force)
    foreach ($item in $items) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Protected path contains a reparse point: $($item.FullName)"
        }
        $acl = Get-Acl -LiteralPath $item.FullName
        if (-not $acl.AreAccessRulesProtected) {
            throw "ACL inheritance is still enabled: $($item.FullName)"
        }
        $ownerSid = ([Security.Principal.NTAccount]$acl.Owner).Translate(
            [Security.Principal.SecurityIdentifier]).Value
        if ($ownerSid -ne 'S-1-5-32-544') {
            throw "Protected path is not owned by Administrators: $($item.FullName)"
        }
        $rules = @($acl.GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier]))
        foreach ($rule in $rules) {
            $sid = $rule.IdentityReference.Value
            if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
                $sid -notin @($requiredFull + $userSid)) {
                throw "Unexpected protected-release ACL entry on $($item.FullName): $sid"
            }
        }
        foreach ($sid in $requiredFull) {
            $rule = $rules | Where-Object { $_.IdentityReference.Value -eq $sid } | Select-Object -First 1
            if (-not $rule -or
                (($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -ne
                    [Security.AccessControl.FileSystemRights]::FullControl)) {
                throw "Required full-control ACL is missing for $sid on $($item.FullName)."
            }
        }
        $userRule = $rules | Where-Object { $_.IdentityReference.Value -eq $userSid } | Select-Object -First 1
        if (-not $userRule -or
            (($userRule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::ReadAndExecute) -ne
                [Security.AccessControl.FileSystemRights]::ReadAndExecute)) {
            throw "The current user is missing read/execute access on $($item.FullName)."
        }
        $writeRights = [Security.AccessControl.FileSystemRights]::WriteData `
            -bor [Security.AccessControl.FileSystemRights]::AppendData `
            -bor [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes `
            -bor [Security.AccessControl.FileSystemRights]::WriteAttributes `
            -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles `
            -bor [Security.AccessControl.FileSystemRights]::Delete `
            -bor [Security.AccessControl.FileSystemRights]::ChangePermissions `
            -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
        if (($userRule.FileSystemRights -band $writeRights) -ne 0) {
            throw "The current user still has write access to protected file: $($item.FullName)"
        }
    }
}

function Install-CodexProtectedRelease {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,

        [Parameter(Mandatory = $true)]
        [ValidatePattern('^\d+\.\d+\.\d+$')]
        [string]$Version
    )

    if (-not (Test-CodexElevated)) { throw 'Installing a protected release requires elevation.' }
    $source = [IO.Path]::GetFullPath($SourceDirectory)
    if ((Get-CodexReleaseVersion -SourceDirectory $source) -ne $Version) {
        throw "Source directory version does not match requested version $Version."
    }
    $sourceExe = Join-Path $source 'CodexLanBridge.exe'
    if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
        throw "Bridge executable is missing: $sourceExe"
    }
    $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($sourceExe).ProductVersion
    if (-not $productVersion -or
        ($productVersion -ne $Version -and
            -not $productVersion.StartsWith("$Version+", [StringComparison]::Ordinal))) {
        throw "Bridge product version '$productVersion' does not match $Version."
    }

    # Preflight the untrusted user-profile source before any elevated recursive copy.
    # The destination is hashed again below, so copy-time changes are also rejected.
    [void](Get-CodexReleaseManifest -Directory $source)

    $protectedRoot = Get-CodexProtectedBridgeRoot
    [void][IO.Directory]::CreateDirectory($protectedRoot)
    Set-CodexProtectedAcl -Path $protectedRoot
    $target = Join-Path $protectedRoot $Version
    if (Test-Path -LiteralPath $target) {
        Assert-CodexReleaseMatches -SourceDirectory $source -DestinationDirectory $target
        Set-CodexProtectedAcl -Path $target
        Assert-CodexProtectedAcl -Path $target
        return $target
    }

    $staging = Join-Path $protectedRoot ('.staging-' + $Version + '-' + [Guid]::NewGuid().ToString('N'))
    try {
        [void][IO.Directory]::CreateDirectory($staging)
        foreach ($item in Get-ChildItem -LiteralPath $source -Force) {
            Copy-Item -LiteralPath $item.FullName -Destination $staging -Recurse -Force
        }
        Assert-CodexReleaseMatches -SourceDirectory $source -DestinationDirectory $staging
        Move-Item -LiteralPath $staging -Destination $target
        Set-CodexProtectedAcl -Path $target
        Assert-CodexReleaseMatches -SourceDirectory $source -DestinationDirectory $target
        Assert-CodexProtectedAcl -Path $target
        return $target
    }
    finally {
        if (Test-Path -LiteralPath $staging) {
            Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-CodexAdministratorTaskStatus {
    param(
        [string]$TaskName = 'Codex LAN Console',

        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot
    )

    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if (-not $task) { return 'MISSING' }
    $runLevel = [string]$task.Principal.RunLevel
    $taskExe = [IO.Path]::GetFullPath([string]$task.Actions[0].Execute)
    if ($runLevel -ne 'Highest') {
        $releasePrefix = ([IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'release'))).TrimEnd(
            [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if ($taskExe.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            return 'STANDARD (REPOSITORY RELEASE)'
        }
        return 'STANDARD (UNEXPECTED PATH)'
    }

    $protectedPrefix = (Get-CodexProtectedBridgeRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if (-not $taskExe.StartsWith($protectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        return 'UNSAFE (HIGHEST OUTSIDE PROGRAM FILES)'
    }
    if (-not (Test-Path -LiteralPath $taskExe -PathType Leaf)) {
        return 'BROKEN (PROTECTED EXE MISSING)'
    }

    $userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $writeRights = [Security.AccessControl.FileSystemRights]::WriteData `
        -bor [Security.AccessControl.FileSystemRights]::AppendData `
        -bor [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes `
        -bor [Security.AccessControl.FileSystemRights]::WriteAttributes `
        -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles `
        -bor [Security.AccessControl.FileSystemRights]::Delete `
        -bor [Security.AccessControl.FileSystemRights]::ChangePermissions `
        -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
    foreach ($itemPath in @((Split-Path -Parent $taskExe), $taskExe)) {
        $acl = Get-Acl -LiteralPath $itemPath
        if (-not $acl.AreAccessRulesProtected) { return 'UNSAFE (ACL INHERITANCE ENABLED)' }
        try {
            $ownerSid = ([Security.Principal.NTAccount]$acl.Owner).Translate(
                [Security.Principal.SecurityIdentifier]).Value
        }
        catch { return 'UNSAFE (ACL OWNER UNVERIFIED)' }
        if ($ownerSid -ne 'S-1-5-32-544') { return 'UNSAFE (ACL OWNER)' }
        $rules = @($acl.GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier]))
        $userRule = $rules | Where-Object {
            $_.IdentityReference.Value -eq $userSid -and
            $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow
        } | Select-Object -First 1
        if (-not $userRule -or ($userRule.FileSystemRights -band $writeRights) -ne 0) {
            return 'UNSAFE (CURRENT USER CAN WRITE)'
        }
    }
    return 'ENABLED (PROTECTED)'
}
