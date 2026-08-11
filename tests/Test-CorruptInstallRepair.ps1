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
$boostixExecutable = Join-Path $projectRoot 'dist\Boostix.exe'
$brandText = [IO.File]::ReadAllText($brandSource)
$versionMatch = [regex]::Match(
    $brandText,
    'ProductVersion\s*=\s*"(?<version>[0-9]+\.[0-9]+\.[0-9]+)"')
if (-not $versionMatch.Success) {
    throw 'Product version was not found in ProductBrand.cs.'
}
$legacyExecutable = Join-Path $projectRoot (
    'dist\MajesticBoost-Setup-' +
    $versionMatch.Groups['version'].Value +
    '.exe')
$source = [IO.File]::ReadAllText($installerSource)
$normalizedSource = [regex]::Replace($source, '\s+', ' ')
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'Boostix-CorruptRepair-' + [Guid]::NewGuid().ToString('N'))
$harnessPath = Join-Path $temporaryRoot 'CorruptRepairHarness.dll'
$foreignSource = Join-Path $temporaryRoot 'ForeignProduct.cs'
$foreignExecutable = Join-Path $temporaryRoot 'ForeignProduct.exe'
$corruptExecutable = Join-Path $temporaryRoot 'Boostix.exe'
$utf8 = New-Object Text.UTF8Encoding($false)

function Invoke-EligibilityCheck {
    param(
        [Parameter(Mandatory = $true)][Reflection.MethodInfo]$Method,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][bool]$Legacy
    )

    try {
        return [bool]$Method.Invoke(
            $null,
            [object[]]@($Path, $Legacy))
    }
    catch {
        $current = $_.Exception
        while ($current.InnerException) {
            $current = $current.InnerException
        }
        throw $current
    }
}

function Assert-SourceContains {
    param(
        [Parameter(Mandatory = $true)][string]$Fragment,
        [Parameter(Mandatory = $true)][string]$Scenario
    )

    if (-not $normalizedSource.Contains($Fragment)) {
        throw "Corrupt-install repair contract is missing: $Scenario"
    }
}

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
        throw "C# compiler was not found: $compiler"
    }
    foreach ($requiredExecutable in @($boostixExecutable, $legacyExecutable)) {
        if (-not (Test-Path -LiteralPath $requiredExecutable -PathType Leaf)) {
            throw "Required built executable was not found: $requiredExecutable"
        }
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
        throw "Installer repair harness did not compile:`r`n$($compilerOutput -join [Environment]::NewLine)"
    }

    $foreignCode = @'
using System.Reflection;
[assembly: AssemblyProduct("Foreign Product")]
[assembly: AssemblyFileVersion("99.0.0.0")]
internal static class ForeignProgram { private static void Main() { } }
'@
    [IO.File]::WriteAllText($foreignSource, $foreignCode, $utf8)
    $foreignCompilerOutput = & $compiler `
        /nologo `
        /target:winexe `
        /utf8output `
        "/out:$foreignExecutable" `
        $foreignSource 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Foreign-product fixture did not compile:`r`n$($foreignCompilerOutput -join [Environment]::NewLine)"
    }
    [IO.File]::WriteAllBytes(
        $corruptExecutable,
        [byte[]](0x4D, 0x5A, 0x00, 0x01, 0x02, 0x03))

    $assembly = [Reflection.Assembly]::Load(
        [IO.File]::ReadAllBytes($harnessPath))
    $engineType = $assembly.GetType(
        'BoostixSetup.InstallerEngine',
        $true,
        $false)
    $flags = [Reflection.BindingFlags]::NonPublic -bor
        [Reflection.BindingFlags]::Static
    $eligibility = $engineType.GetMethod(
        'IsInstalledExecutableRollbackEligible',
        $flags)
    if (-not $eligibility) {
        throw 'The corrupt-install eligibility check was not found.'
    }

    if (-not (Invoke-EligibilityCheck `
            -Method $eligibility `
            -Path $boostixExecutable `
            -Legacy $false)) {
        throw 'A valid Boostix executable was not eligible for durable rollback.'
    }
    if (-not (Invoke-EligibilityCheck `
            -Method $eligibility `
            -Path $legacyExecutable `
            -Legacy $true)) {
        throw 'A valid legacy executable was not eligible for migration rollback.'
    }
    if (Invoke-EligibilityCheck `
            -Method $eligibility `
            -Path $corruptExecutable `
            -Legacy $false) {
        throw 'A corrupt executable was accepted as a rollback source.'
    }
    if (Invoke-EligibilityCheck `
            -Method $eligibility `
            -Path $foreignExecutable `
            -Legacy $false) {
        throw 'A foreign executable was accepted as a rollback source.'
    }
    if (Invoke-EligibilityCheck `
            -Method $eligibility `
            -Path (Join-Path $temporaryRoot 'missing.exe') `
            -Legacy $false) {
        throw 'A missing executable was accepted as a rollback source.'
    }

    Assert-SourceContains `
        -Fragment 'if (rollbackEligible) { InstallUpdateWithHealthRollback(' `
        -Scenario 'only a validated previous executable may enter health rollback'
    Assert-SourceContains `
        -Fragment 'using the transactional repair path without executing it.' `
        -Scenario 'damaged installations must be diagnosed as repair'
    Assert-SourceContains `
        -Fragment 'InstallWithSystemTransactionGuard(createDesktopShortcut, progress);' `
        -Scenario 'damaged installations must use atomic payload replacement'
    Assert-SourceContains `
        -Fragment 'ValidateFileNotReparse(candidate); FileVersionInfo installedInfo' `
        -Scenario 'repair must still refuse a redirected installed executable'
    Assert-SourceContains `
        -Fragment 'downgrade comparison is skipped for transactional repair.' `
        -Scenario 'foreign or metadata-damaged files must not permanently block repair'

    Write-Output 'Corrupt-install transactional repair tests passed.'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
