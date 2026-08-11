[CmdletBinding()]
param(
    # Retained so older CI/release invocations do not break. The V2 safety
    # contract validates source boundaries and does not load a release binary.
    [string]$ApplicationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or
    $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$programPath = Join-Path $projectRoot 'Boostix\Program.cs'
$guardPath = Join-Path $projectRoot 'Boostix\SessionGuard.cs'
$runAllPath = Join-Path $PSScriptRoot 'Run-All.ps1'
$guardTestPath = Join-Path $PSScriptRoot 'Test-SessionGuard.ps1'
$integrationTestPath = Join-Path $PSScriptRoot (
    'Test-SessionGuardIntegrationBoundary.ps1')

foreach ($requiredPath in @(
    $programPath,
    $guardPath,
    $runAllPath,
    $guardTestPath,
    $integrationTestPath
)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required V2 safety-contract file is missing: $requiredPath"
    }
}

function Get-ProductionSourceFiles {
    $roots = @(
        (Join-Path $projectRoot 'Boostix'),
        (Join-Path $projectRoot 'BoostixInstaller')
    )
    $files = @(
        foreach ($root in $roots) {
            if (Test-Path -LiteralPath $root -PathType Container) {
                Get-ChildItem -LiteralPath $root -Recurse -File |
                    Where-Object {
                        $_.Extension -ceq '.cs' -or $_.Extension -ceq '.ps1'
                    }
            }
        }
    )
    if ($files.Count -eq 0) {
        throw 'No production source files were discovered for the safety scan.'
    }
    return $files
}

function Assert-ProductionPatternAbsent {
    param(
        [Parameter(Mandatory = $true)][IO.FileInfo[]]$Files,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Pattern
    )

    $options = [Text.RegularExpressions.RegexOptions]::CultureInvariant -bor
        [Text.RegularExpressions.RegexOptions]::IgnoreCase
    foreach ($file in $Files) {
        $source = [IO.File]::ReadAllText($file.FullName)
        $match = [regex]::Match($source, $Pattern, $options)
        if ($match.Success) {
            $line = 1 + [regex]::Matches(
                $source.Substring(0, $match.Index), "`n").Count
            throw (
                "Forbidden production memory behavior '$Name' was found in " +
                "$($file.FullName):$line")
        }
    }
}

$productionFiles = @(Get-ProductionSourceFiles)
$forbiddenBehaviors = @(
    @{
        Name = 'working-set trim (EmptyWorkingSet)'
        Pattern = '\bEmptyWorkingSet\b'
    },
    @{
        Name = 'working-set trim (SetProcessWorkingSetSize)'
        Pattern = '\bSetProcessWorkingSetSize(?:Ex)?\b'
    },
    @{
        Name = 'standby-list purge (NtSetSystemInformation)'
        Pattern = '\bNtSetSystemInformation\b'
    },
    @{
        Name = 'standby-list purge command'
        Pattern = '\b(?:MemoryPurgeStandbyList|SystemMemoryListInformation|EmptyStandbyList|PurgeStandbyList)\b'
    },
    @{
        Name = 'system file-cache mutation'
        Pattern = '\bSetSystemFileCacheSize\b'
    },
    @{
        Name = 'forced garbage collection'
        Pattern = '\b(?:System\s*\.\s*)?GC\s*\.\s*Collect\s*\('
    },
    @{
        Name = 'forced garbage-collection mode'
        Pattern = '\bGCCollectionMode\s*\.\s*Forced\b'
    }
)
foreach ($behavior in $forbiddenBehaviors) {
    Assert-ProductionPatternAbsent `
        -Files $productionFiles `
        -Name $behavior.Name `
        -Pattern $behavior.Pattern
}
Assert-ProductionPatternAbsent `
    -Files $productionFiles `
    -Name 'runtime call to legacy ActiveMemoryMaintenanceService' `
    -Pattern '\bActiveMemoryMaintenanceService\s*\.\s*(?:Run|RunImmediateForCurrentProcess)\s*\('

$program = [IO.File]::ReadAllText($programPath)
foreach ($legacyCall in @(
    '(?m)^\s*(?:this\s*\.\s*)?RunActiveMemoryMaintenance\s*\('
)) {
    if ([regex]::IsMatch(
            $program,
            $legacyCall,
            [Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
        throw 'Program.cs contains a runtime call to legacy memory maintenance.'
    }
}

foreach ($requiredBoundary in @(
    'new SessionGuardTargetIdentity(',
    'selectedTarget.ProcessId',
    'selectedTarget.ProcessStartTimeUtc',
    'selectedTarget.ExecutablePath',
    'Task ignored = sampler.StartAsync(',
    'guardTarget,',
    'cancellation.Token'
)) {
    if (-not $program.Contains($requiredBoundary)) {
        throw "The exact-target Session Guard boundary is missing: $requiredBoundary"
    }
}

$guardTests = [IO.File]::ReadAllText($guardTestPath)
foreach ($requiredBehaviorTest in @(
    'TestSingleSpikeIsIgnored();',
    'TestSustainedCriticalAlert();',
    'TestZeroCommitHeadroom();',
    'TestRecoveryHysteresisAndCooldown();',
    'TestExactTargetIdentity();',
    'TestWindowsSourceRejectsIdentityMismatch();'
)) {
    if (-not $guardTests.Contains($requiredBehaviorTest)) {
        throw "A required Session Guard regression is missing: $requiredBehaviorTest"
    }
}

$runAll = [IO.File]::ReadAllText($runAllPath)
foreach ($requiredV2Test in @(
    'Test-BackgroundImpactSafety.ps1',
    'Test-GameTargetProfiles.ps1',
    'Test-PerformanceProofAndCrash.ps1',
    'Test-SessionGuard.ps1',
    'Test-SessionGuardIntegrationBoundary.ps1'
)) {
    if (-not $runAll.Contains("'$requiredV2Test'")) {
        throw "Run-All.ps1 does not require the V2 test: $requiredV2Test"
    }
}

Write-Host (
    'Active memory-maintenance safety contract passed: Session Guard is ' +
    'advisory-only and legacy relief has no runtime call site.') `
    -ForegroundColor Green
