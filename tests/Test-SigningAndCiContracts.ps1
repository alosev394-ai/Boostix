[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Security
if ($PSVersionTable.PSEdition -cne 'Desktop' -or
    $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$brandPath = Join-Path $projectRoot 'ProductBrand.cs'
$signingPath = Join-Path $projectRoot 'tools\Sign-UpdateManifest.ps1'
$workflowPath = Join-Path $projectRoot '.github\workflows\ci.yml'
$dependabotPath = Join-Path $projectRoot '.github\dependabot.yml'
$runAllPath = Join-Path $PSScriptRoot 'Run-All.ps1'
$releaseChannelTestPath = Join-Path $PSScriptRoot 'Test-ReleaseUpdateChannel.ps1'
foreach ($requiredPath in @(
    $brandPath,
    $signingPath,
    $workflowPath,
    $dependabotPath,
    $runAllPath,
    $releaseChannelTestPath
)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Release automation contract file is missing: $requiredPath"
    }
}

$brand = [IO.File]::ReadAllText($brandPath)
$productVersionMatches = [regex]::Matches(
    $brand,
    '(?m)^\s*public\s+const\s+string\s+ProductVersion\s*=\s*"' +
    '(?<version>\d+\.\d+\.\d+)"\s*;\s*$')
if ($productVersionMatches.Count -ne 1) {
    throw 'ProductBrand.cs must contain exactly one semantic ProductVersion.'
}
$productVersion = $productVersionMatches[0].Groups['version'].Value

$signing = [IO.File]::ReadAllText($signingPath)
$persistBeforeImport = [regex]::Match(
    $signing,
    '\$rsa\.PersistKeyInCsp\s*=\s*\$false')
$privateKeyImport = [regex]::Match(
    $signing,
    '\$rsa\.FromXmlString\(\$privateKeyXml\)')
if (-not $persistBeforeImport.Success -or
    -not $privateKeyImport.Success -or
    $persistBeforeImport.Index -ge $privateKeyImport.Index) {
    throw 'The signing key must be marked non-persistent before private-key import.'
}
foreach ($bufferName in @(
    'encryptedKey',
    'entropy',
    'privateKeyBytes',
    'manifestBytes',
    'signature'
)) {
    $clearPattern = '\[Array\]::Clear\(\$' +
        [regex]::Escape($bufferName) + ',\s*0,\s*\$' +
        [regex]::Escape($bufferName) + '\.Length\)'
    if (-not [regex]::IsMatch($signing, $clearPattern)) {
        throw "The signing script does not clear byte buffer '$bufferName'."
    }
}
if (-not [regex]::IsMatch(
    $signing,
    '(?s)finally\s*\{.*?\$rsa\.Dispose\(\).*?\}')) {
    throw 'The signing provider is not safely disposed from a finally block.'
}

$workflow = [IO.File]::ReadAllText($workflowPath)
foreach ($requiredWorkflowContract in @(
    'ProductBrand.cs',
    'BOOSTIX_PRODUCT_VERSION',
    'BOOSTIX_FILE_VERSION',
    'BOOSTIX_RELEASE_SNAPSHOT_DIRECTORY',
    'Remove-Item Env:\BOOSTIX_RELEASE_SNAPSHOT_DIRECTORY',
    'Out-File -FilePath $env:GITHUB_ENV',
    '$item.FileVersion -cne $env:BOOSTIX_FILE_VERSION',
    'persist-credentials: false',
    'permissions:'
)) {
    if (-not $workflow.Contains($requiredWorkflowContract)) {
        throw "The CI workflow contract is missing: $requiredWorkflowContract"
    }
}
foreach ($signedSnapshotFile in @(
    'update.json',
    'update.json.sig',
    'update-v2.json',
    'update-v2.json.sig'
)) {
    if (-not $workflow.Contains("'$signedSnapshotFile'")) {
        throw "CI does not preserve signed channel file '$signedSnapshotFile'."
    }
}
foreach ($hardCodedReleasePattern in @(
    '(?:Boostix|MajesticBoost)-Setup-\d+\.\d+\.\d+\.exe',
    "FileVersion\s+-cne\s+'\d+\.\d+\.\d+\.\d+'"
)) {
    if ([regex]::IsMatch($workflow, $hardCodedReleasePattern)) {
        throw "The CI workflow contains hard-coded release metadata: $hardCodedReleasePattern"
    }
}
if ($workflow.Contains('Boostix-Setup-' + $productVersion + '.exe') -or
    $workflow.Contains('MajesticBoost-Setup-' + $productVersion + '.exe')) {
    throw 'The current ProductVersion was copied into an artifact path in CI.'
}

$usesLines = [regex]::Matches($workflow, '(?m)^\s*uses:\s*.+$')
if ($usesLines.Count -eq 0) {
    throw 'The CI workflow does not reference any actions.'
}
foreach ($usesLine in $usesLines) {
    if (-not [regex]::IsMatch(
        $usesLine.Value,
        '^\s*uses:\s*[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+@[0-9a-f]{40}' +
        '\s+#\s+v\d+\.\d+\.\d+\s*$')) {
        throw 'Every CI action must use a full SHA with a version comment: ' +
            $usesLine.Value.Trim()
    }
}

$dependabot = [IO.File]::ReadAllText($dependabotPath)
foreach ($dependabotContract in @(
    '(?m)^version:\s*2\s*$',
    '(?m)^\s*-\s*package-ecosystem:\s*["'']github-actions["'']\s*$',
    '(?m)^\s*directory:\s*["'']/["'']\s*$',
    '(?m)^\s*interval:\s*["'']weekly["'']\s*$'
)) {
    if (-not [regex]::IsMatch($dependabot, $dependabotContract)) {
        throw "Dependabot GitHub Actions contract is missing: $dependabotContract"
    }
}

$runAll = [IO.File]::ReadAllText($runAllPath)
if (-not $runAll.Contains("'Test-SigningAndCiContracts.ps1'")) {
    throw 'The release automation regression test is not mandatory in Run-All.ps1.'
}
if (-not [regex]::IsMatch(
    $runAll,
    '(?s)\$TestFile\.Name\s+-cne\s+''Test-ReleaseUpdateChannel\.ps1''.*?' +
        'EnvironmentVariables\.Remove\(\s*' +
        '''BOOSTIX_RELEASE_SNAPSHOT_DIRECTORY''\s*\)')) {
    throw 'Run-All must expose the signed snapshot only to the release-channel test.'
}

$releaseChannelTest = [IO.File]::ReadAllText($releaseChannelTestPath)
foreach ($signedChannelContract in @(
    '$signedChannelRoot',
    "Join-Path `$signedChannelRoot 'update.json'",
    "Join-Path `$signedChannelRoot 'update.json.sig'",
    "Join-Path `$signedChannelRoot 'update-v2.json'",
    "Join-Path `$signedChannelRoot 'update-v2.json.sig'"
)) {
    if (-not $releaseChannelTest.Contains($signedChannelContract)) {
        throw "The release-channel snapshot contract is missing: $signedChannelContract"
    }
}

$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$testRoot = [IO.Path]::GetFullPath((Join-Path $tempBase (
    'Boostix-SigningContract-' + [Guid]::NewGuid().ToString('N'))))
if (-not $testRoot.StartsWith(
    $tempBase + [IO.Path]::DirectorySeparatorChar,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The signing test directory escaped the system temporary directory.'
}

$testRsa = $null
$verifyRsa = $null
$privateKeyBytes = $null
$encryptedKey = $null
$entropy = $null
$manifestBytes = $null
$signatureBytes = $null
try {
    [void](New-Item -ItemType Directory -Path $testRoot)
    $manifestPath = Join-Path $testRoot 'update-v2.json'
    $signaturePath = Join-Path $testRoot 'update-v2.json.sig'
    $privateKeyPath = Join-Path $testRoot 'manifest-private-v1.dpapi'

    $testRsa = New-Object Security.Cryptography.RSACryptoServiceProvider 3072
    $testRsa.PersistKeyInCsp = $false
    $privateKeyBytes = [Text.Encoding]::UTF8.GetBytes(
        $testRsa.ToXmlString($true))
    $publicKeyXml = $testRsa.ToXmlString($false)
    $entropy = [Text.Encoding]::UTF8.GetBytes(
        'MajesticBoost manifest signing key v1')
    $encryptedKey = [Security.Cryptography.ProtectedData]::Protect(
        $privateKeyBytes,
        $entropy,
        [Security.Cryptography.DataProtectionScope]::CurrentUser)
    [IO.File]::WriteAllBytes($privateKeyPath, $encryptedKey)

    $manifestBytes = [Text.Encoding]::UTF8.GetBytes(
        '{"version":"9.9.9","size":1}')
    [IO.File]::WriteAllBytes($manifestPath, $manifestBytes)
    & $signingPath `
        -ManifestPath $manifestPath `
        -SignaturePath $signaturePath `
        -PrivateKeyPath $privateKeyPath

    $signatureText = [IO.File]::ReadAllText($signaturePath).Trim()
    $signatureBytes = [Convert]::FromBase64String($signatureText)
    $verifyRsa = New-Object Security.Cryptography.RSACryptoServiceProvider
    $verifyRsa.PersistKeyInCsp = $false
    $verifyRsa.FromXmlString($publicKeyXml)
    $sha256Oid = [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256')
    if (-not $verifyRsa.VerifyData(
        $manifestBytes,
        $sha256Oid,
        $signatureBytes)) {
        throw 'The signing script produced an invalid detached signature.'
    }
}
finally {
    $publicKeyXml = $null
    foreach ($buffer in @(
        $signatureBytes,
        $manifestBytes,
        $encryptedKey,
        $privateKeyBytes,
        $entropy
    )) {
        if ($null -ne $buffer) {
            [Array]::Clear($buffer, 0, $buffer.Length)
        }
    }
    if ($null -ne $verifyRsa) {
        $verifyRsa.PersistKeyInCsp = $false
        $verifyRsa.Dispose()
    }
    if ($null -ne $testRsa) {
        $testRsa.PersistKeyInCsp = $false
        $testRsa.Dispose()
    }
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-Host 'Signing and CI contracts regression test passed.' -ForegroundColor Green
