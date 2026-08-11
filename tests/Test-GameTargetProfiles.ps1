[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $projectRoot 'Boostix\GameTargetProfiles.cs'
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'

if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "Game target source was not found: $sourcePath"
}
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "C# compiler was not found: $compiler"
}

$source = [IO.File]::ReadAllText($sourcePath)
foreach ($required in @(
    'class GameTargetIdentity',
    'ProcessStartTimeUtc',
    'NormalizedExecutablePath',
    'TryMatchSavedAutoBoostProfile',
    'SetAutoBoost',
    'HasVisibleMainWindow',
    'File.Replace(',
    'new UTF8Encoding(false)',
    'TryQuarantineCorruptFile',
    'StringComparer.OrdinalIgnoreCase',
    'Environment.SpecialFolder.LocalApplicationData',
    'Path.GetTempPath()',
    'ValidateDirectChildPath',
    'EnsureTrustedFilePath',
    'EnsureNoReparsePointsInExistingChain',
    'FileAttributes.ReparsePoint'
)) {
    if (-not $source.Contains($required)) {
        throw "Game target/profile contract is missing: $required"
    }
}

# Runtime creation of links can be denied by the Windows policy running CI.
# These assertions remain mandatory so such a host cannot silently drop the
# production defenses exercised by the adversarial modes below.
foreach ($requiredSecurityPattern in @(
    'EnsureTrustedFilePath(filePath);',
    'EnsureTrustedFilePath(temporaryPath);',
    'EnsureTrustedFilePath(backupPath);',
    'EnsureTrustedFilePath(quarantinePath);',
    'File.Replace(',
    'File.Move(temporaryPath, filePath)',
    'File.Move(filePath, quarantinePath)'
)) {
    if (-not $source.Contains($requiredSecurityPattern)) {
        throw "Game profile reparse defense is missing: $requiredSecurityPattern"
    }
}
foreach ($forbidden in @(
    'WorkingSet64',
    'Microsoft.Win32',
    'Registry.',
    'WebClient',
    'HttpClient',
    'ProcessStartInfo.Verb',
    'runas'
)) {
    if ($source.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Game target/profile code contains forbidden behavior: $forbidden"
    }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'Boostix-GameTargetProfiles-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $testRoot)
$harnessPath = Join-Path $testRoot 'GameTargetProfilesHarness.cs'
$harnessOutput = Join-Path $testRoot 'GameTargetProfilesHarness.exe'

$harnessSource = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Boostix
{
    internal sealed class FakeGameProcessCatalog : IGameProcessCatalog
    {
        private readonly Dictionary<int, GameProcessSnapshot> snapshots =
            new Dictionary<int, GameProcessSnapshot>();

        internal void Set(GameProcessSnapshot snapshot)
        {
            snapshots[snapshot.ProcessId] = snapshot;
        }

        internal void Remove(int processId)
        {
            snapshots.Remove(processId);
        }

        public IList<GameProcessSnapshot> CaptureAll()
        {
            return snapshots.Values.ToList();
        }

        public bool TryCapture(
            int processId,
            out GameProcessSnapshot snapshot,
            out string error)
        {
            if (snapshots.TryGetValue(processId, out snapshot))
            {
                error = string.Empty;
                return true;
            }

            error = "not running";
            return false;
        }
    }

    internal static class GameTargetProfilesHarness
    {
        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static GameProcessSnapshot Snapshot(
            int processId,
            DateTime startedUtc,
            string path,
            string name,
            bool visible,
            int sessionId)
        {
            return new GameProcessSnapshot(
                processId,
                startedUtc,
                path,
                name,
                name + " window",
                visible,
                sessionId);
        }

        private static int RunAdversarialStore(
            string trustedRoot,
            string externalFile)
        {
            trustedRoot = Path.GetFullPath(trustedRoot);
            externalFile = Path.GetFullPath(externalFile);
            string profilePath = Path.Combine(trustedRoot, "games.dat");
            byte[] originalExternalBytes = File.ReadAllBytes(externalFile);

            GameProfileStore store = new GameProfileStore(
                profilePath,
                trustedRoot);
            GameProfileLoadResult loaded = store.Load();
            Assert(loaded.Corrupt && loaded.Profiles.Count == 0,
                "A redirected store was not rejected before read.");
            Assert(string.IsNullOrEmpty(loaded.QuarantinePath),
                "A redirected store was moved to quarantine.");

            bool saveRejected = false;
            try
            {
                store.Save(new GameProfile[0]);
            }
            catch (IOException)
            {
                saveRejected = true;
            }
            catch (UnauthorizedAccessException)
            {
                saveRejected = true;
            }
            Assert(saveRejected,
                "A redirected store accepted an atomic save.");
            Assert(File.Exists(externalFile) &&
                File.ReadAllBytes(externalFile).SequenceEqual(originalExternalBytes),
                "A redirected store modified the external sentinel.");
            Assert(File.Exists(profilePath),
                "The redirected profile entry was moved or deleted.");

            Console.WriteLine(
                "Game profile reparse adversarial harness passed: " +
                trustedRoot);
            return 0;
        }

        public static int Main(string[] args)
        {
            if (args.Length == 3 &&
                string.Equals(
                    args[0],
                    "--adversarial-store",
                    StringComparison.Ordinal))
            {
                return RunAdversarialStore(args[1], args[2]);
            }

            if (args.Length != 1)
            {
                throw new ArgumentException("A test directory is required.");
            }

            string root = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(root);
            string boostixPath = Path.Combine(root, "Boostix.exe");
            string alphaPath = Path.Combine(root, "AlphaGame.exe");
            string zetaPath = Path.Combine(root, "ZetaGame.exe");
            string replacementPath = Path.Combine(root, "Replacement.exe");
            File.WriteAllBytes(boostixPath, new byte[] { 1 });
            File.WriteAllBytes(alphaPath, new byte[] { 2 });
            File.WriteAllBytes(zetaPath, new byte[] { 3 });
            File.WriteAllBytes(replacementPath, new byte[] { 4 });

            bool arbitraryProductionPathRejected = false;
            try
            {
                new GameProfileStore(Path.Combine(root, "not-production.dat"));
            }
            catch (UnauthorizedAccessException)
            {
                arbitraryProductionPathRejected = true;
            }
            Assert(arbitraryProductionPathRejected,
                "The production constructor accepted a path outside LocalAppData\\Boostix.");

            string productionRoot = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "Boostix");
            new GameProfileStore(Path.Combine(
                productionRoot,
                "game-profiles.dat"));

            bool nestedProductionPathRejected = false;
            try
            {
                new GameProfileStore(Path.Combine(
                    productionRoot,
                    "nested",
                    "game-profiles.dat"));
            }
            catch (UnauthorizedAccessException)
            {
                nestedProductionPathRejected = true;
            }
            Assert(nestedProductionPathRejected,
                "The production constructor accepted a non-direct descendant.");

            bool nonTempTestRootRejected = false;
            try
            {
                new GameProfileStore(
                    Path.Combine(productionRoot, "test.dat"),
                    productionRoot);
            }
            catch (ArgumentException)
            {
                nonTempTestRootRejected = true;
            }
            Assert(nonTempTestRootRejected,
                "The explicit test constructor accepted a non-TEMP root.");

            DateTime startedUtc = DateTime.UtcNow.AddMinutes(-10);
            FakeGameProcessCatalog catalog = new FakeGameProcessCatalog();
            catalog.Set(Snapshot(120, startedUtc, alphaPath, "Alpha Game", true, 7));
            catalog.Set(Snapshot(121, startedUtc, zetaPath, "Zeta Game", true, 7));
            catalog.Set(Snapshot(122, startedUtc, zetaPath, "Hidden Game", false, 7));
            catalog.Set(Snapshot(4, startedUtc, alphaPath, "System", true, 7));
            catalog.Set(Snapshot(9000, startedUtc, boostixPath, "Boostix", true, 7));
            catalog.Set(Snapshot(123, startedUtc, boostixPath, "Renamed Tool", true, 7));
            catalog.Set(Snapshot(124, startedUtc, alphaPath, "Other Session", true, 8));

            GameTargetService service = new GameTargetService(
                catalog,
                boostixPath,
                9000,
                7);

            IList<GameTargetCandidate> candidates = service.EnumerateCandidates();
            Assert(candidates.Count == 2, "Candidate filtering was not fail-closed.");
            Assert(candidates[0].Identity.ProcessId == 120 &&
                candidates[1].Identity.ProcessId == 121,
                "Candidates must be deterministic and alphabetic, not resource-ranked.");

            GameTargetIdentity identity;
            string error;
            Assert(service.TrySelect(120, out identity, out error),
                "A valid explicit target was rejected: " + error);
            Assert(identity.ProcessId == 120 &&
                identity.ProcessStartTimeUtc == startedUtc &&
                GameExecutablePath.AreEquivalent(identity.ExecutablePath, alphaPath),
                "The selected identity lost PID, start time, or path.");

            PropertyInfo processIdProperty = typeof(GameTargetIdentity).GetProperty(
                "ProcessId",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert(processIdProperty != null && !processIdProperty.CanWrite,
                "Game target identity must not expose a mutable PID.");

            GameTargetIdentity rejected;
            Assert(!service.TrySelect(4, out rejected, out error),
                "A system PID was accepted.");
            Assert(!service.TrySelect(9000, out rejected, out error),
                "Boostix accepted its own PID.");
            Assert(!service.TrySelect(123, out rejected, out error),
                "Boostix accepted its own executable under another name.");
            Assert(!service.TrySelect(122, out rejected, out error),
                "A process without a visible window was accepted.");
            Assert(!service.TrySelect(124, out rejected, out error),
                "A process from another session was accepted.");
            Assert(!service.TrySelect(99999, out rejected, out error),
                "A missing PID was accepted.");

            GameProcessSnapshot resolved;
            Assert(service.TryResolve(identity, out resolved, out error),
                "A live selected target did not resolve: " + error);

            catalog.Set(Snapshot(
                120,
                startedUtc,
                alphaPath,
                "Alpha Game",
                false,
                7));
            Assert(service.TryResolve(identity, out resolved, out error),
                "A temporary main-window recreation invalidated an exact live target: " +
                error);

            catalog.Set(Snapshot(
                120,
                startedUtc,
                alphaPath.ToUpperInvariant(),
                "Alpha Game",
                true,
                7));
            Assert(service.TryResolve(identity, out resolved, out error),
                "Case-only path changes must compare as the same Windows path: " + error);

            catalog.Set(Snapshot(
                120,
                startedUtc.AddMinutes(1),
                alphaPath,
                "Alpha Game",
                true,
                7));
            Assert(!service.TryResolve(identity, out resolved, out error) &&
                error.IndexOf("reused", StringComparison.OrdinalIgnoreCase) >= 0,
                "A recycled PID was not rejected as stale.");

            catalog.Set(Snapshot(
                120,
                startedUtc,
                replacementPath,
                "Alpha Game",
                true,
                7));
            Assert(!service.TryResolve(identity, out resolved, out error),
                "A changed executable behind the same PID/start time was accepted.");
            catalog.Remove(120);
            Assert(!service.TryResolve(identity, out resolved, out error),
                "An exited target was still considered live.");
            catalog.Set(Snapshot(120, startedUtc, alphaPath, "Alpha Game", true, 7));

            string display;
            string normalized;
            Assert(!GameExecutablePath.TryNormalize(
                    "relative.exe",
                    out display,
                    out normalized,
                    out error),
                "A relative executable profile path was accepted.");
            Assert(GameExecutablePath.AreEquivalent(
                    "\"" + alphaPath + "\"",
                    alphaPath.ToUpperInvariant()),
                "Quoted/case-only equivalent paths did not match.");

            string profilePath = Path.Combine(root, "profiles", "games.dat");
            GameProfileStore store = new GameProfileStore(
                profilePath,
                Path.GetDirectoryName(profilePath));
            GameProfile autoProfile;
            Assert(!store.TryGetAutoBoostProfile(alphaPath, out autoProfile),
                "Auto Boost matched a profile that was never saved.");

            GameProfile saved = store.Upsert(identity, "Alpha Game", true);
            Assert(File.Exists(profilePath), "The profile store was not created.");
            byte[] serialized = File.ReadAllBytes(profilePath);
            Assert(serialized.Length >= 3 &&
                !(serialized[0] == 0xEF && serialized[1] == 0xBB && serialized[2] == 0xBF),
                "Profiles must use UTF-8 without a BOM.");
            Assert(Directory.GetFiles(
                    Path.GetDirectoryName(profilePath),
                    "*.tmp").Length == 0,
                "An atomic-write temporary file was left behind.");

            GameProfileLoadResult loaded = store.Load();
            Assert(!loaded.Corrupt && loaded.Profiles.Count == 1 &&
                loaded.Profiles[0].AutoBoost &&
                loaded.Profiles[0].DisplayName == "Alpha Game",
                "The saved profile did not round-trip.");

            GameTargetIdentity autoIdentity;
            Assert(service.TryMatchSavedAutoBoostProfile(
                    120,
                    store,
                    out autoIdentity,
                    out autoProfile,
                    out error),
                "A saved Auto Boost profile did not match: " + error);
            Assert(autoIdentity.ProcessStartTimeUtc == startedUtc,
                "Auto Boost did not bind a fresh PID/start-time identity.");

            File.Delete(alphaPath);
            Assert(store.SetAutoBoost(alphaPath, false),
                "An offline saved profile could not disable Auto Boost.");
            loaded = store.Load();
            Assert(!loaded.Corrupt && loaded.Profiles.Count == 1 &&
                !loaded.Profiles[0].AutoBoost &&
                !store.TryGetAutoBoostProfile(alphaPath, out autoProfile),
                "Disabled offline Auto Boost did not round-trip.");
            Assert(!store.SetAutoBoost(replacementPath, true),
                "Auto Boost was enabled for a path that was never saved.");
            Assert(store.Load().Profiles.Count == 1,
                "A missing profile was created implicitly by SetAutoBoost.");
            File.WriteAllBytes(alphaPath, new byte[] { 2 });
            Assert(store.SetAutoBoost(alphaPath.ToUpperInvariant(), true),
                "Case-insensitive offline Auto Boost enable failed.");
            loaded = store.Load();
            Assert(!loaded.Corrupt && loaded.Profiles.Count == 1 &&
                loaded.Profiles[0].AutoBoost &&
                store.TryGetAutoBoostProfile(alphaPath, out autoProfile),
                "Enabled offline Auto Boost did not round-trip.");

            Assert(store.Remove(alphaPath.ToUpperInvariant()),
                "Case-insensitive profile removal failed.");
            Assert(!service.TryMatchSavedAutoBoostProfile(
                    120,
                    store,
                    out autoIdentity,
                    out autoProfile,
                    out error),
                "Auto Boost remained enabled after profile removal.");

            GameProfile firstCase = GameProfile.CreateFromTarget(
                identity,
                "First",
                true);
            GameTargetIdentity upperIdentity = new GameTargetIdentity(
                identity.ProcessId,
                identity.ProcessStartTimeUtc,
                identity.ExecutablePath.ToUpperInvariant(),
                identity.NormalizedExecutablePath.ToUpperInvariant(),
                identity.ProcessName);
            GameProfile secondCase = GameProfile.CreateFromTarget(
                upperIdentity,
                "Second",
                true);
            store.Save(new GameProfile[] { firstCase, secondCase });
            loaded = store.Load();
            Assert(!loaded.Corrupt && loaded.Profiles.Count == 1,
                "Case-only duplicate executable paths were not de-duplicated.");

            store.Upsert(identity, "Alpha Game", false);
            Assert(!store.TryGetAutoBoostProfile(alphaPath, out autoProfile),
                "A saved profile with Auto Boost disabled was matched.");

            File.WriteAllText(profilePath, "not-a-profile", new UTF8Encoding(false));
            loaded = store.Load();
            Assert(loaded.Corrupt && loaded.Profiles.Count == 0,
                "Corrupt profile data was partially trusted.");
            Assert(!File.Exists(profilePath) &&
                !string.IsNullOrWhiteSpace(loaded.QuarantinePath) &&
                File.Exists(loaded.QuarantinePath),
                "Corrupt profile data was not quarantined.");

            Console.WriteLine("Game target/profile regression harness passed.");
            return 0;
        }
    }
}
'@

[IO.File]::WriteAllText(
    $harnessPath,
    $harnessSource,
    [Text.UTF8Encoding]::new($false))

$createdReparsePaths = New-Object 'System.Collections.Generic.List[string]'
try {
    & $compiler @(
        '/nologo',
        '/target:exe',
        '/optimize+',
        '/reference:System.dll',
        '/reference:System.Core.dll',
        "/out:$harnessOutput",
        $sourcePath,
        $harnessPath
    )
    if ($LASTEXITCODE -ne 0) {
        throw "Game target/profile harness compilation failed with exit code $LASTEXITCODE."
    }

    & $harnessOutput $testRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Game target/profile harness failed with exit code $LASTEXITCODE."
    }

    $junctionContainer = Join-Path $testRoot 'junction-container'
    $junctionExternal = Join-Path $testRoot 'junction-external'
    $junctionPath = Join-Path $junctionContainer 'profiles'
    [void](New-Item -ItemType Directory -Path $junctionContainer)
    [void](New-Item -ItemType Directory -Path $junctionExternal)
    $junctionExternalFile = Join-Path $junctionExternal 'games.dat'
    [IO.File]::WriteAllText(
        $junctionExternalFile,
        'external-junction-sentinel',
        [Text.UTF8Encoding]::new($false))
    try {
        [void](New-Item `
            -ItemType Junction `
            -Path $junctionPath `
            -Target $junctionExternal `
            -ErrorAction Stop)
        $junctionItem = Get-Item -LiteralPath $junctionPath -Force
        if (($junctionItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
            throw 'The test junction was not marked as a reparse point.'
        }
        $createdReparsePaths.Add($junctionPath)
        & $harnessOutput `
            '--adversarial-store' `
            $junctionPath `
            $junctionExternalFile
        if ($LASTEXITCODE -ne 0) {
            throw "Directory-junction adversarial harness failed: $LASTEXITCODE."
        }
    }
    catch [System.Management.Automation.PSNotSupportedException] {
        Write-Host 'Junction runtime test unavailable; static assertions remain active.'
    }
    catch [System.UnauthorizedAccessException] {
        Write-Host 'Junction creation denied; static assertions remain active.'
    }
    catch [System.IO.IOException] {
        Write-Host 'Junction runtime test unavailable; static assertions remain active.'
    }

    $fileLinkStore = Join-Path $testRoot 'file-link-store'
    $fileLinkExternal = Join-Path $testRoot 'file-link-external'
    [void](New-Item -ItemType Directory -Path $fileLinkStore)
    [void](New-Item -ItemType Directory -Path $fileLinkExternal)
    $fileLinkTarget = Join-Path $fileLinkExternal 'external.dat'
    $fileLinkPath = Join-Path $fileLinkStore 'games.dat'
    [IO.File]::WriteAllText(
        $fileLinkTarget,
        'external-file-sentinel',
        [Text.UTF8Encoding]::new($false))
    try {
        [void](New-Item `
            -ItemType SymbolicLink `
            -Path $fileLinkPath `
            -Target $fileLinkTarget `
            -ErrorAction Stop)
        $fileLinkItem = Get-Item -LiteralPath $fileLinkPath -Force
        if (($fileLinkItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
            throw 'The test file link was not marked as a reparse point.'
        }
        $createdReparsePaths.Add($fileLinkPath)
        & $harnessOutput `
            '--adversarial-store' `
            $fileLinkStore `
            $fileLinkTarget
        if ($LASTEXITCODE -ne 0) {
            throw "File-symlink adversarial harness failed: $LASTEXITCODE."
        }
    }
    catch [System.Management.Automation.PSNotSupportedException] {
        Write-Host 'File-symlink runtime test unavailable; static assertions remain active.'
    }
    catch [System.UnauthorizedAccessException] {
        Write-Host 'File-symlink creation denied; static assertions remain active.'
    }
    catch [System.IO.IOException] {
        Write-Host 'File-symlink runtime test unavailable; static assertions remain active.'
    }
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $resolvedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $testPrefix = $resolvedTestRoot.TrimEnd('\') + '\'
    foreach ($reparsePath in $createdReparsePaths) {
        $resolvedReparsePath = [IO.Path]::GetFullPath($reparsePath)
        if (-not $resolvedReparsePath.StartsWith(
                $testPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove an out-of-test reparse path: $resolvedReparsePath"
        }

        $reparseItem = Get-Item `
            -LiteralPath $resolvedReparsePath `
            -Force `
            -ErrorAction SilentlyContinue
        if ($null -ne $reparseItem) {
            if (($reparseItem.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -eq 0) {
                throw "Refusing to remove a non-reparse test path: $resolvedReparsePath"
            }
            if (($reparseItem.Attributes -band
                    [IO.FileAttributes]::Directory) -ne 0) {
                [IO.Directory]::Delete($resolvedReparsePath, $false)
            }
            else {
                [IO.File]::Delete($resolvedReparsePath)
            }
        }
    }

    $tempPrefix = $resolvedTempRoot.TrimEnd('\') + '\'
    if ($resolvedTestRoot.StartsWith(
            $tempPrefix,
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTestRoot)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}

Write-Host 'Game target/profile regression test passed.' -ForegroundColor Green
