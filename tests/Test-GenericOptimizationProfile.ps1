[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$applyPath = Join-Path $projectRoot 'outputs\MaxFPS-Apply.ps1'
$restorePath = Join-Path $projectRoot 'outputs\MaxFPS-Restore.ps1'
$flowPath = Join-Path $projectRoot 'Boostix\OptimizationFlow.cs'
$apply = [IO.File]::ReadAllText($applyPath)
$restore = [IO.File]::ReadAllText($restorePath)
$flow = [IO.File]::ReadAllText($flowPath)

foreach ($scriptPath in @($applyPath, $restorePath)) {
    $tokens = $null
    $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile(
        $scriptPath,
        [ref]$tokens,
        [ref]$errors)
    if ($errors.Count -ne 0) {
        throw "Production optimization script does not parse: $scriptPath"
    }
}

$profileStart = $apply.IndexOf(
    '$originalPowerScheme = Get-ActivePowerScheme',
    [StringComparison]::Ordinal)
$profileEnd = $apply.IndexOf(
    '$state.Status = ''Active''',
    $profileStart,
    [StringComparison]::Ordinal)
if ($profileStart -lt 0 -or $profileEnd -le $profileStart) {
    throw 'The bounded Boostix mutation section could not be located.'
}
$profile = $apply.Substring($profileStart, $profileEnd - $profileStart)

foreach ($forbidden in @(
    'Majestic',
    'GTA',
    'Rockstar',
    'EpicGamesLauncher',
    'ShadowPlay',
    'NVIDIA',
    'Wallpaper Engine',
    'VirtualPad',
    'SPUser',
    'SteelSeries',
    'HwSchMode',
    'Set-MpPreference',
    'Set-Service',
    'Stop-Service',
    'Disable-ScheduledTask',
    'Move-Item',
    'Apply-PreparedFile',
    'Start-Process'
)) {
    if ($profile.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "The new Boostix profile still performs a forbidden product/vendor mutation: $forbidden"
    }
}

foreach ($required in @(
    "'Boostix Performance'",
    "'Before Boostix performance optimization'",
    "@(`$processorSubgroup, `$energyPerformancePreference, '20')",
    "@(`$processorSubgroup, `$boostMode, '1')",
    "HKCU:\Software\Microsoft\GameBar' -Name 'AutoGameModeEnabled' -Value 1",
    "HKCU:\Software\Microsoft\GameBar' -Name 'AllowAutoGameMode' -Value 1",
    "HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR' -Name 'AppCaptureEnabled' -Value 0",
    "HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR' -Name 'HistoricalCaptureEnabled' -Value 0",
    "HKCU:\System\GameConfigStore' -Name 'GameDVR_Enabled' -Value 0",
    "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects' -Name 'VisualFXSetting' -Value 2",
    "HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name 'EnableTransparency' -Value 0",
    "HKLM:\SOFTWARE\Policies\Microsoft\Dsh' -Name 'AllowNewsAndInterests' -Value 0",
    "HKCU:\Software\Microsoft\Windows\CurrentVersion\Dsh' -Name 'IsPrelaunchEnabled' -Value 0"
)) {
    if (-not $profile.Contains($required)) {
        throw "The documented Boostix profile is missing a required reversible setting: $required"
    }
}

$trackedWrites = [Regex]::Matches(
    $profile,
    '(?m)^\s*Set-TrackedRegistryValue\s').Count
if ($trackedWrites -ne 9) {
    throw "The new Boostix profile has $trackedWrites tracked registry writes; expected exactly 9."
}
$safeAcWrites = [Regex]::Matches(
    $profile,
    "(?m)^\s*\[void\]\(Invoke-PowerCfg -Arguments @\('/setacvalueindex'").Count
if ($safeAcWrites -ne 1 -or
    [Regex]::Matches($profile, [Regex]::Escape('@($processorSubgroup,')).Count -ne 2 -or
    $profile.IndexOf('/setdcvalueindex', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw 'Boostix provisioning must contain exactly two bounded AC-only processor settings.'
}

foreach ($forbiddenPowerContract in @(
    '$coreParkingMin',
    '$coreParkingMax',
    '$usbSelectiveSuspend',
    '$diskIdle',
    '0cc5b647-c1df-4637-891a-dec35c318583',
    'ea062031-0e34-4ff1-9b6d-eb1059334028',
    '48e6b7a6-50f5-4782-a5d4-53bb8f07e226',
    '6738e2c4-e8a5-4a42-b16a-e040e769756e',
    "@(`$processorSubgroup, `$energyPerformancePreference, '0')",
    "@(`$processorSubgroup, `$boostMode, '2')",
    "@('/setactive', `$maxPowerScheme)",
    'PowerThrottlingOff',
    'SystemResponsiveness'
)) {
    if ($apply.IndexOf($forbiddenPowerContract, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Apply 2.0 contains an unsafe/global power contract: $forbiddenPowerContract"
    }
}

foreach ($required in @(
    "Join-Path `$programDataRoot 'BoostixOptimization'",
    "'BoostixWindowsPerformanceV2'",
    "'Boostix-apply-{0}.json'",
    "'^Boostix-apply-[0-9a-fA-F]{32}\.json$'"
)) {
    if (-not $apply.Contains($required)) {
        throw "The new apply transaction is missing its Boostix identity/safety contract: $required"
    }
}

foreach ($requiredProvisioningContract in @(
    "Join-Path `$programDataRoot 'Boostix'",
    "Join-Path `$boostixProgramDataRoot 'SessionPowerPlan'",
    "Join-Path `$sessionPowerPlanRoot 'trusted-plan.json'",
    '$strictUtf8NoBom = New-Object Text.UTF8Encoding($false, $true)',
    'Assert-TrustedSessionPowerPlanDirectory',
    'Test-HasUntrustedWriteAce',
    'Assert-NoReparsePath -Path $trustedPowerPlanPath',
    '{"version":1,"planName":"Boostix Performance","planGuid":"',
    'Write-TextAtomic -Path $trustedPowerPlanPath -Text $canonicalJson',
    'Set-TrustedPowerPlanFileSecurity -Path $trustedPowerPlanPath',
    "SecurityIdentifier('S-1-5-32-545')",
    '$usersSid, [Security.AccessControl.FileSystemRights]::ReadAndExecute',
    'TrustedConfigWritePending',
    'TrustedConfigCreatedByUs',
    'if ($activeAfterProvisioning -eq $maxPowerScheme)',
    "Invoke-PowerCfg -Arguments @('/setactive', `$originalPowerScheme)",
    'Boostix Performance must not remain globally active after provisioning.',
    'Test-PowerSchemeHasBoostixName',
    'if ($legacyActiveScheme -eq $legacyMaxScheme)',
    'session-scoped Boostix profile'
)) {
    if (-not $apply.Contains($requiredProvisioningContract)) {
        throw "Session power-plan provisioning contract is missing: $requiredProvisioningContract"
    }
}

foreach ($requiredRestoreOwnershipContract in @(
    'Get-TrustedPowerPlanConfiguration',
    'Remove-OwnedTrustedPowerPlanConfiguration',
    'Test-TransactionOwnsTrustedPowerPlanConfiguration',
    'TrustedConfigGuid -ine $MaxScheme',
    'return $created -and -not $restored',
    '$preserveCreatedPlan = $true',
    '-not $preserveCreatedPlan',
    'not attributable to this transaction',
    'An external trusted session power-plan configuration was preserved',
    'invalid size or UTF-8 BOM',
    'corrupt or non-canonical'
)) {
    if (-not $restore.Contains($requiredRestoreOwnershipContract)) {
        throw "Session power-plan restore/fail-closed contract is missing: $requiredRestoreOwnershipContract"
    }
}

# 1.x/v2 rollback states that already recorded these global values remain
# readable only by Restore; Apply 2.0 must never write them again.
foreach ($legacyRestoreOnlyTarget in @('PowerThrottlingOff','SystemResponsiveness')) {
    if ($restore.IndexOf($legacyRestoreOnlyTarget, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Legacy restore-only registry compatibility was removed: $legacyRestoreOnlyTarget"
    }
}

foreach ($requiredLegacyContract in @(
    "Join-Path `$programDataRoot 'CodexGamingOptimization'",
    'GTA5.exe',
    'Majestic Launcher.exe',
    'Wallpaper Engine Service',
    'nefarius_VirtualPad_Updater',
    'SPUser.disabled-by-majesticboost-'
)) {
    if (-not $restore.Contains($requiredLegacyContract)) {
        throw "Legacy rollback compatibility was removed: $requiredLegacyContract"
    }
}
foreach ($requiredRestoreContract in @(
    "Join-Path `$programDataRoot 'BoostixOptimization'",
    "'Boostix-restore-{0}.json'",
    "'^Boostix-restore-[0-9a-fA-F]{32}\.json$'",
    '$requestedBackupRoot -ieq $legacyBackupRoot',
    '$requestedBackupRoot -ine $boostixBackupRoot'
)) {
    if (-not $restore.Contains($requiredRestoreContract)) {
        throw "The dual-root Boostix restore contract is missing: $requiredRestoreContract"
    }
}

# Exercise the production canonical-state and ownership functions through
# isolated seams.  No real power plan, registry value, or ProgramData path is
# touched by this regression harness.
$applyTokens = $null
$applyErrors = $null
$applyAst = [Management.Automation.Language.Parser]::ParseFile(
    $applyPath,
    [ref]$applyTokens,
    [ref]$applyErrors)
$restoreTokens = $null
$restoreErrors = $null
$restoreAst = [Management.Automation.Language.Parser]::ParseFile(
    $restorePath,
    [ref]$restoreTokens,
    [ref]$restoreErrors)
foreach ($definitionSpec in @(
    @{ Ast = $applyAst; Name = 'Test-GuidText' },
    @{ Ast = $applyAst; Name = 'Get-CanonicalTrustedPowerPlanJson' },
    @{ Ast = $applyAst; Name = 'Get-TrustedPowerPlanConfiguration' },
    @{ Ast = $applyAst; Name = 'Publish-TrustedPowerPlanConfiguration' },
    @{ Ast = $restoreAst; Name = 'Test-TransactionOwnsTrustedPowerPlanConfiguration' }
)) {
    $definitionName = [string]$definitionSpec.Name
    $definition = $definitionSpec.Ast.Find(
        { param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $definitionName },
        $true)
    if (-not $definition) {
        throw "Production function was not found for isolated testing: $definitionName"
    }
    Invoke-Expression $definition.Extent.Text
}

$mockTrustedOwner = $true
$mockUntrustedWrite = $false
function Assert-TrustedSessionPowerPlanDirectory { param([string]$Path) }
function Assert-NoReparsePath { param([string]$Path, [string]$StopAt) }
function Test-TrustedOwner { param($Security) return [bool]$script:mockTrustedOwner }
function Test-HasUntrustedWriteAce { param($Security) return [bool]$script:mockUntrustedWrite }
function Initialize-SessionPowerPlanStorage {
    [void][IO.Directory]::CreateDirectory($script:boostixProgramDataRoot)
    [void][IO.Directory]::CreateDirectory($script:sessionPowerPlanRoot)
}
function Write-ProtectedTextAtomic {
    param([string]$Path, [string]$Text)
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($false)))
}
function Write-TextAtomic {
    param([string]$Path, [string]$Text)
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($false)))
}
function Set-TrustedPowerPlanFileSecurity {
    param([string]$Path)
    $security = [IO.File]::GetAccessControl($Path)
    $security.SetAccessRuleProtection($true, $true)
    [IO.File]::SetAccessControl($Path, $security)
}
function Assert-Throws {
    param([Parameter(Mandatory = $true)][scriptblock]$Action, [Parameter(Mandatory = $true)][string]$Scenario)
    $threw = $false
    try { & $Action }
    catch { $threw = $true }
    if (-not $threw) { throw "Fail-closed regression: $Scenario was accepted." }
}

$isolatedRoot = Join-Path ([IO.Path]::GetTempPath()) ('Boostix-PowerProvisioning-' + [Guid]::NewGuid().ToString('N'))
try {
    [void][IO.Directory]::CreateDirectory($isolatedRoot)
    $script:programDataRoot = $isolatedRoot
    $script:boostixProgramDataRoot = Join-Path $isolatedRoot 'Boostix'
    $script:sessionPowerPlanRoot = Join-Path $script:boostixProgramDataRoot 'SessionPowerPlan'
    $script:trustedPowerPlanPath = Join-Path $script:sessionPowerPlanRoot 'trusted-plan.json'
    $script:strictUtf8NoBom = New-Object Text.UTF8Encoding($false, $true)
    $firstGuid = [Guid]::NewGuid().ToString('D').ToLowerInvariant()
    $otherGuid = [Guid]::NewGuid().ToString('D').ToLowerInvariant()

    $published = Publish-TrustedPowerPlanConfiguration -Guid $firstGuid
    if (-not [bool]$published.Created) { throw 'First trusted configuration publication was not attributed.' }
    $expectedCanonical = '{"version":1,"planName":"Boostix Performance","planGuid":"' + $firstGuid + '"}'
    $actualBytes = [IO.File]::ReadAllBytes($script:trustedPowerPlanPath)
    $actualText = $script:strictUtf8NoBom.GetString($actualBytes)
    if ($actualText -cne $expectedCanonical -or
        ($actualBytes.Length -ge 3 -and $actualBytes[0] -eq 0xEF -and $actualBytes[1] -eq 0xBB -and $actualBytes[2] -eq 0xBF)) {
        throw 'Trusted configuration is not exact canonical UTF-8 without BOM.'
    }
    $idempotent = Publish-TrustedPowerPlanConfiguration -Guid $firstGuid
    if ([bool]$idempotent.Created) { throw 'Idempotent publication incorrectly claimed a second creation.' }
    Assert-Throws -Scenario 'a different GUID over an existing trusted configuration' -Action {
        [void](Publish-TrustedPowerPlanConfiguration -Guid $otherGuid)
    }

    $mockTrustedOwner = $false
    Assert-Throws -Scenario 'an untrusted trusted-file owner' -Action {
        [void](Get-TrustedPowerPlanConfiguration)
    }
    $mockTrustedOwner = $true
    $mockUntrustedWrite = $true
    Assert-Throws -Scenario 'an untrusted trusted-file write ACE' -Action {
        [void](Get-TrustedPowerPlanConfiguration)
    }
    $mockUntrustedWrite = $false

    [IO.File]::WriteAllBytes(
        $script:trustedPowerPlanPath,
        [byte[]](0xEF, 0xBB, 0xBF) + (New-Object Text.UTF8Encoding($false)).GetBytes($expectedCanonical))
    Assert-Throws -Scenario 'a UTF-8 BOM in canonical trusted state' -Action {
        [void](Get-TrustedPowerPlanConfiguration)
    }
    [IO.File]::WriteAllText(
        $script:trustedPowerPlanPath,
        $expectedCanonical.TrimEnd('}') + ',"extra":true}',
        (New-Object Text.UTF8Encoding($false)))
    Assert-Throws -Scenario 'unknown trusted-state JSON fields' -Action {
        [void](Get-TrustedPowerPlanConfiguration)
    }

    $owned = [pscustomobject]@{
        SchemeCreated = $true
        TrustedConfigGuid = $firstGuid
        TrustedConfigCreatedByUs = $true
        TrustedConfigWritePending = $false
        TrustedConfigRestored = $false
    }
    if (-not (Test-TransactionOwnsTrustedPowerPlanConfiguration -Power $owned -MaxScheme $firstGuid)) {
        throw 'A matching transaction-created trusted configuration was not recognized.'
    }
    $pending = $owned.PSObject.Copy()
    $pending.TrustedConfigCreatedByUs = $false
    $pending.TrustedConfigWritePending = $true
    if (Test-TransactionOwnsTrustedPowerPlanConfiguration -Power $pending -MaxScheme $firstGuid) {
        throw 'A pending publication was treated as proven file ownership instead of failing closed.'
    }
    $owned.TrustedConfigRestored = $true
    if (Test-TransactionOwnsTrustedPowerPlanConfiguration -Power $owned -MaxScheme $firstGuid) {
        throw 'An already-restored trusted configuration was claimed again.'
    }
    $external = [pscustomobject]@{
        SchemeCreated = $true
        TrustedConfigGuid = $otherGuid
        TrustedConfigCreatedByUs = $true
        TrustedConfigWritePending = $false
        TrustedConfigRestored = $false
    }
    if (Test-TransactionOwnsTrustedPowerPlanConfiguration -Power $external -MaxScheme $firstGuid) {
        throw 'A trusted configuration for an external GUID was claimed.'
    }
    $legacyPower = [pscustomobject]@{ SchemeCreated = $true }
    if (Test-TransactionOwnsTrustedPowerPlanConfiguration -Power $legacyPower -MaxScheme $firstGuid) {
        throw 'An old transaction without trusted-config ownership metadata was claimed.'
    }
}
finally {
    if (Test-Path -LiteralPath $isolatedRoot) {
        Remove-Item -LiteralPath $isolatedRoot -Recurse -Force
    }
}

$consentStart = $flow.IndexOf(
    'private void ShowConsent',
    [StringComparison]::Ordinal)
$consentEnd = $flow.IndexOf(
    'private async void ContinueButtonClick',
    $consentStart,
    [StringComparison]::Ordinal)
if ($consentStart -lt 0 -or $consentEnd -le $consentStart) {
    throw 'The Boostix consent section could not be located.'
}
$consent = $flow.Substring($consentStart, $consentEnd - $consentStart)
$requiredConsent = @(
    'Boostix Performance',
    'Game Mode'
)
$requiredConsent += @(
    'Qm9vc3RpeCDQv9GA0LjQvNC10L3QuNGCINC+0LHRgNCw0YLQuNC80YvQuSDQv9GA0L7RhNC40LvRjCDQv9GA0L7QuNC30LLQvtC00LjRgtC10LvRjNC90L7RgdGC0LggV2luZG93cy4=',
    '0L/QsNGA0LrQvtCy0LrRgyDRj9C00LXRgA==',
    'VVNCLdGB0LXQu9C10LrRgtC40LLQvdC+0LUg0L7RgtC60LvRjtGH0LXQvdC40LU=',
    'RFZSLdC30LDQv9C40YHRjA==',
    '0YDQtdC30LXRgNCyIENQVSDQtNC70Y8g0YTQvtC90L7QstGL0YUg0LfQsNC00LDRhyDQtNC+IDEwJQ==',
    '0LLQuNC30YPQsNC70YzQvdGL0LUg0Y3RhNGE0LXQutGC0Ys=',
    '0YTQvtC90L7QstGL0LUg0LLQuNC00LbQtdGC0YsgV2luZG93cw==',
    '0YLQvtGH0LrRgyDQstC+0YHRgdGC0LDQvdC+0LLQu9C10L3QuNGP',
    '0L/QvtGC0YDQtdCx0YPQtdGC0YHRjyDQv9C10YDQtdC30LDQs9GA0YPQt9C60LA='
) | ForEach-Object {
    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($_))
}
foreach ($requiredConsentItem in $requiredConsent) {
    if (-not $consent.Contains($requiredConsentItem)) {
        throw "The consent screen does not disclose a profile mutation."
    }
}
foreach ($forbiddenConsent in @(
    'Majestic',
    'GTA',
    'NVIDIA',
    'Wallpaper',
    'VirtualPad',
    'Defender',
    'HAGS'
)) {
    if ($consent.IndexOf($forbiddenConsent, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "The generic Boostix consent screen still exposes a legacy product/vendor: $forbiddenConsent"
    }
}

foreach ($requiredFlowContract in @(
    'BoostixDesignTokens.Accent',
    'Environment.SpecialFolder.LocalApplicationData',
    '"Boostix"',
    '"Boostix-" + operation',
    'Path.Combine(programData, "BoostixOptimization")',
    'Path.Combine(programData, "CodexGamingOptimization")',
    'candidateBackupsRoot',
    'pointerBackupsRoot',
    'legacyPendingMarkerPath',
    'legacyCompletedMarkerPath'
)) {
    if (-not $flow.Contains($requiredFlowContract)) {
        throw "The Boostix UI/migration contract is missing: $requiredFlowContract"
    }
}

'Generic Boostix profile, exact consent, safe mutation boundary, and legacy rollback tests passed.'
