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
Add-Type -AssemblyName System.Windows.Forms
if (-not ([Management.Automation.PSTypeName]'BoostixUiDpiNative').Type) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class BoostixUiDpiNative
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    public static uint Read(IntPtr window)
    {
        try
        {
            uint dpi = GetDpiForWindow(window);
            return dpi >= 48 && dpi <= 768 ? dpi : 96;
        }
        catch (EntryPointNotFoundException)
        {
            return 96;
        }
    }
}
'@
}

if (-not ([Management.Automation.PSTypeName]'BoostixUiSelectionEventRecorder').Type) {
    $automationClient =
        [Windows.Automation.AutomationElement].Assembly.Location
    $automationTypes =
        [Windows.Automation.Provider.IRawElementProviderSimple].Assembly.Location
    Add-Type -ReferencedAssemblies @($automationClient, $automationTypes) `
        -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Windows.Automation;

public static class BoostixUiSelectionEventRecorder
{
    private static readonly ConcurrentQueue<string> Events =
        new ConcurrentQueue<string>();
    private static AutomationElement root;
    private static AutomationEventHandler handler;

    public static int Count { get { return Events.Count; } }

    public static void Start(AutomationElement element)
    {
        Stop();
        string ignored;
        while (Events.TryDequeue(out ignored)) { }
        root = element;
        handler = OnEvent;
        Automation.AddAutomationEventHandler(
            SelectionItemPattern.ElementSelectedEvent,
            root,
            TreeScope.Subtree,
            handler);
    }

    public static void Stop()
    {
        if (root != null && handler != null)
        {
            Automation.RemoveAutomationEventHandler(
                SelectionItemPattern.ElementSelectedEvent,
                root,
                handler);
        }
        root = null;
        handler = null;
    }

    private static void OnEvent(object sender, AutomationEventArgs args)
    {
        AutomationElement element = sender as AutomationElement;
        if (element != null)
        {
            Events.Enqueue(element.Current.AutomationId);
        }
    }
}
'@
}

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

        $tabs = [Windows.Automation.AutomationElement]::RootElement.FindAll(
            [Windows.Automation.TreeScope]::Descendants,
            $condition)
        foreach ($tab in $tabs) {
            if ($tab.Current.ProcessId -eq $Process.Id -and
                -not $tab.Current.IsOffscreen) {
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

function Wait-ForElementById {
    param(
        [Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [int]$TimeoutMilliseconds = 10000
    )

    $condition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $element = $Root.FindFirst(
            [Windows.Automation.TreeScope]::Descendants,
            $condition)
        if ($element -and
            $element.Current.IsEnabled -and
            -not $element.Current.IsOffscreen) {
            return $element
        }
        Start-Sleep -Milliseconds 75
    }
    throw "Boost Center element did not appear: $AutomationId"
}

function Get-SelectionItemPattern {
    param([Windows.Automation.AutomationElement]$Element)

    $pattern = $null
    if (-not $Element.TryGetCurrentPattern(
            [Windows.Automation.SelectionItemPattern]::Pattern,
            [ref]$pattern)) {
        throw "Tab does not expose SelectionItemPattern: $($Element.Current.AutomationId)"
    }
    return [Windows.Automation.SelectionItemPattern]$pattern
}

function Get-SelectionPattern {
    param([Windows.Automation.AutomationElement]$Element)

    $pattern = $null
    if (-not $Element.TryGetCurrentPattern(
            [Windows.Automation.SelectionPattern]::Pattern,
            [ref]$pattern)) {
        throw "Tab container does not expose SelectionPattern: $($Element.Current.AutomationId)"
    }
    return [Windows.Automation.SelectionPattern]$pattern
}

function Assert-SameAutomationElement {
    param(
        [Windows.Automation.AutomationElement]$Actual,
        [Windows.Automation.AutomationElement]$Expected,
        [string]$Description
    )

    if (-not $Actual -or -not $Expected -or
        -not [Windows.Automation.Automation]::Compare($Actual, $Expected)) {
        throw "$Description does not reference the Boost Center tab container."
    }
}

function Assert-ContainerSelection {
    param(
        [Windows.Automation.SelectionPattern]$Pattern,
        [Windows.Automation.AutomationElement]$ExpectedTab
    )

    $selection = @($Pattern.Current.GetSelection())
    if ($selection.Count -ne 1) {
        throw "Tab container returned $($selection.Count) selected items; expected exactly one."
    }
    if (-not [Windows.Automation.Automation]::Compare(
            $selection[0],
            $ExpectedTab)) {
        throw "Tab container selection does not match $($ExpectedTab.Current.AutomationId)."
    }
}

function Wait-ForSelectedTab {
    param(
        [Windows.Automation.AutomationElement]$Tab,
        [int]$TimeoutMilliseconds = 5000
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $pattern = Get-SelectionItemPattern $Tab
        if ($pattern.Current.IsSelected) {
            return
        }
        Start-Sleep -Milliseconds 50
    }
    throw "Tab was not selected: $($Tab.Current.AutomationId)"
}

function Wait-ForFocusedAutomationId {
    param(
        [Diagnostics.Process]$Process,
        [string]$AutomationId,
        [int]$TimeoutMilliseconds = 5000
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $focused = [Windows.Automation.AutomationElement]::FocusedElement
        if ($focused -and
            $focused.Current.ProcessId -eq $Process.Id -and
            $focused.Current.AutomationId -ceq $AutomationId) {
            return
        }
        Start-Sleep -Milliseconds 50
    }
    $current = [Windows.Automation.AutomationElement]::FocusedElement
    $currentId = if ($current) { $current.Current.AutomationId } else { '<none>' }
    throw "Keyboard focus was '$currentId', expected '$AutomationId'."
}

function Wait-ForEventCount {
    param(
        [int]$Expected,
        [int]$TimeoutMilliseconds = 3000
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ([BoostixUiSelectionEventRecorder]::Count -ge $Expected) {
            return
        }
        Start-Sleep -Milliseconds 50
    }
    throw (
        "Observed $([BoostixUiSelectionEventRecorder]::Count) tab " +
        "selection events; expected $Expected.")
}

function Select-Tab {
    param([Windows.Automation.AutomationElement]$Tab)

    $pattern = Get-SelectionItemPattern $Tab
    $pattern.Select()
    Wait-ForSelectedTab $Tab
}

function Convert-PixelsToDip {
    param(
        [double]$Pixels,
        [uint32]$Dpi
    )
    # Windows PowerShell 5.1 is system-DPI virtualized. UI Automation bounds
    # are therefore reported in the caller's logical coordinates, while the
    # target process is PerMonitorV2-aware. Undo that virtualization here.
    return $Pixels * $Dpi / 96.0
}

$tabContracts = @(
    @{ Id = 'Boostix.Center.Tab.Readiness'; Name = '0KHQldCQ0J3QoQ=='; PageElement = 'Boostix.Center.SelectTarget' },
    @{ Id = 'Boostix.Center.Tab.Impact'; Name = '0JLQm9CY0K/QndCY0JU='; PageElement = 'Boostix.Center.ImpactScan' },
    @{ Id = 'Boostix.Center.Tab.Report'; Name = '0K3QpNCk0JXQmtCi'; PageElement = 'Boostix.Center.ReportBenchmark' },
    @{ Id = 'Boostix.Center.Tab.Profiles'; Name = '0J/QoNCe0KTQmNCb0Jg='; PageElement = 'Boostix.Center.ProfileAdd' },
    @{ Id = 'Boostix.Center.Tab.Settings'; Name = '0J3QkNCh0KLQoNCe0JnQmtCY'; PageElement = 'Boostix.Center.Setting.0' }
)

$process = $null
$selectionEventsStarted = $false
$tabContainer = $null
try {
    $testInstance = '--test-instance=' + [Guid]::NewGuid().ToString('N')
    $process = Start-Process `
        -FilePath $ApplicationPath `
        -ArgumentList '--skip-setup', '--demo', '--demo-center', $testInstance `
        -PassThru

    $window = Wait-ForCenterWindow -Process $process -TimeoutMilliseconds 20000
    $process.Refresh()
    $dpi = [BoostixUiDpiNative]::Read($process.MainWindowHandle)
    $windowWidthDip = Convert-PixelsToDip `
        $window.Current.BoundingRectangle.Width `
        $dpi
    if ($windowWidthDip -lt 618 -or $windowWidthDip -gt 622) {
        throw (
            "Boost Center width was $windowWidthDip DIP " +
            "($($window.Current.BoundingRectangle.Width) px at $dpi DPI); " +
            'expected approximately 620 DIP.')
    }

    $tabContainer = Wait-ForElementById `
        $window `
        'Boostix.Center.Tabs' `
        15000
    if ($tabContainer.Current.ControlType -ne
        [Windows.Automation.ControlType]::Tab) {
        throw "Boost Center tab container has control type $($tabContainer.Current.ControlType.ProgrammaticName)."
    }
    $containerSelection = Get-SelectionPattern $tabContainer
    if ($containerSelection.Current.CanSelectMultiple) {
        throw 'Boost Center tab container incorrectly permits multiple selection.'
    }
    if (-not $containerSelection.Current.IsSelectionRequired) {
        throw 'Boost Center tab container does not require a selected tab.'
    }

    $tabs = @()
    foreach ($contract in $tabContracts) {
        $tab = Wait-ForElementById $window $contract.Id 15000
        $tabs += $tab
        if (-not $tab.Current.IsKeyboardFocusable) {
            throw "Boost Center tab is not keyboard-focusable: $($contract.Id)"
        }
        if ($tab.Current.ControlType -ne
            [Windows.Automation.ControlType]::TabItem) {
            throw "Boost Center tab has control type $($tab.Current.ControlType.ProgrammaticName): $($contract.Id)"
        }
        $selectionItem = Get-SelectionItemPattern $tab
        Assert-SameAutomationElement `
            $selectionItem.Current.SelectionContainer `
            $tabContainer `
            "SelectionContainer for $($contract.Id)"

        $expectedName = Convert-UiName $contract.Name
        if ($tab.Current.Name.ToUpperInvariant() -cne $expectedName) {
            throw "Boost Center tab name was '$($tab.Current.Name)', expected '$expectedName'."
        }

        $bounds = $tab.Current.BoundingRectangle
        $heightDip = Convert-PixelsToDip $bounds.Height $dpi
        if ($heightDip -lt 38.5 -or $heightDip -gt 45.5) {
            throw "Boost Center tab height was $heightDip DIP; expected a 40..44 DIP target."
        }
        $windowBounds = $window.Current.BoundingRectangle
        if ($bounds.Left -lt $windowBounds.Left -or
            $bounds.Right -gt $windowBounds.Right) {
            throw "Boost Center tab is clipped: $($contract.Id)"
        }
    }

    Wait-ForSelectedTab $tabs[0]
    Assert-ContainerSelection $containerSelection $tabs[0]
    for ($index = 0; $index -lt $tabContracts.Count; $index++) {
        Select-Tab $tabs[$index]
        Assert-ContainerSelection $containerSelection $tabs[$index]
        $pageElement = Wait-ForElementById `
            $window `
            $tabContracts[$index].PageElement `
            10000
        $pageBounds = $pageElement.Current.BoundingRectangle
        $pageHeightDip = Convert-PixelsToDip $pageBounds.Height $dpi
        if ($pageHeightDip -lt 38.5) {
            throw (
                "Primary page action measured $pageHeightDip DIP and is " +
                "smaller than the 40 DIP contract: " +
                $tabContracts[$index].PageElement)
        }

        for ($other = 0; $other -lt $tabs.Count; $other++) {
            $selected = (Get-SelectionItemPattern $tabs[$other]).Current.IsSelected
            if ($selected -ne ($other -eq $index)) {
                throw "SelectionItem state is inconsistent after selecting $($tabContracts[$index].Id)."
            }
        }
    }

    # UIA must announce exactly one selected-element event for a real change,
    # and no event when the already-selected tab is selected again.
    Select-Tab $tabs[0]
    [BoostixUiSelectionEventRecorder]::Start($tabContainer)
    $selectionEventsStarted = $true
    Select-Tab $tabs[1]
    Wait-ForEventCount 1
    Start-Sleep -Milliseconds 300
    if ([BoostixUiSelectionEventRecorder]::Count -ne 1) {
        throw (
            'A single tab change raised ' +
            [BoostixUiSelectionEventRecorder]::Count +
            ' selected-element events.')
    }
    Select-Tab $tabs[1]
    Start-Sleep -Milliseconds 300
    if ([BoostixUiSelectionEventRecorder]::Count -ne 1) {
        throw 'Selecting the current tab raised a duplicate UIA selection event.'
    }
    [BoostixUiSelectionEventRecorder]::Stop()
    $selectionEventsStarted = $false

    # Verify the two keyboard navigation paths without depending on visual
    # animation timing: arrows move within the Tab control, Ctrl+Tab uses the
    # existing overlay command and both keep exactly one selected item.
    Select-Tab $tabs[0]
    $tabs[0].SetFocus()
    [Windows.Forms.SendKeys]::SendWait('{RIGHT}')
    Wait-ForSelectedTab $tabs[1]
    Assert-ContainerSelection $containerSelection $tabs[1]
    Start-Sleep -Milliseconds 350
    Wait-ForFocusedAutomationId $process $tabContracts[1].Id

    [Windows.Forms.SendKeys]::SendWait('^{TAB}')
    Wait-ForSelectedTab $tabs[2]
    Assert-ContainerSelection $containerSelection $tabs[2]
    Start-Sleep -Milliseconds 350
    Wait-ForFocusedAutomationId $process $tabContracts[2].Id

    $tabs[2].SetFocus()
    [Windows.Forms.SendKeys]::SendWait('{LEFT}')
    Wait-ForSelectedTab $tabs[1]
    Assert-ContainerSelection $containerSelection $tabs[1]
    Start-Sleep -Milliseconds 350
    Wait-ForFocusedAutomationId $process $tabContracts[1].Id

    # Escape closes the Center and returns focus to the saved opener rather
    # than leaving the keyboard on a collapsed page control.
    [Windows.Forms.SendKeys]::SendWait('{ESC}')
    Wait-ForFocusedAutomationId $process 'Boostix.OpenCenter' 5000

    Write-Host 'Boost Center 2.0 UIA navigation test passed.' -ForegroundColor Green
}
finally {
    if ($selectionEventsStarted) {
        try {
            [BoostixUiSelectionEventRecorder]::Stop()
        }
        catch { }
    }
    if ($process -and -not $process.HasExited) {
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(3000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
}
