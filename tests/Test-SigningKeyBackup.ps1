[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Security
if ($PSVersionTable.PSEdition -cne 'Desktop' -or
    $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This regression test must run under Windows PowerShell 5.1.'
}

# The update signing key is the only trust root installed clients accept.
# This test proves the key can be created without overwriting an existing one,
# moved through a password-protected backup, and used to sign a manifest.

$projectRoot = Split-Path -Parent $PSScriptRoot
$newKeyScript = Join-Path $projectRoot 'tools\New-UpdateSigningKey.ps1'
$backupScript = Join-Path $projectRoot 'tools\Backup-UpdateSigningKey.ps1'
$signScript = Join-Path $projectRoot 'tools\Sign-UpdateManifest.ps1'
$entropy = [Text.Encoding]::UTF8.GetBytes('Boostix manifest signing key v2')

$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$testRoot = [IO.Path]::GetFullPath((Join-Path $tempBase (
    'Boostix-SigningKey-' + [Guid]::NewGuid().ToString('N'))))
if (-not $testRoot.StartsWith(
        $tempBase + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The signing-key test directory escaped the system temporary directory.'
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Description)
    $failed = $false
    try {
        & $Action
    }
    catch {
        $failed = $true
    }
    if (-not $failed) {
        throw $Description
    }
}

function Read-ProtectedKey {
    param([string]$Path)
    return [Security.Cryptography.ProtectedData]::Unprotect(
        [IO.File]::ReadAllBytes($Path),
        $entropy,
        [Security.Cryptography.DataProtectionScope]::CurrentUser)
}

$password = ConvertTo-SecureString 'boostix-test-backup-passphrase' -AsPlainText -Force
$wrongPassword = ConvertTo-SecureString 'boostix-wrong-passphrase' -AsPlainText -Force
$originalKey = $null
$restoredKey = $null
$verify = $null
try {
    [void](New-Item -ItemType Directory -Path $testRoot)
    $keyPath = Join-Path $testRoot 'manifest-private-v2.dpapi'
    $publicPath = Join-Path $testRoot 'public.xml'
    & $newKeyScript -PrivateKeyPath $keyPath -PublicKeyPath $publicPath | Out-Null
    if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $publicPath -PathType Leaf)) {
        throw 'The key generator did not write the private and public key files.'
    }
    $before = [IO.File]::ReadAllBytes($keyPath)
    Assert-Throws { & $newKeyScript -PrivateKeyPath $keyPath | Out-Null } `
        'The key generator overwrote an existing signing key.'
    if (-not [Linq.Enumerable]::SequenceEqual([byte[]]$before, [byte[]][IO.File]::ReadAllBytes($keyPath))) {
        throw 'A refused key generation still modified the existing key.'
    }
    Assert-Throws { & $newKeyScript -PrivateKeyPath (Join-Path $projectRoot 'leak.dpapi') | Out-Null } `
        'The key generator accepted a private-key path inside the repository.'
    if (Test-Path -LiteralPath (Join-Path $projectRoot 'leak.dpapi')) {
        throw 'A private key was written inside the repository.'
    }

    $publicXml = [IO.File]::ReadAllText($publicPath)
    if ($publicXml -match '<D>|<P>|<Q>|<InverseQ>') {
        throw 'The public key file contains private key material.'
    }
    $originalKey = Read-ProtectedKey $keyPath
    $probe = New-Object Security.Cryptography.RSACryptoServiceProvider
    try {
        $probe.PersistKeyInCsp = $false
        $probe.FromXmlString([Text.Encoding]::UTF8.GetString($originalKey))
        if ($probe.KeySize -ne 3072 -or $probe.PublicOnly) {
            throw "Generated key is not a 3072-bit private key: $($probe.KeySize)."
        }
    }
    finally {
        $probe.PersistKeyInCsp = $false
        $probe.Dispose()
    }

    $backupPath = Join-Path $testRoot 'signing-key.backup'
    & $backupScript -Export -BackupPath $backupPath -PrivateKeyPath $keyPath -Password $password | Out-Null
    $backupBytes = [IO.File]::ReadAllBytes($backupPath)
    $backupText = [Text.Encoding]::ASCII.GetString($backupBytes)
    if ($backupText.Contains('<RSAKeyValue>') -or $backupText.Contains('<Modulus>')) {
        throw 'The backup stores the private key in plain text.'
    }
    Assert-Throws { & $backupScript -Export -BackupPath $backupPath -PrivateKeyPath $keyPath -Password $password | Out-Null } `
        'The backup export overwrote an existing backup file.'

    $restoredPath = Join-Path $testRoot 'restored.dpapi'
    Assert-Throws { & $backupScript -Import -BackupPath $backupPath -PrivateKeyPath $restoredPath -Password $wrongPassword | Out-Null } `
        'The backup import accepted a wrong password.'
    if (Test-Path -LiteralPath $restoredPath) {
        throw 'A rejected import still wrote a key file.'
    }
    $tamperedPath = Join-Path $testRoot 'tampered.backup'
    $tampered = [byte[]]$backupBytes.Clone()
    $tampered[$tampered.Length - 40] = $tampered[$tampered.Length - 40] -bxor 0x5A
    [IO.File]::WriteAllBytes($tamperedPath, $tampered)
    Assert-Throws { & $backupScript -Import -BackupPath $tamperedPath -PrivateKeyPath $restoredPath -Password $password | Out-Null } `
        'The backup import accepted a modified backup file.'

    & $backupScript -Import -BackupPath $backupPath -PrivateKeyPath $restoredPath -Password $password | Out-Null
    $restoredKey = Read-ProtectedKey $restoredPath
    if (-not [Linq.Enumerable]::SequenceEqual([byte[]]$originalKey, [byte[]]$restoredKey)) {
        throw 'The restored signing key differs from the original key.'
    }
    Assert-Throws { & $backupScript -Import -BackupPath $backupPath -PrivateKeyPath $restoredPath -Password $password | Out-Null } `
        'The backup import overwrote an existing signing key.'

    $manifestPath = Join-Path $testRoot 'update-v3.json'
    $signaturePath = Join-Path $testRoot 'update-v3.json.sig'
    $manifestBytes = [Text.Encoding]::UTF8.GetBytes('{"version":"9.9.9","size":1}')
    [IO.File]::WriteAllBytes($manifestPath, $manifestBytes)
    & $signScript -ManifestPath $manifestPath -SignaturePath $signaturePath -PrivateKeyPath $restoredPath | Out-Null
    $verify = New-Object Security.Cryptography.RSACryptoServiceProvider
    $verify.PersistKeyInCsp = $false
    $verify.FromXmlString($publicXml)
    $sha256Oid = [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256')
    if (-not $verify.VerifyData(
            $manifestBytes,
            $sha256Oid,
            [Convert]::FromBase64String([IO.File]::ReadAllText($signaturePath).Trim()))) {
        throw 'A manifest signed with the restored key does not verify with the generated public key.'
    }

    foreach ($frozenName in @('update.json', 'update-v2.json')) {
        $frozenPath = Join-Path $testRoot $frozenName
        [IO.File]::WriteAllBytes($frozenPath, $manifestBytes)
        Assert-Throws { & $signScript -ManifestPath $frozenPath -SignaturePath ($frozenPath + '.sig') -PrivateKeyPath $restoredPath | Out-Null } `
            "The signing script re-signed the frozen $frozenName channel."
    }
}
finally {
    foreach ($buffer in @($originalKey, $restoredKey)) {
        if ($null -ne $buffer) {
            [Array]::Clear($buffer, 0, $buffer.Length)
        }
    }
    if ($null -ne $verify) {
        $verify.PersistKeyInCsp = $false
        $verify.Dispose()
    }
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-Host 'Signing key creation, backup and restore regression test passed.' -ForegroundColor Green
