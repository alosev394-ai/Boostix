using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Boostix
{
    internal enum PerformanceProofCoordinatorState
    {
        AwaitingRun,
        Completed,
        Cancelled,
        Failed
    }

    internal enum PerformanceProofCoordinatorFailure
    {
        None,
        InvalidRun,
        DuplicateRun,
        UnexpectedVariant,
        TargetMismatch,
        ContextMismatch,
        ComparisonFailed
    }

    internal enum PerformanceProofSubmissionStatus
    {
        Accepted,
        Completed,
        Rejected,
        Failed,
        TerminalState
    }

    internal sealed class PerformanceProofStep
    {
        public int StepNumber;
        public int TotalSteps;
        public string PairId;
        public PerformanceProofVariant ExpectedVariant;
        public string SequenceLabel;
        public string Instruction;
        public string TargetIdentity;
        public string ContextKey;

        public bool RequiresBoost
        {
            get { return ExpectedVariant == PerformanceProofVariant.Boost; }
        }
    }

    internal sealed class PerformanceProofCoordinatorSnapshot
    {
        public string ProofId;
        public PerformanceProofCoordinatorState State;
        public PerformanceProofCoordinatorFailure Failure;
        public string Message;
        public int CompletedSteps;
        public int TotalSteps;
        public string TargetIdentity;
        public string ContextKey;
        public PerformanceProofStep NextStep;
        public PerformanceProofResult FinalResult;
    }

    internal sealed class PerformanceProofTransition
    {
        public PerformanceProofSubmissionStatus Status;
        public PerformanceProofCoordinatorState State;
        public string Message;
        public PerformanceProofStep NextStep;
        public PerformanceProofResult FinalResult;
        public string Checkpoint;
    }

    /// <summary>
    /// Coordinates a four-run proof without touching processes, priorities,
    /// power settings or any other machine state. The caller owns capture and
    /// explicitly supplies each completed run.
    /// </summary>
    internal sealed class PerformanceProofCoordinator
    {
        internal const int CheckpointFormatVersion = 1;
        internal const int ProofAlgorithmVersion = 1;
        internal const int TotalStepCount = 4;
        internal const int MaximumFramesPerRun = 200000;
        internal const int MaximumCheckpointBytes = 8 * 1024 * 1024;
        internal const int MaximumCheckpointTextLength = 12 * 1024 * 1024;

        private const int PayloadMagic = 0x43505842;
        private const int MaximumIdentifierLength = 128;
        private const int MaximumIdentityLength = 128;
        private const int MaximumMessageLength = 512;
        private const double MaximumCaptureDurationSeconds = 600.0;
        private const double MaximumCaptureClockSkewMinutes = 5.0;
        private const string CheckpointHeader = "BOOSTIX-PROOF-CHECKPOINT/1";

        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(false, true);
        private static readonly PerformanceProofVariant[] ExpectedVariants =
        {
            PerformanceProofVariant.Baseline,
            PerformanceProofVariant.Boost,
            PerformanceProofVariant.Boost,
            PerformanceProofVariant.Baseline
        };

        private readonly object sync = new object();
        private readonly List<PerformanceProofRun> runs;
        private string proofId;
        private string targetIdentity;
        private string contextKey;
        private string firstPairId;
        private string secondPairId;
        private DateTime startedUtc;
        private int stepIndex;
        private PerformanceProofCoordinatorState state;
        private PerformanceProofCoordinatorFailure failure;
        private string message;
        private PerformanceProofResult finalResult;

        private PerformanceProofCoordinator()
        {
            runs = new List<PerformanceProofRun>(TotalStepCount);
            message = string.Empty;
        }

        public static bool TryStart(
            string targetIdentity,
            string contextKey,
            out PerformanceProofCoordinator coordinator,
            out string error)
        {
            coordinator = null;
            error = string.Empty;
            string normalizedTarget = (targetIdentity ?? string.Empty).Trim();
            string normalizedContext = (contextKey ?? string.Empty).Trim();
            if (!IsVersionedDigest(normalizedTarget, "target-v"))
            {
                error = "Не удалось зафиксировать точную идентичность исполняемого файла.";
                return false;
            }
            if (!IsVersionedDigest(normalizedContext, "proof-v"))
            {
                error = "Не удалось зафиксировать точный сценарий измерения.";
                return false;
            }

            string id = Guid.NewGuid().ToString("N");
            coordinator = new PerformanceProofCoordinator
            {
                proofId = id,
                targetIdentity = normalizedTarget,
                contextKey = normalizedContext,
                firstPairId = id + "-P1",
                secondPairId = id + "-P2",
                startedUtc = DateTime.UtcNow,
                stepIndex = 0,
                state = PerformanceProofCoordinatorState.AwaitingRun,
                failure = PerformanceProofCoordinatorFailure.None
            };
            return true;
        }

        public PerformanceProofCoordinatorSnapshot GetSnapshot()
        {
            lock (sync)
            {
                return CreateSnapshotLocked();
            }
        }

        public PerformanceProofTransition SubmitRun(
            PerformanceProofRun run,
            string issuedPairId,
            int issuedStepNumber)
        {
            lock (sync)
            {
                if (state != PerformanceProofCoordinatorState.AwaitingRun)
                {
                    return CreateTransitionLocked(
                        PerformanceProofSubmissionStatus.TerminalState,
                        "Этот доказательный тест уже завершён.");
                }

                string expectedPairId = stepIndex < 2
                    ? firstPairId
                    : secondPairId;
                if (issuedStepNumber != stepIndex + 1 ||
                    !string.Equals(
                        issuedPairId,
                        expectedPairId,
                        StringComparison.Ordinal))
                {
                    return CreateTransitionLocked(
                        PerformanceProofSubmissionStatus.Rejected,
                        "Результат относится к другому этапу или паре. Текущий этап не изменён.");
                }

                if (run == null)
                {
                    return CreateTransitionLocked(
                        PerformanceProofSubmissionStatus.Rejected,
                        "Замер не содержит данных. Повторите текущий этап.");
                }

                if (!string.Equals(
                        run.TargetIdentity,
                        targetIdentity,
                        StringComparison.Ordinal))
                {
                    return FailLocked(
                        PerformanceProofCoordinatorFailure.TargetMismatch,
                        "Исполняемый файл изменился. Доказательный тест остановлен.");
                }
                if (!string.Equals(
                        run.ContextKey,
                        contextKey,
                        StringComparison.Ordinal))
                {
                    return FailLocked(
                        PerformanceProofCoordinatorFailure.ContextMismatch,
                        "Сценарий или параметры экрана изменились. Доказательный тест остановлен.");
                }
                if (run.Variant != ExpectedVariants[stepIndex])
                {
                    return FailLocked(
                        PerformanceProofCoordinatorFailure.UnexpectedVariant,
                        "Состояние Boost не соответствует текущему этапу. Тест остановлен.");
                }

                string runId = (run.RunId ?? string.Empty).Trim();
                if (!IsSafeIdentifier(runId))
                {
                    return CreateTransitionLocked(
                        PerformanceProofSubmissionStatus.Rejected,
                        "Идентификатор замера недействителен. Повторите текущий этап.");
                }
                foreach (PerformanceProofRun existing in runs)
                {
                    if (string.Equals(existing.RunId, runId, StringComparison.Ordinal))
                    {
                        return FailLocked(
                            PerformanceProofCoordinatorFailure.DuplicateRun,
                            "Повторно передан уже учтённый замер. Тест остановлен.");
                    }
                }

                PerformanceProofRun frozen;
                string validationError;
                if (!TryFreezeAndValidateRun(run, out frozen, out validationError))
                {
                    return CreateTransitionLocked(
                        PerformanceProofSubmissionStatus.Rejected,
                        validationError);
                }
                if (frozen.CapturedUtc < startedUtc ||
                    frozen.CapturedUtc > DateTime.UtcNow.AddMinutes(
                        MaximumCaptureClockSkewMinutes) ||
                    (runs.Count > 0 &&
                     frozen.CapturedUtc <= runs[runs.Count - 1].CapturedUtc))
                {
                    return CreateTransitionLocked(
                        PerformanceProofSubmissionStatus.Rejected,
                        "Время замера нарушает порядок этапов. Повторите текущий этап.");
                }

                runs.Add(frozen);
                stepIndex++;
                if (stepIndex < TotalStepCount)
                {
                    message = "Этап принят. Подготовьте следующий 60-секундный замер.";
                    return CreateTransitionLocked(
                        PerformanceProofSubmissionStatus.Accepted,
                        message);
                }

                finalResult = PerformanceProofService.ComparePairs(
                    BuildPairsLocked());
                if (finalResult == null ||
                    finalResult.Status != PerformanceProofStatus.Completed)
                {
                    return FailLocked(
                        PerformanceProofCoordinatorFailure.ComparisonFailed,
                        "Финальное сравнение не прошло проверку целостности.");
                }

                state = PerformanceProofCoordinatorState.Completed;
                failure = PerformanceProofCoordinatorFailure.None;
                message = finalResult.Summary ?? string.Empty;
                return CreateTransitionLocked(
                    PerformanceProofSubmissionStatus.Completed,
                    message);
            }
        }

        public PerformanceProofTransition Cancel(string reason)
        {
            lock (sync)
            {
                if (state != PerformanceProofCoordinatorState.AwaitingRun)
                {
                    return CreateTransitionLocked(
                        PerformanceProofSubmissionStatus.TerminalState,
                        "Этот доказательный тест уже завершён.");
                }
                state = PerformanceProofCoordinatorState.Cancelled;
                failure = PerformanceProofCoordinatorFailure.None;
                message = SanitizeMessage(
                    reason,
                    "Доказательный тест отменён пользователем.");
                return CreateTransitionLocked(
                    PerformanceProofSubmissionStatus.TerminalState,
                    message);
            }
        }

        public string CreateCheckpoint()
        {
            lock (sync)
            {
                return CreateCheckpointLocked();
            }
        }

        public static bool TryRestore(
            string checkpoint,
            out PerformanceProofCoordinator coordinator,
            out string error)
        {
            coordinator = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(checkpoint) ||
                checkpoint.Length > MaximumCheckpointTextLength)
            {
                error = "Чекпоинт доказательного теста отсутствует или слишком велик.";
                return false;
            }

            try
            {
                string normalized = checkpoint.Replace("\r", string.Empty);
                string[] lines = normalized.Split(new[] { '\n' });
                if (lines.Length != 3 ||
                    !string.Equals(lines[0], CheckpointHeader, StringComparison.Ordinal) ||
                    string.IsNullOrEmpty(lines[1]) ||
                    string.IsNullOrEmpty(lines[2]))
                {
                    error = "Формат чекпоинта доказательного теста повреждён.";
                    return false;
                }

                byte[] payload = Convert.FromBase64String(lines[1]);
                if (payload.Length == 0 || payload.Length > MaximumCheckpointBytes)
                {
                    error = "Размер чекпоинта доказательного теста недопустим.";
                    return false;
                }
                byte[] expectedHash = ParseSha256(lines[2]);
                if (expectedHash == null)
                {
                    error = "Контрольная сумма чекпоинта недействительна.";
                    return false;
                }
                byte[] actualHash;
                using (SHA256 sha = SHA256.Create())
                {
                    actualHash = sha.ComputeHash(payload);
                }
                if (!FixedTimeEquals(expectedHash, actualHash))
                {
                    error = "Контрольная сумма чекпоинта не совпадает.";
                    return false;
                }

                PerformanceProofCoordinator restored = ReadPayload(payload);
                string validationError;
                if (!restored.TryValidateRestoredState(out validationError))
                {
                    error = validationError;
                    return false;
                }
                coordinator = restored;
                return true;
            }
            catch
            {
                coordinator = null;
                error = "Чекпоинт доказательного теста повреждён.";
                return false;
            }
        }

        private PerformanceProofCoordinatorSnapshot CreateSnapshotLocked()
        {
            return new PerformanceProofCoordinatorSnapshot
            {
                ProofId = proofId,
                State = state,
                Failure = failure,
                Message = message ?? string.Empty,
                CompletedSteps = stepIndex,
                TotalSteps = TotalStepCount,
                TargetIdentity = targetIdentity,
                ContextKey = contextKey,
                NextStep = state == PerformanceProofCoordinatorState.AwaitingRun
                    ? CreateStepLocked(stepIndex)
                    : null,
                FinalResult = CloneResult(finalResult)
            };
        }

        private PerformanceProofStep CreateStepLocked(int index)
        {
            if (index < 0 || index >= TotalStepCount)
            {
                return null;
            }
            PerformanceProofVariant variant = ExpectedVariants[index];
            bool firstPair = index < 2;
            return new PerformanceProofStep
            {
                StepNumber = index + 1,
                TotalSteps = TotalStepCount,
                PairId = firstPair ? firstPairId : secondPairId,
                ExpectedVariant = variant,
                SequenceLabel = firstPair ? "A→B" : "B→A",
                Instruction = variant == PerformanceProofVariant.Boost
                    ? "Включите Boost, не меняйте сцену и запустите 60-секундный замер."
                    : "Отключите Boost, не меняйте сцену и запустите 60-секундный замер.",
                TargetIdentity = targetIdentity,
                ContextKey = contextKey
            };
        }

        private PerformanceProofTransition CreateTransitionLocked(
            PerformanceProofSubmissionStatus submissionStatus,
            string transitionMessage)
        {
            return new PerformanceProofTransition
            {
                Status = submissionStatus,
                State = state,
                Message = transitionMessage ?? string.Empty,
                NextStep = state == PerformanceProofCoordinatorState.AwaitingRun
                    ? CreateStepLocked(stepIndex)
                    : null,
                FinalResult = CloneResult(finalResult),
                Checkpoint = CreateCheckpointLocked()
            };
        }

        private PerformanceProofTransition FailLocked(
            PerformanceProofCoordinatorFailure failureCode,
            string failureMessage)
        {
            state = PerformanceProofCoordinatorState.Failed;
            failure = failureCode;
            message = SanitizeMessage(failureMessage, "Доказательный тест остановлен.");
            return CreateTransitionLocked(
                PerformanceProofSubmissionStatus.Failed,
                message);
        }

        private IList<PerformanceProofPair> BuildPairsLocked()
        {
            return new List<PerformanceProofPair>
            {
                new PerformanceProofPair
                {
                    PairId = firstPairId,
                    First = runs[0],
                    Second = runs[1]
                },
                new PerformanceProofPair
                {
                    PairId = secondPairId,
                    First = runs[2],
                    Second = runs[3]
                }
            };
        }

        private static bool TryFreezeAndValidateRun(
            PerformanceProofRun run,
            out PerformanceProofRun frozen,
            out string error)
        {
            frozen = null;
            error = string.Empty;
            if (run.FrameTimesMs == null ||
                run.FrameTimesMs.Count > MaximumFramesPerRun)
            {
                error = "Количество кадров в замере недопустимо.";
                return false;
            }
            if (run.CapturedUtc == DateTime.MinValue ||
                run.CapturedUtc.Kind != DateTimeKind.Utc ||
                double.IsNaN(run.CaptureDurationSeconds) ||
                double.IsInfinity(run.CaptureDurationSeconds) ||
                run.CaptureDurationSeconds < PerformanceProofService.MinimumDurationSeconds ||
                run.CaptureDurationSeconds > MaximumCaptureDurationSeconds)
            {
                error = "Замер должен длиться не менее 50 секунд.";
                return false;
            }

            var frameCopy = new List<double>(run.FrameTimesMs.Count);
            try
            {
                foreach (double frameTime in run.FrameTimesMs)
                {
                    frameCopy.Add(frameTime);
                }
            }
            catch
            {
                error = "Данные кадров изменились во время проверки. Повторите этап.";
                return false;
            }

            PerformanceFrameMetrics metrics =
                PerformanceProofService.CalculateMetrics(frameCopy);
            if (metrics.FrameCount < PerformanceProofService.MinimumFramesPerRun)
            {
                error = "В замере недостаточно корректных кадров. Повторите этап.";
                return false;
            }

            double observedDurationMilliseconds = 0.0;
            foreach (double frameTime in frameCopy)
            {
                if (!double.IsNaN(frameTime) &&
                    !double.IsInfinity(frameTime) &&
                    frameTime > 0.0 &&
                    frameTime <= 10000.0)
                {
                    observedDurationMilliseconds += frameTime;
                }
            }
            if (observedDurationMilliseconds <
                PerformanceProofService.MinimumDurationSeconds * 1000.0)
            {
                error = "Покадровые данные покрывают менее 50 секунд. Повторите этап.";
                return false;
            }

            frozen = new PerformanceProofRun
            {
                RunId = (run.RunId ?? string.Empty).Trim(),
                TargetIdentity = run.TargetIdentity,
                ContextKey = run.ContextKey,
                Variant = run.Variant,
                CapturedUtc = run.CapturedUtc,
                CaptureDurationSeconds = run.CaptureDurationSeconds,
                FrameTimesMs = frameCopy
            };
            return true;
        }

        private string CreateCheckpointLocked()
        {
            byte[] payload;
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, StrictUtf8, true))
                {
                    writer.Write(PayloadMagic);
                    writer.Write(CheckpointFormatVersion);
                    writer.Write(ProofAlgorithmVersion);
                    WriteBoundedString(writer, proofId, MaximumIdentifierLength);
                    WriteBoundedString(writer, targetIdentity, MaximumIdentityLength);
                    WriteBoundedString(writer, contextKey, MaximumIdentityLength);
                    WriteBoundedString(writer, firstPairId, MaximumIdentifierLength);
                    WriteBoundedString(writer, secondPairId, MaximumIdentifierLength);
                    writer.Write(startedUtc.Ticks);
                    writer.Write((int)state);
                    writer.Write((int)failure);
                    WriteBoundedString(writer, message, MaximumMessageLength);
                    writer.Write(stepIndex);
                    writer.Write(runs.Count);
                    foreach (PerformanceProofRun run in runs)
                    {
                        WriteRun(writer, run);
                    }
                }
                payload = stream.ToArray();
            }
            if (payload.Length > MaximumCheckpointBytes)
            {
                throw new InvalidOperationException(
                    "Proof checkpoint exceeded its size limit.");
            }

            byte[] hash;
            using (SHA256 sha = SHA256.Create())
            {
                hash = sha.ComputeHash(payload);
            }
            return CheckpointHeader + "\n" +
                Convert.ToBase64String(payload) + "\n" +
                ToHex(hash);
        }

        private static void WriteRun(BinaryWriter writer, PerformanceProofRun run)
        {
            WriteBoundedString(writer, run.RunId, MaximumIdentifierLength);
            WriteBoundedString(writer, run.TargetIdentity, MaximumIdentityLength);
            WriteBoundedString(writer, run.ContextKey, MaximumIdentityLength);
            writer.Write((int)run.Variant);
            writer.Write(run.CapturedUtc.Ticks);
            writer.Write(run.CaptureDurationSeconds);
            int count = run.FrameTimesMs == null ? 0 : run.FrameTimesMs.Count;
            if (count < 0 || count > MaximumFramesPerRun)
            {
                throw new InvalidOperationException("Invalid proof frame count.");
            }
            writer.Write(count);
            for (int index = 0; index < count; index++)
            {
                writer.Write(run.FrameTimesMs[index]);
            }
        }

        private static PerformanceProofCoordinator ReadPayload(byte[] payload)
        {
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream, StrictUtf8, true))
            {
                if (reader.ReadInt32() != PayloadMagic ||
                    reader.ReadInt32() != CheckpointFormatVersion ||
                    reader.ReadInt32() != ProofAlgorithmVersion)
                {
                    throw new InvalidDataException("Unsupported proof checkpoint.");
                }

                var restored = new PerformanceProofCoordinator
                {
                    proofId = ReadBoundedString(
                        reader,
                        stream,
                        MaximumIdentifierLength),
                    targetIdentity = ReadBoundedString(
                        reader,
                        stream,
                        MaximumIdentityLength),
                    contextKey = ReadBoundedString(
                        reader,
                        stream,
                        MaximumIdentityLength),
                    firstPairId = ReadBoundedString(
                        reader,
                        stream,
                        MaximumIdentifierLength),
                    secondPairId = ReadBoundedString(
                        reader,
                        stream,
                        MaximumIdentifierLength),
                    startedUtc = ReadUtc(reader.ReadInt64()),
                    state = (PerformanceProofCoordinatorState)reader.ReadInt32(),
                    failure = (PerformanceProofCoordinatorFailure)reader.ReadInt32(),
                    message = ReadBoundedString(
                        reader,
                        stream,
                        MaximumMessageLength),
                    stepIndex = reader.ReadInt32()
                };

                int runCount = reader.ReadInt32();
                if (runCount < 0 || runCount > TotalStepCount)
                {
                    throw new InvalidDataException("Invalid proof run count.");
                }
                for (int index = 0; index < runCount; index++)
                {
                    restored.runs.Add(ReadRun(reader, stream));
                }
                if (stream.Position != stream.Length)
                {
                    throw new InvalidDataException("Unexpected proof payload data.");
                }
                return restored;
            }
        }

        private static PerformanceProofRun ReadRun(
            BinaryReader reader,
            Stream stream)
        {
            var run = new PerformanceProofRun
            {
                RunId = ReadBoundedString(
                    reader,
                    stream,
                    MaximumIdentifierLength),
                TargetIdentity = ReadBoundedString(
                    reader,
                    stream,
                    MaximumIdentityLength),
                ContextKey = ReadBoundedString(
                    reader,
                    stream,
                    MaximumIdentityLength),
                Variant = (PerformanceProofVariant)reader.ReadInt32(),
                CapturedUtc = ReadUtc(reader.ReadInt64()),
                CaptureDurationSeconds = reader.ReadDouble()
            };
            int frameCount = reader.ReadInt32();
            if (frameCount < 0 || frameCount > MaximumFramesPerRun ||
                stream.Length - stream.Position < frameCount * sizeof(double))
            {
                throw new InvalidDataException("Invalid proof frame payload.");
            }
            run.FrameTimesMs = new List<double>(frameCount);
            for (int index = 0; index < frameCount; index++)
            {
                run.FrameTimesMs.Add(reader.ReadDouble());
            }
            return run;
        }

        private bool TryValidateRestoredState(out string error)
        {
            error = "Чекпоинт доказательного теста не прошёл проверку целостности.";
            if (!IsProofIdentifier(proofId) ||
                !string.Equals(
                    firstPairId,
                    proofId + "-P1",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    secondPairId,
                    proofId + "-P2",
                    StringComparison.Ordinal) ||
                !IsVersionedDigest(targetIdentity, "target-v") ||
                !IsVersionedDigest(contextKey, "proof-v") ||
                startedUtc == DateTime.MinValue ||
                startedUtc > DateTime.UtcNow.AddDays(1) ||
                !Enum.IsDefined(typeof(PerformanceProofCoordinatorState), state) ||
                !Enum.IsDefined(typeof(PerformanceProofCoordinatorFailure), failure) ||
                stepIndex < 0 || stepIndex > TotalStepCount ||
                runs.Count != stepIndex)
            {
                return false;
            }

            if ((state == PerformanceProofCoordinatorState.AwaitingRun &&
                 (stepIndex >= TotalStepCount ||
                  failure != PerformanceProofCoordinatorFailure.None)) ||
                (state == PerformanceProofCoordinatorState.Completed &&
                 (stepIndex != TotalStepCount ||
                  failure != PerformanceProofCoordinatorFailure.None)) ||
                (state == PerformanceProofCoordinatorState.Cancelled &&
                 (stepIndex >= TotalStepCount ||
                  failure != PerformanceProofCoordinatorFailure.None)) ||
                (state == PerformanceProofCoordinatorState.Failed &&
                 (failure == PerformanceProofCoordinatorFailure.None ||
                  (failure == PerformanceProofCoordinatorFailure.ComparisonFailed
                      ? stepIndex != TotalStepCount
                      : stepIndex >= TotalStepCount))))
            {
                return false;
            }

            var runIds = new HashSet<string>(StringComparer.Ordinal);
            DateTime previousCapture = DateTime.MinValue;
            for (int index = 0; index < runs.Count; index++)
            {
                PerformanceProofRun run = runs[index];
                PerformanceProofRun ignored;
                string ignoredError;
                if (run == null ||
                    !string.Equals(run.TargetIdentity, targetIdentity, StringComparison.Ordinal) ||
                    !string.Equals(run.ContextKey, contextKey, StringComparison.Ordinal) ||
                    run.Variant != ExpectedVariants[index] ||
                    !IsSafeIdentifier(run.RunId) ||
                    !runIds.Add(run.RunId) ||
                    !TryFreezeAndValidateRun(run, out ignored, out ignoredError) ||
                    run.CapturedUtc < startedUtc ||
                    run.CapturedUtc > DateTime.UtcNow.AddMinutes(
                        MaximumCaptureClockSkewMinutes) ||
                    (previousCapture != DateTime.MinValue &&
                     run.CapturedUtc <= previousCapture))
                {
                    return false;
                }
                previousCapture = run.CapturedUtc;
            }

            message = SanitizeMessage(message, string.Empty);
            if (state == PerformanceProofCoordinatorState.Completed)
            {
                finalResult = PerformanceProofService.ComparePairs(
                    BuildPairsLocked());
                if (finalResult == null ||
                    finalResult.Status != PerformanceProofStatus.Completed)
                {
                    return false;
                }
                message = finalResult.Summary ?? string.Empty;
            }
            error = string.Empty;
            return true;
        }

        private static void WriteBoundedString(
            BinaryWriter writer,
            string value,
            int maximumCharacters)
        {
            string candidate = value ?? string.Empty;
            if (candidate.Length > maximumCharacters)
            {
                throw new InvalidOperationException("Proof checkpoint string is too long.");
            }
            byte[] bytes = StrictUtf8.GetBytes(candidate);
            int maximumBytes = maximumCharacters * 4;
            if (bytes.Length > maximumBytes)
            {
                throw new InvalidOperationException("Proof checkpoint string is too large.");
            }
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadBoundedString(
            BinaryReader reader,
            Stream stream,
            int maximumCharacters)
        {
            int length = reader.ReadInt32();
            int maximumBytes = maximumCharacters * 4;
            if (length < 0 || length > maximumBytes ||
                stream.Length - stream.Position < length)
            {
                throw new InvalidDataException("Invalid proof checkpoint string.");
            }
            string value = StrictUtf8.GetString(reader.ReadBytes(length));
            if (value.Length > maximumCharacters)
            {
                throw new InvalidDataException("Proof checkpoint string is too long.");
            }
            return value;
        }

        private static DateTime ReadUtc(long ticks)
        {
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            {
                throw new InvalidDataException("Invalid proof timestamp.");
            }
            return new DateTime(ticks, DateTimeKind.Utc);
        }

        private static bool IsVersionedDigest(string value, string prefix)
        {
            if (string.IsNullOrEmpty(value) ||
                value.Length > MaximumIdentityLength ||
                !value.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
            int colon = value.IndexOf(':');
            if (colon <= prefix.Length || value.Length - colon - 1 != 64)
            {
                return false;
            }
            for (int index = prefix.Length; index < colon; index++)
            {
                if (!char.IsDigit(value[index]))
                {
                    return false;
                }
            }
            for (int index = colon + 1; index < value.Length; index++)
            {
                if (!Uri.IsHexDigit(value[index]))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsSafeIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                value.Length > MaximumIdentifierLength)
            {
                return false;
            }
            foreach (char character in value)
            {
                if (!(char.IsLetterOrDigit(character) ||
                      character == '-' ||
                      character == '_' ||
                      character == '.' ||
                      character == ':'))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsProofIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 32)
            {
                return false;
            }
            foreach (char character in value)
            {
                if (!Uri.IsHexDigit(character))
                {
                    return false;
                }
            }
            return true;
        }

        private static string SanitizeMessage(string value, string fallback)
        {
            string candidate = string.IsNullOrWhiteSpace(value)
                ? fallback
                : value;
            var builder = new StringBuilder();
            bool previousSpace = false;
            foreach (char character in candidate ?? string.Empty)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(
                    character);
                if (char.IsControl(character) ||
                    char.IsWhiteSpace(character) ||
                    category == UnicodeCategory.Format ||
                    category == UnicodeCategory.Surrogate ||
                    category == UnicodeCategory.PrivateUse ||
                    category == UnicodeCategory.OtherNotAssigned)
                {
                    if (!previousSpace && builder.Length > 0)
                    {
                        builder.Append(' ');
                        previousSpace = true;
                    }
                }
                else
                {
                    builder.Append(character);
                    previousSpace = false;
                }
                if (builder.Length >= MaximumMessageLength)
                {
                    break;
                }
            }
            return builder.ToString().Trim();
        }

        private static PerformanceProofResult CloneResult(
            PerformanceProofResult source)
        {
            if (source == null)
            {
                return null;
            }
            return new PerformanceProofResult
            {
                Status = source.Status,
                Verdict = source.Verdict,
                Available = source.Available,
                Conclusive = source.Conclusive,
                Summary = source.Summary ?? string.Empty,
                TargetIdentity = source.TargetIdentity,
                ContextKey = source.ContextKey,
                PairCount = source.PairCount,
                Baseline = CloneMetrics(source.Baseline),
                Boost = CloneMetrics(source.Boost),
                AverageFpsDelta = source.AverageFpsDelta,
                AverageFpsDeltaPercent = source.AverageFpsDeltaPercent,
                OnePercentLowFpsDelta = source.OnePercentLowFpsDelta,
                P95FrameTimeReductionMs = source.P95FrameTimeReductionMs,
                P99FrameTimeReductionMs = source.P99FrameTimeReductionMs,
                FramesOver33PerThousandReduction =
                    source.FramesOver33PerThousandReduction,
                FramesOver50PerThousandReduction =
                    source.FramesOver50PerThousandReduction,
                FramesOver100PerThousandReduction =
                    source.FramesOver100PerThousandReduction,
                VariabilityPercent = source.VariabilityPercent,
                RequiredEffectPercent = source.RequiredEffectPercent,
                PairAverageFpsDeltasPercent =
                    source.PairAverageFpsDeltasPercent == null
                        ? new List<double>()
                        : new List<double>(source.PairAverageFpsDeltasPercent)
            };
        }

        private static PerformanceFrameMetrics CloneMetrics(
            PerformanceFrameMetrics source)
        {
            if (source == null)
            {
                return null;
            }
            return new PerformanceFrameMetrics
            {
                FrameCount = source.FrameCount,
                RejectedFrameCount = source.RejectedFrameCount,
                AverageFps = source.AverageFps,
                OnePercentLowFps = source.OnePercentLowFps,
                P95FrameTimeMs = source.P95FrameTimeMs,
                P99FrameTimeMs = source.P99FrameTimeMs,
                FramesOver33Ms = source.FramesOver33Ms,
                FramesOver50Ms = source.FramesOver50Ms,
                FramesOver100Ms = source.FramesOver100Ms
            };
        }

        private static byte[] ParseSha256(string value)
        {
            string candidate = (value ?? string.Empty).Trim();
            if (candidate.Length != 64)
            {
                return null;
            }
            var bytes = new byte[32];
            for (int index = 0; index < bytes.Length; index++)
            {
                byte parsed;
                if (!byte.TryParse(
                        candidate.Substring(index * 2, 2),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out parsed))
                {
                    return null;
                }
                bytes[index] = parsed;
            }
            return bytes;
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }
            int difference = 0;
            for (int index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }
            return difference == 0;
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes)
            {
                builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }
    }

    /// <summary>
    /// Persists a coordinator checkpoint atomically in a caller-selected app
    /// data location. It never invokes or modifies another process.
    /// </summary>
    internal sealed class PerformanceProofCheckpointStore
    {
        private readonly object sync = new object();
        private readonly string checkpointPath;

        public PerformanceProofCheckpointStore(string checkpointPath)
        {
            if (string.IsNullOrWhiteSpace(checkpointPath))
            {
                throw new ArgumentException("Checkpoint path is required.", "checkpointPath");
            }
            this.checkpointPath = Path.GetFullPath(checkpointPath);
            string fileName = Path.GetFileName(this.checkpointPath);
            if (string.IsNullOrWhiteSpace(fileName) ||
                fileName.IndexOf(':') >= 0)
            {
                throw new ArgumentException("Checkpoint file name is invalid.", "checkpointPath");
            }
        }

        public bool TrySave(
            PerformanceProofCoordinator coordinator,
            out string error)
        {
            error = string.Empty;
            if (coordinator == null)
            {
                error = "Состояние доказательного теста отсутствует.";
                return false;
            }

            lock (sync)
            {
                string temporaryPath = null;
                string backupPath = null;
                try
                {
                    string directory = Path.GetDirectoryName(checkpointPath);
                    if (string.IsNullOrWhiteSpace(directory))
                    {
                        error = "Папка чекпоинта недоступна.";
                        return false;
                    }
                    EnsureSafeDirectory(directory);
                    if (File.Exists(checkpointPath) &&
                        (File.GetAttributes(checkpointPath) &
                         FileAttributes.ReparsePoint) != 0)
                    {
                        error = "Файл чекпоинта не прошёл проверку безопасности.";
                        return false;
                    }

                    string checkpoint = coordinator.CreateCheckpoint();
                    byte[] bytes = new UTF8Encoding(false).GetBytes(checkpoint);
                    if (bytes.Length >
                        PerformanceProofCoordinator.MaximumCheckpointTextLength)
                    {
                        error = "Чекпоинт доказательного теста слишком велик.";
                        return false;
                    }

                    string operationId = Guid.NewGuid().ToString("N");
                    temporaryPath = checkpointPath + "." + operationId + ".tmp";
                    backupPath = checkpointPath + "." + operationId + ".bak";
                    using (var stream = new FileStream(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        64 * 1024,
                        FileOptions.WriteThrough))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }

                    if (File.Exists(checkpointPath))
                    {
                        File.Replace(
                            temporaryPath,
                            checkpointPath,
                            backupPath,
                            true);
                        temporaryPath = null;
                        TryDeleteExactFile(backupPath);
                    }
                    else
                    {
                        File.Move(temporaryPath, checkpointPath);
                        temporaryPath = null;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    error = "Не удалось сохранить чекпоинт: " + ex.GetType().Name + ".";
                    return false;
                }
                finally
                {
                    TryDeleteExactFile(temporaryPath);
                    TryDeleteExactFile(backupPath);
                }
            }
        }

        public bool TryLoad(
            out PerformanceProofCoordinator coordinator,
            out string error)
        {
            coordinator = null;
            error = string.Empty;
            lock (sync)
            {
                try
                {
                    string directory = Path.GetDirectoryName(checkpointPath);
                    if (string.IsNullOrWhiteSpace(directory) ||
                        !Directory.Exists(directory))
                    {
                        error = "Сохранённый доказательный тест не найден.";
                        return false;
                    }
                    EnsureSafeDirectory(directory);
                    var file = new FileInfo(checkpointPath);
                    if (!file.Exists)
                    {
                        error = "Сохранённый доказательный тест не найден.";
                        return false;
                    }
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0 ||
                        file.Length <= 0 ||
                        file.Length >
                            PerformanceProofCoordinator.MaximumCheckpointTextLength)
                    {
                        error = "Файл чекпоинта не прошёл проверку безопасности.";
                        return false;
                    }
                    string content = File.ReadAllText(
                        checkpointPath,
                        new UTF8Encoding(false, true));
                    return PerformanceProofCoordinator.TryRestore(
                        content,
                        out coordinator,
                        out error);
                }
                catch (Exception ex)
                {
                    error = "Не удалось прочитать чекпоинт: " + ex.GetType().Name + ".";
                    return false;
                }
            }
        }

        private static void TryDeleteExactFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // A failed cleanup never replaces the last committed checkpoint.
            }
        }

        private static void EnsureSafeDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            {
                throw new IOException("Checkpoint directory is unavailable.");
            }
            string fullPath = Path.GetFullPath(path);
            var missing = new Stack<string>();
            var cursor = new DirectoryInfo(fullPath);
            while (cursor != null && !cursor.Exists)
            {
                missing.Push(cursor.FullName);
                cursor = cursor.Parent;
            }
            if (cursor == null)
            {
                throw new IOException("Checkpoint directory has no trusted root.");
            }
            for (DirectoryInfo current = cursor;
                 current != null;
                 current = current.Parent)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        "Checkpoint path contains a reparse point.");
                }
            }
            while (missing.Count > 0)
            {
                string next = missing.Pop();
                Directory.CreateDirectory(next);
                var created = new DirectoryInfo(next);
                if ((created.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        "Checkpoint path contains a reparse point.");
                }
            }
        }
    }
}
