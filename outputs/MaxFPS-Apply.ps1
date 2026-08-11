[CmdletBinding()]
param(
    [string]$ResultPath,
    [switch]$AdoptExistingState,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedUserSid
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$utf8NoBom = New-Object Text.UTF8Encoding($false)
$strictUtf8NoBom = New-Object Text.UTF8Encoding($false, $true)
$systemDirectory = [IO.Path]::GetFullPath([Environment]::SystemDirectory)
$programDataRoot = [IO.Path]::GetFullPath([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData))
if (-not $systemDirectory -or -not $programDataRoot) { throw 'Trusted Windows security roots could not be resolved from the OS.' }
$boostixProgramDataRoot = Join-Path $programDataRoot 'Boostix'
$sessionPowerPlanRoot = Join-Path $boostixProgramDataRoot 'SessionPowerPlan'
$trustedPowerPlanPath = Join-Path $sessionPowerPlanRoot 'trusted-plan.json'
$boostixStateRoot = Join-Path $programDataRoot 'BoostixOptimization'
$legacyStateRoot = Join-Path $programDataRoot 'CodexGamingOptimization'
$stateRoot = $boostixStateRoot
if ($AdoptExistingState -and
    -not (Test-Path -LiteralPath (Join-Path $boostixStateRoot 'latest-state.txt') -PathType Leaf) -and
    (Test-Path -LiteralPath (Join-Path $legacyStateRoot 'latest-state.txt') -PathType Leaf)) {
    $stateRoot = $legacyStateRoot
}
$backupRoot = Join-Path $stateRoot 'Backups'
$latestStatePointer = Join-Path $stateRoot 'latest-state.txt'
$transactionLockPath = Join-Path $stateRoot 'transaction.lock'
$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentUserSid = $currentIdentity.User
$expectedIdentitySid = try {
    New-Object Security.Principal.SecurityIdentifier($ExpectedUserSid)
}
catch {
    throw 'ExpectedUserSid is not a valid Windows security identifier.'
}
if (-not $currentUserSid -or $currentUserSid.Value -ine $expectedIdentitySid.Value) {
    throw 'Elevation used a different Windows account. Sign in with an administrator account or grant administrator rights to the current account, then retry.'
}
$administratorsSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
$systemSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
$usersSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')
$storageHadUntrustedWriteBeforeProtection = $false
$untrustedPointerWasDiscarded = $false

function Get-SafeResultPath {
    param([string]$RequestedPath)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    $candidate = if ($RequestedPath) {
        [IO.Path]::GetFullPath($RequestedPath)
    }
    else {
        Join-Path $tempRoot ('Boostix-apply-{0}.json' -f [Guid]::NewGuid().ToString('N'))
    }
    if ([IO.Path]::GetDirectoryName($candidate) -ine $tempRoot) {
        throw 'ResultPath must be a direct child of the current user TEMP directory.'
    }
    if ([IO.Path]::GetFileName($candidate) -notmatch '^Boostix-apply-[0-9a-fA-F]{32}\.json$') {
        throw 'ResultPath must use the Boostix apply GUID filename format.'
    }
    if (Test-Path -LiteralPath $candidate) {
        throw 'ResultPath must be a new, non-existing unique file.'
    }
    $tempItem = Get-Item -LiteralPath $tempRoot -Force
    if (($tempItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The current user TEMP directory must not be a reparse point.'
    }
    return $candidate
}

$effectiveResultPath = Get-SafeResultPath -RequestedPath $ResultPath

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Quote-ProcessArgument {
    param([Parameter(Mandatory = $true)][string]$Value)
    return '"{0}"' -f $Value.Replace('"', '\"')
}

if (-not (Test-IsAdministrator)) {
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', (Quote-ProcessArgument -Value $PSCommandPath)
    )
    $arguments += @('-ResultPath', (Quote-ProcessArgument -Value $effectiveResultPath))
    $arguments += @('-ExpectedUserSid', (Quote-ProcessArgument -Value $ExpectedUserSid))
    if ($AdoptExistingState) {
        $arguments += '-AdoptExistingState'
    }
    $trustedPowerShell = Join-Path $systemDirectory 'WindowsPowerShell\v1.0\powershell.exe'
    $process = Start-Process -FilePath $trustedPowerShell -Verb RunAs -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    exit $process.ExitCode
}

# Elevated Windows PowerShell inherits the caller's environment.  Do not let a
# user-writable PSModulePath select code for privileged Windows calls.
$systemModuleRoot = Join-Path $systemDirectory 'WindowsPowerShell\v1.0\Modules'
$env:PSModulePath = $systemModuleRoot

function Set-ObjectProperty {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Name,
        $Value
    )
    if ($Object.PSObject.Properties.Name -contains $Name) {
        $Object.$Name = $Value
    }
    else {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
}

function Replace-FileWithoutRetainedBackup {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )
    $discardBackup = '{0}.{1}.replace-backup' -f $Destination, [Guid]::NewGuid().ToString('N')
    try {
        # Windows PowerShell 5.1 coerces $null to an empty string for this
        # overload, which makes File.Replace fail with "path is not legal".
        # A real unique backup path keeps the replace atomic on .NET Framework.
        [IO.File]::Replace($Source, $Destination, $discardBackup, $true)
    }
    finally {
        Remove-Item -LiteralPath $discardBackup -Force -ErrorAction SilentlyContinue
    }
}

function Write-TextAtomic {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text
    )
    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    $temporaryPath = '{0}.{1}.tmp' -f $Path, [Guid]::NewGuid().ToString('N')
    try {
        [IO.File]::WriteAllText($temporaryPath, $Text, $utf8NoBom)
        if (Test-Path -LiteralPath $Path) {
            Replace-FileWithoutRetainedBackup -Source $temporaryPath -Destination $Path
        }
        else {
            [IO.File]::Move($temporaryPath, $Path)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Write-JsonAtomic {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value,
        [int]$Depth = 16
    )
    Write-TextAtomic -Path $Path -Text ($Value | ConvertTo-Json -Depth $Depth)
}

function Write-ResultJson {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [int]$Depth = 10
    )
    $bytes = $utf8NoBom.GetBytes(($Value | ConvertTo-Json -Depth $Depth))
    $stream = $null
    $created = $false
    try {
        # CreateNew is the security boundary: the elevated process never follows
        # or overwrites a file that appeared in the user-writable TEMP directory.
        $stream = New-Object IO.FileStream(
            $effectiveResultPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            4096,
            [IO.FileOptions]::WriteThrough)
        $created = $true
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    catch {
        if ($stream) { $stream.Dispose(); $stream = $null }
        if ($created) { Remove-Item -LiteralPath $effectiveResultPath -Force -ErrorAction SilentlyContinue }
        throw
    }
    finally {
        if ($stream) { $stream.Dispose() }
    }
}

function Assert-NoReparsePath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$StopAt
    )
    $candidate = [IO.Path]::GetFullPath($Path)
    $stop = [IO.Path]::GetFullPath($StopAt).TrimEnd('\')
    if (-not ($candidate -ieq $stop) -and
        -not $candidate.StartsWith($stop + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside its trusted root: $candidate"
    }
    $cursor = $candidate
    while ($true) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse points are not allowed in protected state paths: $cursor"
            }
        }
        if ($cursor -ieq $stop) { break }
        $parent = Split-Path -Parent $cursor
        if (-not $parent -or $parent -eq $cursor) { throw "Unable to reach trusted root from: $candidate" }
        $cursor = $parent
    }
}

function Test-TrustedOwner {
    param([Parameter(Mandatory = $true)][Security.AccessControl.FileSystemSecurity]$Security)
    $owner = $Security.GetOwner([Security.Principal.SecurityIdentifier])
    return $owner -eq $administratorsSid -or $owner -eq $systemSid
}

function Test-HasUntrustedWriteAce {
    param([Parameter(Mandatory = $true)][Security.AccessControl.FileSystemSecurity]$Security)
    # Use only atomic mutation bits. Composite rights such as Write, Modify and
    # FullControl also contain read bits and would misclassify ReadAndExecute.
    $dangerous = [int][Security.AccessControl.FileSystemRights]::WriteData -bor
        [int][Security.AccessControl.FileSystemRights]::AppendData -bor
        [int][Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
        [int][Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [int][Security.AccessControl.FileSystemRights]::Delete -bor
        [int][Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [int][Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [int][Security.AccessControl.FileSystemRights]::TakeOwnership
    # Preserve raw generic ACE protection if Windows has not mapped it to
    # object-specific rights yet: GENERIC_ALL | GENERIC_WRITE.
    $genericMutationRights = 0x10000000 -bor 0x40000000
    foreach ($rule in $Security.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) { continue }
        if ($rule.IdentityReference -eq $administratorsSid -or $rule.IdentityReference -eq $systemSid) { continue }
        $ruleRights = [int]$rule.FileSystemRights
        if (($ruleRights -band $dangerous) -ne 0 -or ($ruleRights -band $genericMutationRights) -ne 0) { return $true }
    }
    return $false
}

function New-ProtectedFileSystemSecurity {
    param([switch]$Directory)
    $security = if ($Directory) {
        New-Object Security.AccessControl.DirectorySecurity
    }
    else {
        New-Object Security.AccessControl.FileSecurity
    }
    $security.SetOwner($administratorsSid)
    $security.SetAccessRuleProtection($true, $false)
    if ($Directory) {
        $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
        $propagation = [Security.AccessControl.PropagationFlags]::None
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($systemSid, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, $propagation, [Security.AccessControl.AccessControlType]::Allow)))
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($administratorsSid, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, $propagation, [Security.AccessControl.AccessControlType]::Allow)))
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($currentUserSid, [Security.AccessControl.FileSystemRights]::ReadAndExecute, $inheritance, $propagation, [Security.AccessControl.AccessControlType]::Allow)))
    }
    else {
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($systemSid, [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.AccessControlType]::Allow)))
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($administratorsSid, [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.AccessControlType]::Allow)))
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($currentUserSid, [Security.AccessControl.FileSystemRights]::ReadAndExecute, [Security.AccessControl.AccessControlType]::Allow)))
    }
    return $security
}

function Set-ProtectedDirectorySecurity {
    param([Parameter(Mandatory = $true)][string]$Path)
    $wasExisting = Test-Path -LiteralPath $Path
    if (-not $wasExisting) { [void][IO.Directory]::CreateDirectory($Path) }
    Assert-NoReparsePath -Path $Path -StopAt $programDataRoot
    $existing = [IO.Directory]::GetAccessControl($Path)
    if ($wasExisting -and -not (Test-TrustedOwner -Security $existing)) {
        throw "Protected state directory has an untrusted owner: $Path"
    }
    [IO.Directory]::SetAccessControl($Path, (New-ProtectedFileSystemSecurity -Directory))
    $verified = [IO.Directory]::GetAccessControl($Path)
    if (-not $verified.AreAccessRulesProtected -or -not (Test-TrustedOwner -Security $verified) -or (Test-HasUntrustedWriteAce -Security $verified)) {
        throw "Unable to enforce protected DACL on state directory: $Path"
    }
}

function Set-ProtectedFileSecurity {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$RequireTrustedExisting
    )
    Assert-NoReparsePath -Path $Path -StopAt $programDataRoot
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Protected state file is missing: $Path" }
    $existing = [IO.File]::GetAccessControl($Path)
    if ($RequireTrustedExisting -and ((-not (Test-TrustedOwner -Security $existing)) -or (Test-HasUntrustedWriteAce -Security $existing))) {
        throw "Existing protected state file is user-owned or user-writable: $Path"
    }
    [IO.File]::SetAccessControl($Path, (New-ProtectedFileSystemSecurity))
    $verified = [IO.File]::GetAccessControl($Path)
    if (-not $verified.AreAccessRulesProtected -or -not (Test-TrustedOwner -Security $verified) -or (Test-HasUntrustedWriteAce -Security $verified)) {
        throw "Unable to enforce protected DACL on state file: $Path"
    }
}

function Write-ProtectedTextAtomic {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text
    )
    Write-TextAtomic -Path $Path -Text $Text
    Set-ProtectedFileSecurity -Path $Path
}

function Write-ProtectedJsonAtomic {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value,
        [int]$Depth = 18
    )
    Write-ProtectedTextAtomic -Path $Path -Text ($Value | ConvertTo-Json -Depth $Depth)
}

function New-TrustedPowerPlanFileSystemSecurity {
    param([switch]$Directory)
    $security = if ($Directory) {
        New-Object Security.AccessControl.DirectorySecurity
    }
    else { New-Object Security.AccessControl.FileSecurity }
    $security.SetOwner($administratorsSid)
    $security.SetAccessRuleProtection($true, $false)
    if ($Directory) {
        $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
            [Security.AccessControl.InheritanceFlags]::ObjectInherit
        $propagation = [Security.AccessControl.PropagationFlags]::None
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($systemSid, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, $propagation, [Security.AccessControl.AccessControlType]::Allow)))
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($administratorsSid, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, $propagation, [Security.AccessControl.AccessControlType]::Allow)))
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($usersSid, [Security.AccessControl.FileSystemRights]::ReadAndExecute, $inheritance, $propagation, [Security.AccessControl.AccessControlType]::Allow)))
    }
    else {
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($systemSid, [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.AccessControlType]::Allow)))
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($administratorsSid, [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.AccessControlType]::Allow)))
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($usersSid, [Security.AccessControl.FileSystemRights]::ReadAndExecute, [Security.AccessControl.AccessControlType]::Allow)))
    }
    return $security
}

function Set-TrustedPowerPlanDirectorySecurity {
    param([Parameter(Mandatory = $true)][string]$Path)
    $wasExisting = Test-Path -LiteralPath $Path
    if (-not $wasExisting) { [void][IO.Directory]::CreateDirectory($Path) }
    Assert-NoReparsePath -Path $Path -StopAt $programDataRoot
    $existing = [IO.Directory]::GetAccessControl($Path)
    if ($wasExisting -and ((-not (Test-TrustedOwner -Security $existing)) -or
        (Test-HasUntrustedWriteAce -Security $existing))) {
        throw "Existing session power-plan directory is not trustworthy: $Path"
    }
    [IO.Directory]::SetAccessControl($Path, (New-TrustedPowerPlanFileSystemSecurity -Directory))
    Assert-TrustedSessionPowerPlanDirectory -Path $Path
}

function Set-TrustedPowerPlanFileSecurity {
    param([Parameter(Mandatory = $true)][string]$Path)
    Assert-NoReparsePath -Path $Path -StopAt $programDataRoot
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Trusted session power-plan file is missing: $Path"
    }
    [IO.File]::SetAccessControl($Path, (New-TrustedPowerPlanFileSystemSecurity))
    $verified = [IO.File]::GetAccessControl($Path)
    if (-not $verified.AreAccessRulesProtected -or
        -not (Test-TrustedOwner -Security $verified) -or
        (Test-HasUntrustedWriteAce -Security $verified)) {
        throw "Unable to enforce trusted session power-plan file ACL: $Path"
    }
}

function Assert-TrustedSessionPowerPlanDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)
    Assert-NoReparsePath -Path $Path -StopAt $programDataRoot
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "Session power-plan directory is missing: $Path"
    }
    $security = [IO.Directory]::GetAccessControl($Path)
    if (-not $security.AreAccessRulesProtected -or
        -not (Test-TrustedOwner -Security $security) -or
        (Test-HasUntrustedWriteAce -Security $security)) {
        throw "Session power-plan directory has untrusted owner/write access: $Path"
    }
}

function Initialize-SessionPowerPlanStorage {
    foreach ($existingDirectory in @($boostixProgramDataRoot, $sessionPowerPlanRoot)) {
        if (-not (Test-Path -LiteralPath $existingDirectory)) { continue }
        Assert-TrustedSessionPowerPlanDirectory -Path $existingDirectory
    }
    Set-TrustedPowerPlanDirectorySecurity -Path $boostixProgramDataRoot
    Set-TrustedPowerPlanDirectorySecurity -Path $sessionPowerPlanRoot
    Assert-TrustedSessionPowerPlanDirectory -Path $boostixProgramDataRoot
    Assert-TrustedSessionPowerPlanDirectory -Path $sessionPowerPlanRoot
}

function Get-CanonicalTrustedPowerPlanJson {
    param([Parameter(Mandatory = $true)][string]$Guid)
    if (-not (Test-GuidText -Value $Guid)) {
        throw 'Trusted power-plan GUID is not canonical D format.'
    }
    return '{"version":1,"planName":"Boostix Performance","planGuid":"' +
        $Guid.ToLowerInvariant() + '"}'
}

function Get-TrustedPowerPlanConfiguration {
    foreach ($directory in @($boostixProgramDataRoot, $sessionPowerPlanRoot)) {
        if (Test-Path -LiteralPath $directory) {
            Assert-TrustedSessionPowerPlanDirectory -Path $directory
        }
    }
    $trustedItem = Get-Item -LiteralPath $trustedPowerPlanPath -Force -ErrorAction SilentlyContinue
    if (-not $trustedItem) { return $null }
    Assert-NoReparsePath -Path $trustedPowerPlanPath -StopAt $programDataRoot
    if (-not (Test-Path -LiteralPath $trustedPowerPlanPath -PathType Leaf)) {
        throw 'Trusted power-plan state is not a regular file.'
    }
    $security = [IO.File]::GetAccessControl($trustedPowerPlanPath)
    if (-not $security.AreAccessRulesProtected -or
        -not (Test-TrustedOwner -Security $security) -or
        (Test-HasUntrustedWriteAce -Security $security)) {
        throw 'Trusted power-plan state has an untrusted owner or writable ACL.'
    }
    $bytes = [IO.File]::ReadAllBytes($trustedPowerPlanPath)
    if ($bytes.Length -le 0 -or $bytes.Length -gt 512 -or
        ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and
         $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) {
        throw 'Trusted power-plan state has an invalid size or UTF-8 BOM.'
    }
    $text = $strictUtf8NoBom.GetString($bytes)
    $match = [regex]::Match(
        $text,
        '^\{"version":1,"planName":"Boostix Performance","planGuid":"([0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})"\}$',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success -or -not (Test-GuidText -Value $match.Groups[1].Value) -or
        $text -cne (Get-CanonicalTrustedPowerPlanJson -Guid $match.Groups[1].Value)) {
        throw 'Trusted power-plan JSON is corrupt or non-canonical.'
    }
    return [pscustomobject]@{
        Version = 1
        PlanName = 'Boostix Performance'
        PlanGuid = $match.Groups[1].Value
        CanonicalJson = $text
    }
}

function Publish-TrustedPowerPlanConfiguration {
    param([Parameter(Mandatory = $true)][string]$Guid)
    $canonicalGuid = if (Test-GuidText -Value $Guid) {
        ([Guid]$Guid).ToString('D').ToLowerInvariant()
    }
    else {
        throw 'Trusted power-plan GUID is invalid.'
    }
    $existing = Get-TrustedPowerPlanConfiguration
    if ($existing) {
        if ([string]$existing.PlanGuid -cne $canonicalGuid) {
            throw 'An external trusted power-plan configuration already owns another GUID.'
        }
        return [pscustomobject]@{ Created = $false; PlanGuid = $canonicalGuid }
    }

    Initialize-SessionPowerPlanStorage
    # Re-check after directory creation/ACL hardening to close the existence race.
    $existing = Get-TrustedPowerPlanConfiguration
    if ($existing) {
        if ([string]$existing.PlanGuid -cne $canonicalGuid) {
            throw 'Trusted power-plan state changed before publication.'
        }
        return [pscustomobject]@{ Created = $false; PlanGuid = $canonicalGuid }
    }

    $canonicalJson = Get-CanonicalTrustedPowerPlanJson -Guid $canonicalGuid
    Write-TextAtomic -Path $trustedPowerPlanPath -Text $canonicalJson
    Set-TrustedPowerPlanFileSecurity -Path $trustedPowerPlanPath
    $verified = Get-TrustedPowerPlanConfiguration
    if (-not $verified -or [string]$verified.PlanGuid -cne $canonicalGuid -or
        [string]$verified.CanonicalJson -cne $canonicalJson) {
        throw 'Trusted power-plan state verification failed after atomic publication.'
    }
    return [pscustomobject]@{ Created = $true; PlanGuid = $canonicalGuid }
}

function Initialize-ProtectedStateStorage {
    foreach ($existingDirectory in @($stateRoot, $backupRoot)) {
        if (-not (Test-Path -LiteralPath $existingDirectory -PathType Container)) { continue }
        Assert-NoReparsePath -Path $existingDirectory -StopAt $programDataRoot
        $preSecurity = [IO.Directory]::GetAccessControl($existingDirectory)
        if (-not (Test-TrustedOwner -Security $preSecurity)) { throw "Existing state storage has an untrusted owner: $existingDirectory" }
        if (Test-HasUntrustedWriteAce -Security $preSecurity) { $script:storageHadUntrustedWriteBeforeProtection = $true }
    }
    if (-not $storageHadUntrustedWriteBeforeProtection) {
        foreach ($existingControlFile in @($latestStatePointer, $transactionLockPath)) {
            if (-not (Test-Path -LiteralPath $existingControlFile -PathType Leaf)) { continue }
            Assert-NoReparsePath -Path $existingControlFile -StopAt $programDataRoot
            $preSecurity = [IO.File]::GetAccessControl($existingControlFile)
            if (-not (Test-TrustedOwner -Security $preSecurity) -or (Test-HasUntrustedWriteAce -Security $preSecurity)) {
                throw "Existing state control file is not trustworthy: $existingControlFile"
            }
        }
    }
    Set-ProtectedDirectorySecurity -Path $stateRoot
    Set-ProtectedDirectorySecurity -Path $backupRoot
    if ($storageHadUntrustedWriteBeforeProtection) {
        foreach ($untrustedControlPath in @($latestStatePointer, $transactionLockPath)) {
            $untrustedControlItem = Get-Item -LiteralPath $untrustedControlPath -Force -ErrorAction SilentlyContinue
            if (-not $untrustedControlItem) { continue }
            $quarantineName = '{0}.quarantined-untrusted-{1}' -f $untrustedControlPath, [Guid]::NewGuid().ToString('N')
            Move-Item -LiteralPath $untrustedControlPath -Destination $quarantineName -Force
            if ($untrustedControlPath -ieq $latestStatePointer) { $script:untrustedPointerWasDiscarded = $true }
        }
    }
    if (Test-Path -LiteralPath $latestStatePointer) {
        Set-ProtectedFileSecurity -Path $latestStatePointer -RequireTrustedExisting
    }
    if (Test-Path -LiteralPath $transactionLockPath) {
        Set-ProtectedFileSecurity -Path $transactionLockPath -RequireTrustedExisting
    }
    else {
        $newLock = [IO.File]::Open($transactionLockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $newLock.Dispose()
        Set-ProtectedFileSecurity -Path $transactionLockPath
    }
}

function Remove-ProtectedPointerIfMatches {
    param([Parameter(Mandatory = $true)][string]$ExpectedStatePath)
    if (-not (Test-Path -LiteralPath $latestStatePointer -PathType Leaf)) { return }
    Set-ProtectedFileSecurity -Path $latestStatePointer -RequireTrustedExisting
    $current = (Get-Content -LiteralPath $latestStatePointer -Raw -Encoding UTF8).Trim()
    if ($current -and [IO.Path]::GetFullPath($current) -ieq [IO.Path]::GetFullPath($ExpectedStatePath)) {
        Remove-Item -LiteralPath $latestStatePointer -Force
    }
}

function Test-StatePathAllowed {
    param([Parameter(Mandatory = $true)][string]$Path)
    $candidate = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetFullPath($backupRoot).TrimEnd('\') + '\'
    if (-not $candidate.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($candidate) -ine 'state.json') { return $false }
    $transactionDirectory = [IO.Path]::GetDirectoryName($candidate)
    return [IO.Path]::GetDirectoryName($transactionDirectory) -ieq [IO.Path]::GetFullPath($backupRoot).TrimEnd('\')
}

function Test-StateProvenanceTrusted {
    param([Parameter(Mandatory = $true)][string]$Path)
    try {
        if (-not (Test-StatePathAllowed -Path $Path)) { return $false }
        $candidate = [IO.Path]::GetFullPath($Path)
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { return $false }
        Assert-NoReparsePath -Path $candidate -StopAt $programDataRoot
        $directorySecurity = [IO.Directory]::GetAccessControl((Split-Path -Parent $candidate))
        $fileSecurity = [IO.File]::GetAccessControl($candidate)
        return $directorySecurity.AreAccessRulesProtected -and $fileSecurity.AreAccessRulesProtected -and
            (Test-TrustedOwner -Security $directorySecurity) -and (Test-TrustedOwner -Security $fileSecurity) -and
            -not (Test-HasUntrustedWriteAce -Security $directorySecurity) -and -not (Test-HasUntrustedWriteAce -Security $fileSecurity)
    }
    catch { return $false }
}

function Get-ValidatedStatePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $candidate = [IO.Path]::GetFullPath($Path)
    if (-not (Test-StatePathAllowed -Path $candidate)) {
        throw "Optimization state path is outside the protected backup root: $candidate"
    }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Optimization state does not exist: $candidate"
    }
    $stateDirectory = Split-Path -Parent $candidate
    Set-ProtectedDirectorySecurity -Path $stateDirectory
    Set-ProtectedFileSecurity -Path $candidate -RequireTrustedExisting
    if ((Get-Item -LiteralPath $candidate -Force).Length -gt 2097152) {
        throw 'Optimization state exceeds the safe size limit.'
    }
    return $candidate
}

function Enter-TransactionLock {
    try {
        $stream = [IO.File]::Open(
            $transactionLockPath,
            [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None)
        return $stream
    }
    catch [IO.IOException] {
        throw 'Another Boostix apply/restore transaction is already running.'
    }
}

function Invoke-PowerCfg {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    $powerCfg = Join-Path $systemDirectory 'powercfg.exe'
    $output = & $powerCfg @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $text = ($output | Out-String).Trim()
    if ($exitCode -ne 0) {
        throw "powercfg $($Arguments -join ' ') failed with exit code $exitCode. $text"
    }
    return $text
}

function Get-ActivePowerScheme {
    $output = Invoke-PowerCfg -Arguments @('/getactivescheme')
    $match = [regex]::Match($output, '[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}')
    if (-not $match.Success) {
        throw "Unable to parse the active power scheme GUID: $output"
    }
    return $match.Value.ToLowerInvariant()
}

function Test-PowerSchemeExists {
    param([Parameter(Mandatory = $true)][string]$Guid)
    $output = Invoke-PowerCfg -Arguments @('/list')
    return $output -match [regex]::Escape($Guid)
}

function Test-PowerSchemeHasBoostixName {
    param([Parameter(Mandatory = $true)][string]$Guid)
    if (-not (Test-GuidText -Value $Guid)) { return $false }
    $canonicalGuid = ([Guid]$Guid).ToString('D')
    $matchingLines = @((Invoke-PowerCfg -Arguments @('/list')) -split "`r?`n" |
        Where-Object { $_ -match [regex]::Escape($canonicalGuid) })
    if ($matchingLines.Count -ne 1) { return $false }
    return $matchingLines[0] -match '\(\s*Boostix Performance\s*\)\s*\*?\s*$'
}

function Get-PowerSchemeGuids {
    $output = Invoke-PowerCfg -Arguments @('/list')
    return @([regex]::Matches($output, '[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}') |
        ForEach-Object { $_.Value.ToLowerInvariant() } |
        Select-Object -Unique)
}

function ConvertTo-ComparableJson {
    param($Value)
    if ($null -eq $Value) { return '<null>' }
    return ($Value | ConvertTo-Json -Compress -Depth 12)
}

function Test-ValueEqual {
    param($Left, $Right)
    return (ConvertTo-ComparableJson -Value $Left) -ceq (ConvertTo-ComparableJson -Value $Right)
}

function Get-AllowedRegistryTarget {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name
    )
    $key = ('{0}|{1}' -f $Path.TrimEnd('\'), $Name).ToLowerInvariant()
    $dwordTargets = @{
        'hkcu:\software\microsoft\gamebar|autogamemodeenabled' = 1
        'hkcu:\software\microsoft\gamebar|allowautogamemode' = 1
        'hkcu:\software\microsoft\windows\currentversion\gamedvr|appcaptureenabled' = 0
        'hkcu:\software\microsoft\windows\currentversion\gamedvr|historicalcaptureenabled' = 0
        'hkcu:\system\gameconfigstore|gamedvr_enabled' = 0
        'hkcu:\software\microsoft\windows\currentversion\explorer\visualeffects|visualfxsetting' = 2
        'hkcu:\software\microsoft\windows\currentversion\themes\personalize|enabletransparency' = 0
        'hklm:\software\policies\microsoft\dsh|allownewsandinterests' = 0
        'hkcu:\software\microsoft\windows\currentversion\dsh|isprelaunchenabled' = 0
    }
    if ($dwordTargets.ContainsKey($key)) {
        return [pscustomobject]@{ KeyExists = $true; Exists = $true; Kind = 'DWord'; Value = [int]$dwordTargets[$key] }
    }
    return $null
}

function Assert-RegistryMutationAllowed {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Kind
    )
    $allowed = Get-AllowedRegistryTarget -Path $Path -Name $Name
    $requested = [pscustomobject]@{ KeyExists = $true; Exists = $true; Kind = $Kind; Value = $Value }
    if ($null -eq $allowed -or -not (Test-RegistrySnapshotEqual -Left $allowed -Right $requested)) {
        throw "Registry mutation target is not allowlisted: $Path::$Name"
    }
}

function Test-GuidText {
    param([string]$Value)
    $parsed = [Guid]::Empty
    return [Guid]::TryParseExact($Value, 'D', [ref]$parsed)
}

function Get-RegistrySnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name
    )
    $keyExists = Test-Path -LiteralPath $Path
    $valueExists = $false
    $kind = $null
    $value = $null
    if ($keyExists) {
        $key = Get-Item -LiteralPath $Path
        $valueExists = @($key.GetValueNames()) -contains $Name
        if ($valueExists) {
            $kind = $key.GetValueKind($Name).ToString()
            $value = $key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        }
    }
    return [pscustomobject]@{
        KeyExists = [bool]$keyExists
        Exists = [bool]$valueExists
        Kind = $kind
        Value = $value
    }
}

function Test-RegistrySnapshotEqual {
    param($Left, $Right)
    if ([bool]$Left.Exists -ne [bool]$Right.Exists) { return $false }
    if (-not [bool]$Left.Exists) { return $true }
    return ([string]$Left.Kind -ceq [string]$Right.Kind) -and
        (Test-ValueEqual -Left $Left.Value -Right $Right.Value)
}

function Test-StateActive {
    param([Parameter(Mandatory = $true)]$CandidateState)
    if (($CandidateState.PSObject.Properties.Name -contains 'MutationsStarted') -and -not [bool]$CandidateState.MutationsStarted) {
        return $false
    }
    if (($CandidateState.PSObject.Properties.Name -contains 'RestoredAt') -and $CandidateState.RestoredAt) {
        return $false
    }
    if ($CandidateState.PSObject.Properties.Name -contains 'Status') {
        return [string]$CandidateState.Status -notin @('Restored', 'RestoredWithConflicts', 'AlreadyRestored', 'SupersededLegacy', 'AbortedNoChanges')
    }
    return $true
}

function Upgrade-LegacyState {
    param(
        [Parameter(Mandatory = $true)]$LegacyState,
        [Parameter(Mandatory = $true)][string]$LegacyStatePath
    )
    $operationId = if (($LegacyState.PSObject.Properties.Name -contains 'OperationId') -and $LegacyState.OperationId) {
        [string]$LegacyState.OperationId
    }
    else {
        [Guid]::NewGuid().ToString('D')
    }
    Set-ObjectProperty -Object $LegacyState -Name 'LegacyVersion' -Value $(if ($LegacyState.PSObject.Properties.Name -contains 'Version') { $LegacyState.Version } else { 1 })
    Set-ObjectProperty -Object $LegacyState -Name 'Version' -Value 2
    Set-ObjectProperty -Object $LegacyState -Name 'OperationId' -Value $operationId
    Set-ObjectProperty -Object $LegacyState -Name 'Status' -Value 'Active'
    Set-ObjectProperty -Object $LegacyState -Name 'AdoptedAt' -Value (Get-Date).ToString('o')
    Set-ObjectProperty -Object $LegacyState -Name 'StateFile' -Value $LegacyStatePath
    Set-ObjectProperty -Object $LegacyState -Name 'BackupDirectory' -Value (Split-Path -Parent $LegacyStatePath)
    if (-not ($LegacyState.PSObject.Properties.Name -contains 'Conflicts')) {
        Set-ObjectProperty -Object $LegacyState -Name 'Conflicts' -Value @()
    }
    if (-not ($LegacyState.PSObject.Properties.Name -contains 'Warnings')) {
        Set-ObjectProperty -Object $LegacyState -Name 'Warnings' -Value @()
    }

    $upgradedRegistry = @()
    foreach ($entry in @($LegacyState.Registry)) {
        $before = if ($entry.PSObject.Properties.Name -contains 'Before') { $entry.Before } else { [pscustomobject]@{
                KeyExists = [bool]$entry.KeyExisted
                Exists = [bool]$entry.ValueExisted
                Kind = $entry.Kind
                Value = $entry.Value
            } }
        $target = Get-AllowedRegistryTarget -Path ([string]$entry.Path) -Name ([string]$entry.Name)
        if ($null -eq $target) {
            $LegacyState.Warnings = @($LegacyState.Warnings) + "Unknown legacy registry entry was retained as non-restorable: $($entry.Path)::$($entry.Name)"
        }
        $upgradedRegistry += [pscustomobject]@{
            Path = [string]$entry.Path
            Name = [string]$entry.Name
            Before = $before
            Target = $target
            # Legacy state has no trustworthy per-run post-image.  Adoption must
            # never claim an earlier mutation as belonging to this Continue.
            ChangedByUs = $false
            Applied = $false
            Restored = $false
            RestoredAt = $null
        }
    }
    $LegacyState.Registry = @($upgradedRegistry)

    $upgradedServices = @()
    foreach ($entry in @($LegacyState.Services)) {
        $before = if ($entry.PSObject.Properties.Name -contains 'Before') { $entry.Before } else { [pscustomobject]@{
                StartMode = [string]$entry.StartMode
                Running = [bool]$entry.WasRunning
            } }
        $upgradedServices += [pscustomobject]@{
            Name = [string]$entry.Name
            Before = $before
            Target = $null
            ChangedByUs = $false
            Applied = $false
            Restored = $false
            RestoredAt = $null
        }
    }
    $LegacyState.Services = @($upgradedServices)

    $upgradedTasks = @()
    foreach ($entry in @($LegacyState.Tasks)) {
        $before = if ($entry.PSObject.Properties.Name -contains 'Before') { $entry.Before } else { [pscustomobject]@{ Enabled = [bool]$entry.WasEnabled } }
        $upgradedTasks += [pscustomobject]@{
            TaskName = [string]$entry.TaskName
            TaskPath = [string]$entry.TaskPath
            Before = $before
            Target = $null
            ChangedByUs = $false
            Applied = $false
            Restored = $false
            RestoredAt = $null
        }
    }
    $LegacyState.Tasks = @($upgradedTasks)

    $upgradedFiles = @()
    foreach ($entry in @($LegacyState.Files)) {
        $beforeHash = if ($entry.PSObject.Properties.Name -contains 'BeforeHash') { [string]$entry.BeforeHash } else { $null }
        $afterHash = if ($entry.PSObject.Properties.Name -contains 'AfterHash') { [string]$entry.AfterHash } else { $null }
        $upgradedFiles += [pscustomobject]@{
            Original = [string]$entry.Original
            Backup = [string]$entry.Backup
            BeforeHash = $beforeHash
            AfterHash = $afterHash
            Target = $afterHash
            ChangedByUs = $false
            Applied = $false
            Purpose = 'Ignored legacy file optimization (post-image not trustworthy)'
            Restored = $false
            RestoredAt = $null
        }
    }
    $LegacyState.Files = @($upgradedFiles)

    if (-not ($LegacyState.PSObject.Properties.Name -contains 'MovedDirectories')) {
        Set-ObjectProperty -Object $LegacyState -Name 'MovedDirectories' -Value @()
    }
    $upgradedMoves = @()
    foreach ($entry in @($LegacyState.MovedDirectories)) {
        $upgradedMoves += [pscustomobject]@{
            Original = [string]$entry.Original
            Disabled = [string]$entry.Disabled
            Target = [pscustomobject]@{ OriginalExists = $false; DisabledExists = $true }
            ChangedByUs = $false
            Applied = $false
            Conflict = 'IgnoredLegacyMoveWithoutTrustedPostImage'
            Restored = $false
            RestoredAt = $null
        }
    }
    $LegacyState.MovedDirectories = @($upgradedMoves)
    if ($upgradedMoves.Count -gt 0) {
        $LegacyState.Conflicts = @($LegacyState.Conflicts) + 'Legacy moved-directory entries were not adopted for automatic rollback.'
    }

    if (-not (Test-GuidText -Value ([string]$LegacyState.OriginalPowerScheme)) -or
        -not (Test-GuidText -Value ([string]$LegacyState.MaxPowerScheme)) -or
        [string]$LegacyState.OriginalPowerScheme -ieq [string]$LegacyState.MaxPowerScheme) {
        throw 'Legacy state power scheme GUIDs are not safely adoptable.'
    }
    Set-ObjectProperty -Object $LegacyState -Name 'Power' -Value ([pscustomobject]@{
        Before = [string]$LegacyState.OriginalPowerScheme
        Target = [string]$LegacyState.MaxPowerScheme
        ChangedByUs = $false
        SchemeCreated = $false
    })
    Set-ObjectProperty -Object $LegacyState -Name 'PowerSchemeCreated' -Value $false
    if (($LegacyState.PSObject.Properties.Name -contains 'Defender') -and $LegacyState.Defender) {
        $LegacyState.Defender = $null
        $LegacyState.Warnings = @($LegacyState.Warnings) + 'Legacy Defender changes are not restorable because no trustworthy per-run post-image exists.'
    }
    $LegacyState.Warnings = @($LegacyState.Warnings) + 'Legacy files and moved directories were conservatively marked non-restorable.'
    Write-ProtectedJsonAtomic -Path $LegacyStatePath -Value $LegacyState
    return $LegacyState
}

$transactionLock = $null
$transcriptStarted = $false
$state = $null
$statePath = $null
$exitCode = 0
$failureResultAlreadyWritten = $false
$mutationMarkerPersisted = $false
$quarantinedStatePath = $null

try {
    Initialize-ProtectedStateStorage
    $transactionLock = Enter-TransactionLock

    $existingStatePath = $null
    $existingState = $null
    if (Test-Path -LiteralPath $latestStatePointer -PathType Leaf) {
        if ((Get-Item -LiteralPath $latestStatePointer -Force).Length -gt 4096) {
            throw 'The Boostix state pointer exceeds the safe size limit.'
        }
        $pointerText = (Get-Content -LiteralPath $latestStatePointer -Raw -Encoding UTF8).Trim()
        if ($pointerText) {
            if ($storageHadUntrustedWriteBeforeProtection) {
                if (-not (Test-StatePathAllowed -Path $pointerText)) { throw 'Untrusted-provenance state pointer is outside the backup allowlist.' }
                $quarantinedStatePath = [IO.Path]::GetFullPath($pointerText)
                Remove-ProtectedPointerIfMatches -ExpectedStatePath $quarantinedStatePath
            }
            else {
                if (-not (Test-StateProvenanceTrusted -Path $pointerText)) {
                    if (Test-StatePathAllowed -Path $pointerText) { $quarantinedStatePath = [IO.Path]::GetFullPath($pointerText) }
                    Remove-Item -LiteralPath $latestStatePointer -Force
                    $untrustedPointerWasDiscarded = $true
                }
                else {
                    $existingStatePath = Get-ValidatedStatePath -Path $pointerText
                    $existingState = Get-Content -LiteralPath $existingStatePath -Raw -Encoding UTF8 | ConvertFrom-Json
                    if (($existingState.PSObject.Properties.Name -contains 'Version') -and [int]$existingState.Version -ge 2) {
                    if (-not ($existingState.PSObject.Properties.Name -contains 'StateFile') -or
                        [IO.Path]::GetFullPath([string]$existingState.StateFile) -ine $existingStatePath) {
                        throw 'The protected state self-reference does not match the pointer.'
                    }
                    if (-not ($existingState.PSObject.Properties.Name -contains 'BackupDirectory') -or
                        [IO.Path]::GetFullPath([string]$existingState.BackupDirectory) -ine (Split-Path -Parent $existingStatePath)) {
                        throw 'The protected state backup directory is invalid.'
                    }
                    if (-not (Test-GuidText -Value ([string]$existingState.OperationId))) {
                        throw 'The protected state OperationId is invalid.'
                    }
                }
                    if (-not (Test-StateActive -CandidateState $existingState)) {
                        Remove-ProtectedPointerIfMatches -ExpectedStatePath $existingStatePath
                        $existingStatePath = $null
                        $existingState = $null
                    }
                }
            }
        }
    }

    if ($existingState -and $AdoptExistingState -and
        (-not ($existingState.PSObject.Properties.Name -contains 'Version') -or [int]$existingState.Version -lt 2)) {
        $legacyStatePath = $existingStatePath
        $statePath = $legacyStatePath
        $existingState = Upgrade-LegacyState -LegacyState $existingState -LegacyStatePath $legacyStatePath
        $legacyActiveScheme = Get-ActivePowerScheme
        $legacyMaxScheme = ([string]$existingState.MaxPowerScheme).ToLowerInvariant()
        $legacyOriginalScheme = ([string]$existingState.OriginalPowerScheme).ToLowerInvariant()
        if ($legacyActiveScheme -eq $legacyMaxScheme) {
            if (-not (Test-PowerSchemeExists -Guid $legacyOriginalScheme)) {
                throw 'The original power scheme is missing; the legacy globally active Boostix plan cannot be migrated safely.'
            }
            [void](Invoke-PowerCfg -Arguments @('/setactive', $legacyOriginalScheme))
            if ((Get-ActivePowerScheme) -ne $legacyOriginalScheme) {
                throw 'The original power scheme could not be restored while migrating legacy optimization state.'
            }
            $existingState.Warnings = @($existingState.Warnings) + 'The legacy globally active performance plan was returned to its original plan before creating the session-scoped Boostix profile.'
        }
        elseif ($legacyActiveScheme -ne $legacyOriginalScheme) {
            $existingState.Warnings = @($existingState.Warnings) + "The active power plan is an external override and was preserved during legacy migration: $legacyActiveScheme"
        }
        $existingState.Status = 'SupersededLegacy'
        Set-ObjectProperty -Object $existingState -Name 'Phase' -Value 'SupersededLegacy'
        Set-ObjectProperty -Object $existingState -Name 'MutationsStarted' -Value $false
        Set-ObjectProperty -Object $existingState -Name 'SupersededAt' -Value (Get-Date).ToString('o')
        Set-ObjectProperty -Object $existingState -Name 'Warnings' -Value (@($existingState.Warnings) + 'Legacy state was archived without rollback; a fresh v2 transaction now tracks only changes made by this Continue.')
        Write-ProtectedJsonAtomic -Path $legacyStatePath -Value $existingState
        Remove-ProtectedPointerIfMatches -ExpectedStatePath $legacyStatePath
        # Continue into the normal new-transaction branch.  Its fresh snapshots
        # make Cancel restore exactly the state present before this Continue.
        $existingState = $null
        $existingStatePath = $null
        $statePath = $null
    }

    if ($existingState) {
        if (-not $AdoptExistingState) {
            $blockedResult = [ordered]@{
                Status = 'BlockedActiveTransaction'
                OperationId = $(if ($existingState.PSObject.Properties.Name -contains 'OperationId') { $existingState.OperationId } else { $null })
                StateFile = $existingStatePath
                Message = 'An active Boostix optimization transaction already exists. Use -AdoptExistingState or restore it first.'
            }
            Write-ResultJson -Value $blockedResult -Depth 8
            $failureResultAlreadyWritten = $true
            $statePath = $existingStatePath
            throw $blockedResult.Message
        }

        $statePath = $existingStatePath
        if (($existingState.PSObject.Properties.Name -contains 'Status') -and
            [string]$existingState.Status -in @('ApplyFailed', 'RestoreIncomplete')) {
            throw "The existing transaction is incomplete ($($existingState.Status)) and must be restored before it can be applied again."
        }
        if (-not (Test-GuidText -Value ([string]$existingState.OperationId))) {
            throw 'The adopted state has an invalid OperationId.'
        }
        $maxScheme = [string]$existingState.MaxPowerScheme
        if (-not (Test-GuidText -Value $maxScheme) -or -not (Test-PowerSchemeExists -Guid $maxScheme)) {
            throw "The adopted state references a missing Boostix power scheme: $maxScheme"
        }
        if (-not (Test-PowerSchemeHasBoostixName -Guid $maxScheme)) {
            throw "The adopted power scheme does not have the exact trusted Boostix Performance name: $maxScheme"
        }
        $activeScheme = Get-ActivePowerScheme
        $powerPlanPreserved = $true
        if ($activeScheme -eq $maxScheme.ToLowerInvariant()) {
            $originalScheme = [string]$existingState.OriginalPowerScheme
            if (-not (Test-GuidText -Value $originalScheme) -or -not (Test-PowerSchemeExists -Guid $originalScheme)) {
                throw 'The original power scheme is missing; the legacy globally active Boostix plan cannot be migrated safely.'
            }
            [void](Invoke-PowerCfg -Arguments @('/setactive', $originalScheme))
            if ((Get-ActivePowerScheme) -ne $originalScheme.ToLowerInvariant()) {
                throw 'The original power scheme could not be restored while migrating the adopted state.'
            }
            Set-ObjectProperty -Object $existingState -Name 'Warnings' -Value (@($existingState.Warnings) + 'A legacy globally active Boostix plan was returned to the original plan; Boostix Performance is now session-scoped.')
            Write-ProtectedJsonAtomic -Path $existingStatePath -Value $existingState
        }
        elseif ($activeScheme -ne ([string]$existingState.OriginalPowerScheme).ToLowerInvariant()) {
            Set-ObjectProperty -Object $existingState -Name 'Warnings' -Value (@($existingState.Warnings) + 'The active power plan differs from the adopted Boostix plan and was preserved as a user override.')
            Write-ProtectedJsonAtomic -Path $existingStatePath -Value $existingState
        }

        if ($existingState.Power -and [bool]$existingState.Power.SchemeCreated) {
            foreach ($property in @(
                @{ Name = 'TrustedConfigGuid'; Value = $maxScheme.ToLowerInvariant() },
                @{ Name = 'TrustedConfigCreatedByUs'; Value = $false },
                @{ Name = 'TrustedConfigWritePending'; Value = $false },
                @{ Name = 'TrustedConfigRestored'; Value = $false }
            )) {
                if (-not ($existingState.Power.PSObject.Properties.Name -contains $property.Name)) {
                    Set-ObjectProperty -Object $existingState.Power -Name $property.Name -Value $property.Value
                }
            }
            $trustedConfig = Get-TrustedPowerPlanConfiguration
            if ($trustedConfig -and [string]$trustedConfig.PlanGuid -cne $maxScheme.ToLowerInvariant()) {
                throw 'A trusted session power-plan configuration already references another GUID.'
            }
            if (-not $trustedConfig) {
                Set-ObjectProperty -Object $existingState.Power -Name 'TrustedConfigWritePending' -Value $true
                Write-ProtectedJsonAtomic -Path $existingStatePath -Value $existingState
                $publishedConfig = Publish-TrustedPowerPlanConfiguration -Guid $maxScheme
                if (-not [bool]$publishedConfig.Created) {
                    throw 'The trusted session power-plan configuration appeared concurrently and was not claimed.'
                }
                Set-ObjectProperty -Object $existingState.Power -Name 'TrustedConfigCreatedByUs' -Value $true
                Set-ObjectProperty -Object $existingState.Power -Name 'TrustedConfigWritePending' -Value $false
                Write-ProtectedJsonAtomic -Path $existingStatePath -Value $existingState
            }
            elseif ([bool]$existingState.Power.TrustedConfigWritePending) {
                # Recovery after a crash between atomic publication and the state update.
                Set-ObjectProperty -Object $existingState.Power -Name 'TrustedConfigCreatedByUs' -Value $true
                Set-ObjectProperty -Object $existingState.Power -Name 'TrustedConfigWritePending' -Value $false
                Write-ProtectedJsonAtomic -Path $existingStatePath -Value $existingState
            }
        }
        Write-ProtectedTextAtomic -Path $latestStatePointer -Text $existingStatePath
        $adoptResult = [ordered]@{
            Status = 'AlreadyApplied'
            AdoptedExistingState = $true
            OperationId = [string]$existingState.OperationId
            AppliedAt = (Get-Date).ToString('o')
            StateFile = $existingStatePath
            OriginalPowerScheme = [string]$existingState.OriginalPowerScheme
            MaxPowerScheme = $maxScheme
            BackupDirectory = [string]$existingState.BackupDirectory
            PowerPlanPreserved = $powerPlanPreserved
            RebootRecommended = $true
        }
        Write-ResultJson -Value $adoptResult -Depth 8
    }
    else {
        $operationId = [Guid]::NewGuid().ToString('D')
        $stamp = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss-fff'), $operationId
        $backupDirectory = Join-Path $backupRoot $stamp
        [void][IO.Directory]::CreateDirectory($backupDirectory)
        Set-ProtectedDirectorySecurity -Path $backupDirectory
        $statePath = Join-Path $backupDirectory 'state.json'
        $logPath = Join-Path $backupDirectory 'apply.log'

        $state = [ordered]@{
            Version = 2
            Profile = 'BoostixWindowsPerformanceV2'
            OperationId = $operationId
            Status = 'Applying'
            Phase = 'Prepared'
            MutationsStarted = $false
            CreatedAt = (Get-Date).ToString('o')
            StateFile = $statePath
            BackupDirectory = $backupDirectory
            OriginalPowerScheme = $null
            MaxPowerScheme = $null
            PowerSchemeCreated = $false
            Power = $null
            PowerSchemesBefore = @()
            UnknownCreatedPowerSchemes = @()
            QuarantinedUntrustedState = $quarantinedStatePath
            Registry = @()
            Services = @()
            Tasks = @()
            Files = @()
            MovedDirectories = @()
            Defender = $null
            Warnings = $(if ($quarantinedStatePath) { @("An existing state with untrusted provenance was quarantined without being restored: $quarantinedStatePath") } elseif ($untrustedPointerWasDiscarded) { @('An existing state pointer with untrusted provenance was quarantined without being read or restored.') } else { @() })
            Conflicts = @()
            AppliedAt = $null
            LastError = $null
            RestoredAt = $null
        }

        function Save-State {
            Write-ProtectedJsonAtomic -Path $statePath -Value $state -Depth 18
        }

        function Add-Warning {
            param([Parameter(Mandatory = $true)][string]$Message)
            $state.Warnings = @($state.Warnings) + $Message
            Save-State
            Write-Warning $Message
        }

        function Set-TrackedRegistryValue {
            param(
                [Parameter(Mandatory = $true)][string]$Path,
                [Parameter(Mandatory = $true)][string]$Name,
                [Parameter(Mandatory = $true)]$Value,
                [Parameter(Mandatory = $true)][ValidateSet('DWord', 'String', 'Binary')][string]$Kind
            )
            Assert-RegistryMutationAllowed -Path $Path -Name $Name -Value $Value -Kind $Kind
            $before = Get-RegistrySnapshot -Path $Path -Name $Name
            $target = [pscustomobject]@{ KeyExists = $true; Exists = $true; Kind = $Kind; Value = $Value }
            $changedByUs = -not (Test-RegistrySnapshotEqual -Left $before -Right $target)
            $entry = [pscustomobject]@{
                Path = $Path
                Name = $Name
                Before = $before
                Target = $target
                ChangedByUs = $changedByUs
                Applied = (-not $changedByUs)
                Restored = $false
                RestoredAt = $null
            }
            $state.Registry = @($state.Registry) + $entry
            Save-State
            if ($changedByUs) {
                $currentBeforeWrite = Get-RegistrySnapshot -Path $Path -Name $Name
                if (-not (Test-RegistrySnapshotEqual -Left $currentBeforeWrite -Right $before)) {
                    $entry.ChangedByUs = $false
                    $entry.Applied = $false
                    Set-ObjectProperty -Object $entry -Name 'Conflict' -Value 'ValueChangedBeforeWrite'
                    $state.Conflicts = @($state.Conflicts) + "Registry value changed while Boost was preparing it and was preserved: $Path::$Name"
                    Save-State
                    return
                }
                if (-not (Test-Path -LiteralPath $Path)) {
                    New-Item -Path $Path -Force | Out-Null
                }
                New-ItemProperty -Path $Path -Name $Name -Value $Value -PropertyType $Kind -Force | Out-Null
                $after = Get-RegistrySnapshot -Path $Path -Name $Name
                if (-not (Test-RegistrySnapshotEqual -Left $after -Right $target)) {
                    throw "Registry write verification failed for $Path::$Name"
                }
                $entry.Applied = $true
                Save-State
            }
        }

        Save-State
        Write-ProtectedTextAtomic -Path $latestStatePointer -Text $statePath
        Start-Transcript -Path $logPath -Force | Out-Null
        $transcriptStarted = $true
        Set-ProtectedFileSecurity -Path $logPath

        $originalPowerScheme = Get-ActivePowerScheme
        $powerSchemesBefore = @(Get-PowerSchemeGuids)
        foreach ($existingDirectory in @($boostixProgramDataRoot, $sessionPowerPlanRoot)) {
            if (Test-Path -LiteralPath $existingDirectory) {
                Assert-TrustedSessionPowerPlanDirectory -Path $existingDirectory
            }
        }
        if (Get-TrustedPowerPlanConfiguration) {
            throw 'A trusted session power-plan configuration already exists without an adoptable transaction; no new plan was created.'
        }
        $state.OriginalPowerScheme = $originalPowerScheme
        $state.PowerSchemesBefore = @($powerSchemesBefore)
        $state.Phase = 'PowerPlanCreationPending'
        $state.MutationsStarted = $true
        Save-State
        $mutationMarkerPersisted = $true

        $duplicateOutput = $null
        $duplicateFailure = $null
        try {
            $duplicateOutput = Invoke-PowerCfg -Arguments @('/duplicatescheme', $originalPowerScheme)
        }
        catch {
            $duplicateFailure = $_.Exception
        }

        $powerSchemesAfter = @(Get-PowerSchemeGuids)
        $newPowerSchemes = @($powerSchemesAfter | Where-Object { $powerSchemesBefore -notcontains $_ })
        $duplicateMatch = [regex]::Match([string]$duplicateOutput, '[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}')
        $maxPowerScheme = $null
        if ($duplicateMatch.Success) {
            $outputGuid = $duplicateMatch.Value.ToLowerInvariant()
            if ($powerSchemesAfter -contains $outputGuid -and $powerSchemesBefore -notcontains $outputGuid) {
                $maxPowerScheme = $outputGuid
            }
        }
        if (-not $maxPowerScheme -and $newPowerSchemes.Count -eq 1) {
            $maxPowerScheme = [string]$newPowerSchemes[0]
        }
        if (-not $maxPowerScheme -and $newPowerSchemes.Count -eq 0) {
            # No observable system mutation occurred.  Revert the pre-mutation
            # marker so catch can archive this transaction without recovery.
            $state.MutationsStarted = $false
            $state.Phase = 'PowerPlanCreationFailedNoChanges'
            Save-State
            $mutationMarkerPersisted = $false
            if ($duplicateFailure) { throw $duplicateFailure }
            throw "Power plan duplication produced no new scheme: $duplicateOutput"
        }
        if (-not $maxPowerScheme) {
            $state.UnknownCreatedPowerSchemes = @($newPowerSchemes)
            $state.Phase = 'PowerPlanCreationAmbiguous'
            $state.Conflicts = @($state.Conflicts) + 'Power plan duplication created multiple unidentifiable schemes; they will be preserved for manual review.'
            Save-State
            if ($duplicateFailure) { throw $duplicateFailure }
            throw 'Power plan duplication result is ambiguous; created schemes were preserved for manual review.'
        }
        $state.MaxPowerScheme = $maxPowerScheme
        $state.PowerSchemeCreated = $true
        $state.Power = [pscustomobject]@{
            Before = $originalPowerScheme
            Target = $maxPowerScheme
            ChangedByUs = $true
            SchemeCreated = $true
            TrustedConfigGuid = $maxPowerScheme
            TrustedConfigCreatedByUs = $false
            TrustedConfigWritePending = $false
            TrustedConfigRestored = $false
        }
        $state.Phase = 'PowerPlanCreated'
        Save-State

        if ($duplicateFailure) { throw $duplicateFailure }

        [void](Invoke-PowerCfg -Arguments @('/changename', $maxPowerScheme, 'Boostix Performance', 'Reversible Windows performance profile created by Boostix.'))
        if (-not (Test-PowerSchemeExists -Guid $maxPowerScheme) -or
            -not (Test-PowerSchemeHasBoostixName -Guid $maxPowerScheme)) {
            throw 'Boostix power scheme creation/name verification failed.'
        }
        $state.Phase = 'ApplyingSettings'
        Save-State

        try {
            Microsoft.PowerShell.Management\Checkpoint-Computer -Description 'Before Boostix performance optimization' -RestorePointType MODIFY_SETTINGS | Out-Null
        }
        catch {
            Add-Warning -Message "Restore point was not created: $($_.Exception.Message)"
        }
        $processorSubgroup = '54533251-82be-4824-96c1-47b60b740d00'
        $energyPerformancePreference = '36687f9e-e3a5-4dbf-b1dc-15eb381c6863'
        $boostMode = 'be337238-0d82-4146-a960-4f3749d470c7'
        foreach ($powerSetting in @(
            @($processorSubgroup, $energyPerformancePreference, '20'),
            @($processorSubgroup, $boostMode, '1')
        )) {
            [void](Invoke-PowerCfg -Arguments @('/setacvalueindex', $maxPowerScheme, $powerSetting[0], $powerSetting[1], $powerSetting[2]))
        }

        $activeAfterProvisioning = Get-ActivePowerScheme
        if ($activeAfterProvisioning -eq $maxPowerScheme) {
            [void](Invoke-PowerCfg -Arguments @('/setactive', $originalPowerScheme))
            if ((Get-ActivePowerScheme) -ne $originalPowerScheme) {
                throw 'The original active power scheme could not be restored after provisioning.'
            }
        }
        elseif ($activeAfterProvisioning -ne $originalPowerScheme) {
            Add-Warning -Message "The active power plan changed externally during provisioning and was preserved: $activeAfterProvisioning"
        }
        if ((Get-ActivePowerScheme) -eq $maxPowerScheme) {
            throw 'Boostix Performance must not remain globally active after provisioning.'
        }

        $state.Phase = 'TrustedConfigPublicationPending'
        $state.Power.TrustedConfigWritePending = $true
        Save-State
        $publishedConfig = Publish-TrustedPowerPlanConfiguration -Guid $maxPowerScheme
        if (-not [bool]$publishedConfig.Created) {
            throw 'The trusted session power-plan configuration appeared concurrently and was not claimed.'
        }
        $state.Power.TrustedConfigCreatedByUs = $true
        $state.Power.TrustedConfigWritePending = $false
        $state.Phase = 'TrustedConfigPublished'
        Save-State

        Set-TrackedRegistryValue -Path 'HKCU:\Software\Microsoft\GameBar' -Name 'AutoGameModeEnabled' -Value 1 -Kind DWord
        Set-TrackedRegistryValue -Path 'HKCU:\Software\Microsoft\GameBar' -Name 'AllowAutoGameMode' -Value 1 -Kind DWord
        Set-TrackedRegistryValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR' -Name 'AppCaptureEnabled' -Value 0 -Kind DWord
        Set-TrackedRegistryValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR' -Name 'HistoricalCaptureEnabled' -Value 0 -Kind DWord
        Set-TrackedRegistryValue -Path 'HKCU:\System\GameConfigStore' -Name 'GameDVR_Enabled' -Value 0 -Kind DWord
        Set-TrackedRegistryValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects' -Name 'VisualFXSetting' -Value 2 -Kind DWord
        Set-TrackedRegistryValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name 'EnableTransparency' -Value 0 -Kind DWord
        Set-TrackedRegistryValue -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh' -Name 'AllowNewsAndInterests' -Value 0 -Kind DWord
        Set-TrackedRegistryValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Dsh' -Name 'IsPrelaunchEnabled' -Value 0 -Kind DWord

        $state.Status = 'Active'
        $state.Phase = 'Active'
        $state.AppliedAt = (Get-Date).ToString('o')
        Save-State

        $result = [ordered]@{
            Status = 'Applied'
            OperationId = $operationId
            AppliedAt = $state.AppliedAt
            StateFile = $statePath
            OriginalPowerScheme = $state.OriginalPowerScheme
            MaxPowerScheme = $state.MaxPowerScheme
            BackupDirectory = $backupDirectory
            RebootRecommended = $true
            Warnings = @($state.Warnings)
            Conflicts = @($state.Conflicts)
        }
        Write-ResultJson -Value $result -Depth 10
    }
}
catch {
    $exitCode = 1
    $failureMessage = $_.Exception.Message
    $resultStatePath = $statePath
    if ($state -and $statePath) {
        try {
            $mutationsStarted = [bool]$mutationMarkerPersisted
            if ($mutationsStarted) {
                $state.Status = 'ApplyFailed'
            }
            else {
                $state.Status = 'AbortedNoChanges'
                $state.Phase = 'AbortedNoChanges'
            }
            $state.LastError = $failureMessage
            Save-State
            if (-not $mutationsStarted) {
                Remove-ProtectedPointerIfMatches -ExpectedStatePath $statePath
                if (-not (Test-Path -LiteralPath $latestStatePointer -PathType Leaf)) {
                    $resultStatePath = $null
                }
            }
        }
        catch {}
    }
    if (-not $failureResultAlreadyWritten) {
        try {
            $failedResult = [ordered]@{
                Status = 'Failed'
                OperationId = $(if ($state) { $state.OperationId } else { $null })
                StateFile = $resultStatePath
                Phase = $(if ($state -and ($state.PSObject.Properties.Name -contains 'Phase')) { $state.Phase } else { $null })
                MutationsStarted = $(if ($state -and ($state.PSObject.Properties.Name -contains 'MutationsStarted')) { [bool]$state.MutationsStarted } else { $null })
                Error = $failureMessage
                RebootRecommended = $false
            }
            Write-ResultJson -Value $failedResult -Depth 8
        }
        catch {}
    }
    [Console]::Error.WriteLine($failureMessage)
}
finally {
    if ($transcriptStarted) {
        try { Stop-Transcript | Out-Null } catch {}
    }
    if ($transactionLock) {
        $transactionLock.Dispose()
    }
}

exit $exitCode
