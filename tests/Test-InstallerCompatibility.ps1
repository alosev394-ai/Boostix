[CmdletBinding()]
param(
    [string]$InstallerPath,
    [string]$LatestInstallerPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$brandSource = [IO.File]::ReadAllText(
    (Join-Path $projectRoot 'ProductBrand.cs'))
$productVersionMatch = [regex]::Match(
    $brandSource,
    'ProductVersion\s*=\s*"(?<version>[0-9]+\.[0-9]+\.[0-9]+)"')
$assemblyVersionMatch = [regex]::Match(
    $brandSource,
    'AssemblyVersion\s*=\s*"(?<version>[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)"')
if (-not $productVersionMatch.Success -or
    -not $assemblyVersionMatch.Success) {
    throw 'The current product versions were not found in ProductBrand.cs.'
}
$releaseVersion = $productVersionMatch.Groups['version'].Value
$assemblyVersion = $assemblyVersionMatch.Groups['version'].Value
if ($assemblyVersion -cne ($releaseVersion + '.0')) {
    throw 'ProductVersion and AssemblyVersion are inconsistent in ProductBrand.cs.'
}
$parsedCurrentVersion = [Version]$assemblyVersion
$futurePatchVersion = '{0}.{1}.{2}.0' -f
    $parsedCurrentVersion.Major,
    $parsedCurrentVersion.Minor,
    ($parsedCurrentVersion.Build + 1)
$futureMajorVersion = '{0}.0.0.0' -f ($parsedCurrentVersion.Major + 1)
if (-not $InstallerPath) {
    $InstallerPath = Join-Path $projectRoot (
        'dist\Boostix-Setup-' + $releaseVersion + '.exe')
}
if (-not $LatestInstallerPath) {
    $LatestInstallerPath = Join-Path $projectRoot 'dist\Boostix-Setup-Latest.exe'
}
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
$LatestInstallerPath = (Resolve-Path -LiteralPath $LatestInstallerPath).Path

$installerHash = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash
$latestHash = (Get-FileHash -LiteralPath $LatestInstallerPath -Algorithm SHA256).Hash
if ($installerHash -cne $latestHash) {
    throw 'The stable Latest setup is not byte-identical to the versioned setup.'
}

$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($InstallerPath)
if ($versionInfo.ProductName -cne 'Boostix' -or
    $versionInfo.FileVersion -cne $assemblyVersion -or
    $versionInfo.CompanyName -cne 'Silas Suspect') {
    throw "Installer product metadata does not match release $releaseVersion."
}

$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($InstallerPath))
$engineType = $assembly.GetType('BoostixSetup.InstallerEngine', $true, $false)
$programType = $assembly.GetType('BoostixSetup.Program', $true, $false)
$flags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static
$publicStaticFlags = [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::Static
$downgradeMethod = $engineType.GetMethod('IsDowngrade', $flags)
if (-not $downgradeMethod) {
    throw 'Compiled installer downgrade guard was not found.'
}
$mutexName = [string]$programType.GetField('SetupMutexName', $flags).GetRawConstantValue()
if ($mutexName -cne 'Global\SilasSuspect.Boostix.Setup') {
    throw 'Installer does not use the expected global install/uninstall mutex.'
}
$installDirectory = [string]$engineType.GetField(
    'InstallDirectory',
    $publicStaticFlags).GetValue($null)
$installedExe = [string]$engineType.GetField(
    'InstalledExe',
    $publicStaticFlags).GetValue($null)
if ((Split-Path -Leaf $installDirectory) -cne 'Boostix' -or
    (Split-Path -Leaf $installedExe) -cne 'Boostix.exe' -or
    (Split-Path -Parent $installedExe) -cne $installDirectory) {
    throw 'Installer public product paths are not fully branded as Boostix.'
}

function Test-DowngradeDecision {
    param([string]$Installed, [string]$Setup, [bool]$Expected)

    $arguments = New-Object 'object[]' 2
    $arguments[0] = $Installed
    $arguments[1] = $Setup
    $actual = [bool]$downgradeMethod.Invoke($null, $arguments)
    if ($actual -ne $Expected) {
        throw "Downgrade decision for installed=$Installed setup=$Setup was $actual; expected $Expected."
    }
}

Test-DowngradeDecision -Installed '1.5.1.0' -Setup $assemblyVersion -Expected $false
Test-DowngradeDecision -Installed '1.8.1.0' -Setup $assemblyVersion -Expected $false
Test-DowngradeDecision -Installed '1.8.9.0' -Setup $assemblyVersion -Expected $false
Test-DowngradeDecision -Installed '1.9.0.0' -Setup $assemblyVersion -Expected $false
Test-DowngradeDecision -Installed $assemblyVersion -Setup $assemblyVersion -Expected $false
Test-DowngradeDecision -Installed $futurePatchVersion -Setup $assemblyVersion -Expected $true
Test-DowngradeDecision -Installed $futureMajorVersion -Setup $assemblyVersion -Expected $true
Test-DowngradeDecision -Installed 'invalid' -Setup $assemblyVersion -Expected $false

$payloadStream = $assembly.GetManifestResourceStream('Boostix.Payload.exe')
if (-not $payloadStream) {
    throw 'Embedded Boostix payload is missing.'
}
try {
    $memory = New-Object IO.MemoryStream
    try {
        $payloadStream.CopyTo($memory)
        $payloadAssembly = [Reflection.Assembly]::Load($memory.ToArray())
        if ($payloadAssembly.GetName().Version.ToString() -cne $assemblyVersion) {
            throw "Embedded application version does not match installer version $releaseVersion."
        }
        $payloadCompany = @(
            $payloadAssembly.GetCustomAttributes(
                [Reflection.AssemblyCompanyAttribute],
                $false)
        ) | Select-Object -First 1
        if (-not $payloadCompany -or $payloadCompany.Company -cne 'Silas Suspect') {
            throw 'Embedded application author metadata is not Silas Suspect.'
        }
    }
    finally {
        $memory.Dispose()
    }
}
finally {
    $payloadStream.Dispose()
}

$sessionStream = $assembly.GetManifestResourceStream('Boostix.BoostSession.ps1')
if (-not $sessionStream -or $sessionStream.Length -eq 0) {
    throw 'Embedded Boostix session payload is missing.'
}
$sessionStream.Dispose()
if ($assembly.GetManifestResourceStream('MajesticBoost.Payload.exe')) {
    throw 'The installer still publishes a legacy-branded primary payload resource.'
}

$presentMonStream = $assembly.GetManifestResourceStream('Boostix.PresentMon.exe')
if (-not $presentMonStream) {
    throw 'Embedded PresentMon payload is missing.'
}
try {
    if ($presentMonStream.Length -ne 956768) {
        throw 'Embedded PresentMon payload has the wrong length.'
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = ([BitConverter]::ToString($sha.ComputeHash($presentMonStream))).Replace('-', '').ToLowerInvariant()
        if ($hash -cne '9bec3083069f58f911e6a512f4806db51a27bd096103087bc1d05ef54c80a191') {
            throw 'Embedded PresentMon payload has the wrong SHA-256.'
        }
    }
    finally {
        $sha.Dispose()
    }
}
finally {
    $presentMonStream.Dispose()
}
foreach ($noticeName in @(
    'Boostix.PresentMon.License.txt',
    'Boostix.PresentMon.ThirdParty.txt'
)) {
    $notice = $assembly.GetManifestResourceStream($noticeName)
    if (-not $notice -or $notice.Length -eq 0) {
        throw "Embedded PresentMon notice is missing: $noticeName"
    }
    $notice.Dispose()
}

$source = [IO.File]::ReadAllText((Join-Path $projectRoot 'BoostixInstaller\Program.cs'))
$validationLoop = $source.IndexOf('ValidateStagedPayload(item.StagePath, item.Executable);', [StringComparison]::Ordinal)
$stopInstalledApp = $source.IndexOf('StopInstalledApplication();', $validationLoop, [StringComparison]::Ordinal)
$commitLoop = $source.IndexOf('CommitStagedFile(', $stopInstalledApp, [StringComparison]::Ordinal)
if ($validationLoop -lt 0 -or $stopInstalledApp -le $validationLoop -or
    $commitLoop -le $stopInstalledApp) {
    throw 'Installer must validate every payload before stopping the old app and committing files.'
}
$registrationCapture = $source.IndexOf(
    'CapturePostInstallRegistration();',
    [StringComparison]::Ordinal)
$registrationCommit = $source.IndexOf(
    'registerInstallation();',
    $commitLoop,
    [StringComparison]::Ordinal)
$transactionSuccess = $source.IndexOf(
    'installationSucceeded = true;',
    $registrationCommit,
    [StringComparison]::Ordinal)
$registrationCompensation = $source.IndexOf(
    'RestorePostInstallRegistration(registrationSnapshot);',
    $registrationCapture,
    [StringComparison]::Ordinal)
if ($registrationCapture -lt 0 -or $registrationCompensation -le $registrationCapture -or
    $registrationCommit -le $commitLoop -or $transactionSuccess -le $registrationCommit) {
    throw 'Shortcuts and registry registration must participate in the payload transaction with compensation.'
}
if ($source.Contains('File.Copy(Application.ExecutablePath, UninstallerExe, true)')) {
    throw 'Uninstall.exe is still published outside the payload transaction.'
}
foreach ($requiredText in @(
    'AssemblyCompany(ProductBrand.CompanyName)',
    'uninstall.SetValue("Publisher", "Silas Suspect", RegistryValueKind.String)',
    'ValidatePresentMonPayload(item.StagePath)',
    'TryDeleteIfExists(item.StagePath)',
    'Boostix.PresentMon.exe',
    'items.Count - 1; index >= 0; index--',
    'GetDesktopShortcutPreference()',
    'ScheduleUpdateSourceCleanupIfNeeded()',
    'CanonicalSetupResourceName =',
    '"Boostix.CanonicalSetup.exe"',
    'CreatePayloadItem(token, "Uninstall", CanonicalSetupResourceName, UninstallerExe',
    'CleanupLegacyInstallationAfterSuccess()',
    'CaptureRegistryTree(child, childName)',
    'RestoreRegistryKey(baseKey, snapshot.AppPathsKey)',
    'RestoreRegistryKey(baseKey, snapshot.UninstallKey)',
    'TryRestoreOptionalShortcut(snapshot.DesktopShortcut)',
    'TryRestoreOptionalShortcut(snapshot.StartMenuShortcut)',
    '^(?:Boostix|MajesticBoost)\.Update\.[0-9a-f]{32}$'
)) {
    if (-not $source.Contains($requiredText)) {
        throw "Installer resilience policy is missing: $requiredText"
    }
}

Write-Host 'Installer compatibility regression test passed.' -ForegroundColor Green
