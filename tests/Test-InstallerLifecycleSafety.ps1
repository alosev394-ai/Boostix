[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $projectRoot 'BoostixInstaller\Program.cs'
$brandSource = Join-Path $projectRoot 'ProductBrand.cs'
$source = [IO.File]::ReadAllText($sourcePath)
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'MajesticBoost-InstallerLifecycle-' + [Guid]::NewGuid().ToString('N'))
$harnessPath = Join-Path $temporaryRoot (
    'InstallerLifecycleHarness-' + [Guid]::NewGuid().ToString('N') + '.dll')
$stateRoot = Join-Path $temporaryRoot 'BoostixOptimization'
$legacyStateRoot = Join-Path $temporaryRoot 'CodexGamingOptimization'
$utf8 = New-Object Text.UTF8Encoding($false)

function Get-DeepestException {
    param([Parameter(Mandatory = $true)][Exception]$Exception)

    $current = $Exception
    while ($current.InnerException) {
        $current = $current.InnerException
    }
    return $current
}

function Assert-InvocationFails {
    param(
        [Parameter(Mandatory = $true)][Reflection.MethodInfo]$Method,
        [Parameter(Mandatory = $true)][object[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Scenario,
        [Type]$ExpectedType = [InvalidOperationException]
    )

    try {
        $result = $Method.Invoke($null, $Arguments)
        if ($result -is [IDisposable]) {
            $result.Dispose()
        }
    }
    catch {
        $current = $_.Exception
        while ($current) {
            if ($ExpectedType.IsAssignableFrom($current.GetType())) {
                return
            }
            $current = $current.InnerException
        }
        $actual = Get-DeepestException -Exception $_.Exception
        throw "$Scenario failed with $($actual.GetType().FullName), expected $($ExpectedType.FullName): $($actual.Message)"
    }
    throw "$Scenario unexpectedly succeeded."
}

function Write-Utf8File {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    [IO.File]::WriteAllText($Path, $Content, $utf8)
}

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
        throw "C# compiler was not found: $compiler"
    }

    $compilerOutput = & $compiler `
        /nologo `
        /target:library `
        /utf8output `
        "/out:$harnessPath" `
        /reference:System.dll `
        /reference:System.Core.dll `
        /reference:System.Drawing.dll `
        /reference:System.Windows.Forms.dll `
        /reference:System.Security.dll `
        $brandSource `
        $sourcePath 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Installer source did not compile:`r`n$($compilerOutput -join [Environment]::NewLine)"
    }

    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($harnessPath))
    $engineType = $assembly.GetType('BoostixSetup.InstallerEngine', $true, $false)
    $flags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static
    $acquireGuard = $engineType.GetMethod(
        'AcquireSystemTransactionGuardAtRoot',
        $flags)
    $acquireGuards = $engineType.GetMethod(
        'AcquireSystemTransactionGuardsAtRoots',
        $flags)
    $validateUninstall = $engineType.GetMethod(
        'EnsureUninstallStateAllowsRemovalAtRoot',
        $flags)
    $validateUninstallRoots = $engineType.GetMethod(
        'EnsureUninstallStateAllowsRemovalAtRoots',
        $flags)
    $captureShortcut = $engineType.GetMethod(
        'CaptureShortcut',
        $flags)
    if (-not $acquireGuard -or -not $acquireGuards -or
        -not $validateUninstall -or -not $validateUninstallRoots -or
        -not $captureShortcut) {
        throw 'Installer lifecycle safety helpers were not found in the compiled source.'
    }

    $shortcutBoundary = Join-Path $temporaryRoot 'CommonPrograms'
    [IO.Directory]::CreateDirectory($shortcutBoundary) | Out-Null
    $newShortcutDirectory = Join-Path $shortcutBoundary 'Boostix'
    $newShortcutPath = Join-Path $newShortcutDirectory 'Boostix.lnk'
    $shortcutArguments = New-Object 'object[]' 1
    $shortcutArguments[0] = [string]$newShortcutPath
    $shortcutSnapshot = $captureShortcut.Invoke($null, $shortcutArguments)
    if ($null -eq $shortcutSnapshot -or
        [bool]$shortcutSnapshot.GetType().GetField('Existed').GetValue(
            $shortcutSnapshot)) {
        throw 'A clean missing Start Menu product directory was not snapshotted as absent.'
    }
    if (Test-Path -LiteralPath $newShortcutDirectory) {
        throw 'Shortcut snapshot unexpectedly created the missing product directory.'
    }

    $occupiedShortcutDirectory = Join-Path $shortcutBoundary 'Occupied'
    [IO.File]::WriteAllText($occupiedShortcutDirectory, 'occupied', $utf8)
    $occupiedArguments = New-Object 'object[]' 1
    $occupiedArguments[0] = [string](Join-Path `
        $occupiedShortcutDirectory 'Boostix.lnk')
    Assert-InvocationFails `
        -Method $captureShortcut `
        -Arguments $occupiedArguments `
        -Scenario 'Shortcut snapshot with a file occupying the product directory' `
        -ExpectedType ([IO.IOException])

    # This uses only unique temporary roots. It never opens a real ProgramData lock.
    $dualGuardArguments = New-Object 'object[]' 3
    $dualGuardArguments[0] = [string]$stateRoot
    $dualGuardArguments[1] = [string]$legacyStateRoot
    $dualGuardArguments[2] = [string]'test operation'
    $guard = [IDisposable]$acquireGuards.Invoke(
        $null,
        $dualGuardArguments)
    try {
        foreach ($lockedRoot in @($stateRoot, $legacyStateRoot)) {
            $secondGuardArguments = New-Object 'object[]' 2
            $secondGuardArguments[0] = [string]$lockedRoot
            $secondGuardArguments[1] = [string]'second test operation'
            Assert-InvocationFails `
                -Method $acquireGuard `
                -Arguments $secondGuardArguments `
                -Scenario "A second operation while $lockedRoot transaction.lock is held"
        }
    }
    finally {
        $guard.Dispose()
    }

    $releasedGuardArguments = New-Object 'object[]' 2
    $releasedGuardArguments[0] = [string]$stateRoot
    $releasedGuardArguments[1] = [string]'operation after release'
    $guardAfterRelease = [IDisposable]$acquireGuard.Invoke(
        $null,
        $releasedGuardArguments)
    $guardAfterRelease.Dispose()
    $legacyReleasedArguments = New-Object 'object[]' 2
    $legacyReleasedArguments[0] = [string]$legacyStateRoot
    $legacyReleasedArguments[1] = [string]'legacy operation after release'
    $legacyGuardAfterRelease = [IDisposable]$acquireGuard.Invoke(
        $null,
        $legacyReleasedArguments)
    $legacyGuardAfterRelease.Dispose()

    $backupRoot = Join-Path $stateRoot 'Backups'
    $backupDirectory = Join-Path $backupRoot 'test-transaction'
    [IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
    $statePath = Join-Path $backupDirectory 'state.json'
    $pointerPath = Join-Path $stateRoot 'latest-state.txt'

    Write-Utf8File -Path $statePath -Content '{"Version":2,"Status":"Active"}'
    Write-Utf8File -Path $pointerPath -Content $statePath
    $stateArguments = New-Object 'object[]' 1
    $stateArguments[0] = [string]$stateRoot
    Assert-InvocationFails `
        -Method $validateUninstall `
        -Arguments $stateArguments `
        -Scenario 'Uninstall with an Active optimization transaction'

    $rootArguments = New-Object 'object[]' 2
    $rootArguments[0] = [string]$stateRoot
    $rootArguments[1] = [string](Join-Path $temporaryRoot 'MissingLegacyState')
    Assert-InvocationFails `
        -Method $validateUninstallRoots `
        -Arguments $rootArguments `
        -Scenario 'Uninstall with an Active BoostixOptimization transaction'

    Write-Utf8File -Path $statePath -Content '{"Version":2,"Status":"Restored"}'
    [void]$validateUninstall.Invoke($null, $stateArguments)
    [void]$validateUninstallRoots.Invoke($null, $rootArguments)

    Write-Utf8File -Path $statePath -Content '{"Version":2,"Status":"UnknownState"}'
    Assert-InvocationFails `
        -Method $validateUninstall `
        -Arguments $stateArguments `
        -Scenario 'Uninstall with an ambiguous transaction status'

    $nestedDirectory = Join-Path $backupDirectory 'nested'
    [IO.Directory]::CreateDirectory($nestedDirectory) | Out-Null
    $nestedState = Join-Path $nestedDirectory 'state.json'
    Write-Utf8File -Path $nestedState -Content '{"Version":2,"Status":"Restored"}'
    Write-Utf8File -Path $pointerPath -Content $nestedState
    Assert-InvocationFails `
        -Method $validateUninstall `
        -Arguments $stateArguments `
        -Scenario 'Uninstall with state.json below a nested backup directory'

    Write-Utf8File -Path $pointerPath -Content ('x' * 4097)
    Assert-InvocationFails `
        -Method $validateUninstall `
        -Arguments $stateArguments `
        -Scenario 'Uninstall with an oversized latest-state pointer'

    $installStart = $source.IndexOf(
        'public static void Install(bool createDesktopShortcut',
        [StringComparison]::Ordinal)
    $installGuard = $source.IndexOf(
        'AcquireSystemTransactionGuard(',
        $installStart,
        [StringComparison]::Ordinal)
    $installCore = $source.IndexOf(
        'InstallWithSystemTransactionGuard(createDesktopShortcut, progress);',
        $installGuard,
        [StringComparison]::Ordinal)
    if ($installStart -lt 0 -or $installGuard -le $installStart -or
        $installCore -le $installGuard) {
        throw 'Install does not acquire and retain the system transaction guard before mutation.'
    }

    $uninstallStart = $source.IndexOf(
        'public static void Uninstall(bool quiet)',
        [StringComparison]::Ordinal)
    $uninstallGuard = $source.IndexOf(
        'AcquireSystemTransactionGuard(',
        $uninstallStart,
        [StringComparison]::Ordinal)
    $uninstallStateCheck = $source.IndexOf(
        'EnsureUninstallStateAllowsRemoval();',
        $uninstallGuard,
        [StringComparison]::Ordinal)
    $uninstallStop = $source.IndexOf(
        'StopInstalledApplication();',
        $uninstallStateCheck,
        [StringComparison]::Ordinal)
    if ($uninstallStart -lt 0 -or $uninstallGuard -le $uninstallStart -or
        $uninstallStateCheck -le $uninstallGuard -or
        $uninstallStop -le $uninstallStateCheck) {
        throw 'Uninstall must acquire the lock and validate recovery state before stopping the app.'
    }

    foreach ($requiredContract in @(
        'FileShare.None',
        'FileAttributes.ReparsePoint',
        'OptimizationStatePointerMaximumBytes',
        'OptimizationStateMaximumBytes',
        'IsDirectChildPath',
        'CreateUnsafeUninstallStateException',
        '"BoostixOptimization"',
        'AcquireSystemTransactionGuardsAtRoots',
        'EnsureUninstallStateAllowsRemovalAtRoots',
        'if (quiet)',
        'throw;'
    )) {
        if (-not $source.Contains($requiredContract)) {
            throw "Installer lifecycle safety contract is missing: $requiredContract"
        }
    }

    $formStart = $source.IndexOf(
        'internal sealed class InstallerForm',
        [StringComparison]::Ordinal)
    $formSection = $source.Substring($formStart)
    foreach ($asyncContract in @(
        'installOperationRunning',
        'System.Threading.ThreadPool.QueueUserWorkItem',
        'ReportInstallProgressFromWorker',
        'PostToUi',
        'BeginInvoke(action)',
        'protected override void OnFormClosing'
    )) {
        if (-not $formSection.Contains($asyncContract)) {
            throw "Installer UI async contract is missing: $asyncContract"
        }
    }
    if ($formSection.Contains('InstallerEngine.Install(desktopShortcut.Checked);') -or
        $formSection.Contains('Application.DoEvents();')) {
        throw 'InstallerForm still runs installation synchronously or pumps nested UI messages.'
    }

    Write-Host 'Installer lifecycle safety regression test passed.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
        $systemTemporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
        $expectedPrefix = $systemTemporaryRoot + '\MajesticBoost-InstallerLifecycle-'
        if ($resolvedTemporaryRoot.StartsWith(
                $expectedPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
        }
    }
}
