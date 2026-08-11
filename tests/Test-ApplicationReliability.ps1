[CmdletBinding()]
param(
    [string]$ApplicationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $ApplicationPath) {
    $ApplicationPath = Join-Path $projectRoot 'dist\Boostix.exe'
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path
$program = [IO.File]::ReadAllText(
    (Join-Path $projectRoot 'Boostix\Program.cs'))
$features = [IO.File]::ReadAllText(
    (Join-Path $projectRoot 'Boostix\BoostFeatures.cs'))

foreach ($required in @(
    'Local\SilasSuspect.Boostix.Application',
    'applicationMutex.WaitOne(0, false)',
    'catch (AbandonedMutexException)',
    'applicationMutex.ReleaseMutex()',
    'DispatcherUnhandledException',
    'AppDomain.CurrentDomain.UnhandledException',
    'TaskScheduler.UnobservedTaskException',
    'internal static class CrashLog',
    'MaximumLogBytes = 512 * 1024',
    'BoostSessionReportStore.WriteAllTextAtomic(',
    'StartExactTargetExitWatcher(generation)',
    'StopExactTargetExitWatcher();',
    'process.StartTime.ToUniversalTime()',
    'GameExecutablePath.AreEquivalent(',
    'process.Exited += handler',
    'process.EnableRaisingEvents = true',
    'exitedProcess.ExitTime.ToUniversalTime()',
    'exitCode = exitedProcess.ExitCode',
    'ExitCode = observedExitCode'
)) {
    if (-not $program.Contains($required)) {
        throw "Application reliability contract is missing: $required"
    }
}

foreach ($required in @(
    'public int MemorySamples;',
    'public long MemoryReliefBytes;',
    'public long MinimumCommitHeadroomBytes;',
    'public long PeakTargetPrivateBytes;',
    'public string TargetCrashCode;',
    '(version != 1 && version != 2 && version != 3 && version != 4)',
    'LegacySessionsDirectory',
    'IsSafeKnownSessionsDirectory',
    '(file.Attributes & FileAttributes.ReparsePoint) != 0',
    'public static void WriteAllTextAtomic('
)) {
    if (-not $features.Contains($required)) {
        throw "Session reliability contract is missing: $required"
    }
}

$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($ApplicationPath))
$allStatic = [Reflection.BindingFlags]::Public -bor
    [Reflection.BindingFlags]::NonPublic -bor
    [Reflection.BindingFlags]::Static
$windowType = $assembly.GetType('Boostix.BoostWindow', $true)
$indexedKey = $windowType.GetMethod('IsIndexedResultKey', $allStatic)
if (-not $indexedKey) {
    throw 'The structured result key parser was not compiled.'
}

function Test-IndexedKey {
    param([string]$Key, [string]$Prefix)
    return [bool]$indexedKey.Invoke(
        $null,
        [object[]]@([string]$Key, [string]$Prefix))
}

foreach ($case in @(
    @{ Key = 'Process1'; Prefix = 'Process'; Expected = $true },
    @{ Key = 'PROCESS27'; Prefix = 'Process'; Expected = $true },
    @{ Key = 'StoppedProcessCount'; Prefix = 'Process'; Expected = $false },
    @{ Key = 'ProcessCount'; Prefix = 'Process'; Expected = $false },
    @{ Key = 'Process01'; Prefix = 'Process'; Expected = $false },
    @{ Key = 'Warning1'; Prefix = 'Warning'; Expected = $true },
    @{ Key = 'WarningCount'; Prefix = 'Warning'; Expected = $false },
    @{ Key = 'Warning0'; Prefix = 'Warning'; Expected = $false },
    @{ Key = 'WarningX'; Prefix = 'Warning'; Expected = $false }
)) {
    $actual = Test-IndexedKey `
        -Key ([string]$case.Key) `
        -Prefix ([string]$case.Prefix)
    if ($actual -ne $case.Expected) {
        throw "Unexpected result-key classification for $($case.Key): $actual"
    }
}

$storeType = $assembly.GetType('Boostix.BoostSessionReportStore', $true)
$atomicWrite = $storeType.GetMethod('WriteAllTextAtomic', $allStatic)
if (-not $atomicWrite) {
    throw 'Atomic settings writer was not compiled.'
}

# Once the sampler has observed a genuine zero commit reserve, a later healthy
# diagnostic sample must not rewrite it as if the critical event never happened.
$instanceFlags = [Reflection.BindingFlags]::Instance -bor
    [Reflection.BindingFlags]::Public -bor
    [Reflection.BindingFlags]::NonPublic
$reportType = $assembly.GetType('Boostix.BoostSessionReport', $true)
$diagnosticType = $assembly.GetType('Boostix.DiagnosticSnapshot', $true)
$report = [Activator]::CreateInstance($reportType, $true)
$diagnostic = [Activator]::CreateInstance($diagnosticType, $true)
$reportType.GetField('MemorySamples', $instanceFlags).SetValue($report, 1)
$reportType.GetField('MinimumCommitHeadroomBytes', $instanceFlags).SetValue(
    $report,
    [long]0)
$reportType.GetField('MinimumAvailableMemoryBytes', $instanceFlags).SetValue(
    $report,
    [long]0)
$diagnosticType.GetField('MemoryAvailable', $instanceFlags).SetValue(
    $diagnostic,
    $true)
$diagnosticType.GetField('PhysicalAvailableBytes', $instanceFlags).SetValue(
    $diagnostic,
    [long](8GB))
$diagnosticType.GetField('CommitHeadroomBytes', $instanceFlags).SetValue(
    $diagnostic,
    [long](12GB))
$diagnosticType.GetField('PhysicalTotalBytes', $instanceFlags).SetValue(
    $diagnostic,
    [long](16GB))
$diagnosticType.GetField('CommitLimitBytes', $instanceFlags).SetValue(
    $diagnostic,
    [long](32GB))
$applyDiagnostic = $reportType.GetMethod(
    'ApplyDiagnosticSnapshot',
    $instanceFlags)
[void]$applyDiagnostic.Invoke($report, [object[]]@($diagnostic))
$minimumCommit = [long]$reportType.GetField(
    'MinimumCommitHeadroomBytes',
    $instanceFlags).GetValue($report)
if ($minimumCommit -ne 0) {
    throw "A zero commit-headroom observation was lost: $minimumCommit"
}
$testRoot = Join-Path $env:TEMP (
    'Boostix-AppReliability-' + [Guid]::NewGuid().ToString('N'))
try {
    [void](New-Item -ItemType Directory -Path $testRoot)
    [string]$destination = Join-Path $testRoot 'boost-preferences.ini'
    $writeArguments = New-Object 'System.Object[]' 2
    $writeArguments.SetValue([string]$destination, 0)
    $writeArguments.SetValue([string]"AutoBoost=True`r`n", 1)
    [void]$atomicWrite.Invoke($null, $writeArguments)
    if ([IO.File]::ReadAllText($destination) -cne "AutoBoost=True`r`n") {
        throw 'Atomic settings writer produced unexpected content.'
    }
    $leftovers = Get-ChildItem -LiteralPath $testRoot -Force |
        Where-Object { $_.Name -ne 'boost-preferences.ini' }
    if ($leftovers) {
        throw 'Atomic settings writer left temporary or backup files.'
    }
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

'Application single-instance, result parsing, telemetry, and atomic-write tests passed.'
