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
$brandSource = [IO.File]::ReadAllText((Join-Path $projectRoot 'ProductBrand.cs'))
$versionMatch = [regex]::Match(
    $brandSource,
    'ProductVersion\s*=\s*"(?<version>[0-9]+\.[0-9]+\.[0-9]+)"')
if (-not $versionMatch.Success) {
    throw 'The release version was not found in ProductBrand.cs.'
}
$releaseVersion = $versionMatch.Groups['version'].Value
$assemblyVersion = $releaseVersion + '.0'
$installerFileName = 'Boostix-Setup-' + $releaseVersion + '.exe'
$legacyInstallerFileName = 'MajesticBoost-Setup-' + $releaseVersion + '.exe'
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
        Join-Path $releaseSnapshotDirectory $installerFileName
    }
    else {
        Join-Path (Join-Path $projectRoot 'dist') $installerFileName
    }
}
if (-not $LegacyInstallerPath) {
    $LegacyInstallerPath = if ($releaseSnapshotDirectory) {
        Join-Path $releaseSnapshotDirectory $legacyInstallerFileName
    }
    else {
        Join-Path (Join-Path $projectRoot 'dist') $legacyInstallerFileName
    }
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
$LegacyInstallerPath = (Resolve-Path -LiteralPath $LegacyInstallerPath).Path

$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($ApplicationPath))
if ($assembly.GetName().Version.ToString() -cne $assemblyVersion) {
    throw "Compiled application version is not $assemblyVersion."
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
if (-not $manifestUrl.EndsWith('/update-v3.json', [StringComparison]::Ordinal) -or
    -not $signatureUrl.EndsWith('/update-v3.json.sig', [StringComparison]::Ordinal)) {
    throw "Compiled $releaseVersion application is not pinned to the v3 update channel."
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

$signedChannelRoot = if ($releaseSnapshotDirectory) {
    $releaseSnapshotDirectory
}
else {
    $projectRoot
}
$v3ManifestPath = Join-Path $signedChannelRoot 'update-v3.json'
$v3SignaturePath = Join-Path $signedChannelRoot 'update-v3.json.sig'
Test-SignedManifest -ManifestPath $v3ManifestPath -SignaturePath $v3SignaturePath
$v3 = Get-Content -Raw -Encoding UTF8 $v3ManifestPath | ConvertFrom-Json

# update.json (1.x) and update-v2.json (2.0.x) were signed with the retired v1
# key. They stay frozen so clients embedding that key keep a valid manifest;
# the compiled updater must no longer trust them.
$retiredPublicKeyXml = '<RSAKeyValue><Modulus>vCSgQnLtxkncktDMNkZo6cnqx3cBrLMm8z6R+jj/ljBCAm/yiC8fs1GTy7mzPBkH+LhEiEYJlx/HAVVfVXUI4hMEamtYUffbjkeCwrcpOTm9dBXDEiLOQ4ZV5Niisvws/TVqCHPwZj8ck4c/gISjUWotDGkuViPThl5suJImn4zXSo9pnJS5c2G5Pn62NMk2L3HaCmBPSeuFMbYah3XYgjQj7+K8LQ2HkXIwNl9pcJc/Pt8VarA7lVH5u9boct9YIe811iLAyKZ/h+xxN2stBKEE1Eb+HQnO6X6SrdmY+I0jjqsT1uy7yNwAE+ASlAu7iAw+L+nQB1ndi0F2/TWQ73J9Nw5E/GLtVkco9p0aCsiYvBX99Cu+02EMuICSRzfljKWfCD+TIlyX0HzDnLhFV+M3JVweSRLo1UWlyfOWdda3Re4mSUXk0YNyGegCnW/PFSjKgvm9ufYeEHTFoiLCGrsPknSsH5nrSFqCk/UCefupyJnNLfLB53SM8luedAJ9</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>'
$legacyManifestPath = Join-Path $signedChannelRoot 'update.json'
$legacySignaturePath = Join-Path $signedChannelRoot 'update.json.sig'
$v2ManifestPath = Join-Path $signedChannelRoot 'update-v2.json'
$v2SignaturePath = Join-Path $signedChannelRoot 'update-v2.json.sig'
$retiredRsa = New-Object Security.Cryptography.RSACryptoServiceProvider
try {
    $retiredRsa.PersistKeyInCsp = $false
    $retiredRsa.FromXmlString($retiredPublicKeyXml)
    $sha256Oid = [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256')
    foreach ($frozen in @(
        @{ Manifest = $legacyManifestPath; Signature = $legacySignaturePath },
        @{ Manifest = $v2ManifestPath; Signature = $v2SignaturePath }
    )) {
        $frozenBytes = [IO.File]::ReadAllBytes($frozen.Manifest)
        $frozenSignature = [Convert]::FromBase64String(
            [IO.File]::ReadAllText($frozen.Signature).Trim())
        if (-not $retiredRsa.VerifyData($frozenBytes, $sha256Oid, $frozenSignature)) {
            throw "The frozen channel was modified and no longer verifies: $($frozen.Manifest)"
        }
        $rejected = $false
        try {
            Test-SignedManifest -ManifestPath $frozen.Manifest -SignaturePath $frozen.Signature
        }
        catch {
            $rejected = $true
        }
        if (-not $rejected) {
            throw "The compiled updater still trusts the retired key: $($frozen.Manifest)"
        }
    }
}
finally {
    $retiredRsa.PersistKeyInCsp = $false
    $retiredRsa.Dispose()
}

$legacy = Get-Content -Raw -Encoding UTF8 $legacyManifestPath | ConvertFrom-Json
$v2 = Get-Content -Raw -Encoding UTF8 $v2ManifestPath | ConvertFrom-Json
$frozenVersion = [string]$v2.version
if ([string]$legacy.version -cne $frozenVersion -or
    [version]$frozenVersion -ge [version]$releaseVersion) {
    throw 'The frozen channels must describe one release older than the current one.'
}
$frozenInstallerName = 'Boostix-Setup-' + $frozenVersion + '.exe'
$frozenInstallerPath = if ($releaseSnapshotDirectory) {
    Join-Path $releaseSnapshotDirectory $frozenInstallerName
}
else {
    Join-Path (Join-Path $projectRoot 'dist') $frozenInstallerName
}
if (-not (Test-Path -LiteralPath $frozenInstallerPath -PathType Leaf)) {
    throw "The installer named by the frozen update-v2.json is not published: $frozenInstallerName"
}
$frozenInstaller = Get-Item -LiteralPath $frozenInstallerPath
if ([string]$v2.boostixInstallerUrl -cne (
        'https://raw.githubusercontent.com/alosev394-ai/Boostix/main/dist/' +
        $frozenInstallerName) -or
    [string]$v2.boostixSha256 -cne (Get-FileHash -LiteralPath $frozenInstallerPath -Algorithm SHA256).Hash -or
    [long]$v2.boostixSize -ne [long]$frozenInstaller.Length) {
    throw 'The frozen update-v2.json no longer matches its published installer.'
}
$installer = Get-Item -LiteralPath $InstallerPath
$installerHash = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash
$legacyInstaller = Get-Item -LiteralPath $LegacyInstallerPath
$legacyInstallerHash = (
    Get-FileHash -LiteralPath $LegacyInstallerPath -Algorithm SHA256).Hash
$installerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($InstallerPath)
$legacyInstallerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo(
    $LegacyInstallerPath)
if ($installerVersion.ProductName -cne 'Boostix' -or
    $installerVersion.FileVersion -cne $assemblyVersion -or
    $installerVersion.CompanyName -cne 'Silas Suspect' -or
    $legacyInstallerVersion.ProductName -cne 'Majestic Boost' -or
    $legacyInstallerVersion.FileVersion -cne $assemblyVersion -or
    $legacyInstallerVersion.CompanyName -cne 'Silas Suspect') {
    throw 'The compatibility bridge version-resource contract is invalid.'
}

$expectedLegacyFields = @('schemaVersion', 'version', 'installerUrl', 'sha256', 'size')
$actualLegacyFields = @($legacy.PSObject.Properties.Name)
if ($actualLegacyFields.Count -ne $expectedLegacyFields.Count -or
    @($expectedLegacyFields | Where-Object { $_ -cnotin $actualLegacyFields }).Count -ne 0) {
    throw 'Frozen update.json must contain exactly the signed schema-v1 fields.'
}
$expectedLegacyUrl =
    'https://raw.githubusercontent.com/alosev394-ai/MajesticBoost/main/dist/' +
    $legacyInstallerFileName

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

$canonicalSetup = $legacyAssembly.GetManifestResourceStream(
    'Boostix.CanonicalSetup.exe')
if (-not $canonicalSetup) {
    throw 'The compatibility bridge is missing its canonical Boostix setup resource.'
}
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'Boostix-Bridge-' + [Guid]::NewGuid().ToString('N'))
$canonicalSetupPath = Join-Path $temporaryRoot $installerFileName
try {
    [void][IO.Directory]::CreateDirectory($temporaryRoot)
    try {
        $output = [IO.File]::Open(
            $canonicalSetupPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        try {
            $canonicalSetup.CopyTo($output)
        }
        finally {
            $output.Dispose()
        }
    }
    finally {
        $canonicalSetup.Dispose()
    }

    $canonicalVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo(
        $canonicalSetupPath)
    $canonicalHash = (
        Get-FileHash -LiteralPath $canonicalSetupPath -Algorithm SHA256).Hash
    if ($canonicalVersion.ProductName -cne 'Boostix' -or
        $canonicalVersion.FileVersion -cne $assemblyVersion -or
        $canonicalVersion.CompanyName -cne 'Silas Suspect' -or
        $canonicalHash -cne $installerHash) {
        throw 'The embedded canonical uninstaller is not the verified primary Boostix setup.'
    }

    $installerEngine = $legacyAssembly.GetType(
        'BoostixSetup.InstallerEngine',
        $true,
        $false)
    $validatePayload = $installerEngine.GetMethod(
        'ValidateStagedPayload',
        [Reflection.BindingFlags]::NonPublic -bor
            [Reflection.BindingFlags]::Static)
    if (-not $validatePayload) {
        throw 'The staged executable validation method was not found.'
    }
    try {
        $validationArguments = New-Object 'object[]' 2
        $validationArguments[0] = [string]$canonicalSetupPath
        $validationArguments[1] = [bool]$true
        [void]$validatePayload.Invoke($null, $validationArguments)
    }
    catch [Reflection.TargetInvocationException] {
        throw $_.Exception.InnerException
    }
}
finally {
    if ($canonicalSetup) {
        $canonicalSetup.Dispose()
    }
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

$expectedBoostixUrl =
    'https://raw.githubusercontent.com/alosev394-ai/Boostix/main/dist/' +
    $installerFileName
if ([string]$v3.version -cne $releaseVersion -or
    [long]$v3.size -ne [long]$legacyInstaller.Length -or
    [string]$v3.sha256 -cne $legacyInstallerHash -or
    [string]$v3.installerUrl -cne $expectedLegacyUrl -or
    [string]$v3.boostixInstallerUrl -cne $expectedBoostixUrl -or
    [string]$v3.boostixSha256 -cne $installerHash -or
    [long]$v3.boostixSize -ne [long]$installer.Length) {
    throw 'The v3 manifest does not exactly describe the release installer.'
}

Write-Host 'Release update channel regression test passed.' -ForegroundColor Green
