[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSEdition -cne 'Desktop' -or
    $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This test must run in Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$proofSource = Join-Path $projectRoot 'Boostix\PerformanceProof.cs'
$coordinatorSource = Join-Path $projectRoot 'Boostix\PerformanceProofCoordinator.cs'
$programSource = Join-Path $projectRoot 'Boostix\Program.cs'
$centerSource = Join-Path $projectRoot 'Boostix\BoostCenterOverlay.cs'
$buildSource = Join-Path $projectRoot 'build.ps1'
$runAllSource = Join-Path $projectRoot 'tests\Run-All.ps1'
foreach ($sourcePath in @($proofSource, $coordinatorSource)) {
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Required source was not found: $sourcePath"
    }
}

$programText = [IO.File]::ReadAllText($programSource)
$centerText = [IO.File]::ReadAllText($centerSource)
$buildText = [IO.File]::ReadAllText($buildSource)
$runAllText = [IO.File]::ReadAllText($runAllSource)
foreach ($contract in @(
    'GuidedProofScenarioId',
    'PerformanceProofCoordinator.TryStart(',
    'proofCoordinator.SubmitRun(',
    'proofCheckpointStore.TrySave(',
    'BuildPerformanceProofContext(',
    'result.FrameTimesMs',
    'boostActive != issuedStep.RequiresBoost'
)) {
    if (-not $programText.Contains($contract)) {
        throw "Proof Mode production integration is missing: $contract"
    }
}
foreach ($contract in @(
    'BuildPerformanceProofCard(',
    'string proofButtonText = "PROOF MODE',
    'Boostix.Center.CancelProof'
)) {
    if (-not $centerText.Contains($contract)) {
        throw "Proof Mode UI integration is missing: $contract"
    }
}
if (-not $buildText.Contains('Boostix\PerformanceProofCoordinator.cs') -or
    -not $runAllText.Contains('Test-PerformanceProofCoordinator.ps1')) {
    throw 'Proof Mode is not part of the production build and mandatory suite.'
}

$frameworkFolders = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319')
)
$compiler = $frameworkFolders |
    ForEach-Object { Join-Path $_ 'csc.exe' } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($compiler)) {
    throw '.NET Framework C# compiler was not found.'
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'Boostix-ProofCoordinator-' + [Guid]::NewGuid().ToString('N'))
$harnessPath = Join-Path $tempRoot 'ProofCoordinatorHarness.cs'
$outputPath = Join-Path $tempRoot 'ProofCoordinatorHarness.exe'

$harnessSource = @'
using System;
using System.Collections.Generic;
using System.IO;

namespace Boostix
{
    internal static class ProofCoordinatorHarness
    {
        private static string Target;
        private static string Context;

        public static int Main()
        {
            PerformanceProofContext proofContext = CreateContext();
            Target = proofContext.BuildExecutableIdentity();
            Context = proofContext.BuildContextKey();

            TestStartContract();
            TestGuidedSequenceAndRestore();
            TestNeutralResultAndIsolation();
            TestFailClosedMismatchAndOrdering();
            TestCancellationAndSanitization();
            TestCheckpointIntegrityAndAtomicStore();

            Console.WriteLine("PASS: Proof Mode coordinator contracts are valid.");
            return 0;
        }

        private static PerformanceProofContext CreateContext()
        {
            return new PerformanceProofContext
            {
                ExecutablePath = @"C:\Games\Sample\game.exe",
                ExecutableSha256 = new string('A', 64),
                ExecutableVersion = "2.0.0",
                ExecutableLength = 123456,
                ScenarioId = "Repeatable downtown route",
                DisplayWidth = 1920,
                DisplayHeight = 1080,
                RefreshRateHz = 144.0,
                DisplayMode = "Borderless",
                GraphicsPreset = "High",
                GraphicsAdapterId = "GPU-001",
                VSyncEnabled = false,
                FrameLimit = 144
            };
        }

        private static void TestStartContract()
        {
            PerformanceProofCoordinator coordinator;
            string error;
            Assert(!PerformanceProofCoordinator.TryStart(
                "game.exe",
                Context,
                out coordinator,
                out error),
                "Raw target names must not start a proof.");
            Assert(coordinator == null && !string.IsNullOrWhiteSpace(error),
                "Invalid start must explain the refusal.");

            Assert(!PerformanceProofCoordinator.TryStart(
                Target,
                "1920x1080",
                out coordinator,
                out error),
                "Raw display metadata must not replace an exact context key.");

            Assert(PerformanceProofCoordinator.TryStart(
                Target,
                Context,
                out coordinator,
                out error),
                "A valid frozen target/context must start the proof.");
            PerformanceProofCoordinatorSnapshot snapshot = coordinator.GetSnapshot();
            Assert(snapshot.State == PerformanceProofCoordinatorState.AwaitingRun,
                "New proof must await its first run.");
            Assert(snapshot.CompletedSteps == 0 && snapshot.TotalSteps == 4,
                "New proof must expose the four guided steps.");
            Assert(snapshot.NextStep != null,
                "Step one must be available.");
            Assert(snapshot.NextStep.StepNumber == 1,
                "Step one number is invalid: " + snapshot.NextStep.StepNumber + ".");
            Assert(snapshot.NextStep.ExpectedVariant == PerformanceProofVariant.Baseline,
                "Step one must request baseline.");
            Assert(snapshot.NextStep.SequenceLabel == "A\u2192B",
                "Step one sequence is invalid: " + snapshot.NextStep.SequenceLabel + ".");
            Assert(!snapshot.NextStep.RequiresBoost,
                "Step one must not require Boost.");
            Assert(snapshot.NextStep.TargetIdentity == Target &&
                   snapshot.NextStep.ContextKey == Context,
                "The step must expose the frozen exact target/context.");
            Assert(snapshot.ProofId.Length > 0 && snapshot.NextStep.PairId.Length > 0,
                "Proof and pair IDs must be frozen at start.");
            Assert(coordinator.CreateCheckpoint().IndexOf(
                @"C:\Games\Sample",
                StringComparison.OrdinalIgnoreCase) < 0,
                "Checkpoint must not leak the executable path.");

            PerformanceProofCoordinator replacement = Start();
            PerformanceProofTransition stale = replacement.SubmitRun(
                MakeRun(
                    "old-proof-result",
                    PerformanceProofVariant.Baseline,
                    DateTime.UtcNow.AddSeconds(1),
                    60.0,
                    MakeFrames(3600, 16.0, 55.0)),
                snapshot.NextStep.PairId,
                snapshot.NextStep.StepNumber);
            Assert(stale.Status == PerformanceProofSubmissionStatus.Rejected &&
                   stale.NextStep != null && stale.NextStep.StepNumber == 1 &&
                   stale.NextStep.PairId != snapshot.NextStep.PairId,
                "A result issued by a replaced proof must never enter the new proof.");
        }

        private static void TestGuidedSequenceAndRestore()
        {
            PerformanceProofCoordinator coordinator = Start();
            PerformanceProofCoordinatorSnapshot initial = coordinator.GetSnapshot();
            string proofId = initial.ProofId;
            string firstPair = initial.NextStep.PairId;
            DateTime capture = DateTime.UtcNow.AddSeconds(1);

            PerformanceProofRun shortRun = MakeRun(
                "short",
                PerformanceProofVariant.Baseline,
                capture,
                20.0,
                MakeFrames(100, 16.0, 60.0));
            PerformanceProofTransition transition = SubmitCurrent(coordinator, shortRun);
            Assert(transition.Status == PerformanceProofSubmissionStatus.Rejected &&
                   transition.State == PerformanceProofCoordinatorState.AwaitingRun &&
                   transition.NextStep.StepNumber == 1,
                "Insufficient run must be recoverably rejected without advancing.");

            PerformanceProofRun tooFewFrames = MakeRun(
                "too-few-frames",
                PerformanceProofVariant.Baseline,
                capture,
                60.0,
                MakeFrames(299, 210.0, 210.0));
            transition = SubmitCurrent(coordinator, tooFewFrames);
            Assert(transition.Status == PerformanceProofSubmissionStatus.Rejected &&
                   transition.NextStep.StepNumber == 1,
                "Fewer than the service minimum frames must not advance the proof.");

            PerformanceProofRun baselineOne = MakeRun(
                "run-A1",
                PerformanceProofVariant.Baseline,
                capture,
                60.0,
                MakeFrames(3600, 16.5, 58.0));
            transition = SubmitCurrent(coordinator, baselineOne);
            Assert(transition.Status == PerformanceProofSubmissionStatus.Accepted &&
                   transition.NextStep.StepNumber == 2 &&
                   transition.NextStep.ExpectedVariant == PerformanceProofVariant.Boost &&
                   transition.NextStep.RequiresBoost &&
                   transition.NextStep.PairId == firstPair,
                "Step two must request Boost in the same A-to-B pair.");

            baselineOne.FrameTimesMs.Clear();
            PerformanceProofCoordinator restored;
            string restoreError;
            Assert(PerformanceProofCoordinator.TryRestore(
                transition.Checkpoint,
                out restored,
                out restoreError),
                "Accepted step checkpoint must restore: " + restoreError);
            PerformanceProofCoordinatorSnapshot resumed = restored.GetSnapshot();
            Assert(resumed.ProofId == proofId &&
                   resumed.CompletedSteps == 1 &&
                   resumed.NextStep.PairId == firstPair,
                "Restart must retain the exact proof, pair and current step.");

            transition = restored.SubmitRun(
                MakeRun(
                    "stale-step-result",
                    PerformanceProofVariant.Boost,
                    capture.AddSeconds(61),
                    60.0,
                    MakeFrames(4000, 14.0, 38.0)),
                firstPair,
                1);
            Assert(transition.Status == PerformanceProofSubmissionStatus.Rejected &&
                   transition.State == PerformanceProofCoordinatorState.AwaitingRun &&
                   transition.NextStep.StepNumber == 2,
                "A stale async result must not advance or terminate the current step.");

            transition = SubmitCurrent(restored, MakeRun(
                "run-B1",
                PerformanceProofVariant.Boost,
                capture.AddSeconds(61),
                60.0,
                MakeFrames(4000, 14.0, 38.0)));
            Assert(transition.Status == PerformanceProofSubmissionStatus.Accepted &&
                   transition.NextStep.StepNumber == 3 &&
                   transition.NextStep.ExpectedVariant == PerformanceProofVariant.Boost &&
                   transition.NextStep.SequenceLabel == "B\u2192A" &&
                   transition.NextStep.PairId != firstPair,
                "Step three must begin the independent B-to-A pair with Boost.");
            string secondPair = transition.NextStep.PairId;

            transition = SubmitCurrent(restored, MakeRun(
                "run-B2",
                PerformanceProofVariant.Boost,
                capture.AddSeconds(122),
                60.0,
                MakeFrames(4000, 14.0, 38.0)));
            Assert(transition.Status == PerformanceProofSubmissionStatus.Accepted &&
                   transition.NextStep.StepNumber == 4 &&
                   transition.NextStep.ExpectedVariant == PerformanceProofVariant.Baseline &&
                   !transition.NextStep.RequiresBoost &&
                   transition.NextStep.PairId == secondPair,
                "Step four must request baseline in the same B-to-A pair.");

            transition = SubmitCurrent(restored, MakeRun(
                "run-A2",
                PerformanceProofVariant.Baseline,
                capture.AddSeconds(183),
                60.0,
                MakeFrames(3600, 16.5, 58.0)));
            Assert(transition.Status == PerformanceProofSubmissionStatus.Completed &&
                   transition.State == PerformanceProofCoordinatorState.Completed &&
                   transition.NextStep == null &&
                   transition.FinalResult != null &&
                   transition.FinalResult.Status == PerformanceProofStatus.Completed &&
                   transition.FinalResult.PairCount == 2 &&
                   transition.FinalResult.Verdict == PerformanceProofVerdict.Positive &&
                   transition.FinalResult.Conclusive,
                "Four AB/BA runs must produce the real comparison result.");

            Assert(PerformanceProofCoordinator.TryRestore(
                transition.Checkpoint,
                out restored,
                out restoreError),
                "Completed checkpoint must restore: " + restoreError);
            resumed = restored.GetSnapshot();
            Assert(resumed.State == PerformanceProofCoordinatorState.Completed &&
                   resumed.CompletedSteps == 4 &&
                   resumed.NextStep == null &&
                   resumed.FinalResult != null &&
                   resumed.FinalResult.Verdict == PerformanceProofVerdict.Positive,
                "Completed result must be recomputed and retained across restart.");
            Assert(restored.SubmitRun(
                MakeRun(
                    "too-late",
                    PerformanceProofVariant.Baseline,
                    capture.AddSeconds(244),
                    60.0,
                    MakeFrames(3600, 16.0, 60.0)),
                secondPair,
                4).Status ==
                PerformanceProofSubmissionStatus.TerminalState,
                "Terminal proof must never accept another run.");
        }

        private static void TestNeutralResultAndIsolation()
        {
            PerformanceProofCoordinator coordinator = Start();
            DateTime capture = DateTime.UtcNow.AddSeconds(1);
            List<double> frames = MakeFrames(3600, 17.0, 55.0);
            Assert(SubmitCurrent(coordinator, MakeRun(
                "neutral-A1",
                PerformanceProofVariant.Baseline,
                capture,
                60.0,
                new List<double>(frames))).Status ==
                PerformanceProofSubmissionStatus.Accepted,
                "Neutral step one failed.");
            Assert(SubmitCurrent(coordinator, MakeRun(
                "neutral-B1",
                PerformanceProofVariant.Boost,
                capture.AddSeconds(61),
                60.0,
                new List<double>(frames))).Status ==
                PerformanceProofSubmissionStatus.Accepted,
                "Neutral step two failed.");
            Assert(SubmitCurrent(coordinator, MakeRun(
                "neutral-B2",
                PerformanceProofVariant.Boost,
                capture.AddSeconds(122),
                60.0,
                new List<double>(frames))).Status ==
                PerformanceProofSubmissionStatus.Accepted,
                "Neutral step three failed.");
            PerformanceProofTransition completed = SubmitCurrent(coordinator, MakeRun(
                "neutral-A2",
                PerformanceProofVariant.Baseline,
                capture.AddSeconds(183),
                60.0,
                new List<double>(frames)));
            Assert(completed.Status == PerformanceProofSubmissionStatus.Completed &&
                   completed.FinalResult.Verdict == PerformanceProofVerdict.Neutral &&
                   !completed.FinalResult.Conclusive,
                "No measurable effect must complete with an explicit neutral verdict.");

            string originalSummary = completed.FinalResult.Summary;
            double originalBaseline = completed.FinalResult.Baseline.AverageFps;
            int originalDeltaCount =
                completed.FinalResult.PairAverageFpsDeltasPercent.Count;
            completed.FinalResult.Summary = "forged";
            completed.FinalResult.Baseline.AverageFps = -1.0;
            completed.FinalResult.PairAverageFpsDeltasPercent.Clear();
            PerformanceProofResult fresh = coordinator.GetSnapshot().FinalResult;
            Assert(fresh.Summary == originalSummary &&
                   Math.Abs(fresh.Baseline.AverageFps - originalBaseline) < 0.000001 &&
                   fresh.PairAverageFpsDeltasPercent.Count == originalDeltaCount,
                "Caller mutation must not corrupt the coordinator's final evidence.");
        }

        private static void TestFailClosedMismatchAndOrdering()
        {
            DateTime capture = DateTime.UtcNow.AddSeconds(1);
            PerformanceProofCoordinator coordinator = Start();
            PerformanceProofRun wrongTarget = MakeRun(
                "wrong-target",
                PerformanceProofVariant.Baseline,
                capture,
                60.0,
                MakeFrames(3600, 16.0, 55.0));
            wrongTarget.TargetIdentity = "target-v1:" + new string('B', 64);
            PerformanceProofTransition transition = SubmitCurrent(coordinator, wrongTarget);
            AssertFailed(transition, PerformanceProofCoordinatorFailure.TargetMismatch,
                "Target mismatch must fail closed.");

            PerformanceProofCoordinator restored;
            string error;
            Assert(PerformanceProofCoordinator.TryRestore(
                transition.Checkpoint,
                out restored,
                out error),
                "Failed target-mismatch checkpoint must restore.");
            Assert(restored.GetSnapshot().Failure ==
                PerformanceProofCoordinatorFailure.TargetMismatch,
                "Failure evidence must survive restart.");

            coordinator = Start();
            PerformanceProofRun wrongContext = MakeRun(
                "wrong-context",
                PerformanceProofVariant.Baseline,
                capture,
                60.0,
                MakeFrames(3600, 16.0, 55.0));
            wrongContext.ContextKey = "proof-v1:" + new string('C', 64);
            AssertFailed(
                SubmitCurrent(coordinator, wrongContext),
                PerformanceProofCoordinatorFailure.ContextMismatch,
                "Context mismatch must fail closed.");

            coordinator = Start();
            AssertFailed(
                SubmitCurrent(coordinator, MakeRun(
                    "wrong-order",
                    PerformanceProofVariant.Boost,
                    capture,
                    60.0,
                    MakeFrames(3600, 14.0, 40.0))),
                PerformanceProofCoordinatorFailure.UnexpectedVariant,
                "Unexpected Boost state must fail closed.");

            coordinator = Start();
            transition = SubmitCurrent(coordinator, MakeRun(
                "same-id",
                PerformanceProofVariant.Baseline,
                capture,
                60.0,
                MakeFrames(3600, 16.0, 55.0)));
            Assert(transition.Status == PerformanceProofSubmissionStatus.Accepted,
                "Duplicate setup run must be accepted first.");
            AssertFailed(
                SubmitCurrent(coordinator, MakeRun(
                    "same-id",
                    PerformanceProofVariant.Boost,
                    capture.AddSeconds(61),
                    60.0,
                    MakeFrames(4000, 14.0, 38.0))),
                PerformanceProofCoordinatorFailure.DuplicateRun,
                "Duplicate run ID must fail closed.");

            coordinator = Start();
            PerformanceProofRun invalidTime = MakeRun(
                "local-time",
                PerformanceProofVariant.Baseline,
                DateTime.Now,
                60.0,
                MakeFrames(3600, 16.0, 55.0));
            transition = SubmitCurrent(coordinator, invalidTime);
            Assert(transition.Status == PerformanceProofSubmissionStatus.Rejected &&
                   transition.NextStep != null && transition.NextStep.StepNumber == 1,
                "Non-UTC timestamp must be recoverably rejected.");

            coordinator = Start();
            PerformanceProofRun futureTime = MakeRun(
                "future-time",
                PerformanceProofVariant.Baseline,
                DateTime.UtcNow.AddMinutes(10),
                60.0,
                MakeFrames(3600, 16.0, 55.0));
            transition = SubmitCurrent(coordinator, futureTime);
            Assert(transition.Status == PerformanceProofSubmissionStatus.Rejected &&
                   transition.NextStep != null && transition.NextStep.StepNumber == 1,
                "Implausibly future capture timestamp must be rejected.");
        }

        private static void TestCancellationAndSanitization()
        {
            PerformanceProofCoordinator coordinator = Start();
            PerformanceProofTransition accepted = SubmitCurrent(coordinator, MakeRun(
                "cancel-A",
                PerformanceProofVariant.Baseline,
                DateTime.UtcNow.AddSeconds(1),
                60.0,
                MakeFrames(3600, 16.0, 55.0)));
            Assert(accepted.Status == PerformanceProofSubmissionStatus.Accepted,
                "Cancellation setup run must be accepted.");
            string hostileReason =
                " user\r\nreason\0\u202E " + new string('X', 1000);
            PerformanceProofTransition cancelled = coordinator.Cancel(hostileReason);
            Assert(cancelled.State == PerformanceProofCoordinatorState.Cancelled &&
                   cancelled.Status == PerformanceProofSubmissionStatus.TerminalState &&
                   cancelled.NextStep == null &&
                   cancelled.Message.Length <= 512 &&
                   cancelled.Message.IndexOf('\r') < 0 &&
                   cancelled.Message.IndexOf('\n') < 0 &&
                   cancelled.Message.IndexOf('\0') < 0 &&
                   cancelled.Message.IndexOf('\u202E') < 0,
                "Cancellation must be terminal and its reason bounded/sanitized.");

            PerformanceProofCoordinator restored;
            string error;
            Assert(PerformanceProofCoordinator.TryRestore(
                cancelled.Checkpoint,
                out restored,
                out error),
                "Cancelled checkpoint must restore.");
            Assert(restored.GetSnapshot().State ==
                PerformanceProofCoordinatorState.Cancelled,
                "Cancellation must survive restart.");
        }

        private static void TestCheckpointIntegrityAndAtomicStore()
        {
            PerformanceProofCoordinator coordinator = Start();
            string checkpoint = coordinator.CreateCheckpoint();
            string[] lines = checkpoint.Split('\n');
            Assert(lines.Length == 3, "Checkpoint must be a versioned three-line envelope.");

            char replacement = lines[1][0] == 'A' ? 'B' : 'A';
            lines[1] = replacement + lines[1].Substring(1);
            string corrupted = string.Join("\n", lines);
            PerformanceProofCoordinator ignored;
            string error;
            Assert(!PerformanceProofCoordinator.TryRestore(
                corrupted,
                out ignored,
                out error) && ignored == null,
                "Payload corruption must be rejected by its checksum.");
            Assert(!PerformanceProofCoordinator.TryRestore(
                new string('A', PerformanceProofCoordinator.MaximumCheckpointTextLength + 1),
                out ignored,
                out error),
                "Oversized checkpoint must be rejected before decoding.");

            string path = Path.Combine(
                Path.GetTempPath(),
                "Boostix-ProofStore-" + Guid.NewGuid().ToString("N") + ".checkpoint");
            try
            {
                var store = new PerformanceProofCheckpointStore(path);
                Assert(store.TrySave(coordinator, out error),
                    "Initial atomic save failed: " + error);
                PerformanceProofCoordinator loaded;
                Assert(store.TryLoad(out loaded, out error),
                    "Atomic load failed: " + error);
                Assert(loaded.GetSnapshot().ProofId == coordinator.GetSnapshot().ProofId,
                    "Store load changed the proof identity.");

                PerformanceProofTransition accepted = SubmitCurrent(loaded, MakeRun(
                    "store-A",
                    PerformanceProofVariant.Baseline,
                    DateTime.UtcNow.AddSeconds(1),
                    60.0,
                    MakeFrames(3600, 16.0, 55.0)));
                Assert(accepted.Status == PerformanceProofSubmissionStatus.Accepted,
                    "Stored proof did not resume.");
                Assert(store.TrySave(loaded, out error),
                    "Atomic replacement save failed: " + error);
                Assert(store.TryLoad(out loaded, out error) &&
                       loaded.GetSnapshot().CompletedSteps == 1,
                    "Replacement checkpoint did not commit the new step.");

                string directory = Path.GetDirectoryName(path);
                string prefix = Path.GetFileName(path) + ".";
                foreach (string sibling in Directory.GetFiles(
                    directory,
                    Path.GetFileName(path) + ".*"))
                {
                    Assert(!Path.GetFileName(sibling).StartsWith(
                        prefix,
                        StringComparison.Ordinal),
                        "Atomic store left a temporary or backup file behind.");
                }
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        private static PerformanceProofCoordinator Start()
        {
            PerformanceProofCoordinator coordinator;
            string error;
            Assert(PerformanceProofCoordinator.TryStart(
                Target,
                Context,
                out coordinator,
                out error),
                "Could not start proof coordinator: " + error);
            return coordinator;
        }

        private static PerformanceProofTransition SubmitCurrent(
            PerformanceProofCoordinator coordinator,
            PerformanceProofRun run)
        {
            PerformanceProofCoordinatorSnapshot snapshot = coordinator.GetSnapshot();
            Assert(snapshot.NextStep != null,
                "Test attempted to submit without an issued step.");
            return coordinator.SubmitRun(
                run,
                snapshot.NextStep.PairId,
                snapshot.NextStep.StepNumber);
        }

        private static PerformanceProofRun MakeRun(
            string id,
            PerformanceProofVariant variant,
            DateTime capturedUtc,
            double durationSeconds,
            List<double> frameTimes)
        {
            return new PerformanceProofRun
            {
                RunId = id,
                TargetIdentity = Target,
                ContextKey = Context,
                Variant = variant,
                CapturedUtc = capturedUtc,
                CaptureDurationSeconds = durationSeconds,
                FrameTimesMs = frameTimes
            };
        }

        private static List<double> MakeFrames(
            int count,
            double regularMilliseconds,
            double slowMilliseconds)
        {
            var values = new List<double>(count);
            for (int index = 0; index < count; index++)
            {
                values.Add(index % 80 == 0
                    ? slowMilliseconds
                    : regularMilliseconds);
            }
            return values;
        }

        private static void AssertFailed(
            PerformanceProofTransition transition,
            PerformanceProofCoordinatorFailure failure,
            string message)
        {
            Assert(transition.Status == PerformanceProofSubmissionStatus.Failed &&
                   transition.State == PerformanceProofCoordinatorState.Failed &&
                   transition.NextStep == null,
                message);
            PerformanceProofCoordinator restored;
            string error;
            Assert(PerformanceProofCoordinator.TryRestore(
                transition.Checkpoint,
                out restored,
                out error),
                "Failed checkpoint must restore: " + error);
            Assert(restored.GetSnapshot().Failure == failure,
                "Restored checkpoint lost the expected failure code.");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
'@

New-Item -ItemType Directory -Path $tempRoot | Out-Null
try {
    [IO.File]::WriteAllText(
        $harnessPath,
        $harnessSource,
        [Text.UTF8Encoding]::new($true))

    $compilerOutput = & $compiler /nologo /warn:4 /target:exe `
        "/out:$outputPath" $proofSource $coordinatorSource $harnessPath 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Coordinator harness compilation failed:`n$($compilerOutput -join [Environment]::NewLine)"
    }

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $runtimeOutput = & $outputPath 2>&1
    $runtimeExitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousErrorActionPreference
    if ($runtimeExitCode -ne 0) {
        throw "Coordinator harness failed:`n$($runtimeOutput -join [Environment]::NewLine)"
    }

    $sourceText = [IO.File]::ReadAllText(
        $coordinatorSource,
        [Text.Encoding]::UTF8)
    $forbiddenPatterns = @(
        'Process\.GetProcess',
        'ProcessPriorityClass',
        'Microsoft\.Win32\.Registry',
        '\bpowercfg\b',
        'EmptyWorkingSet',
        'Stop-Process'
    )
    foreach ($pattern in $forbiddenPatterns) {
        if ($sourceText -match $pattern) {
            throw "Coordinator contains a forbidden machine-state operation: $pattern"
        }
    }

    $runtimeOutput | ForEach-Object { Write-Output $_ }
}
finally {
    if (Test-Path -LiteralPath $tempRoot -PathType Container) {
        $resolvedTemp = [IO.Path]::GetFullPath($tempRoot)
        $resolvedSystemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (-not $resolvedTemp.StartsWith(
            $resolvedSystemTemp,
            [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean unexpected test path: $resolvedTemp"
        }
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}
