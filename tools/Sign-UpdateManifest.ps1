[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$ManifestPath,

    [Parameter(Mandatory = $false)]
    [string]$SignaturePath,

    [Parameter(Mandatory = $false)]
    [string]$PrivateKeyPath = (Join-Path $env:LOCALAPPDATA 'BoostixSigning\manifest-private-v2.dpapi')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Security

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDirectory
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $projectRoot 'update-v3.json'
}
if ([string]::IsNullOrWhiteSpace($SignaturePath)) {
    $SignaturePath = Join-Path $projectRoot 'update-v3.json.sig'
}

# update.json (1.x clients) and update-v2.json (2.0.x clients) were signed with
# the retired v1 key, which no longer exists. They stay frozen byte-for-byte so
# those clients keep reading a valid manifest; new releases use update-v3.json
# and key v2 (tools\New-UpdateSigningKey.ps1).
foreach ($frozenName in @('update.json', 'update-v2.json')) {
    if ((Split-Path -Leaf $ManifestPath) -ieq $frozenName) {
        throw "The $frozenName channel is frozen with the retired v1 key and must not be re-signed. Use update-v3.json."
    }
}

if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "Update manifest not found: $ManifestPath"
}
if (-not (Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf)) {
    throw "Encrypted signing key not found: $PrivateKeyPath. Restore it with tools\Backup-UpdateSigningKey.ps1 -Import."
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
        'Boostix manifest signing key v2')
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
