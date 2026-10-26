[CmdletBinding()]
param(
    [string]$ApplicationPath
)

# "Collect diagnostics" must produce one zip with the safe report, a system
# summary and the Boostix logs (crash, update, setup transactions, installer),
# redacting personal data while keeping the system and registry paths that
# make a log useful (for example which policy key Windows refused).

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or
    $PSVersionTable.PSVersion.Major -ne 5 -or
    [IntPtr]::Size -ne 8) {
    throw 'This regression test requires 64-bit Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $ApplicationPath) {
    $ApplicationPath = Join-Path $projectRoot 'dist\Boostix.exe'
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path

function Convert-UiName {
    param([string]$Base64)
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Base64))
}

# Source contracts: the archive is reachable from Settings and Reports, saved
# as .zip, and the build references System.IO.Compression.
$build = [IO.File]::ReadAllText((Join-Path $projectRoot 'build.ps1'))
if (-not $build.Contains("'/reference:System.IO.Compression.dll'")) {
    throw 'The app build does not reference System.IO.Compression.dll.'
}
$overlay = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\BoostCenterOverlay.cs'))
$collectLabel = Convert-UiName '0KHQvtCx0YDQsNGC0Ywg0LTQuNCw0LPQvdC+0YHRgtC40LrRgw=='
foreach ($required in @(
    '"Boostix.Center.CollectDiagnostics"',
    '"Boostix.Center.ExportDiagnostics"',
    ('"' + $collectLabel + '"')
)) {
    if (-not $overlay.Contains($required)) {
        throw "The diagnostics UI contract is missing: $required"
    }
}
$program = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\Program.cs'))
foreach ($required in @(
    'DiagnosticBundleBuilder.WriteBundle(',
    'DiagnosticBundleBuilder.CollectDefaultSources()',
    '|*.zip"',
    'DefaultExt = ".zip"'
)) {
    if (-not $program.Contains($required)) {
        throw "The diagnostics export handler contract is missing: $required"
    }
}

$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($ApplicationPath))
$allStatic = [Reflection.BindingFlags]::Public -bor
    [Reflection.BindingFlags]::NonPublic -bor
    [Reflection.BindingFlags]::Static
$bundleType = $assembly.GetType('Boostix.DiagnosticBundleBuilder', $true)
$sourceType = $assembly.GetType('Boostix.DiagnosticBundleSource', $true)
$exportType = $assembly.GetType('Boostix.DiagnosticExportBuilder', $true)
$sanitizeLog = $exportType.GetMethod('SanitizeLogText', $allStatic)
$writeBundle = $bundleType.GetMethod('WriteBundle', $allStatic)
$collect = $bundleType.GetMethod('CollectDefaultSources', $allStatic)
$summary = $bundleType.GetMethod('BuildSystemSummary', $allStatic)
if (-not $sanitizeLog -or -not $writeBundle -or -not $collect -or -not $summary) {
    throw 'The diagnostic bundle API was not compiled.'
}
$maximumLogCharacters = [int]$bundleType.GetField('MaximumLogCharacters', $allStatic).GetRawConstantValue()

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('Boostix-DiagnosticBundle-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null

    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $email = 'private.user@example.com'
    $profilePath = Join-Path $env:USERPROFILE 'Documents\Saves\slot1.dat'
    $privatePath = 'D:\Private\Photos\holiday.png'
    $registryPath = 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh::AllowNewsAndInterests'
    $installedScript = Join-Path $env:ProgramFiles 'Boostix\MaxFPS-Apply.ps1'
    $backupPath = Join-Path $env:ProgramData 'BoostixOptimization\Backups\x\state.json'
    $logText = @(
        "Computer: $env:COMPUTERNAME",
        "Run as: $env:COMPUTERNAME\$env:USERNAME",
        "Host: $installedScript -ExpectedUserSid $sid",
        "Profile file: $profilePath",
        "Other file: $privatePath",
        "Contact: $email",
        'password=do-not-export-this',
        "Windows refused to change $registryPath; this setting was skipped.",
        "Backup: $backupPath"
    ) -join "`r`n"

    $safe = [string]$sanitizeLog.Invoke($null, [object[]]@([string]$logText))
    foreach ($sensitive in @($env:USERNAME, $env:COMPUTERNAME, $sid, $email, 'do-not-export-this', $env:USERPROFILE, $privatePath)) {
        if ($safe.IndexOf($sensitive, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Log redaction leaked sensitive content: $sensitive"
        }
    }
    foreach ($kept in @($registryPath, $installedScript, $backupPath, '<user-profile>', '<path>', '<computer>', '<user>', '<sid>', '<email>', '<redacted>')) {
        if (-not $safe.Contains($kept)) {
            throw "Log redaction removed useful or expected content: $kept"
        }
    }

    # Fixture logs: one small, one larger than the per-log bound, one missing.
    $crashLog = Join-Path $testRoot 'crash.log'
    [IO.File]::WriteAllText($crashLog, $logText, (New-Object Text.UTF8Encoding($true)))
    $bigLog = Join-Path $testRoot 'update.log'
    $bigLines = New-Object Text.StringBuilder
    $line = 0
    while ($bigLines.Length -lt ($maximumLogCharacters * 2)) {
        [void]$bigLines.AppendLine(('line {0:D7} ' -f $line) + ('x' * 60))
        $line++
    }
    [void]$bigLines.AppendLine('FINAL LINE MARKER')
    [IO.File]::WriteAllText($bigLog, $bigLines.ToString(), (New-Object Text.UTF8Encoding($false)))

    $sourceList = [Activator]::CreateInstance(
        [Collections.Generic.List`1].MakeGenericType($sourceType))
    $sourceList.Add([Activator]::CreateInstance($sourceType, [object[]]@([string]'logs/crash.log', [string]$crashLog)))
    $sourceList.Add([Activator]::CreateInstance($sourceType, [object[]]@([string]'logs/update.log', [string]$bigLog)))
    $sourceList.Add([Activator]::CreateInstance($sourceType, [object[]]@([string]'setup/missing/apply.log', [string](Join-Path $testRoot 'missing.log'))))
    $sourceList.Add([Activator]::CreateInstance($sourceType, [object[]]@([string]'../escape.txt', [string]$crashLog)))

    $destination = Join-Path $testRoot 'Boostix-Diagnostic-test.zip'
    $systemText = [string]$summary.Invoke($null, [object[]]@([string]'9.9.9'))
    if (-not $systemText.Contains('Boostix=9.9.9') -or -not $systemText.Contains('WindowsBuild=')) {
        throw 'The system summary does not name the Boostix version and Windows build.'
    }
    [void]$writeBundle.Invoke($null, [object[]]@([string]$destination, [string]'SAFE REPORT BODY', $systemText, $sourceList))

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($destination)
    try {
        $entries = @{}
        foreach ($entry in $archive.Entries) {
            $reader = New-Object IO.StreamReader($entry.Open(), [Text.Encoding]::UTF8)
            try { $entries[$entry.FullName] = $reader.ReadToEnd() } finally { $reader.Dispose() }
        }
    }
    finally {
        $archive.Dispose()
    }

    foreach ($name in @('report.txt', 'system.txt', 'manifest.txt', 'logs/crash.log', 'logs/update.log')) {
        if (-not $entries.ContainsKey($name)) {
            throw "The diagnostic archive is missing $name. Entries: $($entries.Keys -join ', ')"
        }
    }
    if ($entries.Keys | Where-Object { $_ -like '*escape*' -or $_ -like '*..*' }) {
        throw 'An unsafe entry name was written into the diagnostic archive.'
    }
    if ($entries['report.txt'] -cne 'SAFE REPORT BODY') {
        throw 'The safe report was not stored unchanged.'
    }
    foreach ($sensitive in @($env:USERNAME, $env:COMPUTERNAME, $sid, $email, 'do-not-export-this')) {
        foreach ($name in @($entries.Keys)) {
            if ($entries[$name].IndexOf($sensitive, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw "Archive entry $name leaked sensitive content: $sensitive"
            }
        }
    }
    if (-not $entries['logs/crash.log'].Contains($registryPath)) {
        throw 'The archived crash log lost the registry path needed for diagnosis.'
    }
    $tail = $entries['logs/update.log']
    if ($tail.Length -gt ($maximumLogCharacters + 200) -or
        -not $tail.Contains('FINAL LINE MARKER') -or
        $tail.Contains('line 0000000 ') -or
        -not $tail.Contains('[EARLIER LINES OMITTED]')) {
        throw 'A large log must be reduced to its most recent part with an omission marker.'
    }
    $manifest = $entries['manifest.txt']
    if (-not $manifest.Contains('setup/missing/apply.log: not found') -or
        -not $manifest.Contains('logs/crash.log: included')) {
        throw "The archive manifest does not report included and missing logs:`r`n$manifest"
    }

    # The default sources stay inside the Boostix data folders.
    $defaults = $collect.Invoke($null, @())
    $localRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Boostix'
    $commonRoot = [Environment]::GetFolderPath('CommonApplicationData')
    $defaultNames = @()
    foreach ($source in $defaults) {
        $defaultNames += $source.EntryName
        $path = [IO.Path]::GetFullPath($source.FilePath)
        if (-not ($path.StartsWith($localRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $path.StartsWith((Join-Path $commonRoot 'BoostixOptimization\Backups') + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $path.StartsWith((Join-Path $commonRoot 'Boostix\Logs') + '\', [StringComparison]::OrdinalIgnoreCase))) {
            throw "A default diagnostic source is outside the Boostix data folders: $path"
        }
    }
    foreach ($name in @('logs/crash.log', 'logs/update.log')) {
        if ($defaultNames -notcontains $name) {
            throw "The default diagnostic sources do not include $name."
        }
    }

    Write-Host 'Diagnostic bundle test passed: one zip with report, system summary, redacted logs, tail-bounded large logs, safe entry names and a manifest.' -ForegroundColor Green
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
