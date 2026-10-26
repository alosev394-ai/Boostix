[CmdletBinding(DefaultParameterSetName = 'Export')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Export')]
    [switch]$Export,

    [Parameter(Mandatory = $true, ParameterSetName = 'Import')]
    [switch]$Import,

    # Password-protected backup file (keep it off this PC: USB drive,
    # password manager attachment or another secure location).
    [Parameter(Mandatory = $true)]
    [string]$BackupPath,

    [Parameter(Mandatory = $false)]
    [string]$PrivateKeyPath = (Join-Path $env:LOCALAPPDATA 'BoostixSigning\manifest-private-v2.dpapi'),

    # Optional; when omitted the password is read interactively.
    [Parameter(Mandatory = $false)]
    [Security.SecureString]$Password
)

# Moves the update-manifest signing key between machines or Windows accounts.
# The DPAPI file only works for the Windows user that created it; this backup
# is portable and protected by a password you type (PBKDF2-SHA256, AES-256-CBC,
# HMAC-SHA256 encrypt-then-MAC).

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Security

$magic = [Text.Encoding]::ASCII.GetBytes('BOOSTIX-SIGNING-KEY-BACKUP-1')
$iterations = 310000
$entropy = [Text.Encoding]::UTF8.GetBytes('Boostix manifest signing key v2')

function Read-BackupPassword {
    param([bool]$Confirm)

    if ($null -ne $Password) {
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password))
        if ($plain.Length -lt 12) {
            throw 'Use a password of at least 12 characters.'
        }
        return [Text.Encoding]::UTF8.GetBytes($plain)
    }
    $first = Read-Host -AsSecureString 'Backup password (12+ characters)'
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($first))
    if ($plain.Length -lt 12) {
        throw 'Use a password of at least 12 characters.'
    }
    if ($Confirm) {
        $second = Read-Host -AsSecureString 'Repeat the password'
        $repeat = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR($second))
        if ($repeat -cne $plain) {
            throw 'The passwords do not match.'
        }
    }
    return [Text.Encoding]::UTF8.GetBytes($plain)
}

function Get-DerivedKeys {
    param([byte[]]$Password, [byte[]]$Salt, [int]$Rounds)

    $kdf = New-Object Security.Cryptography.Rfc2898DeriveBytes(
        $Password,
        $Salt,
        $Rounds,
        [Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        return $kdf.GetBytes(64)
    }
    finally {
        $kdf.Dispose()
    }
}

function New-Aes {
    param([byte[]]$Key, [byte[]]$Iv)
    $aes = New-Object Security.Cryptography.AesCryptoServiceProvider
    $aes.KeySize = 256
    $aes.Mode = [Security.Cryptography.CipherMode]::CBC
    $aes.Padding = [Security.Cryptography.PaddingMode]::PKCS7
    $aes.Key = $Key
    $aes.IV = $Iv
    return $aes
}

$passwordBytes = $null
$keys = $null
$plainKey = $null
try {
    if ($Export) {
        if (-not (Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf)) {
            throw "Signing key not found: $PrivateKeyPath"
        }
        if (Test-Path -LiteralPath $BackupPath) {
            throw "The backup file already exists and was left unchanged: $BackupPath"
        }
        $plainKey = [Security.Cryptography.ProtectedData]::Unprotect(
            [IO.File]::ReadAllBytes($PrivateKeyPath),
            $entropy,
            [Security.Cryptography.DataProtectionScope]::CurrentUser)
        $passwordBytes = Read-BackupPassword -Confirm $true

        $random = [Security.Cryptography.RandomNumberGenerator]::Create()
        $salt = New-Object byte[] 16
        $iv = New-Object byte[] 16
        $random.GetBytes($salt)
        $random.GetBytes($iv)
        $random.Dispose()
        $keys = Get-DerivedKeys -Password $passwordBytes -Salt $salt -Rounds $iterations
        $encryptionKey = $keys[0..31]
        $macKey = $keys[32..63]

        $aes = New-Aes -Key $encryptionKey -Iv $iv
        try {
            $cipher = $aes.CreateEncryptor().TransformFinalBlock(
                $plainKey, 0, $plainKey.Length)
        }
        finally {
            $aes.Dispose()
        }
        $body = New-Object IO.MemoryStream
        $body.Write($magic, 0, $magic.Length)
        $body.Write([BitConverter]::GetBytes([int]$iterations), 0, 4)
        $body.Write($salt, 0, $salt.Length)
        $body.Write($iv, 0, $iv.Length)
        $body.Write($cipher, 0, $cipher.Length)
        $payload = $body.ToArray()
        $hmac = New-Object Security.Cryptography.HMACSHA256(,[byte[]]$macKey)
        try {
            $tag = $hmac.ComputeHash($payload)
        }
        finally {
            $hmac.Dispose()
        }
        $stream = [IO.File]::Open(
            $BackupPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        try {
            $stream.Write($payload, 0, $payload.Length)
            $stream.Write($tag, 0, $tag.Length)
        }
        finally {
            $stream.Dispose()
        }
        Write-Host "Encrypted backup written: $BackupPath"
        Write-Host 'Store it away from this PC. Without the file and the password the key cannot be restored.'
    }
    else {
        if (Test-Path -LiteralPath $PrivateKeyPath) {
            throw "A signing key already exists and was left unchanged: $PrivateKeyPath"
        }
        $data = [IO.File]::ReadAllBytes($BackupPath)
        $headerLength = $magic.Length + 4 + 16 + 16
        if ($data.Length -le $headerLength + 32) {
            throw 'The backup file is truncated.'
        }
        for ($index = 0; $index -lt $magic.Length; $index++) {
            if ($data[$index] -ne $magic[$index]) {
                throw 'This is not a Boostix signing-key backup.'
            }
        }
        $rounds = [BitConverter]::ToInt32($data, $magic.Length)
        if ($rounds -lt 100000 -or $rounds -gt 10000000) {
            throw 'The backup file header is invalid.'
        }
        $salt = $data[($magic.Length + 4)..($magic.Length + 19)]
        $iv = $data[($magic.Length + 20)..($magic.Length + 35)]
        $payloadLength = $data.Length - 32
        $payload = New-Object byte[] $payloadLength
        [Array]::Copy($data, 0, $payload, 0, $payloadLength)
        $tag = New-Object byte[] 32
        [Array]::Copy($data, $payloadLength, $tag, 0, 32)
        $cipher = New-Object byte[] ($payloadLength - $headerLength)
        [Array]::Copy($data, $headerLength, $cipher, 0, $cipher.Length)

        $passwordBytes = Read-BackupPassword -Confirm $false
        $keys = Get-DerivedKeys -Password $passwordBytes -Salt $salt -Rounds $rounds
        $encryptionKey = $keys[0..31]
        $macKey = $keys[32..63]
        $hmac = New-Object Security.Cryptography.HMACSHA256(,[byte[]]$macKey)
        try {
            $expected = $hmac.ComputeHash($payload)
        }
        finally {
            $hmac.Dispose()
        }
        $difference = 0
        for ($index = 0; $index -lt 32; $index++) {
            $difference = $difference -bor ($expected[$index] -bxor $tag[$index])
        }
        if ($difference -ne 0) {
            throw 'Wrong password or a damaged backup file.'
        }
        $aes = New-Aes -Key $encryptionKey -Iv $iv
        try {
            $plainKey = $aes.CreateDecryptor().TransformFinalBlock(
                $cipher, 0, $cipher.Length)
        }
        finally {
            $aes.Dispose()
        }

        $check = New-Object Security.Cryptography.RSACryptoServiceProvider
        try {
            $check.PersistKeyInCsp = $false
            $check.FromXmlString([Text.Encoding]::UTF8.GetString($plainKey))
            if ($check.PublicOnly -or $check.KeySize -lt 3072) {
                throw 'The backup does not contain a 3072-bit private key.'
            }
        }
        finally {
            $check.PersistKeyInCsp = $false
            $check.Dispose()
        }

        $protected = [Security.Cryptography.ProtectedData]::Protect(
            $plainKey,
            $entropy,
            [Security.Cryptography.DataProtectionScope]::CurrentUser)
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $PrivateKeyPath))
        $stream = [IO.File]::Open(
            $PrivateKeyPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        try {
            $stream.Write($protected, 0, $protected.Length)
        }
        finally {
            $stream.Dispose()
        }
        Write-Host "Signing key restored for this Windows user: $PrivateKeyPath"
    }
}
finally {
    foreach ($buffer in @($passwordBytes, $keys, $plainKey)) {
        if ($null -ne $buffer) {
            [Array]::Clear($buffer, 0, $buffer.Length)
        }
    }
}
