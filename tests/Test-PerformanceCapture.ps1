[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$fixture = Join-Path $PSScriptRoot 'fixtures\presentmon-v2.csv'
$captureSource = [IO.File]::ReadAllText(
    (Join-Path $projectRoot 'Boostix\PerformanceCapture.cs'))
$installerSource = [IO.File]::ReadAllText(
    (Join-Path $projectRoot 'BoostixInstaller\Program.cs'))
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'Boostix-PerformanceCapture-' + [Guid]::NewGuid().ToString('N'))
$harness = Join-Path $temporaryRoot 'PerformanceCaptureParserHarness.exe'
$missingProcessIdFixture = Join-Path $temporaryRoot 'missing-process-id.csv'
$missingProcessIdRowsFixture = Join-Path $temporaryRoot 'missing-process-id-rows.csv'

foreach ($required in @(
    'Environment.SpecialFolder.CommonApplicationData',
    'ResolveProtectedCaptureDirectory()',
    'ValidateProtectedCaptureDirectory',
    'ValidateElevatedCaptureFile(elevatedOutputPath);',
    'ProductBrand.ProductFileName + "-PresentMon-"',
    'FileAttributes.ReparsePoint',
    'CalculateOnePercentLowFps(sorted)',
    'Math.Ceiling(sortedFrameTimes.Count * 0.01)',
    'Process.GetProcesses()',
    'process.MainWindowHandle == IntPtr.Zero',
    'process.WorkingSet64 < MinimumTargetWorkingSetBytes',
    'ExcludedTargetProcesses.Contains(processName)',
    'GameExecutablePath.AreEquivalent(',
    'if (processIdIndex < 0)',
    'parsed.Status == PerformanceCaptureStatus.Completed &&'
)) {
    if (-not $captureSource.Contains($required)) {
        throw "Protected elevated capture contract is missing: $required"
    }
}

if ($captureSource.Contains('OnePercentLowFps = 1000.0 / p99')) {
    throw '1% low must average the slowest 1% frame times, not reuse P99.'
}
foreach ($forbiddenPublicTarget in @(
    '"GTA V',
    '"GTA5"',
    '"GTA5_Enhanced"'
)) {
    if ($captureSource.Contains($forbiddenPublicTarget)) {
        throw "Performance capture is still tied to a single game: $forbiddenPublicTarget"
    }
}

$elevatedPathStart = $captureSource.IndexOf(
    'private static string CreateElevatedCapturePath()',
    [StringComparison]::Ordinal)
$elevatedPathEnd = $captureSource.IndexOf(
    'private static void ValidateProtectedCaptureDirectory',
    $elevatedPathStart,
    [StringComparison]::Ordinal)
if ($elevatedPathStart -lt 0 -or $elevatedPathEnd -le $elevatedPathStart) {
    throw 'The protected elevated capture path section could not be located.'
}
$elevatedPathSection = $captureSource.Substring(
    $elevatedPathStart,
    $elevatedPathEnd - $elevatedPathStart)
if ($elevatedPathSection.Contains('SpecialFolder.Windows') -or
    $elevatedPathSection.Contains('"Temp"')) {
    throw 'Elevated capture must not stage CSV files in Windows\Temp.'
}

foreach ($required in @(
    'PrepareCaptureDirectoryTransaction',
    'ApplyCaptureDirectoryTransaction',
    'RollbackCaptureDirectoryTransaction',
    'SetAccessRuleProtection(true, false)',
    'WellKnownSidType.LocalSystemSid',
    'WellKnownSidType.BuiltinAdministratorsSid',
    'WellKnownSidType.AuthenticatedUserSid',
    'FileSystemRights.ReadAndExecute',
    'FileSystemRights.Delete',
    'PropagationFlags.InheritOnly',
    'Directory.CreateDirectory(path, security)',
    'TryPruneProtectedCaptureFiles(true)'
)) {
    if (-not $installerSource.Contains($required)) {
        throw "Protected capture installer contract is missing: $required"
    }
}

try {
    [void](New-Item -ItemType Directory -Path $temporaryRoot -Force)
    $arguments = @(
        '/nologo',
        '/target:exe',
        '/optimize+',
        '/reference:System.dll',
        '/reference:System.Core.dll',
        '/reference:System.Management.dll',
        "/out:$harness",
        (Join-Path $projectRoot 'ProductBrand.cs'),
        (Join-Path $projectRoot 'Boostix\BoostFeatures.cs'),
        (Join-Path $projectRoot 'Boostix\DiagnosticsFeatures.cs'),
        (Join-Path $projectRoot 'Boostix\SessionInsights.cs'),
        (Join-Path $projectRoot 'Boostix\GameTargetProfiles.cs'),
        (Join-Path $projectRoot 'Boostix\PerformanceCapture.cs'),
        (Join-Path $PSScriptRoot 'PerformanceCaptureParserHarness.cs')
    )

    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "PerformanceCapture harness compilation failed with exit code $LASTEXITCODE."
    }

    & $harness $fixture
    if ($LASTEXITCODE -ne 0) {
        throw "PerformanceCapture parser test failed with exit code $LASTEXITCODE."
    }

    $missingProcessIdLines = New-Object 'Collections.Generic.List[string]'
    $missingProcessIdLines.Add('Application,SwapChainAddress,FrameTime')
    for ($index = 0; $index -lt 650; $index++) {
        $missingProcessIdLines.Add('"Unbound App",0x1234,16')
    }
    [IO.File]::WriteAllLines(
        $missingProcessIdFixture,
        $missingProcessIdLines,
        [Text.UTF8Encoding]::new($false))

    $assembly = [Reflection.Assembly]::Load(
        [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($harness)))
    $serviceType = $assembly.GetType(
        'Boostix.PerformanceCaptureService',
        $true)
    $parseMethod = $serviceType.GetMethod(
        'ParseCaptureCsvForTesting',
        [Reflection.BindingFlags]'Static,NonPublic')
    if ($null -eq $parseMethod) {
        throw 'Performance capture parser test entry point was not found.'
    }
    $parseArguments = New-Object 'System.Object[]' 4
    $parseArguments.SetValue([string]$missingProcessIdFixture, 0)
    $parseArguments.SetValue([int]4242, 1)
    $parseArguments.SetValue([string]'UnboundApp', 2)
    $parseArguments.SetValue([datetime]::UtcNow, 3)
    $missingPidResult = $parseMethod.Invoke($null, $parseArguments)
    $statusField = $missingPidResult.GetType().GetField('Status')
    $performanceField = $missingPidResult.GetType().GetField('Performance')
    $missingPidStatus = [string]($statusField.GetValue($missingPidResult))
    $missingPidPerformance = $performanceField.GetValue($missingPidResult)
    $availableField = $missingPidPerformance.GetType().GetField('Available')
    if ($missingPidStatus -cne 'InvalidCapture' -or
        [bool]$availableField.GetValue($missingPidPerformance)) {
        throw "A PresentMon CSV without ProcessID was accepted for an exact target: $missingPidStatus"
    }

    $missingProcessIdRowLines = New-Object 'Collections.Generic.List[string]'
    $missingProcessIdRowLines.Add('FrameTime,ProcessID')
    for ($index = 0; $index -lt 650; $index++) {
        $missingProcessIdRowLines.Add('16')
    }
    [IO.File]::WriteAllLines(
        $missingProcessIdRowsFixture,
        $missingProcessIdRowLines,
        [Text.UTF8Encoding]::new($false))
    $parseArguments.SetValue([string]$missingProcessIdRowsFixture, 0)
    $parseArguments.SetValue([datetime]::UtcNow, 3)
    $missingPidRowsResult = $parseMethod.Invoke($null, $parseArguments)
    $missingPidRowsStatus = [string]($statusField.GetValue($missingPidRowsResult))
    $missingPidRowsPerformance = $performanceField.GetValue($missingPidRowsResult)
    if ($missingPidRowsStatus -cne 'InvalidCapture' -or
        [bool]$availableField.GetValue($missingPidRowsPerformance)) {
        throw "PresentMon rows without their required ProcessID value were accepted: $missingPidRowsStatus"
    }

    $targetType = $assembly.GetType('Boostix.PerformanceTargetProcess', $true)
    $target = [Activator]::CreateInstance($targetType, $true)
    $targetFlags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
    $sameTargetMethod = $serviceType.GetMethod(
        'IsSameTargetProcessRunning',
        [Reflection.BindingFlags]'Static,NonPublic')
    if ($null -eq $sameTargetMethod) {
        throw 'Exact live-target validation method was not found.'
    }
    $currentProcess = [Diagnostics.Process]::GetCurrentProcess()
    try {
        $targetType.GetField('ProcessId', $targetFlags).SetValue(
            $target,
            $currentProcess.Id)
        $targetType.GetField('ProcessName', $targetFlags).SetValue(
            $target,
            $currentProcess.ProcessName)
        $targetType.GetField('StartTimeUtc', $targetFlags).SetValue(
            $target,
            $currentProcess.StartTime.ToUniversalTime())
        $currentExecutable = $currentProcess.MainModule.FileName
        $targetType.GetField('ExecutablePath', $targetFlags).SetValue(
            $target,
            $currentExecutable.ToUpperInvariant())
        $sameTargetArguments = New-Object 'System.Object[]' 1
        $sameTargetArguments.SetValue($target, 0)
        if (-not [bool]$sameTargetMethod.Invoke($null, $sameTargetArguments)) {
            throw 'Case-equivalent exact executable path was rejected.'
        }

        $targetType.GetField('ExecutablePath', $targetFlags).SetValue(
            $target,
            (Join-Path $temporaryRoot 'different.exe'))
        if ([bool]$sameTargetMethod.Invoke($null, $sameTargetArguments)) {
            throw 'A live PID/start pair with a different executable path was accepted.'
        }

        $targetType.GetField('ExecutablePath', $targetFlags).SetValue(
            $target,
            [string]::Empty)
        if ([bool]$sameTargetMethod.Invoke($null, $sameTargetArguments)) {
            throw 'An exact capture target without an executable path was accepted.'
        }
    }
    finally {
        $currentProcess.Dispose()
    }
}
finally {
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolvedTarget = [IO.Path]::GetFullPath($temporaryRoot)
    if (
        $resolvedTarget.StartsWith(
            $resolvedTemp + 'Boostix-PerformanceCapture-',
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTarget)
    ) {
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force -ErrorAction SilentlyContinue
    }
}
