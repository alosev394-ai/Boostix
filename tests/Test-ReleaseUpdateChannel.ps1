[CmdletBinding()]
param(
    [string]$ApplicationPath,
    [string]$InstallerPath,
    [string]$LegacyInstallerPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$releaseSnapshotDirectory = $env:BOOSTIX_RELEASE_SNAPSHOT_DIRECTORY
if (-not [string]::IsNullOrWhiteSpace($releaseSnapshotDirectory)) {
    $releaseSnapshotDirectory = [IO.Path]::GetFullPath(
        $releaseSnapshotDirectory)
    if (-not (Test-Path -LiteralPath $releaseSnapshotDirectory -PathType Container)) {
        throw "The signed release snapshot directory is missing: $releaseSnapshotDirectory"
    }
}
if (-not $ApplicationPath) {
    $ApplicationPath = if ($releaseSnapshotDirectory) {
        Join-Path $releaseSnapshotDirectory 'Boostix.exe'
    }
    else {
        Join-Path $projectRoot 'dist\Boostix.exe'
    }
}
if (-not $InstallerPath) {
    $InstallerPath = if ($releaseSnapshotDirectory) {
        Join-Path $releaseSnapshotDirectory 'Boostix-Setup-1.9.0.exe'
    }
    else {
        Join-Path $projectRoot 'dist\Boostix-Setup-1.9.0.exe'
    }
}
if (-not $LegacyInstallerPath) {
    $LegacyInstallerPath = if ($releaseSnapshotDirectory) {
        Join-Path $releaseSnapshotDirectory 'MajesticBoost-Setup-1.9.0.exe'
    }
    else {
        Join-Path $projectRoot 'dist\MajesticBoost-Setup-1.9.0.exe'
    }
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
$LegacyInstallerPath = (Resolve-Path -LiteralPath $LegacyInstallerPath).Path

$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($ApplicationPath))
if ($assembly.GetName().Version.ToString() -cne '1.9.0.0') {
    throw 'Compiled application version is not 1.9.0.0.'
}
$company = @(
    $assembly.GetCustomAttributes(
        [Reflection.AssemblyCompanyAttribute],
        $false)
) | Select-Object -First 1
if (-not $company -or $company.Company -cne 'Silas Suspect') {
    throw 'Compiled application author metadata is not Silas Suspect.'
}

$overlayType = $assembly.GetType('Boostix.UpdateFlowOverlay', $true, $false)
$flags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static
$manifestUrl = [string]$overlayType.GetField('ManifestUrl', $flags).GetRawConstantValue()
$signatureUrl = [string]$overlayType.GetField('ManifestSignatureUrl', $flags).GetRawConstantValue()
if (-not $manifestUrl.EndsWith('/update-v2.json', [StringComparison]::Ordinal) -or
    -not $signatureUrl.EndsWith('/update-v2.json.sig', [StringComparison]::Ordinal)) {
    throw 'Compiled 1.9.0 application is not pinned to the v2 update channel.'
}

$decodeMethod = $overlayType.GetMethod('DecodeManifestSignature', $flags)
$verifyMethod = $overlayType.GetMethod('VerifyManifestSignature', $flags)
if (-not $decodeMethod -or -not $verifyMethod) {
    throw 'Compiled manifest verification methods were not found.'
}

function Test-SignedManifest {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$SignaturePath
    )

    $manifestBytes = [IO.File]::ReadAllBytes($ManifestPath)
    $signaturePayload = [IO.File]::ReadAllBytes($SignaturePath)
    try {
        $decodeArguments = New-Object 'object[]' 1
        $decodeArguments[0] = $signaturePayload
        $signatureBytes = [byte[]]$decodeMethod.Invoke($null, $decodeArguments)
        $verifyArguments = New-Object 'object[]' 2
        $verifyArguments[0] = $manifestBytes
        $verifyArguments[1] = $signatureBytes
        [void]$verifyMethod.Invoke($null, $verifyArguments)
    }
    catch [Reflection.TargetInvocationException] {
        throw $_.Exception.InnerException
    }
}

$legacyManifestPath = Join-Path $projectRoot 'update.json'
$legacySignaturePath = Join-Path $projectRoot 'update.json.sig'
$v2ManifestPath = Join-Path $projectRoot 'update-v2.json'
$v2SignaturePath = Join-Path $projectRoot 'update-v2.json.sig'
Test-SignedManifest -ManifestPath $legacyManifestPath -SignaturePath $legacySignaturePath
Test-SignedManifest -ManifestPath $v2ManifestPath -SignaturePath $v2SignaturePath

$legacy = Get-Content -Raw -Encoding UTF8 $legacyManifestPath | ConvertFrom-Json
if ([string]$legacy.version -cne '0.0.0' -or [long]$legacy.size -ne 1L -or
    [string]$legacy.sha256 -cne ('0' * 64)) {
    throw 'Legacy update channel is not safely frozen below every released application version.'
}

$v2 = Get-Content -Raw -Encoding UTF8 $v2ManifestPath | ConvertFrom-Json
$installer = Get-Item -LiteralPath $InstallerPath
$installerHash = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash
$legacyInstaller = Get-Item -LiteralPath $LegacyInstallerPath
$legacyInstallerHash = (
    Get-FileHash -LiteralPath $LegacyInstallerPath -Algorithm SHA256).Hash
$installerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($InstallerPath)
$legacyInstallerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo(
    $LegacyInstallerPath)
if ($installerVersion.ProductName -cne 'Boostix' -or
    $installerVersion.FileVersion -cne '1.9.0.0' -or
    $installerVersion.CompanyName -cne 'Silas Suspect' -or
    $legacyInstallerVersion.ProductName -cne 'Majestic Boost' -or
    $legacyInstallerVersion.FileVersion -cne '1.9.0.0' -or
    $legacyInstallerVersion.CompanyName -cne 'Silas Suspect') {
    throw 'The compatibility bridge version-resource contract is invalid.'
}

$legacyAssembly = [Reflection.Assembly]::Load(
    [IO.File]::ReadAllBytes($LegacyInstallerPath))
$primaryPayload = $null
$primaryInstallerAssembly = [Reflection.Assembly]::Load(
    [IO.File]::ReadAllBytes($InstallerPath))
$primaryPayload = $primaryInstallerAssembly.GetManifestResourceStream(
    'Boostix.Payload.exe')
$legacyPayload = $legacyAssembly.GetManifestResourceStream(
    'Boostix.Payload.exe')
if (-not $primaryPayload -or -not $legacyPayload) {
    throw 'A setup variant is missing the shared Boostix payload.'
}
try {
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $primaryPayloadHash = [BitConverter]::ToString(
            $sha256.ComputeHash($primaryPayload)).Replace('-', '')
        $sha256.Initialize()
        $legacyPayloadHash = [BitConverter]::ToString(
            $sha256.ComputeHash($legacyPayload)).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}
finally {
    $primaryPayload.Dispose()
    $legacyPayload.Dispose()
}
if ($primaryPayloadHash -cne $legacyPayloadHash) {
    throw 'The compatibility bridge does not embed the verified Boostix payload.'
}
if ([string]$v2.version -cne '1.9.0' -or
    [long]$v2.size -ne [long]$legacyInstaller.Length -or
    [string]$v2.sha256 -cne $legacyInstallerHash -or
    [string]$v2.installerUrl -cne 'https://raw.githubusercontent.com/alosev394-ai/MajesticBoost/main/dist/MajesticBoost-Setup-1.9.0.exe' -or
    [string]$v2.boostixInstallerUrl -cne 'https://raw.githubusercontent.com/alosev394-ai/Boostix/main/dist/Boostix-Setup-1.9.0.exe' -or
    [string]$v2.boostixSha256 -cne $installerHash -or
    [long]$v2.boostixSize -ne [long]$installer.Length) {
    throw 'V2 manifest does not exactly describe the release installer.'
}

Write-Host 'Release update channel regression test passed.' -ForegroundColor Green
