[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$PrepareSignedUpdate
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

if (-not (Test-Path -LiteralPath $compiler)) {
    throw ".NET Framework C# compiler not found: $compiler"
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

# A Boostix release must not leave a stale legacy "Latest" binary beside the
# newly produced artifacts. Match only known product executable names directly
# under dist; release history remains recoverable from Git.
$resolvedDistDirectory = [IO.Path]::GetFullPath($distDirectory)
foreach ($legacyArtifact in Get-ChildItem -LiteralPath $resolvedDistDirectory -File) {
    if ((Split-Path -Parent $legacyArtifact.FullName) -ceq $resolvedDistDirectory -and
        $legacyArtifact.Name -cmatch '^MajesticBoost(?:-Setup-(?:Latest|[0-9]+\.[0-9]+\.[0-9]+))?\.exe$') {
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
    "/out:$appOutput",
    "$projectRoot\ProductBrand.cs",
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

$legacySetupArguments = @(
    '/define:LEGACY_UPDATE_BRIDGE',
    "/out:$legacySetupOutput"
) + @(
    $setupArguments | Where-Object {
        -not $_.StartsWith('/out:', [StringComparison]::OrdinalIgnoreCase)
    })
& $compiler @legacySetupArguments
if ($LASTEXITCODE -ne 0) {
    throw "Boostix compatibility bridge compilation failed with exit code $LASTEXITCODE."
}

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
    $manifestPath = Join-Path $projectRoot 'update-v2.json'
    $signaturePath = Join-Path $projectRoot 'update-v2.json.sig'
    $manifest = [string]::Join(
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
        $manifestPath,
        $manifest,
        (New-Object Text.UTF8Encoding($false)))

    $signScript = Join-Path $projectRoot 'tools\Sign-UpdateManifest.ps1'
    & powershell.exe `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $signScript `
        -ManifestPath $manifestPath `
        -SignaturePath $signaturePath
    if ($LASTEXITCODE -ne 0) {
        throw "Signed update preparation failed with exit code $LASTEXITCODE."
    }
}

$hashes | Format-Table -AutoSize
