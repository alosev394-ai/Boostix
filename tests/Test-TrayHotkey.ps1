[CmdletBinding()]
param(
    [string]$ApplicationPath
)

# Ctrl+Alt+B switches Boost on and off while a game has focus, the tray icon
# offers the same action, and both are user settings that persist.

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

function Convert-UiName {
    param([string]$Base64)
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Base64))
}

$program = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\Program.cs'))
foreach ($required in @(
    '"GlobalHotkey=" + centerSettings.GlobalHotkey',
    '"MinimizeToTray=" + centerSettings.MinimizeToTray',
    'BoostHotkey.Register(windowHandle)',
    'HasLaunchArgument(launchArguments, "--demo-tray")',
    'trayIcon.Dispose();'
)) {
    if (-not $program.Contains($required)) {
        throw "The tray/hotkey contract is missing: $required"
    }
}
$build = [IO.File]::ReadAllText((Join-Path $projectRoot 'build.ps1'))
foreach ($required in @('"$projectRoot\Boostix\TrayIntegration.cs"', "'/reference:System.Windows.Forms.dll'", "'/reference:System.Drawing.dll'")) {
    if (-not $build.Contains($required)) {
        throw "The app build is missing $required"
    }
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
if (-not ([Management.Automation.PSTypeName]'BoostixHotkeyInput').Type) {
    # Virtual-key input: SendKeys maps characters through the active keyboard
    # layout, and 'b' does not exist on a Russian layout.
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class BoostixHotkeyInput
{
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    private const uint KeyUp = 0x0002;
    public static void PressControlAltB()
    {
        keybd_event(0x11, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        keybd_event(0x42, 0, 0, UIntPtr.Zero);
        keybd_event(0x42, 0, KeyUp, UIntPtr.Zero);
        keybd_event(0x12, 0, KeyUp, UIntPtr.Zero);
        keybd_event(0x11, 0, KeyUp, UIntPtr.Zero);
    }
}
'@
}
$activatePrefix = Convert-UiName '0JDQutGC0LjQstC40YDQvtCy0LDRgtGM'
$deactivatePrefix = Convert-UiName '0J7RgtC60LvRjtGH0LjRgtGM'

function Wait-ForBoostName {
    param($Window, [string]$Prefix, [int]$TimeoutSeconds)
    $condition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, 'Boostix.Activate')
    $until = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $button = $Window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
        if ($button -and $button.Current.IsEnabled -and
            $button.Current.Name.StartsWith($Prefix, [StringComparison]::CurrentCultureIgnoreCase)) {
            return $true
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    return $false
}

$process = Start-Process -FilePath $ApplicationPath -PassThru -ArgumentList @(
    '--skip-setup',
    '--demo',
    '--demo-target',
    '--demo-tray',
    ('--test-instance=' + [Guid]::NewGuid().ToString('N')))
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
    if (-not (Wait-ForBoostName -Window $window -Prefix $activatePrefix -TimeoutSeconds 10)) {
        throw 'The Boost control did not become ready.'
    }

    # Global hotkeys fire regardless of which window has focus.
    [BoostixHotkeyInput]::PressControlAltB()
    if (-not (Wait-ForBoostName -Window $window -Prefix $deactivatePrefix -TimeoutSeconds 8)) {
        throw 'Ctrl+Alt+B did not switch Boost on (the shortcut may be taken by another program).'
    }
    [BoostixHotkeyInput]::PressControlAltB()
    if (-not (Wait-ForBoostName -Window $window -Prefix $activatePrefix -TimeoutSeconds 8)) {
        throw 'Ctrl+Alt+B did not switch Boost off.'
    }

    # The two new settings sit after the existing ones.
    $tabCondition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, 'Boostix.Center.Tab.Settings')
    $tab = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $tabCondition)
    $tab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $settingCondition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, 'Boostix.Center.Setting.8')
    $until = [DateTime]::UtcNow.AddSeconds(5)
    $hotkeySetting = $null
    while (-not $hotkeySetting -and [DateTime]::UtcNow -lt $until) {
        $hotkeySetting = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $settingCondition)
        if (-not $hotkeySetting) { Start-Sleep -Milliseconds 100 }
    }
    if (-not $hotkeySetting -or -not $hotkeySetting.Current.Name.Contains('Ctrl+Alt+B')) {
        throw 'The hotkey setting is missing from the Settings page.'
    }
    $toggleState = $hotkeySetting.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
    if ($toggleState -ne [Windows.Automation.ToggleState]::On) {
        throw 'The hotkey setting should be on by default.'
    }
}
finally {
    if ($process -and -not $process.HasExited) {
        # A graceful close disposes the tray icon instead of leaving a ghost.
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

Write-Host 'Tray/hotkey test passed: Ctrl+Alt+B switches Boost on and off, and the hotkey setting is on by default.' -ForegroundColor Green
