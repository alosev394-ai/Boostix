[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or
    $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$installerSource = Join-Path $projectRoot 'BoostixInstaller\Program.cs'
$brandSource = Join-Path $projectRoot 'ProductBrand.cs'
$source = [IO.File]::ReadAllText($installerSource)
$normalizedSource = [regex]::Replace($source, '\s+', ' ')
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'Boostix-LegacyMigration-' + [Guid]::NewGuid().ToString('N'))
$harnessPath = Join-Path $temporaryRoot 'LegacyMigrationHarness.dll'
$utf8 = New-Object Text.UTF8Encoding($false)

function Get-DeepestException {
    param([Parameter(Mandatory = $true)][Exception]$Exception)

    $current = $Exception
    while ($current.InnerException) {
        $current = $current.InnerException
    }
    return $current
}

function Read-RollbackState {
    param(
        [Parameter(Mandatory = $true)][Reflection.MethodInfo]$Method,
        [Parameter(Mandatory = $true)][string]$Directory
    )

    $arguments = New-Object 'object[]' 1
    $arguments[0] = $Directory
    try {
        return $Method.Invoke($null, $arguments)
    }
    catch {
        throw (Get-DeepestException -Exception $_.Exception)
    }
}

function Write-RollbackState {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$Format,
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$PreviousInstallation,
        [Parameter(Mandatory = $true)][string]$ExpectedSid
    )

    [IO.Directory]::CreateDirectory($Directory) | Out-Null
    $transaction = Split-Path -Leaf $Directory
    $lines = @(
        "Format=$Format"
        "Transaction=$transaction"
        'Status=Prepared',
        'PreviousVersion=1.8.1.0',
        'ExpectedVersion=1.9.4.0',
        "ExpectedSid=$ExpectedSid"
    )
    if ($Format -ceq '2') {
        $lines += "PreviousInstallation=$PreviousInstallation"
    }
    [IO.File]::WriteAllText(
        (Join-Path $Directory 'state.dat'),
        ([string]::Join("`n", $lines) + "`n"),
        $utf8)
}

function Assert-SourceContains {
    param(
        [Parameter(Mandatory = $true)][string]$Fragment,
        [Parameter(Mandatory = $true)][string]$Scenario
    )

    if (-not $normalizedSource.Contains($Fragment)) {
        throw "Legacy migration safety contract is missing: $Scenario"
    }
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
        $installerSource 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Installer source did not compile:`r`n$($compilerOutput -join [Environment]::NewLine)"
    }

    $assembly = [Reflection.Assembly]::Load(
        [IO.File]::ReadAllBytes($harnessPath))
    $engineType = $assembly.GetType(
        'BoostixSetup.InstallerEngine',
        $true,
        $false)
    $flags = [Reflection.BindingFlags]::NonPublic -bor
        [Reflection.BindingFlags]::Static
    $readUpdateState = $engineType.GetMethod('ReadUpdateState', $flags)
    if (-not $readUpdateState) {
        throw 'The durable update-state reader was not found.'
    }

    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $format1Directory = Join-Path $temporaryRoot (
        [Guid]::NewGuid().ToString('N'))
    Write-RollbackState `
        -Directory $format1Directory `
        -Format '1' `
        -PreviousInstallation '' `
        -ExpectedSid $sid
    try {
        $format1State = Read-RollbackState `
            -Method $readUpdateState `
            -Directory $format1Directory
    }
    catch {
        throw "Format-1 compatibility state was rejected: $($_.Exception.Message)"
    }
    $previousField = $format1State.GetType().GetField(
        'PreviousInstallation')
    if (-not $previousField -or
        $previousField.GetValue($format1State).ToString() -cne 'Boostix') {
        throw 'Format-1 rollback state no longer defaults safely to Boostix.'
    }

    $format2Directory = Join-Path $temporaryRoot (
        [Guid]::NewGuid().ToString('N'))
    Write-RollbackState `
        -Directory $format2Directory `
        -Format '2' `
        -PreviousInstallation 'Legacy' `
        -ExpectedSid $sid
    try {
        $format2State = Read-RollbackState `
            -Method $readUpdateState `
            -Directory $format2Directory
    }
    catch {
        throw "Format-2 legacy state was rejected: $($_.Exception.Message)"
    }
    if ($previousField.GetValue($format2State).ToString() -cne 'Legacy') {
        throw 'Format-2 rollback state did not preserve the legacy source kind.'
    }

    $invalidDirectory = Join-Path $temporaryRoot (
        [Guid]::NewGuid().ToString('N'))
    Write-RollbackState `
        -Directory $invalidDirectory `
        -Format '2' `
        -PreviousInstallation 'legacy' `
        -ExpectedSid $sid
    try {
        [void](Read-RollbackState `
            -Method $readUpdateState `
            -Directory $invalidDirectory)
        throw 'An invalid legacy source kind was accepted.'
    }
    catch [IO.InvalidDataException] {
    }

    Assert-SourceContains `
        -Fragment 'bool legacyMigration = !boostixInstalled && File.Exists(LegacyInstalledExe);' `
        -Scenario 'legacy-only installations must enter the health-checked update path'
    Assert-SourceContains `
        -Fragment 'InstallUpdateWithHealthRollback( createDesktopShortcut, progress, legacyMigration);' `
        -Scenario 'the migration kind must reach the durable transaction'
    Assert-SourceContains `
        -Fragment 'legacyMigration ? LegacyInstallDirectory : InstallDirectory;' `
        -Scenario 'the prior legacy directory must be snapshotted'
    Assert-SourceContains `
        -Fragment 'legacy ? "MajesticBoost.exe" : "Boostix.exe"' `
        -Scenario 'snapshot identity validation must select the prior executable'
    Assert-SourceContains `
        -Fragment 'RestoreFileSnapshot( transaction.RootDirectory, previousInstallDirectory);' `
        -Scenario 'rollback must restore the selected prior installation'
    Assert-SourceContains `
        -Fragment 'DeleteAllowlistedDirectoryTree( GetMachineProgramFilesDirectory(), InstallDirectory, UninstallProductDirectoryNames, null);' `
        -Scenario 'failed Boostix files must be removed through the allowlisted deletion path'
    Assert-SourceContains `
        -Fragment 'LaunchPreviousInstalledApplication(state);' `
        -Scenario 'watchdog rollback must relaunch the actual prior product'
    $transactionLaunches = [regex]::Matches(
        $normalizedSource,
        [regex]::Escape(
            'LaunchPreviousInstalledApplication(transaction.State);')).Count
    if ($transactionLaunches -lt 2) {
        throw 'A pre-health failure can restore the prior application without relaunching it.'
    }

    $healthIndex = $normalizedSource.IndexOf(
        'if (!LaunchAndWaitForUpdateHealth(transaction, token))',
        [StringComparison]::Ordinal)
    $commitIndex = $normalizedSource.IndexOf(
        'SetUpdateRollbackStatus(transaction, UpdateRollbackStatus.Committed);',
        [StringComparison]::Ordinal)
    $installCallIndex = $normalizedSource.IndexOf(
        'InstallUpdateWithHealthRollback( createDesktopShortcut, progress, legacyMigration);',
        [StringComparison]::Ordinal)
    $cleanupIndex = $normalizedSource.IndexOf(
        'CleanupLegacyInstallationAfterSuccess();',
        [StringComparison]::Ordinal)
    if ($healthIndex -lt 0 -or $commitIndex -le $healthIndex) {
        throw 'The new application can be committed before its health probe succeeds.'
    }
    if ($installCallIndex -lt 0 -or $cleanupIndex -le $installCallIndex) {
        throw 'Legacy cleanup is no longer outside and after the installation branch.'
    }

    Write-Output 'Legacy-only migration rollback safety tests passed.'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
