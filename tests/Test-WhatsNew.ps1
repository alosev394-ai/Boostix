[CmdletBinding()]
param(
    [string]$ApplicationPath
)

# After an update Boostix shows "What's new" once, built from the CHANGELOG
# entry embedded in the app. Fresh installs and same-version launches stay
# quiet, and demo/test launches never touch the per-user version marker.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $ApplicationPath) {
    $ApplicationPath = Join-Path $projectRoot 'dist\Boostix.exe'
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path

$build = [IO.File]::ReadAllText((Join-Path $projectRoot 'build.ps1'))
foreach ($required in @(
    '"$projectRoot\Boostix\WhatsNew.cs"',
    '"/resource:$projectRoot\CHANGELOG.md,Boostix.Changelog.md"'
)) {
    if (-not $build.Contains($required)) {
        throw "The app build does not include the What's new contract: $required"
    }
}
$program = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\Program.cs'))
foreach ($required in @('WhatsNewContent.ShouldShow(', '"last-run-version.txt"', '"--demo-whats-new"')) {
    if (-not $program.Contains($required)) {
        throw "The startup What's new contract is missing: $required"
    }
}

$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($ApplicationPath))
$allStatic = [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static
$contentType = $assembly.GetType('Boostix.WhatsNewContent', $true)
$parse = $contentType.GetMethod('ParseHighlights', $allStatic)
$shouldShow = $contentType.GetMethod('ShouldShow', $allStatic)
$load = $contentType.GetMethod('LoadHighlights', $allStatic)
if (-not $parse -or -not $shouldShow -or -not $load) {
    throw "The What's new API was not compiled."
}
$version = [string]$assembly.GetType('Boostix.Branding.ProductBrand', $true).GetField('ProductVersion', $allStatic).GetRawConstantValue()

# Parsing: only the requested version, bullets joined across wrapped lines,
# markdown emphasis, code and links reduced to plain text.
$changelog = @(
    '# History',
    '',
    '## 3.1.0 BETA - 2030-01-02',
    '',
    '### New',
    '',
    '- **Bold** feature with `code` and a',
    '  [link](https://example.com) that wraps.',
    '- Second item.',
    '',
    '### Fixes',
    '',
    '- Third item.',
    '',
    'Details: notes.',
    '',
    '## 3.0.0 BETA - 2030-01-01',
    '',
    '- Old item.'
) -join "`r`n"
$items = @($parse.Invoke($null, [object[]]@([string]$changelog, [string]'3.1.0')))
if ($items.Count -ne 3 -or
    $items[0] -cne 'Bold feature with code and a link that wraps.' -or
    $items[1] -cne 'Second item.' -or
    $items[2] -cne 'Third item.') {
    throw "Changelog highlights were not parsed as expected: $($items -join ' | ')"
}
if (@($parse.Invoke($null, [object[]]@([string]$changelog, [string]'3.0.1'))).Count -ne 0 -or
    @($parse.Invoke($null, [object[]]@([string]$changelog, [string]'3.1'))).Count -ne 0) {
    throw 'An unknown or partial version must not match a changelog entry.'
}

# When to show.
foreach ($case in @(
    @{ Last = $null; Current = '2.2.0'; Earlier = $false; Expected = $false; Label = 'fresh install' },
    @{ Last = $null; Current = '2.2.0'; Earlier = $true; Expected = $true; Label = 'first launch after updating from a version without a marker' },
    @{ Last = '2.1.1'; Current = '2.2.0'; Earlier = $true; Expected = $true; Label = 'update' },
    @{ Last = '2.2.0'; Current = '2.2.0'; Earlier = $true; Expected = $false; Label = 'same version' },
    @{ Last = '2.3.0'; Current = '2.2.0'; Earlier = $true; Expected = $false; Label = 'downgrade' },
    @{ Last = 'garbage'; Current = '2.2.0'; Earlier = $false; Expected = $false; Label = 'unreadable marker on a fresh profile' }
)) {
    $actual = [bool]$shouldShow.Invoke($null, [object[]]@($case.Last, [string]$case.Current, [bool]$case.Earlier))
    if ($actual -ne $case.Expected) {
        throw "ShouldShow returned $actual for the $($case.Label) case; expected $($case.Expected)."
    }
}

# The shipped build carries highlights for its own version.
$shipped = @($load.Invoke($null, [object[]]@([string]$version)))
if ($shipped.Count -lt 1) {
    throw "The embedded CHANGELOG has no highlights for $version."
}

# The overlay (demo flag only; no marker file is read or written).
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$markerPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Boostix\last-run-version.txt'
$markerBefore = if (Test-Path -LiteralPath $markerPath) { (Get-Item -LiteralPath $markerPath).LastWriteTimeUtc } else { $null }

function Wait-ForElement {
    param($Root, [string]$AutomationId, [int]$TimeoutSeconds = 10)
    $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    $until = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $element = $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
        if ($element -and -not $element.Current.IsOffscreen) { return $element }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    return $null
}

foreach ($withFlag in @($true, $false)) {
    $arguments = @('--skip-setup', '--demo', ('--test-instance=' + [Guid]::NewGuid().ToString('N')))
    if ($withFlag) { $arguments += '--demo-whats-new' }
    $process = Start-Process -FilePath $ApplicationPath -PassThru -ArgumentList $arguments
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while ($process.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 100
            $process.Refresh()
        }
        if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
            throw 'Boostix did not open its window.'
        }
        $window = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
        if (-not $withFlag) {
            if (-not (Wait-ForElement -Root $window -AutomationId 'Boostix.Status' -TimeoutSeconds 10)) {
                throw 'The home page did not render in the plain demo launch.'
            }
            if (Wait-ForElement -Root $window -AutomationId 'Boostix.WhatsNew' -TimeoutSeconds 2) {
                throw "What's new appeared in a demo launch without --demo-whats-new."
            }
            continue
        }

        $overlay = Wait-ForElement -Root $window -AutomationId 'Boostix.WhatsNew' -TimeoutSeconds 10
        if (-not $overlay) {
            throw "What's new did not appear with --demo-whats-new."
        }
        $title = Wait-ForElement -Root $window -AutomationId 'Boostix.WhatsNew.Title' -TimeoutSeconds 3
        if (-not $title -or -not $title.Current.Name.Contains($version)) {
            throw "The What's new title does not name version $version."
        }
        $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, 'Boostix.WhatsNew.Item')
        $shownItems = @($window.FindAll([Windows.Automation.TreeScope]::Descendants, $condition))
        if ($shownItems.Count -ne $shipped.Count) {
            throw "What's new shows $($shownItems.Count) items; the changelog has $($shipped.Count)."
        }
        $close = Wait-ForElement -Root $window -AutomationId 'Boostix.WhatsNew.Close' -TimeoutSeconds 3
        if (-not $close) {
            throw "What's new has no close button."
        }
        $close.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        $until = [DateTime]::UtcNow.AddSeconds(5)
        while ((Wait-ForElement -Root $window -AutomationId 'Boostix.WhatsNew' -TimeoutSeconds 0) -and [DateTime]::UtcNow -lt $until) {
            Start-Sleep -Milliseconds 100
        }
        if (Wait-ForElement -Root $window -AutomationId 'Boostix.WhatsNew' -TimeoutSeconds 0) {
            throw "What's new did not close."
        }
        if ($process.HasExited) {
            throw "Closing What's new closed Boostix."
        }
    }
    finally {
        if ($process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

$markerAfter = if (Test-Path -LiteralPath $markerPath) { (Get-Item -LiteralPath $markerPath).LastWriteTimeUtc } else { $null }
if ($markerAfter -ne $markerBefore) {
    throw 'A demo launch wrote the per-user last-run-version marker.'
}

Write-Host "What's new test passed: changelog parsing, show-once rules, embedded highlights for $version, and the demo overlay opens and closes without touching the version marker." -ForegroundColor Green
