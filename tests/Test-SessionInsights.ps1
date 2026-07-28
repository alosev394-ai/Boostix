[CmdletBinding()]
param(
    [string]$ApplicationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSEdition -cne 'Desktop' -or
    $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This test must run in Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ApplicationPath)) {
    $ApplicationPath = Join-Path $projectRoot 'dist\Boostix.exe'
}

if (-not (Test-Path -LiteralPath $ApplicationPath -PathType Leaf)) {
    throw "Application was not found: $ApplicationPath"
}

$assembly = [Reflection.Assembly]::LoadFile(
    [IO.Path]::GetFullPath($ApplicationPath))
$reportType = $assembly.GetType('Boostix.BoostSessionReport', $true)
$performanceType = $assembly.GetType('Boostix.BoostPerformanceResult', $true)
$assistantType = $assembly.GetType('Boostix.BoostCrashAssistant', $true)
$comparisonType = $assembly.GetType('Boostix.BoostSessionComparison', $true)
$categoryType = $assembly.GetType('Boostix.BoostCrashCategory', $true)
$snapshotType = $assembly.GetType('Boostix.DiagnosticSnapshot', $true)
$pressureType = $assembly.GetType('Boostix.DiagnosticPressureLevel', $true)
$storeType = $assembly.GetType('Boostix.BoostSessionReportStore', $true)
$outcomeType = $assembly.GetType('Boostix.BoostActionOutcome', $true)
$windowType = $assembly.GetType('Boostix.BoostWindow', $true)

$bindingFlags = [Reflection.BindingFlags]'Public,NonPublic,Static,Instance'

function New-Report {
    param(
        [string]$Id,
        [datetime]$StartedUtc
    )

    $report = [Activator]::CreateInstance($reportType, $true)
    $reportType.GetField('SessionId', $bindingFlags).SetValue($report, $Id)
    $reportType.GetField('StartedUtc', $bindingFlags).SetValue($report, $StartedUtc)
    return $report
}

function Set-Field {
    param(
        [object]$Target,
        [Type]$Type,
        [string]$Name,
        [object]$Value
    )

    $field = $Type.GetField($Name, $bindingFlags)
    if ($null -eq $field) {
        throw "Field not found: $($Type.FullName).$Name"
    }
    $field.SetValue($Target, $Value)
}

$analyzeMethod = $assistantType.GetMethod('Analyze', $bindingFlags)
if ($null -eq $analyzeMethod) {
    throw 'BoostCrashAssistant.Analyze was not found.'
}

$accessReport = New-Report -Id ([Guid]::NewGuid().ToString('N')) -StartedUtc ([datetime]::UtcNow)
Set-Field $accessReport $reportType 'TargetCrashCode' 'c0000005'
Set-Field $accessReport $reportType 'TargetCrashModule' 'C:\Users\private\ReShade64.dll'
$accessInsight = $analyzeMethod.Invoke($null, @($accessReport))
$accessCategory = $accessInsight.GetType().GetField('Category', $bindingFlags).GetValue($accessInsight)
if ([string]$accessCategory -cne 'AccessViolation') {
    throw "0xC0000005 was classified as $accessCategory."
}
$accessEvidence = [string]$accessInsight.GetType().GetField('Evidence', $bindingFlags).GetValue($accessInsight)
if ($accessEvidence -notmatch 'ReShade64\.dll' -or $accessEvidence -match 'Users\\private') {
    throw 'Crash evidence must contain only the module file name, not its private path.'
}

$memoryReport = New-Report -Id ([Guid]::NewGuid().ToString('N')) -StartedUtc ([datetime]::UtcNow)
Set-Field $memoryReport $reportType 'TargetCrashCode' '0xC000012D'
$memoryInsight = $analyzeMethod.Invoke($null, @($memoryReport))
$memoryCategory = $memoryInsight.GetType().GetField('Category', $bindingFlags).GetValue($memoryInsight)
if ([string]$memoryCategory -cne 'MemoryPressure') {
    throw "0xC000012D was classified as $memoryCategory."
}

$graphicsReport = New-Report -Id ([Guid]::NewGuid().ToString('N')) -StartedUtc ([datetime]::UtcNow)
Set-Field $graphicsReport $reportType 'TargetCrashCode' '0x887A0006'
$graphicsInsight = $analyzeMethod.Invoke($null, @($graphicsReport))
$graphicsCategory = $graphicsInsight.GetType().GetField('Category', $bindingFlags).GetValue($graphicsInsight)
if ([string]$graphicsCategory -cne 'GraphicsDevice') {
    throw "0x887A0006 was classified as $graphicsCategory."
}

$previous = New-Report -Id ([Guid]::NewGuid().ToString('N')) -StartedUtc ([datetime]::UtcNow.AddHours(-2))
$current = New-Report -Id ([Guid]::NewGuid().ToString('N')) -StartedUtc ([datetime]::UtcNow.AddHours(-1))

$previousPerformance = [Activator]::CreateInstance($performanceType, $true)
Set-Field $previousPerformance $performanceType 'Available' $true
Set-Field $previousPerformance $performanceType 'AverageFps' ([double]100)
Set-Field $previousPerformance $performanceType 'OnePercentLowFps' ([double]70)
Set-Field $previousPerformance $performanceType 'P95FrameTimeMs' ([double]15)
Set-Field $previousPerformance $performanceType 'Frames' 1000
Set-Field $previousPerformance $performanceType 'FramesOver50Ms' 12
Set-Field $previousPerformance $performanceType 'ProcessName' 'SampleApp.exe'
Set-Field $previous $reportType 'Performance' $previousPerformance

$currentPerformance = [Activator]::CreateInstance($performanceType, $true)
Set-Field $currentPerformance $performanceType 'Available' $true
Set-Field $currentPerformance $performanceType 'AverageFps' ([double]112.5)
Set-Field $currentPerformance $performanceType 'OnePercentLowFps' ([double]78)
Set-Field $currentPerformance $performanceType 'P95FrameTimeMs' ([double]12)
Set-Field $currentPerformance $performanceType 'Frames' 1000
Set-Field $currentPerformance $performanceType 'FramesOver50Ms' 5
Set-Field $currentPerformance $performanceType 'ProcessName' 'sampleapp'
Set-Field $current $reportType 'Performance' $currentPerformance

$differentTarget = New-Report -Id ([Guid]::NewGuid().ToString('N')) -StartedUtc ([datetime]::UtcNow.AddMinutes(-90))
$differentPerformance = [Activator]::CreateInstance($performanceType, $true)
Set-Field $differentPerformance $performanceType 'Available' $true
Set-Field $differentPerformance $performanceType 'AverageFps' ([double]1)
Set-Field $differentPerformance $performanceType 'OnePercentLowFps' ([double]1)
Set-Field $differentPerformance $performanceType 'P95FrameTimeMs' ([double]999)
Set-Field $differentPerformance $performanceType 'Frames' 1000
Set-Field $differentPerformance $performanceType 'FramesOver50Ms' 999
Set-Field $differentPerformance $performanceType 'ProcessName' 'DifferentApp.exe'
Set-Field $differentTarget $reportType 'Performance' $differentPerformance

$listType = [Collections.Generic.List``1].MakeGenericType($reportType)
$recent = [Activator]::CreateInstance($listType)
[void]$listType.GetMethod('Add').Invoke($recent, @($current))
[void]$listType.GetMethod('Add').Invoke($recent, @($differentTarget))
[void]$listType.GetMethod('Add').Invoke($recent, @($previous))

$compareMethod = $comparisonType.GetMethod('Compare', $bindingFlags)
$comparison = $compareMethod.Invoke($null, @($current, $recent))
$comparisonResultType = $comparison.GetType()
if ([bool]$comparisonResultType.GetField('Available', $bindingFlags).GetValue($comparison)) {
    throw 'Process-name equality alone must not produce an FPS delta claim.'
}

Set-Field $previousPerformance $performanceType 'ComparisonContextKey' 'repeatable-scene-v1'
Set-Field $currentPerformance $performanceType 'ComparisonContextKey' 'repeatable-scene-v1'
$comparison = $compareMethod.Invoke($null, @($current, $recent))
if (-not [bool]$comparisonResultType.GetField('Available', $bindingFlags).GetValue($comparison)) {
    throw 'Comparable FPS sessions were not matched.'
}
if ([double]$comparisonResultType.GetField('AverageFpsDelta', $bindingFlags).GetValue($comparison) -ne 12.5) {
    throw 'Average FPS delta is incorrect.'
}
if ([int]$comparisonResultType.GetField('FramesOver50MsDelta', $bindingFlags).GetValue($comparison) -ne -7) {
    throw 'Slow-frame delta is incorrect.'
}
if ([string]$comparisonResultType.GetField('ComparedSessionId', $bindingFlags).GetValue($comparison) -cne
    [string]$reportType.GetField('SessionId', $bindingFlags).GetValue($previous)) {
    throw 'FPS comparison mixed measurements from different tracked processes.'
}

Set-Field $previousPerformance $performanceType 'ComparisonContextKey' 'different-scene'
$comparison = $compareMethod.Invoke($null, @($current, $recent))
if ([bool]$comparisonResultType.GetField('Available', $bindingFlags).GetValue($comparison)) {
    throw 'FPS comparison mixed measurements with different explicit contexts.'
}
Set-Field $previousPerformance $performanceType 'ComparisonContextKey' 'repeatable-scene-v1'

$resourceReport = New-Report -Id ([Guid]::NewGuid().ToString('N')) -StartedUtc ([datetime]::UtcNow)
$snapshot = [Activator]::CreateInstance($snapshotType, $true)
Set-Field $snapshot $snapshotType 'MemoryAvailable' $true
Set-Field $snapshot $snapshotType 'PhysicalTotalBytes' ([long](16GB))
Set-Field $snapshot $snapshotType 'PhysicalAvailableBytes' ([long](2GB))
Set-Field $snapshot $snapshotType 'CommitLimitBytes' ([long](24GB))
Set-Field $snapshot $snapshotType 'CommitHeadroomBytes' ([long](1GB))
Set-Field $snapshot $snapshotType 'PageFileAvailable' $true
Set-Field $snapshot $snapshotType 'PageFileAllocatedBytes' ([long](8GB))
Set-Field $snapshot $snapshotType 'PageFileUsedBytes' ([long](3GB))
Set-Field $snapshot $snapshotType 'GpuUsageAvailable' $true
Set-Field $snapshot $snapshotType 'GpuBudgetAvailable' $true
Set-Field $snapshot $snapshotType 'GpuTotalAvailable' $true
Set-Field $snapshot $snapshotType 'GpuDedicatedUsageBytes' ([long](6GB))
Set-Field $snapshot $snapshotType 'GpuDedicatedBudgetBytes' ([long](8GB))
Set-Field $snapshot $snapshotType 'GpuDedicatedTotalBytes' ([long](8GB))
Set-Field $snapshot $snapshotType 'GpuAdapterNames' 'Test GPU'
Set-Field $snapshot $snapshotType 'GpuAdapterLuid' '00000001_00000002'
Set-Field $snapshot $snapshotType 'Pressure' ([Enum]::Parse($pressureType, 'Critical'))

$applySnapshot = $reportType.GetMethod('ApplyDiagnosticSnapshot', $bindingFlags)
$applySnapshot.Invoke($resourceReport, @($snapshot))
if ([int]$reportType.GetField('DiagnosticSamples', $bindingFlags).GetValue($resourceReport) -ne 1 -or
    [int]$reportType.GetField('GpuMemorySamples', $bindingFlags).GetValue($resourceReport) -ne 1 -or
    [long]$reportType.GetField('MinimumGpuDedicatedHeadroomBytes', $bindingFlags).GetValue($resourceReport) -ne 2GB -or
    [string]$reportType.GetField('WorstResourcePressure', $bindingFlags).GetValue($resourceReport) -cne 'Critical') {
    throw 'Session resource telemetry did not preserve its first critical GPU sample.'
}

$lowerPressureAdapter = [Activator]::CreateInstance($snapshotType, $true)
Set-Field $lowerPressureAdapter $snapshotType 'GpuUsageAvailable' $true
Set-Field $lowerPressureAdapter $snapshotType 'GpuTotalAvailable' $true
Set-Field $lowerPressureAdapter $snapshotType 'GpuDedicatedUsageBytes' ([long](4GB))
Set-Field $lowerPressureAdapter $snapshotType 'GpuDedicatedTotalBytes' ([long](16GB))
Set-Field $lowerPressureAdapter $snapshotType 'GpuAdapterNames' 'Other GPU'
Set-Field $lowerPressureAdapter $snapshotType 'GpuAdapterLuid' '00000003_00000004'
$applySnapshot.Invoke($resourceReport, @($lowerPressureAdapter))
if ([string]$reportType.GetField('GpuAdapterLuid', $bindingFlags).GetValue($resourceReport) -cne
        '00000001_00000002' -or
    [long]$reportType.GetField('GpuDedicatedTotalBytes', $bindingFlags).GetValue($resourceReport) -ne 8GB -or
    [long]$reportType.GetField('PeakGpuDedicatedUsageBytes', $bindingFlags).GetValue($resourceReport) -ne 6GB) {
    throw 'A lower-pressure adapter replaced the selected GPU and mixed its capacity.'
}

$higherPressureAdapter = [Activator]::CreateInstance($snapshotType, $true)
Set-Field $higherPressureAdapter $snapshotType 'GpuUsageAvailable' $true
Set-Field $higherPressureAdapter $snapshotType 'GpuTotalAvailable' $true
Set-Field $higherPressureAdapter $snapshotType 'GpuDedicatedUsageBytes' ([long](15GB))
Set-Field $higherPressureAdapter $snapshotType 'GpuDedicatedTotalBytes' ([long](16GB))
Set-Field $higherPressureAdapter $snapshotType 'GpuAdapterNames' 'Other GPU'
Set-Field $higherPressureAdapter $snapshotType 'GpuAdapterLuid' '00000003_00000004'
$applySnapshot.Invoke($resourceReport, @($higherPressureAdapter))
if ([string]$reportType.GetField('GpuAdapterLuid', $bindingFlags).GetValue($resourceReport) -cne
        '00000003_00000004' -or
    [string]$reportType.GetField('GpuAdapterNames', $bindingFlags).GetValue($resourceReport) -cne
        'Other GPU' -or
    [long]$reportType.GetField('GpuDedicatedTotalBytes', $bindingFlags).GetValue($resourceReport) -ne 16GB -or
    [long]$reportType.GetField('PeakGpuDedicatedUsageBytes', $bindingFlags).GetValue($resourceReport) -ne 15GB -or
    [long]$reportType.GetField('MinimumGpuDedicatedHeadroomBytes', $bindingFlags).GetValue($resourceReport) -ne 1GB -or
    [int]$reportType.GetField('GpuMemorySamples', $bindingFlags).GetValue($resourceReport) -ne 1) {
    throw 'A higher-pressure adapter did not replace all linked GPU telemetry atomically.'
}

$serialize = $storeType.GetMethod('Serialize', $bindingFlags)
$deserialize = $storeType.GetMethod('Deserialize', $bindingFlags)
$resourcePerformance = [Activator]::CreateInstance($performanceType, $true)
Set-Field $resourcePerformance $performanceType 'Available' $true
Set-Field $resourcePerformance $performanceType 'Frames' 1000
Set-Field $resourcePerformance $performanceType 'ProcessName' 'test-game'
Set-Field $resourcePerformance $performanceType 'ComparisonContextKey' 'roundtrip-context'
Set-Field $resourceReport $reportType 'Performance' $resourcePerformance
Set-Field $resourceReport $reportType 'TargetName' 'SampleApp'
Set-Field $resourceReport $reportType 'PeakTargetWorkingSetBytes' ([long](2GB))
Set-Field $resourceReport $reportType 'PeakTargetPrivateBytes' ([long](3GB))
Set-Field $resourceReport $reportType 'TargetCrashCode' '0xC0000005'
Set-Field $resourceReport $reportType 'TargetCrashModule' 'sample.dll'
$serialized = [string]$serialize.Invoke($null, @($resourceReport))
foreach ($legacyKey in @(
    'PeakGameWorkingSetBytes=',
    'PeakGamePrivateBytes=',
    'GameCrashCode=',
    'GameCrashModule=',
    'GameCrashOffset=',
    'GameCrashUtc=',
    'GameName='
)) {
    if ($serialized -match ('(?m)^' + [regex]::Escape($legacyKey))) {
        throw "A new session report wrote the legacy key $legacyKey"
    }
}
$roundTrip = $deserialize.Invoke(
    $null,
    [object[]](,[string[]]($serialized -split "\r?\n")))
if ($null -eq $roundTrip -or
    [int]$reportType.GetField('Version', $bindingFlags).GetValue($roundTrip) -ne 4 -or
    [string]$reportType.GetField('GpuAdapterNames', $bindingFlags).GetValue($roundTrip) -cne 'Other GPU' -or
    [string]$reportType.GetField('GpuAdapterLuid', $bindingFlags).GetValue($roundTrip) -cne '00000003_00000004' -or
    [long]$reportType.GetField('PageFileAllocatedBytes', $bindingFlags).GetValue($roundTrip) -ne 8GB -or
    [string]$reportType.GetField('TargetName', $bindingFlags).GetValue($roundTrip) -cne 'SampleApp' -or
    [long]$reportType.GetField('PeakTargetWorkingSetBytes', $bindingFlags).GetValue($roundTrip) -ne 2GB -or
    [long]$reportType.GetField('PeakTargetPrivateBytes', $bindingFlags).GetValue($roundTrip) -ne 3GB -or
    [string]$reportType.GetField('TargetCrashCode', $bindingFlags).GetValue($roundTrip) -cne '0xC0000005' -or
    [string]$performanceType.GetField(
        'ComparisonContextKey',
        $bindingFlags).GetValue(
            $reportType.GetField('Performance', $bindingFlags).GetValue($roundTrip)) -cne
        'roundtrip-context') {
    throw 'Version 4 session telemetry did not survive serialization.'
}

$legacySerialized = $serialized.Replace('Version=4', 'Version=3')
$legacySerialized = $legacySerialized.Replace(
    'PeakTargetWorkingSetBytes=',
    'PeakGameWorkingSetBytes=')
$legacySerialized = $legacySerialized.Replace(
    'PeakTargetPrivateBytes=',
    'PeakGamePrivateBytes=')
$legacySerialized = $legacySerialized.Replace('TargetCrashCode=', 'GameCrashCode=')
$legacySerialized = $legacySerialized.Replace('TargetCrashModule=', 'GameCrashModule=')
$legacySerialized = $legacySerialized.Replace('TargetCrashOffset=', 'GameCrashOffset=')
$legacySerialized = $legacySerialized.Replace('TargetCrashUtc=', 'GameCrashUtc=')
$legacySerialized = $legacySerialized.Replace('TargetName=', 'GameName=')
$legacyStatus = [Convert]::ToBase64String(
    [Text.Encoding]::UTF8.GetBytes('GameCrashed'))
$legacySerialized = [regex]::Replace(
    $legacySerialized,
    '(?m)^Status=.*$',
    'Status=' + $legacyStatus)
$legacyRoundTrip = $deserialize.Invoke(
    $null,
    [object[]](,[string[]]($legacySerialized -split "\r?\n")))
if ($null -eq $legacyRoundTrip -or
    [string]$reportType.GetField('TargetName', $bindingFlags).GetValue($legacyRoundTrip) -cne 'SampleApp' -or
    [long]$reportType.GetField('PeakTargetPrivateBytes', $bindingFlags).GetValue($legacyRoundTrip) -ne 3GB -or
    [string]$reportType.GetField('TargetCrashModule', $bindingFlags).GetValue($legacyRoundTrip) -cne 'sample.dll' -or
    [string]$reportType.GetField('Status', $bindingFlags).GetValue($legacyRoundTrip) -cne 'TargetCrashed') {
    throw 'Legacy version 3 session keys were not migrated into the generic schema.'
}

$cloneSource = New-Report -Id ([Guid]::NewGuid().ToString('N')) -StartedUtc ([datetime]::UtcNow)
$clonePerformance = [Activator]::CreateInstance($performanceType, $true)
Set-Field $clonePerformance $performanceType 'Available' $true
Set-Field $clonePerformance $performanceType 'AverageFps' ([double]60)
Set-Field $clonePerformance $performanceType 'ComparisonContextKey' 'clone-context'
Set-Field $cloneSource $reportType 'Performance' $clonePerformance
$addAction = $reportType.GetMethod('AddAction', $bindingFlags)
$changedOutcome = [Enum]::Parse($outcomeType, 'Changed')
$addAction.Invoke($cloneSource, @('FIRST', 'before clone', $changedOutcome))
$cloneMethod = $reportType.GetMethod('Clone', $bindingFlags)
$clonedReport = $cloneMethod.Invoke($cloneSource, @())
Set-Field $clonePerformance $performanceType 'AverageFps' ([double]999)
$addAction.Invoke($cloneSource, @('SECOND', 'after clone', $changedOutcome))
$clonedPerformance = $reportType.GetField(
    'Performance',
    $bindingFlags).GetValue($clonedReport)
$clonedActions = $reportType.GetField(
    'Actions',
    $bindingFlags).GetValue($clonedReport)
if ([double]$performanceType.GetField(
        'AverageFps',
        $bindingFlags).GetValue($clonedPerformance) -ne 60 -or
    [string]$performanceType.GetField(
        'ComparisonContextKey',
        $bindingFlags).GetValue($clonedPerformance) -cne 'clone-context' -or
    [int]$clonedActions.Count -ne 1) {
    throw 'Session report cloning did not isolate mutable performance/actions state.'
}

$mergeMethod = $windowType.GetMethod(
    'MergeExportSessionSnapshot',
    $bindingFlags)
if ($null -eq $mergeMethod) {
    throw 'The bounded in-memory export merge was not compiled.'
}
$activeExport = New-Report -Id 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' -StartedUtc ([datetime]::UtcNow)
Set-Field $activeExport $reportType 'DiagnosticSamples' 7
$staleExport = New-Report -Id 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' -StartedUtc ([datetime]::UtcNow.AddMinutes(-5))
Set-Field $staleExport $reportType 'DiagnosticSamples' 1
$olderExport = New-Report -Id 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' -StartedUtc ([datetime]::UtcNow.AddMinutes(-10))
$storedExports = [Activator]::CreateInstance($listType)
[void]$listType.GetMethod('Add').Invoke($storedExports, @($staleExport))
[void]$listType.GetMethod('Add').Invoke($storedExports, @($olderExport))
$memoryExports = [Activator]::CreateInstance($listType)
[void]$listType.GetMethod('Add').Invoke($memoryExports, @($staleExport))
$mergedExports = $mergeMethod.Invoke(
    $null,
    @($storedExports, $memoryExports, $activeExport))
$firstMerged = $mergedExports[0]
if ($mergedExports.Count -ne 2 -or
    [string]$reportType.GetField(
        'SessionId',
        $bindingFlags).GetValue($firstMerged) -cne
        'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' -or
    [int]$reportType.GetField(
        'DiagnosticSamples',
        $bindingFlags).GetValue($firstMerged) -ne 7) {
    throw 'Diagnostic export lost the active session or preferred a stale disk copy.'
}

Write-Host 'Session insight tests passed.'
