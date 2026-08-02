[CmdletBinding()]
param(
    [string]$ApplicationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This UI regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $ApplicationPath) {
    $ApplicationPath = Join-Path $projectRoot 'dist\Boostix.exe'
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Convert-UiName {
    param([string]$Base64)
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Base64))
}

function Wait-ForCenterWindow {
    param(
        [Diagnostics.Process]$Process,
        [int]$TimeoutMilliseconds
    )

    $condition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::AutomationIdProperty,
        'Boostix.Center.Tab.Readiness')
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "Portable application exited unexpectedly with code $($Process.ExitCode)."
        }

        $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll(
            [Windows.Automation.TreeScope]::Descendants,
            $condition)
        foreach ($window in $windows) {
            if ($window.Current.ProcessId -eq $Process.Id -and
                -not $window.Current.IsOffscreen) {
                $Process.Refresh()
                if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
                    return [Windows.Automation.AutomationElement]::FromHandle(
                        $Process.MainWindowHandle)
                }
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'Portable Boost Center window did not appear.'
}

$readinessName = Convert-UiName '0LPQvtGC0L7QstC90L7RgdGC0Yw='
$reportName = Convert-UiName '0L7RgtGH0ZHRgg=='
$settingsName = Convert-UiName '0L3QsNGB0YLRgNC+0LnQutC4'
$benchmarkName = Convert-UiName '0JfQsNC/0YPRgdGC0LjRgtGMINGC0LXRgdGCIEZQUyDQvdCwIDYwINGB0LXQutGD0L3QtA=='
$checkBeforeBoostName = Convert-UiName '0L/RgNC+0LLQtdGA0LrQsCDQv9C10YDQtdC0INC30LDQv9GD0YHQutC+0Lw='
$restoreName = Convert-UiName '0J7RgtC60YDRi9GC0Ywg0LHQtdC30L7Qv9Cw0YHQvdC+0LUg0LLQvtGB0YHRgtCw0L3QvtCy0LvQtdC90LjQtSDRgdC40YHRgtC10LzQvdGL0YUg0L3QsNGB0YLRgNC+0LXQug=='

function Wait-ForElement {
    param(
        [Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [int]$TimeoutMilliseconds
    )

    $condition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $element = $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
        if ($element -and
            $element.Current.IsEnabled -and
            -not $element.Current.IsOffscreen) {
            return $element
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Boost Center element did not appear: $Name"
}

function Invoke-Element {
    param([Windows.Automation.AutomationElement]$Element)

    $pattern = $Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
    ([Windows.Automation.InvokePattern]$pattern).Invoke()
}

$process = $null
try {
    $testInstance = '--test-instance=' + [Guid]::NewGuid().ToString('N')
    $process = Start-Process `
        -FilePath $ApplicationPath `
        -ArgumentList '--skip-setup', '--demo', '--demo-center', $testInstance `
        -PassThru

    $window = Wait-ForCenterWindow -Process $process -TimeoutMilliseconds 20000
    $readiness = Wait-ForElement -Root $window -Name $readinessName -TimeoutMilliseconds 20000
    $report = Wait-ForElement -Root $window -Name $reportName -TimeoutMilliseconds 10000
    $settings = Wait-ForElement -Root $window -Name $settingsName -TimeoutMilliseconds 10000
    foreach ($tab in @($readiness, $report, $settings)) {
        if (-not $tab.Current.IsKeyboardFocusable) {
            throw "Boost Center tab is not keyboard-focusable: $($tab.Current.Name)"
        }
    }
    Invoke-Element -Element $report
    [void](Wait-ForElement -Root $window -Name $benchmarkName -TimeoutMilliseconds 10000)
    Invoke-Element -Element $settings
    $checkBeforeBoost = Wait-ForElement -Root $window -Name $checkBeforeBoostName -TimeoutMilliseconds 10000
    if (-not $checkBeforeBoost.Current.IsKeyboardFocusable) {
        throw 'Boost Center setting toggle is not keyboard-focusable.'
    }
    [void]$checkBeforeBoost.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    [void](Wait-ForElement -Root $window -Name $restoreName -TimeoutMilliseconds 10000)

    Invoke-Element -Element $report
    Invoke-Element -Element $readiness
    Invoke-Element -Element $settings
    [void](Wait-ForElement -Root $window -Name $checkBeforeBoostName -TimeoutMilliseconds 10000)
    [void](Wait-ForElement -Root $window -Name $restoreName -TimeoutMilliseconds 10000)

    Write-Host 'Boost Center UI navigation test passed.' -ForegroundColor Green
}
finally {
    if ($process -and -not $process.HasExited) {
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(3000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
}
