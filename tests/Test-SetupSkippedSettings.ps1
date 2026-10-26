[CmdletBinding()]
param(
    [string]$ApplicationPath
)

# Since 2.1.1 a setting Windows refuses is skipped instead of aborting the
# profile. The user must see what was skipped (and whether the Windows
# restore point was created) on the "reboot required" card, not only in the
# setup log.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$applyPath = Join-Path $projectRoot 'outputs\MaxFPS-Apply.ps1'
if (-not $ApplicationPath) {
    $ApplicationPath = Join-Path $projectRoot 'dist\Boostix.exe'
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path

function Convert-UiName {
    param([string]$Base64)
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Base64))
}

function Get-ProductionFunctionScriptBlock {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string]$Name
    )
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$errors)
    if ($errors.Count -ne 0) {
        throw "Cannot parse production script: $ScriptPath"
    }
    $definition = $ast.Find(
        {
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $Name
        },
        $true)
    if (-not $definition) {
        throw "Production function was not found: $Name"
    }
    return [ScriptBlock]::Create($definition.Extent.Text)
}

# 1. The apply result reports skipped settings and the restore point outcome.
$apply = [IO.File]::ReadAllText($applyPath)
foreach ($required in @(
    'SkippedSettings = @(Get-SkippedRegistrySettings -State $state)',
    'RestorePointCreated = $restorePointCreated',
    '$restorePointCreated = $true'
)) {
    if (-not $apply.Contains($required)) {
        throw "The apply result contract is missing: $required"
    }
}

$skipped = & {
    param($ProductionScript)
    . (Get-ProductionFunctionScriptBlock -ScriptPath $ProductionScript -Name 'Get-SkippedRegistrySettings')
    $state = [pscustomobject]@{
        Registry = @(
            [pscustomobject]@{ Path = 'HKCU:\Software\Microsoft\GameBar'; Name = 'AllowAutoGameMode'; ChangedByUs = $true },
            [pscustomobject]@{ Path = 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh'; Name = 'AllowNewsAndInterests'; ChangedByUs = $false; Conflict = 'AccessDenied' },
            [pscustomobject]@{ Path = 'HKCU:\Software\X'; Name = 'Changed'; ChangedByUs = $false; Conflict = 'ValueChangedBeforeWrite' }
        )
    }
    , @(Get-SkippedRegistrySettings -State $state)
} $applyPath
if ($skipped.Count -ne 1 -or $skipped[0] -cne 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh::AllowNewsAndInterests') {
    throw "Get-SkippedRegistrySettings must list only refused writes, got: $($skipped -join '; ')"
}

# 2. The app reads that result JSON (as ConvertTo-Json writes it) and turns it
#    into plain-language notes.
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($ApplicationPath))
$allStatic = [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static
$notesType = $assembly.GetType('Boostix.OptimizationApplyNotes', $true)
$readSkipped = $notesType.GetMethod('ReadSkippedSettings', $allStatic)
$readRestorePoint = $notesType.GetMethod('ReadRestorePointCreated', $allStatic)
$describe = $notesType.GetMethod('Describe', $allStatic)
if (-not $readSkipped -or -not $readRestorePoint -or -not $describe) {
    throw 'The apply-notes API was not compiled.'
}

$one = [ordered]@{ Status = 'Applied'; SkippedSettings = @('HKLM:\SOFTWARE\Policies\Microsoft\Dsh::AllowNewsAndInterests'); RestorePointCreated = $false } | ConvertTo-Json -Depth 10
$two = [ordered]@{ Status = 'Applied'; SkippedSettings = @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize::EnableTransparency', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Dsh::IsPrelaunchEnabled'); RestorePointCreated = $true } | ConvertTo-Json -Depth 10
$none = [ordered]@{ Status = 'Applied'; SkippedSettings = @(); RestorePointCreated = $true } | ConvertTo-Json -Depth 10
$legacy = [ordered]@{ Status = 'Applied'; StateFile = 'C:\ProgramData\x.json' } | ConvertTo-Json -Depth 10

$oneSkipped = @($readSkipped.Invoke($null, [object[]]@([string]$one)))
if ($oneSkipped.Count -ne 1 -or $oneSkipped[0] -cne 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh::AllowNewsAndInterests') {
    throw "A single skipped setting was not read back: $($oneSkipped -join '; ')"
}
if (@($readSkipped.Invoke($null, [object[]]@([string]$two))).Count -ne 2 -or
    @($readSkipped.Invoke($null, [object[]]@([string]$none))).Count -ne 0 -or
    @($readSkipped.Invoke($null, [object[]]@([string]$legacy))).Count -ne 0) {
    throw 'Skipped settings were not read correctly for two, zero or a legacy result.'
}
if ([bool]$readRestorePoint.Invoke($null, [object[]]@([string]$one)) -or
    -not [bool]$readRestorePoint.Invoke($null, [object[]]@([string]$two)) -or
    -not [bool]$readRestorePoint.Invoke($null, [object[]]@([string]$legacy))) {
    throw 'RestorePointCreated must be read as written and default to true when absent.'
}

$widgets = Convert-UiName '0YTQvtC90L7QstGL0LUg0LLQuNC00LbQtdGC0Ys='
$refused = Convert-UiName 'V2luZG93cyDQvdC1INGA0LDQt9GA0LXRiNC40LvQsCDQuNC30LzQtdC90LjRgtGM'
$noRestorePoint = Convert-UiName '0KLQvtGH0LrQsCDQstC+0YHRgdGC0LDQvdC+0LLQu9C10L3QuNGPIFdpbmRvd3Mg0L3QtSDRgdC+0LfQtNCw0L3QsA=='
$notes = @($describe.Invoke($null, [object[]]@([string[]]$oneSkipped, $false)))
$joined = $notes -join "`n"
if (-not $joined.Contains($refused) -or -not $joined.Contains($widgets) -or -not $joined.Contains($noRestorePoint)) {
    throw "The notes do not explain the skipped widgets setting and the missing restore point:`n$joined"
}
if ($joined.Contains('HKLM') -or $joined.Contains('AllowNewsAndInterests')) {
    throw 'The notes must name the setting in plain language, not as a registry path.'
}
$quiet = @($describe.Invoke($null, [object[]]@([string[]]@(), $true)))
if ($quiet.Count -ne 0) {
    throw 'A clean apply must not show any notes.'
}

# 3. The reboot card shows the notes (demo flow, no system changes).
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$applyName = Convert-UiName '0J/RgNC+0LTQvtC70LbQuNGC0Ywg0Lgg0L/RgNC40LzQtdC90LjRgtGMINC+0L/RgtC40LzQuNC30LDRhtC40Y4g0L/RgNC+0LjQt9Cy0L7QtNC40YLQtdC70YzQvdC+0YHRgtC4'
$rebootTitle = Convert-UiName '0J3Rg9C20L3QsCDQv9C10YDQtdC30LDQs9GA0YPQt9C60LA='
$process = Start-Process -FilePath $ApplicationPath -PassThru -ArgumentList @(
    '--demo',
    '--demo-setup',
    '--demo-setup-notes',
    ('--test-instance=' + [Guid]::NewGuid().ToString('N')))
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ($process.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
    }
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'Boostix did not open its window for the demo setup flow.'
    }
    $window = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)

    function Find-VisibleText {
        param([string]$Fragment, [int]$TimeoutSeconds = 10)
        $condition = New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Text)
        $until = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        do {
            foreach ($text in $window.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)) {
                if ($text.Current.Name.Contains($Fragment) -and -not $text.Current.IsOffscreen) { return $text }
            }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $until)
        return $null
    }

    $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $applyName)
    $applyButton = $null
    $until = [DateTime]::UtcNow.AddSeconds(10)
    while (-not $applyButton -and [DateTime]::UtcNow -lt $until) {
        $applyButton = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
        if (-not $applyButton) { Start-Sleep -Milliseconds 100 }
    }
    if (-not $applyButton) {
        throw 'The setup consent card with the apply button did not appear.'
    }
    $applyButton.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()

    if (-not (Find-VisibleText -Fragment $rebootTitle -TimeoutSeconds 15)) {
        throw 'The reboot-required card did not appear after the demo apply.'
    }
    $skippedNote = Find-VisibleText -Fragment $widgets -TimeoutSeconds 3
    if (-not $skippedNote -or -not $skippedNote.Current.Name.Contains($refused)) {
        throw 'The reboot-required card does not tell the user that Windows refused the widgets setting.'
    }
    if (-not (Find-VisibleText -Fragment $noRestorePoint -TimeoutSeconds 3)) {
        throw 'The reboot-required card does not mention the missing restore point.'
    }
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
}

Write-Host 'Setup skipped-settings test passed: the apply result lists refused settings and the restore point outcome, and the reboot card explains them in plain language.' -ForegroundColor Green
