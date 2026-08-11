[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$PrepareSignedUpdate,
    [string]$AuthenticodeCertificateThumbprint =
        $env:BOOSTIX_AUTHENTICODE_THUMBPRINT,
    [string]$AuthenticodeTimestampServer =
        'http://timestamp.digicert.com',
    [switch]$RequireAuthenticode
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpfRoot = Join-Path $frameworkRoot 'WPF'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$workDirectory = Join-Path $projectRoot 'work'
$distDirectory = Join-Path $projectRoot 'dist'
$brandSourcePath = Join-Path $projectRoot 'ProductBrand.cs'
$brandSource = [IO.File]::ReadAllText($brandSourcePath)
$versionMatch = [regex]::Match(
    $brandSource,
    'ProductVersion\s*=\s*"(?<version>[0-9]+\.[0-9]+\.[0-9]+)"')
if (-not $versionMatch.Success) {
    throw "Product version was not found in $brandSourcePath"
}
$releaseVersion = $versionMatch.Groups['version'].Value
$appOutput = Join-Path $workDirectory 'Boostix.exe'
$setupOutput = Join-Path $workDirectory "Boostix-Setup-$releaseVersion.exe"
$legacySetupOutput = Join-Path $workDirectory (
    "MajesticBoost-Setup-$releaseVersion.exe")
$versionedSetupOutput = Join-Path $distDirectory "Boostix-Setup-$releaseVersion.exe"
$latestSetupOutput = Join-Path $distDirectory 'Boostix-Setup-Latest.exe'
$legacyCompatibilitySetupOutput = Join-Path $distDirectory (
    "MajesticBoost-Setup-$releaseVersion.exe")
$presentMonPath = Join-Path $projectRoot 'third_party\PresentMon\PresentMon.exe'
$presentMonLicensePath = Join-Path $projectRoot 'third_party\PresentMon\LICENSE.txt'
$presentMonThirdPartyPath = Join-Path $projectRoot 'third_party\PresentMon\THIRD_PARTY.txt'

function Get-BoostixSignTool {
    $roots = @(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $candidates = foreach ($root in $roots) {
        $kitBin = Join-Path $root 'Windows Kits\10\bin'
        if (Test-Path -LiteralPath $kitBin -PathType Container) {
            Get-ChildItem -LiteralPath $kitBin -Recurse -Filter 'signtool.exe' -File `
                -ErrorAction SilentlyContinue |
                Where-Object { $_.Directory.Name -ieq 'x64' }
        }
    }
    $selected = @($candidates | Sort-Object FullName -Descending | Select-Object -First 1)
    if ($selected.Count -ne 1) {
        throw 'Windows SDK signtool.exe (x64) was not found.'
    }
    return $selected[0].FullName
}

$authenticodeEnabled = -not [string]::IsNullOrWhiteSpace(
    $AuthenticodeCertificateThumbprint)
$signTool = $null
if ($authenticodeEnabled) {
    $AuthenticodeCertificateThumbprint =
        ($AuthenticodeCertificateThumbprint -replace '\s', '').ToUpperInvariant()
    if ($AuthenticodeCertificateThumbprint -notmatch '^[0-9A-F]{40}$') {
        throw 'Authenticode certificate thumbprint must be exactly 40 hexadecimal characters.'
    }
    [Uri]$timestampUri = $null
    if (-not [Uri]::TryCreate(
            $AuthenticodeTimestampServer,
            [UriKind]::Absolute,
            [ref]$timestampUri) -or
        $timestampUri.Scheme -notin @('http', 'https') -or
        -not [string]::IsNullOrEmpty($timestampUri.UserInfo)) {
        throw 'Authenticode timestamp server must be an absolute HTTP(S) URL without user information.'
    }
    $signTool = Get-BoostixSignTool
}
elseif ($RequireAuthenticode) {
    throw 'A trusted Authenticode certificate is required for this build.'
}
else {
    Write-Warning (
        'Building unsigned EXE files. Supply -AuthenticodeCertificateThumbprint ' +
        'for a distributable release; unsigned binaries remain subject to SmartScreen/WDAC policy.')
}

function Invoke-BoostixAuthenticodeSigning {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not $authenticodeEnabled) {
        return
    }
    & $signTool sign `
        /sha1 $AuthenticodeCertificateThumbprint `
        /fd SHA256 `
        /td SHA256 `
        /tr $AuthenticodeTimestampServer `
        /v `
        $Path
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode signing failed for $Path with exit code $LASTEXITCODE."
    }
    & $signTool verify /pa /all /v $Path
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode verification failed for $Path with exit code $LASTEXITCODE."
    }
}
$systemPowerShell = Join-Path ([Environment]::SystemDirectory) `
    'WindowsPowerShell\v1.0\powershell.exe'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw ".NET Framework C# compiler not found: $compiler"
}
if (-not (Test-Path -LiteralPath $systemPowerShell -PathType Leaf)) {
    throw "System Windows PowerShell not found: $systemPowerShell"
}

$presentMon = Get-Item -LiteralPath $presentMonPath -ErrorAction Stop
$presentMonHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $presentMonPath).Hash.ToLowerInvariant()
if ($presentMon.Length -ne 956768 -or
    $presentMonHash -cne '9bec3083069f58f911e6a512f4806db51a27bd096103087bc1d05ef54c80a191') {
    throw 'Pinned PresentMon 2.5.1 binary failed its size or SHA-256 validation.'
}
foreach ($licensePath in @($presentMonLicensePath, $presentMonThirdPartyPath)) {
    if (-not (Test-Path -LiteralPath $licensePath) -or (Get-Item -LiteralPath $licensePath).Length -eq 0) {
        throw "PresentMon notice is missing or empty: $licensePath"
    }
}

[void](New-Item -ItemType Directory -Path $workDirectory -Force)
[void](New-Item -ItemType Directory -Path $distDirectory -Force)

# A Boostix release must not leave stale versioned binaries beside the newly
# produced artifacts. Match only known product executable names directly under
# dist; published release history remains recoverable from immutable GitHub
# releases and from Git itself.
$resolvedDistDirectory = [IO.Path]::GetFullPath($distDirectory)
foreach ($legacyArtifact in Get-ChildItem -LiteralPath $resolvedDistDirectory -File) {
    $knownLegacyArtifact =
        $legacyArtifact.Name -cmatch
            '^MajesticBoost(?:-Setup-(?:Latest|[0-9]+\.[0-9]+\.[0-9]+))?\.exe$'
    $staleCanonicalArtifact =
        $legacyArtifact.Name -cmatch
            '^Boostix-Setup-[0-9]+\.[0-9]+\.[0-9]+\.exe$' -and
        $legacyArtifact.Name -cne "Boostix-Setup-$releaseVersion.exe"
    if ((Split-Path -Parent $legacyArtifact.FullName) -ceq $resolvedDistDirectory -and
        ($knownLegacyArtifact -or $staleCanonicalArtifact)) {
        Remove-Item -LiteralPath $legacyArtifact.FullName -Force
    }
}

$appArguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    "/win32icon:$projectRoot\Boostix\Boostix.ico",
    "/win32manifest:$projectRoot\Boostix\app.manifest",
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Management.dll',
    "/reference:$frameworkRoot\System.Xaml.dll",
    "/reference:$wpfRoot\WindowsBase.dll",
    "/reference:$wpfRoot\PresentationCore.dll",
    "/reference:$wpfRoot\PresentationFramework.dll",
    "/reference:$wpfRoot\UIAutomationProvider.dll",
    "/reference:$wpfRoot\UIAutomationTypes.dll",
    "/out:$appOutput",
    "$projectRoot\ProductBrand.cs",
    "$projectRoot\Boostix\DesignTokens.cs",
    "$projectRoot\Boostix\BackgroundImpact.cs",
    "$projectRoot\Boostix\GameTargetProfiles.cs",
    "$projectRoot\Boostix\SessionGuard.cs",
    "$projectRoot\Boostix\SessionPowerPlan.cs",
    "$projectRoot\Boostix\PerformanceProof.cs",
    "$projectRoot\Boostix\PerformanceProofCoordinator.cs",
    "$projectRoot\Boostix\CrashCorrelation.cs",
    "$projectRoot\Boostix\Program.cs",
    "$projectRoot\Boostix\BoostFeatures.cs",
    "$projectRoot\Boostix\DiagnosticsFeatures.cs",
    "$projectRoot\Boostix\SessionInsights.cs",
    "$projectRoot\Boostix\BoostCenterOverlay.cs",
    "$projectRoot\Boostix\PerformanceCapture.cs",
    "$projectRoot\Boostix\OptimizationFlow.cs",
    "$projectRoot\Boostix\UpdateFlow.cs"
)

& $compiler @appArguments
if ($LASTEXITCODE -ne 0) {
    throw "Boostix compilation failed with exit code $LASTEXITCODE."
}
Invoke-BoostixAuthenticodeSigning -Path $appOutput

$setupArguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    "/win32icon:$projectRoot\Boostix\Boostix.ico",
    "/win32manifest:$projectRoot\BoostixInstaller\app.manifest",
    '/reference:System.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    "/resource:$appOutput,Boostix.Payload.exe",
    "/resource:$projectRoot\outputs\Boost-Session.ps1,Boostix.BoostSession.ps1",
    "/resource:$projectRoot\outputs\MaxFPS-Apply.ps1,Boostix.MaxFPSApply.ps1",
    "/resource:$projectRoot\outputs\MaxFPS-Restore.ps1,Boostix.MaxFPSRestore.ps1",
    "/resource:$presentMonPath,Boostix.PresentMon.exe",
    "/resource:$presentMonLicensePath,Boostix.PresentMon.License.txt",
    "/resource:$presentMonThirdPartyPath,Boostix.PresentMon.ThirdParty.txt",
    "/out:$setupOutput",
    "$projectRoot\ProductBrand.cs",
    "$projectRoot\BoostixInstaller\Program.cs"
)

& $compiler @setupArguments
if ($LASTEXITCODE -ne 0) {
    throw "Boostix installer compilation failed with exit code $LASTEXITCODE."
}
Invoke-BoostixAuthenticodeSigning -Path $setupOutput

$legacySetupArguments = @(
    '/define:LEGACY_UPDATE_BRIDGE',
    "/resource:$setupOutput,Boostix.CanonicalSetup.exe",
    "/out:$legacySetupOutput"
) + @(
    $setupArguments | Where-Object {
        -not $_.StartsWith('/out:', [StringComparison]::OrdinalIgnoreCase)
    })
& $compiler @legacySetupArguments
if ($LASTEXITCODE -ne 0) {
    throw "Boostix compatibility bridge compilation failed with exit code $LASTEXITCODE."
}
Invoke-BoostixAuthenticodeSigning -Path $legacySetupOutput

Copy-Item -LiteralPath $appOutput -Destination (Join-Path $distDirectory 'Boostix.exe') -Force
Copy-Item -LiteralPath $setupOutput -Destination $versionedSetupOutput -Force
Copy-Item -LiteralPath $setupOutput -Destination $latestSetupOutput -Force
# Transport-only bridge for signed-manifest schema v1 clients. Only its file
# version ProductName remains legacy for the strict 1.8.x validation contract;
# its payload, UI, install paths, and shortcuts are Boostix.
Copy-Item -LiteralPath $legacySetupOutput -Destination $legacyCompatibilitySetupOutput -Force

$releaseFiles = @(
    (Join-Path $distDirectory 'Boostix.exe'),
    $versionedSetupOutput,
    $latestSetupOutput,
    $legacyCompatibilitySetupOutput
)
$hashes = Get-FileHash -Algorithm SHA256 -LiteralPath $releaseFiles
$hashLines = foreach ($hash in $hashes) {
    $hash.Hash + ' *' + (Split-Path -Leaf $hash.Path)
}
[IO.File]::WriteAllText(
    (Join-Path $distDirectory 'SHA256SUMS.txt'),
    ([string]::Join("`n", $hashLines) + "`n"),
    (New-Object Text.UTF8Encoding($false)))

if ($PrepareSignedUpdate) {
    $installer = Get-Item -LiteralPath $versionedSetupOutput
    $installerHash = (
        Get-FileHash -Algorithm SHA256 -LiteralPath $versionedSetupOutput
    ).Hash
    $legacyInstaller = Get-Item -LiteralPath $legacyCompatibilitySetupOutput
    $legacyInstallerHash = (
        Get-FileHash -Algorithm SHA256 -LiteralPath $legacyCompatibilitySetupOutput
    ).Hash
    $legacyManifestPath = Join-Path $projectRoot 'update.json'
    $legacySignaturePath = Join-Path $projectRoot 'update.json.sig'
    $legacyManifest = [string]::Join(
        "`n",
        @(
            '{',
            '  "schemaVersion": 1,',
            ('  "version": "' + $releaseVersion + '",'),
            ('  "installerUrl": "https://raw.githubusercontent.com/alosev394-ai/MajesticBoost/main/dist/MajesticBoost-Setup-' + $releaseVersion + '.exe",'),
            ('  "sha256": "' + $legacyInstallerHash + '",'),
            ('  "size": ' + $legacyInstaller.Length),
            '}',
            ''
        ))
    [IO.File]::WriteAllText(
        $legacyManifestPath,
        $legacyManifest,
        (New-Object Text.UTF8Encoding($false)))

    $v2ManifestPath = Join-Path $projectRoot 'update-v2.json'
    $v2SignaturePath = Join-Path $projectRoot 'update-v2.json.sig'
    $v2Manifest = [string]::Join(
        "`n",
        @(
            '{',
            '  "schemaVersion": 1,',
            ('  "version": "' + $releaseVersion + '",'),
            ('  "installerUrl": "https://raw.githubusercontent.com/alosev394-ai/MajesticBoost/main/dist/MajesticBoost-Setup-' + $releaseVersion + '.exe",'),
            ('  "sha256": "' + $legacyInstallerHash + '",'),
            ('  "size": ' + $legacyInstaller.Length + ','),
            ('  "boostixInstallerUrl": "https://raw.githubusercontent.com/alosev394-ai/Boostix/main/dist/Boostix-Setup-' + $releaseVersion + '.exe",'),
            ('  "boostixSha256": "' + $installerHash + '",'),
            ('  "boostixSize": ' + $installer.Length),
            '}',
            ''
        ))
    [IO.File]::WriteAllText(
        $v2ManifestPath,
        $v2Manifest,
        (New-Object Text.UTF8Encoding($false)))

    $signScript = Join-Path $projectRoot 'tools\Sign-UpdateManifest.ps1'
    & $systemPowerShell `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $signScript `
        -ManifestPath $legacyManifestPath `
        -SignaturePath $legacySignaturePath `
        -AllowLegacyChannel
    if ($LASTEXITCODE -ne 0) {
        throw "Legacy signed update preparation failed with exit code $LASTEXITCODE."
    }
    & $systemPowerShell `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $signScript `
        -ManifestPath $v2ManifestPath `
        -SignaturePath $v2SignaturePath
    if ($LASTEXITCODE -ne 0) {
        throw "Signed update preparation failed with exit code $LASTEXITCODE."
    }
}

$hashes | Format-Table -AutoSize
