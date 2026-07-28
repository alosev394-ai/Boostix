[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $projectRoot 'BoostixInstaller\Program.cs'
$brandSource = Join-Path $projectRoot 'ProductBrand.cs'
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'Boostix-UninstallCleanup-' + [Guid]::NewGuid().ToString('N'))
$harnessPath = Join-Path $temporaryRoot 'UninstallCleanupHarness.dll'
$junctions = New-Object 'Collections.Generic.List[string]'

function Get-DeepestException {
    param([Parameter(Mandatory = $true)][Exception]$Exception)

    $current = $Exception
    while ($current.InnerException) {
        $current = $current.InnerException
    }
    return $current
}

function Invoke-Cleanup {
    param(
        [Parameter(Mandatory = $true)][Reflection.MethodInfo]$Method,
        [Parameter(Mandatory = $true)][string]$Boundary,
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][string[]]$AllowedNames,
        [AllowNull()][string]$PreservedPath
    )

    $arguments = New-Object 'object[]' 4
    $arguments[0] = [string]$Boundary
    $arguments[1] = [string]$Target
    $arguments[2] = [string[]]$AllowedNames
    $arguments[3] = $PreservedPath
    return [bool]$Method.Invoke($null, $arguments)
}

function Assert-CleanupFails {
    param(
        [Parameter(Mandatory = $true)][Reflection.MethodInfo]$Method,
        [Parameter(Mandatory = $true)][string]$Boundary,
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][string[]]$AllowedNames,
        [Parameter(Mandatory = $true)][string]$Scenario,
        [AllowNull()][string]$PreservedPath
    )

    try {
        [void](Invoke-Cleanup `
            -Method $Method `
            -Boundary $Boundary `
            -Target $Target `
            -AllowedNames $AllowedNames `
            -PreservedPath $PreservedPath)
    }
    catch {
        $actual = Get-DeepestException -Exception $_.Exception
        if ($actual -is [IO.IOException] -or
            $actual -is [UnauthorizedAccessException] -or
            $actual -is [System.ComponentModel.Win32Exception]) {
            return
        }
        throw "$Scenario failed with $($actual.GetType().FullName): $($actual.Message)"
    }
    throw "$Scenario unexpectedly succeeded."
}

function New-TestJunction {
    param(
        [Parameter(Mandatory = $true)][string]$Link,
        [Parameter(Mandatory = $true)][string]$Target
    )

    $command = 'mklink /J "{0}" "{1}"' -f $Link, $Target
    $output = & $env:ComSpec /d /c $command 2>&1
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $Link)) {
        throw "Could not create an adversarial junction: $($output -join ' ')"
    }
    $junctions.Add($Link)
}

function Try-NewTestFileSymlink {
    param(
        [Parameter(Mandatory = $true)][string]$Link,
        [Parameter(Mandatory = $true)][string]$Target
    )

    $command = 'mklink "{0}" "{1}"' -f $Link, $Target
    $previousErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & $env:ComSpec /d /c $command 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorAction
    }
    if ($exitCode -ne 0 -or -not (Test-Path -LiteralPath $Link)) {
        return $false
    }
    return $true
}

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
        throw "C# compiler was not found: $compiler"
    }

    $compilerOutput = & $compiler `
        /nologo `
        /target:library `
        /utf8output `
        "/out:$harnessPath" `
        /reference:System.dll `
        /reference:System.Core.dll `
        /reference:System.Drawing.dll `
        /reference:System.Windows.Forms.dll `
        /reference:System.Security.dll `
        $brandSource `
        $sourcePath 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Installer source did not compile:`r`n$($compilerOutput -join [Environment]::NewLine)"
    }

    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($harnessPath))
    $engineType = $assembly.GetType(
        'BoostixSetup.InstallerEngine',
        $true,
        $false)
    $flags = [Reflection.BindingFlags]::NonPublic -bor
        [Reflection.BindingFlags]::Static
    $deleteTree = $engineType.GetMethod(
        'DeleteAllowlistedDirectoryTree',
        $flags)
    if (-not $deleteTree) {
        throw 'The compiled installer does not contain its protected deletion helper.'
    }

    $allowedDataNames = [string[]]@('Boostix', 'MajesticBoost')
    $boundary = Join-Path $temporaryRoot 'LocalAppData'
    $outside = Join-Path $temporaryRoot 'Outside'
    [IO.Directory]::CreateDirectory($boundary) | Out-Null
    [IO.Directory]::CreateDirectory($outside) | Out-Null
    $outsideSentinel = Join-Path $outside 'outside-sentinel.txt'
    [IO.File]::WriteAllText($outsideSentinel, 'must survive')

    # A normal Boostix tree is removed while a sibling remains untouched.
    $safeTarget = Join-Path $boundary 'Boostix'
    $safeNested = Join-Path $safeTarget 'Logs\Nested'
    [IO.Directory]::CreateDirectory($safeNested) | Out-Null
    [IO.File]::WriteAllText((Join-Path $safeTarget 'state.json'), '{}')
    [IO.File]::WriteAllText((Join-Path $safeNested 'session.log'), 'log')
    $sibling = Join-Path $boundary 'Unrelated'
    [IO.Directory]::CreateDirectory($sibling) | Out-Null
    $siblingSentinel = Join-Path $sibling 'keep.txt'
    [IO.File]::WriteAllText($siblingSentinel, 'keep')
    if (-not (Invoke-Cleanup `
        -Method $deleteTree `
        -Boundary $boundary `
        -Target $safeTarget `
        -AllowedNames $allowedDataNames `
        -PreservedPath $null)) {
        throw 'A normal Boostix data tree was not completely deleted.'
    }
    if ((Test-Path -LiteralPath $safeTarget) -or
        -not (Test-Path -LiteralPath $siblingSentinel)) {
        throw 'Allowlisted cleanup crossed its direct-child target boundary.'
    }

    # The legacy MajesticBoost data path remains intentionally supported.
    $legacyTarget = Join-Path $boundary 'MajesticBoost'
    [IO.Directory]::CreateDirectory($legacyTarget) | Out-Null
    [IO.File]::WriteAllText((Join-Path $legacyTarget 'legacy.log'), 'legacy')
    if (-not (Invoke-Cleanup `
        -Method $deleteTree `
        -Boundary $boundary `
        -Target $legacyTarget `
        -AllowedNames $allowedDataNames `
        -PreservedPath $null) -or
        (Test-Path -LiteralPath $legacyTarget)) {
        throw 'The allowlisted legacy cleanup path was not removed.'
    }

    # A sibling name cannot be smuggled into the cleanup API.
    Assert-CleanupFails `
        -Method $deleteTree `
        -Boundary $boundary `
        -Target $sibling `
        -AllowedNames $allowedDataNames `
        -Scenario 'Disallowed direct-child cleanup'
    if (-not (Test-Path -LiteralPath $siblingSentinel)) {
        throw 'A disallowed sibling was modified.'
    }

    # Start Menu cleanup uses a separate, explicit public/legacy name allowlist.
    $allowedProductNames = [string[]]@('Boostix', 'Majestic Boost')
    $programsBoundary = Join-Path $temporaryRoot 'CommonPrograms'
    $legacyMenu = Join-Path $programsBoundary 'Majestic Boost'
    [IO.Directory]::CreateDirectory($legacyMenu) | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $legacyMenu 'Majestic Boost.lnk'),
        'shortcut-placeholder')
    if (-not (Invoke-Cleanup `
        -Method $deleteTree `
        -Boundary $programsBoundary `
        -Target $legacyMenu `
        -AllowedNames $allowedProductNames `
        -PreservedPath $null) -or
        (Test-Path -LiteralPath $legacyMenu)) {
        throw 'The allowlisted legacy Start Menu directory was not removed.'
    }

    # A junction at the target root must never redirect elevated deletion.
    $rootJunction = Join-Path $boundary 'Boostix'
    New-TestJunction -Link $rootJunction -Target $outside
    Assert-CleanupFails `
        -Method $deleteTree `
        -Boundary $boundary `
        -Target $rootJunction `
        -AllowedNames $allowedDataNames `
        -Scenario 'Root junction cleanup'
    if (-not (Test-Path -LiteralPath $outsideSentinel)) {
        throw 'Root junction cleanup deleted outside the target boundary.'
    }
    & $env:ComSpec /d /c ('rmdir "{0}"' -f $rootJunction) | Out-Null
    [void]$junctions.Remove($rootJunction)

    # The full tree is preflighted before deletion, so a nested junction leaves
    # both the external sentinel and ordinary in-tree content untouched.
    $nestedJunctionTarget = Join-Path $boundary 'Boostix'
    [IO.Directory]::CreateDirectory($nestedJunctionTarget) | Out-Null
    $ownedFile = Join-Path $nestedJunctionTarget 'owned.txt'
    [IO.File]::WriteAllText($ownedFile, 'owned')
    $nestedJunction = Join-Path $nestedJunctionTarget 'Redirected'
    New-TestJunction -Link $nestedJunction -Target $outside
    Assert-CleanupFails `
        -Method $deleteTree `
        -Boundary $boundary `
        -Target $nestedJunctionTarget `
        -AllowedNames $allowedDataNames `
        -Scenario 'Nested junction cleanup'
    if (-not (Test-Path -LiteralPath $outsideSentinel) -or
        -not (Test-Path -LiteralPath $ownedFile)) {
        throw 'Nested junction preflight was not fail-closed.'
    }
    & $env:ComSpec /d /c ('rmdir "{0}"' -f $nestedJunction) | Out-Null
    [void]$junctions.Remove($nestedJunction)
    [IO.Directory]::Delete($nestedJunctionTarget, $true)

    # Reparse points in the trusted boundary chain are rejected as well.
    $realBoundary = Join-Path $temporaryRoot 'RealBoundary'
    [IO.Directory]::CreateDirectory($realBoundary) | Out-Null
    $realTarget = Join-Path $realBoundary 'Boostix'
    [IO.Directory]::CreateDirectory($realTarget) | Out-Null
    [IO.File]::WriteAllText((Join-Path $realTarget 'state.txt'), 'state')
    $boundaryJunction = Join-Path $temporaryRoot 'BoundaryJunction'
    New-TestJunction -Link $boundaryJunction -Target $realBoundary
    Assert-CleanupFails `
        -Method $deleteTree `
        -Boundary $boundaryJunction `
        -Target (Join-Path $boundaryJunction 'Boostix') `
        -AllowedNames $allowedDataNames `
        -Scenario 'Junction in boundary chain'
    if (-not (Test-Path -LiteralPath (Join-Path $realTarget 'state.txt'))) {
        throw 'Boundary junction cleanup modified the real target.'
    }
    & $env:ComSpec /d /c ('rmdir "{0}"' -f $boundaryJunction) | Out-Null
    [void]$junctions.Remove($boundaryJunction)

    # File symlinks use the same ReparsePoint defense. Creation can be denied
    # on Windows installations where Developer Mode is disabled.
    $symlinkTarget = Join-Path $boundary 'Boostix'
    [IO.Directory]::CreateDirectory($symlinkTarget) | Out-Null
    $symlinkOwnedFile = Join-Path $symlinkTarget 'owned.txt'
    [IO.File]::WriteAllText($symlinkOwnedFile, 'owned')
    $fileSymlink = Join-Path $symlinkTarget 'outside-link.txt'
    if (Try-NewTestFileSymlink -Link $fileSymlink -Target $outsideSentinel) {
        Assert-CleanupFails `
            -Method $deleteTree `
            -Boundary $boundary `
            -Target $symlinkTarget `
            -AllowedNames $allowedDataNames `
            -Scenario 'Nested file symlink cleanup'
        if (-not (Test-Path -LiteralPath $outsideSentinel) -or
            -not (Test-Path -LiteralPath $symlinkOwnedFile)) {
            throw 'File symlink preflight was not fail-closed.'
        }
        [IO.File]::Delete($fileSymlink)
    }
    [IO.Directory]::Delete($symlinkTarget, $true)

    # Final self-cleanup may preserve only an in-tree executable. All other
    # entries are removed without recursively invoking PowerShell.
    $preservedTarget = Join-Path $boundary 'Boostix'
    [IO.Directory]::CreateDirectory($preservedTarget) | Out-Null
    $preserved = Join-Path $preservedTarget 'Uninstall.exe'
    $discarded = Join-Path $preservedTarget 'discarded.dat'
    [IO.File]::WriteAllText($preserved, 'running-image-placeholder')
    [IO.File]::WriteAllText($discarded, 'discard')
    if (Invoke-Cleanup `
        -Method $deleteTree `
        -Boundary $boundary `
        -Target $preservedTarget `
        -AllowedNames $allowedDataNames `
        -PreservedPath $preserved) {
        throw 'Cleanup incorrectly reported that a preserved tree was removed.'
    }
    if (-not (Test-Path -LiteralPath $preserved) -or
        (Test-Path -LiteralPath $discarded)) {
        throw 'Preserved final-cleanup behavior is incorrect.'
    }

    # A file cannot impersonate an allowlisted product directory.
    [IO.File]::Delete($preserved)
    [IO.Directory]::Delete($preservedTarget, $false)
    [IO.File]::WriteAllText($preservedTarget, 'not a directory')
    Assert-CleanupFails `
        -Method $deleteTree `
        -Boundary $boundary `
        -Target $preservedTarget `
        -AllowedNames $allowedDataNames `
        -Scenario 'File occupying allowlisted directory'
    if (-not (Test-Path -LiteralPath $preservedTarget -PathType Leaf)) {
        throw 'A file occupying the product directory was modified.'
    }

    $source = [IO.File]::ReadAllText($sourcePath)
    foreach ($requiredContract in @(
        'DeleteAllowlistedDirectoryTree(',
        'FileFlagOpenReparsePoint',
        'GetFinalPathNameByHandle',
        'SetFileInformationByHandle',
        'UninstallProductDirectoryNames',
        'UninstallLocalDataDirectoryNames',
        'ScheduleVerifiedInstallDirectoryRemoval('
    )) {
        if (-not $source.Contains($requiredContract)) {
            throw "Protected uninstall cleanup contract is missing: $requiredContract"
        }
    }
    if ($source.Contains("Remove-Item -LiteralPath '") -and
        $source.Contains('escapedInstallDirectory')) {
        throw 'The installer still contains its former recursive PowerShell cleanup.'
    }
    foreach ($unsafeDeletion in @(
        'Directory.Delete(startMenuDirectory, true)',
        'Directory.Delete(localData, true)'
    )) {
        if ($source.Contains($unsafeDeletion)) {
            throw "The installer still contains unsafe recursive deletion: $unsafeDeletion"
        }
    }

    Write-Host 'Uninstall cleanup adversarial regression test passed.' -ForegroundColor Green
}
finally {
    foreach ($junction in $junctions) {
        if (Test-Path -LiteralPath $junction) {
            & $env:ComSpec /d /c ('rmdir "{0}"' -f $junction) | Out-Null
        }
    }

    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
        $systemTemporaryRoot = [IO.Path]::GetFullPath(
            [IO.Path]::GetTempPath()).TrimEnd('\')
        $expectedPrefix = $systemTemporaryRoot + '\Boostix-UninstallCleanup-'
        if ($resolvedTemporaryRoot.StartsWith(
            $expectedPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
        }
    }
}
