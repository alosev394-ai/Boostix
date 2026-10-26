[CmdletBinding()]
param(
    [string]$ApplicationPath
)

# Regression for "Apply settings closes the app": Windows can refuse a single
# allowlisted registry value even to an elevated administrator (seen with
# HKLM\SOFTWARE\Policies\Microsoft\Dsh\AllowNewsAndInterests on 25H2). That one
# refusal must not abort the whole profile, and an automatic rollback after a
# failed apply must explain itself instead of silently closing Boostix.

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

function Get-ProductionFunctionScriptBlock {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string]$Name
    )
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        $ScriptPath,
        [ref]$tokens,
        [ref]$errors)
    if ($errors.Count -ne 0) {
        throw "Cannot parse production script: $ScriptPath"
    }
    $definition = $ast.Find(
        {
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -ceq $Name
        },
        $true)
    if (-not $definition) {
        throw "Production function was not found: $Name"
    }
    return [ScriptBlock]::Create($definition.Extent.Text)
}

function Convert-UiName {
    param([string]$Base64)
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Base64))
}

# Runs the production Set-TrackedRegistryValue against an HKCU fixture with
# the transaction plumbing (allowlist, state persistence) stubbed out.
function Invoke-TrackedWrite {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name,
        [scriptblock]$NewItemPropertyOverride
    )
    & {
        param($ProductionScript, $KeyPath, $ValueName, $Override)
        $ErrorActionPreference = 'Stop'
        foreach ($functionName in @(
            'Set-ObjectProperty',
            'Test-ValueEqual',
            'Get-RegistrySnapshot',
            'Test-RegistrySnapshotEqual',
            'Set-TrackedRegistryValue'
        )) {
            . (Get-ProductionFunctionScriptBlock -ScriptPath $ProductionScript -Name $functionName)
        }
        $state = [pscustomobject]@{ Registry = @(); Warnings = @(); Conflicts = @() }
        function Assert-RegistryMutationAllowed { param($Path, $Name, $Value, $Kind) }
        function Save-State { }
        function Add-Warning {
            param([Parameter(Mandatory = $true)][string]$Message)
            $state.Warnings = @($state.Warnings) + $Message
        }
        if ($Override) {
            Set-Item -Path Function:\New-ItemProperty -Value $Override
        }
        $thrown = $null
        try {
            Set-TrackedRegistryValue -Path $KeyPath -Name $ValueName -Value 0 -Kind DWord
        }
        catch {
            $thrown = $_.Exception
        }
        [pscustomobject]@{ State = $state; Thrown = $thrown }
    } $applyPath $Path $Name $NewItemPropertyOverride
}

function Assert-SkippedAsDenied {
    param($Outcome, [string]$Path, [string]$Name, [string]$Label)
    if ($Outcome.Thrown) {
        throw "${Label}: a refused registry write aborted the profile: $($Outcome.Thrown.GetType().FullName): $($Outcome.Thrown.Message)"
    }
    $entries = @($Outcome.State.Registry)
    if ($entries.Count -ne 1) {
        throw "${Label}: expected one tracked registry entry, got $($entries.Count)."
    }
    $entry = $entries[0]
    if ([bool]$entry.ChangedByUs -or [bool]$entry.Applied) {
        throw "${Label}: a refused write must be recorded as not changed by Boostix (ChangedByUs=$($entry.ChangedByUs), Applied=$($entry.Applied))."
    }
    if (-not ($entry.PSObject.Properties.Name -contains 'Conflict') -or [string]$entry.Conflict -cne 'AccessDenied') {
        throw "${Label}: the refused write is not marked with Conflict=AccessDenied."
    }
    $expected = "${Path}::$Name"
    if (-not (@($Outcome.State.Warnings) | Where-Object { $_ -like "*$expected*" })) {
        throw "${Label}: no warning names the skipped setting $expected."
    }
    if ((Test-Path -LiteralPath $Path) -and
        (@((Get-Item -LiteralPath $Path).GetValueNames()) -contains $Name)) {
        throw "${Label}: the refused value unexpectedly exists."
    }
}

$fixtureRoot = 'HKCU:\Software\Boostix-OptimizationFailure-' + [Guid]::NewGuid().ToString('N')
$process = $null
try {
    New-Item -Path $fixtureRoot -Force | Out-Null

    # 1. A real ACL refusal: the key exists but denies setting values.
    $deniedKey = Join-Path $fixtureRoot 'DeniedByAcl'
    New-Item -Path $deniedKey -Force | Out-Null
    $acl = Get-Acl -Path $deniedKey
    $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl.AddAccessRule((New-Object Security.AccessControl.RegistryAccessRule(
        $currentUser,
        [Security.AccessControl.RegistryRights]::SetValue,
        [Security.AccessControl.AccessControlType]::Deny)))
    Set-Acl -Path $deniedKey -AclObject $acl
    $outcome = Invoke-TrackedWrite -Path $deniedKey -Name 'Probe'
    Assert-SkippedAsDenied -Outcome $outcome -Path $deniedKey -Name 'Probe' -Label 'ACL refusal'
    if (-not (Test-Path -LiteralPath $deniedKey)) {
        throw 'ACL refusal: a key that existed before Boostix must be preserved.'
    }

    # 2. The field failure: the key can be created, but setting the value is
    #    refused with UnauthorizedAccessException. The key Boostix created for
    #    the write must not be left behind.
    $createdKey = Join-Path $fixtureRoot 'CreatedForWrite'
    $outcome = Invoke-TrackedWrite -Path $createdKey -Name 'AllowNewsAndInterests' -NewItemPropertyOverride {
        throw (New-Object System.UnauthorizedAccessException('Attempted to perform an unauthorized operation.'))
    }
    Assert-SkippedAsDenied -Outcome $outcome -Path $createdKey -Name 'AllowNewsAndInterests' -Label 'Value refusal'
    if (Test-Path -LiteralPath $createdKey) {
        throw 'Value refusal: the empty key created only for the refused write was left behind.'
    }

    # 3. Any other failure still aborts the transaction (fail closed).
    $ioKey = Join-Path $fixtureRoot 'IoFailure'
    $outcome = Invoke-TrackedWrite -Path $ioKey -Name 'Probe' -NewItemPropertyOverride {
        throw (New-Object System.IO.IOException('Simulated registry I/O failure.'))
    }
    if (-not $outcome.Thrown) {
        throw 'A registry failure other than an access refusal must still abort the profile.'
    }

    # 3b. Rollback removes a key that only existed because the profile created
    #     it, and keeps keys that were there before (or whose history is unknown).
    $restorePath = Join-Path $projectRoot 'outputs\MaxFPS-Restore.ps1'
    foreach ($case in @(
        @{ Name = 'CreatedByProfile'; Snapshot = [pscustomobject]@{ KeyExists = $false; Exists = $false; Kind = $null; Value = $null }; KeepKey = $false },
        @{ Name = 'ExistedBefore'; Snapshot = [pscustomobject]@{ KeyExists = $true; Exists = $false; Kind = $null; Value = $null }; KeepKey = $true },
        @{ Name = 'LegacyState'; Snapshot = [pscustomobject]@{ Exists = $false; Kind = $null; Value = $null }; KeepKey = $true }
    )) {
        $caseKey = Join-Path $fixtureRoot ('Restore' + $case.Name)
        New-Item -Path $caseKey -Force | Out-Null
        New-ItemProperty -Path $caseKey -Name 'AllowNewsAndInterests' -Value 0 -PropertyType DWord -Force | Out-Null
        & {
            param($ProductionScript, $KeyPath, $Snapshot)
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            . (Get-ProductionFunctionScriptBlock -ScriptPath $ProductionScript -Name 'Restore-RegistrySnapshot')
            Restore-RegistrySnapshot -Path $KeyPath -Name 'AllowNewsAndInterests' -Snapshot $Snapshot
        } $restorePath $caseKey $case.Snapshot
        $exists = Test-Path -LiteralPath $caseKey
        if ($exists -ne $case.KeepKey) {
            throw "Rollback case $($case.Name): key exists=$exists, expected $($case.KeepKey)."
        }
        if ($exists -and (@((Get-Item -LiteralPath $caseKey).GetValueNames()) -contains 'AllowNewsAndInterests')) {
            throw "Rollback case $($case.Name): the value created by the profile was not removed."
        }
    }

    # 4. The real failed-apply path uses the same explaining rollback as the
    #    demo flow exercised below, not the rollback that closes Boostix.
    $flowSource = [IO.File]::ReadAllText((Join-Path $projectRoot 'Boostix\OptimizationFlow.cs'))
    $branchStart = $flowSource.IndexOf('if (canRestorePartialChanges)')
    $branchEnd = if ($branchStart -ge 0) { $flowSource.IndexOf('return;', $branchStart) } else { -1 }
    if ($branchEnd -lt 0) {
        throw 'The failed-apply rollback branch was not found in OptimizationFlow.cs.'
    }
    $branch = $flowSource.Substring($branchStart, $branchEnd - $branchStart)
    if (-not $branch.Contains('await RestoreAfterFailedApplyAsync();') -or $branch.Contains('RestoreAndCloseAsync')) {
        throw 'A failed apply must roll back with RestoreAfterFailedApplyAsync, not close Boostix.'
    }

    # 5. An automatic rollback after a failed apply keeps Boostix open and
    #    explains what happened (demo flow, no system changes).
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $applyName = Convert-UiName '0J/RgNC+0LTQvtC70LbQuNGC0Ywg0Lgg0L/RgNC40LzQtdC90LjRgtGMINC+0L/RgtC40LzQuNC30LDRhtC40Y4g0L/RgNC+0LjQt9Cy0L7QtNC40YLQtdC70YzQvdC+0YHRgtC4'
    $skipName = Convert-UiName '0J/RgNC+0LTQvtC70LbQuNGC0Ywg0LHQtdC3INGB0LjRgdGC0LXQvNC90L7QuSDQvtC/0YLQuNC80LjQt9Cw0YbQuNC4'
    $rolledBackPhrase = Convert-UiName '0L7RgtC80LXQvdC40Lsg0LLRgdC1INC40LfQvNC10L3QtdC90LjRjw=='

    $process = Start-Process -FilePath $ApplicationPath -PassThru -ArgumentList @(
        '--demo',
        '--demo-setup',
        '--demo-apply-failure',
        ('--test-instance=' + [Guid]::NewGuid().ToString('N')))
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ($process.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
    }
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'Boostix did not open its window for the demo setup flow.'
    }
    $window = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)

    function Find-ByName {
        param([string]$Name, [int]$TimeoutSeconds = 10)
        $condition = New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::NameProperty, $Name)
        $until = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        do {
            $found = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
            if ($found) { return $found }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $until)
        return $null
    }

    function Find-TextContaining {
        param([string]$Fragment, [int]$TimeoutSeconds = 10)
        $condition = New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Text)
        $until = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        do {
            if ($process.HasExited) { return $null }
            # A collapsed WPF card stays in the UIA tree, reported offscreen.
            foreach ($text in $window.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)) {
                if ($text.Current.Name.Contains($Fragment) -and -not $text.Current.IsOffscreen) { return $text }
            }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $until)
        return $null
    }

    $applyButton = Find-ByName -Name $applyName
    if (-not $applyButton) {
        throw 'The setup consent card with the apply button did not appear.'
    }
    $applyButton.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()

    $explanation = Find-TextContaining -Fragment $rolledBackPhrase -TimeoutSeconds 15
    if ($process.HasExited) {
        throw "Boostix closed after the failed apply was rolled back (exit code $($process.ExitCode)) instead of explaining it."
    }
    if (-not $explanation) {
        throw 'After the automatic rollback Boostix did not explain that all changes were reverted.'
    }

    $skipButton = Find-ByName -Name $skipName -TimeoutSeconds 5
    if (-not $skipButton) {
        throw 'After the automatic rollback there is no way to continue without system optimization.'
    }
    $skipButton.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $until = [DateTime]::UtcNow.AddSeconds(5)
    while ((Find-TextContaining -Fragment $rolledBackPhrase -TimeoutSeconds 0) -and [DateTime]::UtcNow -lt $until) {
        Start-Sleep -Milliseconds 100
    }
    if ($process.HasExited) {
        throw 'Continuing without optimization closed Boostix.'
    }
    if (Find-TextContaining -Fragment $rolledBackPhrase -TimeoutSeconds 0) {
        throw 'Continuing without optimization did not dismiss the rollback explanation.'
    }

    Write-Host 'Optimization failure recovery test passed: refused registry writes are skipped with a warning, other failures still abort, and an automatic rollback keeps Boostix open with an explanation.' -ForegroundColor Green
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $fixtureRoot) {
        # Set-Acl reopens the key for writing, which the deny rule refuses;
        # open it for WRITE_DAC only to lift the rule before deleting.
        $fixtureSubPath = $fixtureRoot.Substring('HKCU:\'.Length)
        foreach ($key in @(Get-ChildItem -LiteralPath $fixtureRoot -ErrorAction SilentlyContinue)) {
            $handle = $null
            try {
                $handle = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey(
                    $fixtureSubPath + '\' + $key.PSChildName,
                    [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree,
                    [Security.AccessControl.RegistryRights]'ChangePermissions, ReadPermissions')
                $keyAcl = $handle.GetAccessControl()
                foreach ($rule in @($keyAcl.GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier]) |
                    Where-Object { $_.AccessControlType -eq 'Deny' })) {
                    [void]$keyAcl.RemoveAccessRule($rule)
                }
                $handle.SetAccessControl($keyAcl)
            }
            catch { }
            finally {
                if ($handle) { $handle.Close() }
            }
        }
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $fixtureRoot) {
            Write-Warning "Test registry fixture could not be removed: $fixtureRoot"
        }
    }
}
