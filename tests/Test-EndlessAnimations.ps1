[CmdletBinding()]
param(
    [string]$ApplicationPath
)

# An endless animation keeps the processor busy after its screen is hidden or
# replaced: WPF ticks it even on a collapsed or detached element. The update
# check left its progress bar running this way, and Boostix spent 40% of a core
# with its window open and up to 2.5% at the clock for the rest of the day.

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

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase

$assembly = [Reflection.Assembly]::LoadFrom($ApplicationPath)
$private = [Reflection.BindingFlags]'Instance,NonPublic'

function Get-Animated {
    param($Root)
    $found = New-Object 'System.Collections.Generic.List[object]'
    $pending = New-Object 'System.Collections.Generic.Stack[object]'
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        $node = $pending.Pop()
        if ($node -is [Windows.UIElement]) {
            if ($node.HasAnimatedProperties) { $found.Add($node) }
            $transform = $node.RenderTransform
            if ($transform -is [Windows.Media.Animation.Animatable] -and $transform.HasAnimatedProperties) {
                $found.Add($transform)
            }
        }
        if ($node -is [Windows.DependencyObject]) {
            foreach ($child in [Windows.LogicalTreeHelper]::GetChildren($node)) {
                if ($child -is [Windows.DependencyObject]) { $pending.Push($child) }
            }
        }
    }
    return $found.ToArray()
}

function Test-Overlay {
    param([string]$TypeName, [string]$ShowMethod, [object[]]$ShowArguments)
    $type = $assembly.GetType($TypeName, $true)
    $window = [Windows.Window]::new()
    $overlay = $type.GetConstructors([Reflection.BindingFlags]'Instance,Public,NonPublic')[0].Invoke(
        [object[]]@($window, [string[]]@(), $null, $null))
    $show = $type.GetMethod($ShowMethod, $private)
    $hide = $type.GetMethod('HideFlow', $private)

    $show.Invoke($overlay, $ShowArguments)
    $running = @(Get-Animated $overlay)
    if (-not [Windows.SystemParameters]::ClientAreaAnimation) {
        "${TypeName}: Windows animations are off here, nothing to stop."
        return
    }
    if ($running.Count -eq 0) {
        throw "${TypeName}: the working screen did not start its progress animation."
    }

    # Replacing the screen detaches the old bar; its animation must end with it.
    $show.Invoke($overlay, $ShowArguments)
    foreach ($old in $running) {
        if ($old.HasAnimatedProperties) {
            throw "${TypeName}: a replaced working screen keeps animating."
        }
    }

    $hide.Invoke($overlay, @())
    $left = @(Get-Animated $overlay)
    if ($left.Count -ne 0) {
        throw "${TypeName}: $($left.Count) animation(s) keep running after the screen is hidden."
    }
    "${TypeName}: the progress animation stops when the screen is replaced or hidden."
}

Test-Overlay 'Boostix.UpdateFlowOverlay' 'ShowChecking' @()
Test-Overlay 'Boostix.OptimizationFlowOverlay' 'ShowWorking' @('Title', 'Description', $null)

'Endless animation test passed.'
