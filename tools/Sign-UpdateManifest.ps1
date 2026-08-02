[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$ManifestPath,

    [Parameter(Mandatory = $false)]
    [string]$SignaturePath,

    [Parameter(Mandatory = $false)]
    [switch]$AllowLegacyChannel,

    [Parameter(Mandatory = $false)]
    [string]$PrivateKeyPath = (Join-Path $env:LOCALAPPDATA 'BoostixSigning\manifest-private-v1.dpapi')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Security

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDirectory
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $projectRoot 'update-v2.json'
}
if ([string]::IsNullOrWhiteSpace($SignaturePath)) {
    $SignaturePath = Join-Path $projectRoot 'update-v2.json.sig'
}

if ((Split-Path -Leaf $ManifestPath) -ieq 'update.json' -and -not $AllowLegacyChannel) {
    throw 'The legacy update.json channel is frozen. Use update-v2.json, or pass -AllowLegacyChannel only for an intentional emergency repair.'
}

if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "Update manifest not found: $ManifestPath"
}
if (-not $PSBoundParameters.ContainsKey('PrivateKeyPath') -and
    -not (Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf)) {
    # Reuse the existing DPAPI-protected release key during the product-name
    # migration. The signing identity and public key intentionally stay stable.
    $legacyPrivateKeyPath = Join-Path $env:LOCALAPPDATA 'MajesticBoostSigning\manifest-private-v1.dpapi'
    if (Test-Path -LiteralPath $legacyPrivateKeyPath -PathType Leaf) {
        $PrivateKeyPath = $legacyPrivateKeyPath
    }
}
if (-not (Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf)) {
    throw "Encrypted signing key not found: $PrivateKeyPath"
}

$encryptedKey = $null
$entropy = $null
$privateKeyBytes = $null
$privateKeyXml = $null
$manifestBytes = $null
$signature = $null
$rsa = $null
try {
    $encryptedKey = [IO.File]::ReadAllBytes($PrivateKeyPath)
    $entropy = [Text.Encoding]::UTF8.GetBytes(
        'MajesticBoost manifest signing key v1')
    $privateKeyBytes = [Security.Cryptography.ProtectedData]::Unprotect(
        $encryptedKey,
        $entropy,
        [Security.Cryptography.DataProtectionScope]::CurrentUser)
    [Array]::Clear($encryptedKey, 0, $encryptedKey.Length)
    $encryptedKey = $null
    [Array]::Clear($entropy, 0, $entropy.Length)
    $entropy = $null

    $rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
    # FromXmlString otherwise imports the private material into a persistent
    # CSP key container owned by the release operator.
    $rsa.PersistKeyInCsp = $false
    $privateKeyXml = [Text.Encoding]::UTF8.GetString($privateKeyBytes)
    $rsa.FromXmlString($privateKeyXml)
    $privateKeyXml = $null
    [Array]::Clear($privateKeyBytes, 0, $privateKeyBytes.Length)
    $privateKeyBytes = $null
    if ($rsa.KeySize -lt 3072) {
        throw "Signing key is too small: $($rsa.KeySize) bits."
    }

    $manifestBytes = [IO.File]::ReadAllBytes($ManifestPath)
    if ($manifestBytes.Length -le 0 -or $manifestBytes.Length -gt 16384) {
        throw "Manifest size must be between 1 and 16384 bytes."
    }

    $sha256Oid = [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256')
    $signature = $rsa.SignData($manifestBytes, $sha256Oid)
    $base64 = [Convert]::ToBase64String($signature)
    [IO.File]::WriteAllText($SignaturePath, $base64, (New-Object Text.UTF8Encoding($false)))
    Write-Host "Signed $ManifestPath"
    Write-Host "Signature: $SignaturePath"
}
finally {
    $privateKeyXml = $null
    if ($null -ne $signature) {
        [Array]::Clear($signature, 0, $signature.Length)
    }
    if ($null -ne $manifestBytes) {
        [Array]::Clear($manifestBytes, 0, $manifestBytes.Length)
    }
    if ($null -ne $privateKeyBytes) {
        [Array]::Clear($privateKeyBytes, 0, $privateKeyBytes.Length)
    }
    if ($null -ne $entropy) {
        [Array]::Clear($entropy, 0, $entropy.Length)
    }
    if ($null -ne $encryptedKey) {
        [Array]::Clear($encryptedKey, 0, $encryptedKey.Length)
    }
    if ($null -ne $rsa) {
        try {
            $rsa.PersistKeyInCsp = $false
        }
        finally {
            $rsa.Dispose()
        }
    }
}
