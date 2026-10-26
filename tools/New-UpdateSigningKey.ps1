[CmdletBinding()]
param(
    # DPAPI-protected private key; never place it inside the repository.
    [Parameter(Mandatory = $false)]
    [string]$PrivateKeyPath = (Join-Path $env:LOCALAPPDATA 'BoostixSigning\manifest-private-v2.dpapi'),

    # Optional file that receives the public RSA key XML for UpdateFlow.cs.
    [Parameter(Mandatory = $false)]
    [string]$PublicKeyPath
)

# Creates the Boostix update-manifest signing key (RSA-3072, key v2). The
# private key is encrypted with DPAPI for the current Windows user and is
# written only if no key exists yet. Back it up with
# tools\Backup-UpdateSigningKey.ps1 -Export: a lost key cannot be recovered
# and installed clients only accept manifests signed by their embedded key.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Security

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $scriptDirectory))
$PrivateKeyPath = [IO.Path]::GetFullPath($PrivateKeyPath)
if ($PrivateKeyPath.StartsWith(
        $projectRoot.TrimEnd('\') + '\',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The private signing key must be stored outside the repository.'
}
if (Test-Path -LiteralPath $PrivateKeyPath) {
    throw "A signing key already exists and was left unchanged: $PrivateKeyPath"
}

$rsa = $null
$privateKeyBytes = $null
$entropy = $null
$encryptedKey = $null
try {
    $rsa = New-Object Security.Cryptography.RSACryptoServiceProvider 3072
    $rsa.PersistKeyInCsp = $false
    $privateKeyBytes = [Text.Encoding]::UTF8.GetBytes($rsa.ToXmlString($true))
    $publicKeyXml = $rsa.ToXmlString($false)
    $entropy = [Text.Encoding]::UTF8.GetBytes('Boostix manifest signing key v2')
    $encryptedKey = [Security.Cryptography.ProtectedData]::Protect(
        $privateKeyBytes,
        $entropy,
        [Security.Cryptography.DataProtectionScope]::CurrentUser)

    $directory = Split-Path -Parent $PrivateKeyPath
    [void][IO.Directory]::CreateDirectory($directory)
    $stream = [IO.File]::Open(
        $PrivateKeyPath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    try {
        $stream.Write($encryptedKey, 0, $encryptedKey.Length)
    }
    finally {
        $stream.Dispose()
    }

    if (-not [string]::IsNullOrWhiteSpace($PublicKeyPath)) {
        [IO.File]::WriteAllText(
            $PublicKeyPath,
            $publicKeyXml,
            (New-Object Text.UTF8Encoding($false)))
    }

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $fingerprint = [BitConverter]::ToString(
            $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($publicKeyXml))).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
    Write-Host "Created signing key: $PrivateKeyPath"
    Write-Host "Public key SHA-256: $fingerprint"
    Write-Host 'Back it up now: tools\Backup-UpdateSigningKey.ps1 -Export -BackupPath <file>'
}
finally {
    foreach ($buffer in @($privateKeyBytes, $entropy, $encryptedKey)) {
        if ($null -ne $buffer) {
            [Array]::Clear($buffer, 0, $buffer.Length)
        }
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
