[CmdletBinding()]
param(
    [string]$ApplicationPath
)

# Ctrl+Alt+B switches Boost on and off while a game has focus, the tray icon
# offers the same action, and both are user settings that persist.

$ErrorActionPreference = 'Stop'

# A window taken the moment it appears can stay empty for UI Automation:
# WPF has not published its content yet, and the element keeps that answer.
# Each look takes the window again from its handle.
function Get-FreshRoot {
    param($Root)
    try {
        $handle = $Root.Current.NativeWindowHandle
        if ($handle -ne 0) {
            return [Windows.Automation.AutomationElement]::FromHandle([IntPtr]$handle)
        }
    }
    catch { }
    return $Root
}
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
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
    private const uint KeyUp = 0x0002;
    public static bool IsControlAltBFree()
    {
        const int probeId = 0xB0B;
        if (!RegisterHotKey(IntPtr.Zero, probeId, 0x0001 | 0x0002, 0x42))
        {
            return false;
        }
        UnregisterHotKey(IntPtr.Zero, probeId);
        return true;
    }
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
        $button = (Get-FreshRoot $Window).FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
        if ($button -and $button.Current.IsEnabled -and
            $button.Current.Name.StartsWith($Prefix, [StringComparison]::CurrentCultureIgnoreCase)) {
            return $true
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    return $false
}

# A global hotkey goes to whoever registered it first. When the developer's
# own Boostix holds Ctrl+Alt+B, pressing it would switch Boost on and off in
# that real copy (it did once: a two-second Manual session with the test
# window as the game), so the press is skipped. CI has no other copy.
$hotkeyFree = [BoostixHotkeyInput]::IsControlAltBFree()

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

    if ($hotkeyFree) {
        # Global hotkeys fire regardless of which window has focus.
        [BoostixHotkeyInput]::PressControlAltB()
        if (-not (Wait-ForBoostName -Window $window -Prefix $deactivatePrefix -TimeoutSeconds 8)) {
            throw 'Ctrl+Alt+B did not switch Boost on.'
        }
        [BoostixHotkeyInput]::PressControlAltB()
        if (-not (Wait-ForBoostName -Window $window -Prefix $activatePrefix -TimeoutSeconds 8)) {
            throw 'Ctrl+Alt+B did not switch Boost off.'
        }
    }
    else {
        Write-Warning 'Ctrl+Alt+B belongs to another program (usually a running Boostix), so it was not pressed. Close that program to test the shortcut itself.'
    }

    # The two new settings sit after the existing ones.
    $tabCondition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, 'Boostix.Center.Tab.Settings')
    $tab = (Get-FreshRoot $window).FindFirst([Windows.Automation.TreeScope]::Descendants, $tabCondition)
    $tab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $settingCondition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, 'Boostix.Center.Setting.2')
    $until = [DateTime]::UtcNow.AddSeconds(5)
    $hotkeySetting = $null
    while (-not $hotkeySetting -and [DateTime]::UtcNow -lt $until) {
        $hotkeySetting = (Get-FreshRoot $window).FindFirst([Windows.Automation.TreeScope]::Descendants, $settingCondition)
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

if ($hotkeyFree) {
    Write-Host 'Tray/hotkey test passed: Ctrl+Alt+B switches Boost on and off, and the hotkey setting is on by default.' -ForegroundColor Green
}
else {
    Write-Host 'Tray/hotkey test passed without pressing Ctrl+Alt+B (held by another program); the hotkey setting is on by default.' -ForegroundColor Yellow
}
