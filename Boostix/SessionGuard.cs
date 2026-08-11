using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Boostix
{
    /// <summary>
    /// Stores a bounded, thread-safe history while preserving oldest-to-newest order.
    /// </summary>
    internal sealed class BoundedRingBuffer<T>
    {
        private readonly object syncRoot = new object();
        private readonly T[] items;
        private int count;
        private int nextIndex;

        /// <summary>
        /// Initializes a new bounded history.
        /// </summary>
        /// <param name="capacity">Maximum number of entries to retain.</param>
        public BoundedRingBuffer(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    "capacity",
                    "Ring-buffer capacity must be greater than zero.");
            }

            items = new T[capacity];
        }

        /// <summary>
        /// Gets the maximum number of retained entries.
        /// </summary>
        public int Capacity
        {
            get { return items.Length; }
        }

        /// <summary>
        /// Gets the current number of retained entries.
        /// </summary>
        public int Count
        {
            get
            {
                lock (syncRoot)
                {
                    return count;
                }
            }
        }

        /// <summary>
        /// Appends an entry, overwriting the oldest entry when the buffer is full.
        /// </summary>
        /// <param name="item">Entry to append.</param>
        public void Add(T item)
        {
            lock (syncRoot)
            {
                items[nextIndex] = item;
                nextIndex = (nextIndex + 1) % items.Length;
                if (count < items.Length)
                {
                    count++;
                }
            }
        }

        /// <summary>
        /// Returns a stable oldest-to-newest copy of the retained entries.
        /// </summary>
        /// <returns>A detached snapshot.</returns>
        public T[] Snapshot()
        {
            lock (syncRoot)
            {
                var snapshot = new T[count];
                int firstIndex = count == items.Length ? nextIndex : 0;
                for (int index = 0; index < count; index++)
                {
                    snapshot[index] = items[(firstIndex + index) % items.Length];
                }

                return snapshot;
            }
        }

        /// <summary>
        /// Removes all retained entries.
        /// </summary>
        public void Clear()
        {
            lock (syncRoot)
            {
                Array.Clear(items, 0, items.Length);
                count = 0;
                nextIndex = 0;
            }
        }
    }

    /// <summary>
    /// Identifies one exact process instance. A PID alone is never considered
    /// sufficient because Windows can reuse it after the original process exits.
    /// </summary>
    internal sealed class SessionGuardTargetIdentity
    {
        public SessionGuardTargetIdentity(
            int processId,
            DateTime processStartTimeUtc,
            string executablePath)
        {
            if (processId <= 0)
            {
                throw new ArgumentOutOfRangeException("processId");
            }
            if (processStartTimeUtc == DateTime.MinValue ||
                processStartTimeUtc == DateTime.MaxValue)
            {
                throw new ArgumentOutOfRangeException("processStartTimeUtc");
            }

            ProcessId = processId;
            ProcessStartTimeUtc = processStartTimeUtc.Kind == DateTimeKind.Utc
                ? processStartTimeUtc
                : processStartTimeUtc.ToUniversalTime();
            ExecutablePath = NormalizeExecutablePath(executablePath);
        }

        public int ProcessId { get; private set; }
        public DateTime ProcessStartTimeUtc { get; private set; }
        public string ExecutablePath { get; private set; }

        public bool HasExecutablePath
        {
            get { return !String.IsNullOrEmpty(ExecutablePath); }
        }

        public bool Matches(
            int processId,
            DateTime processStartTimeUtc,
            string executablePath)
        {
            if (processId != ProcessId)
            {
                return false;
            }

            DateTime actualStartUtc = processStartTimeUtc.Kind == DateTimeKind.Utc
                ? processStartTimeUtc
                : processStartTimeUtc.ToUniversalTime();
            if (actualStartUtc != ProcessStartTimeUtc)
            {
                return false;
            }

            return !HasExecutablePath || String.Equals(
                ExecutablePath,
                NormalizeExecutablePath(executablePath),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeExecutablePath(string executablePath)
        {
            if (String.IsNullOrWhiteSpace(executablePath))
            {
                return String.Empty;
            }

            string normalized = executablePath.Trim().Trim('"');
            if (normalized.StartsWith(
                    @"\\?\UNC\",
                    StringComparison.OrdinalIgnoreCase))
            {
                normalized = @"\\" + normalized.Substring(8);
            }
            else if (normalized.StartsWith(
                    @"\\?\",
                    StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(4);
            }

            try
            {
                if (!Path.IsPathRooted(normalized))
                {
                    throw new ArgumentException(
                        "The target executable path must be absolute.");
                }
                normalized = Path.GetFullPath(normalized);
            }
            catch (Exception error)
            {
                throw new ArgumentException(
                    "The target executable path is invalid.",
                    "executablePath",
                    error);
            }

            return normalized.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }
    }

    /// <summary>
    /// Represents one inexpensive Session Guard observation.
    /// </summary>
    internal sealed class SessionGuardSample
    {
        /// <summary>
        /// Initializes a validated Session Guard observation.
        /// </summary>
        public SessionGuardSample(
            DateTime capturedUtc,
            long monotonicTimestamp,
            bool systemMetricsAvailable,
            long totalPhysicalBytes,
            long availablePhysicalBytes,
            long committedBytes,
            long commitLimitBytes,
            bool targetMetricsAvailable,
            int targetProcessId,
            long targetPrivateBytes,
            long targetWorkingSetBytes,
            double targetCpuPercent,
            long targetReadBytesDelta,
            long targetWriteBytesDelta,
            string unavailableReason)
        {
            if (monotonicTimestamp < 0)
            {
                throw new ArgumentOutOfRangeException("monotonicTimestamp");
            }

            EnsureNonNegative(totalPhysicalBytes, "totalPhysicalBytes");
            EnsureNonNegative(availablePhysicalBytes, "availablePhysicalBytes");
            EnsureNonNegative(committedBytes, "committedBytes");
            EnsureNonNegative(commitLimitBytes, "commitLimitBytes");
            EnsureNonNegative(targetPrivateBytes, "targetPrivateBytes");
            EnsureNonNegative(targetWorkingSetBytes, "targetWorkingSetBytes");
            EnsureNonNegative(targetReadBytesDelta, "targetReadBytesDelta");
            EnsureNonNegative(targetWriteBytesDelta, "targetWriteBytesDelta");

            if (systemMetricsAvailable &&
                (totalPhysicalBytes <= 0 ||
                 availablePhysicalBytes > totalPhysicalBytes ||
                 commitLimitBytes <= 0))
            {
                throw new ArgumentException(
                    "Available system metrics must be internally consistent.");
            }

            if (targetMetricsAvailable && targetProcessId <= 0)
            {
                throw new ArgumentOutOfRangeException("targetProcessId");
            }

            if (targetMetricsAvailable &&
                (Double.IsNaN(targetCpuPercent) ||
                 Double.IsInfinity(targetCpuPercent) ||
                 targetCpuPercent < 0.0 ||
                 targetCpuPercent > 100.0))
            {
                throw new ArgumentOutOfRangeException("targetCpuPercent");
            }

            CapturedUtc = capturedUtc.Kind == DateTimeKind.Utc
                ? capturedUtc
                : capturedUtc.ToUniversalTime();
            MonotonicTimestamp = monotonicTimestamp;
            SystemMetricsAvailable = systemMetricsAvailable;
            TotalPhysicalBytes = totalPhysicalBytes;
            AvailablePhysicalBytes = availablePhysicalBytes;
            CommittedBytes = committedBytes;
            CommitLimitBytes = commitLimitBytes;
            CommitHeadroomBytes = Math.Max(0L, commitLimitBytes - committedBytes);
            TargetMetricsAvailable = targetMetricsAvailable;
            TargetProcessId = targetProcessId;
            TargetPrivateBytes = targetPrivateBytes;
            TargetWorkingSetBytes = targetWorkingSetBytes;
            TargetCpuPercent = targetMetricsAvailable ? targetCpuPercent : 0.0;
            TargetReadBytesDelta = targetReadBytesDelta;
            TargetWriteBytesDelta = targetWriteBytesDelta;
            UnavailableReason = unavailableReason ?? String.Empty;
        }

        public DateTime CapturedUtc { get; private set; }
        public long MonotonicTimestamp { get; private set; }
        public bool SystemMetricsAvailable { get; private set; }
        public long TotalPhysicalBytes { get; private set; }
        public long AvailablePhysicalBytes { get; private set; }
        public long CommittedBytes { get; private set; }
        public long CommitLimitBytes { get; private set; }
        public long CommitHeadroomBytes { get; private set; }
        public bool TargetMetricsAvailable { get; private set; }
        public int TargetProcessId { get; private set; }
        public long TargetPrivateBytes { get; private set; }
        public long TargetWorkingSetBytes { get; private set; }
        public double TargetCpuPercent { get; private set; }
        public long TargetReadBytesDelta { get; private set; }
        public long TargetWriteBytesDelta { get; private set; }
        public string UnavailableReason { get; private set; }

        private static void EnsureNonNegative(long value, string parameterName)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }
    }

    internal enum SessionGuardPressureDecision
    {
        MetricsUnavailable,
        Healthy,
        ObservingPressure,
        CriticalAlertRaised,
        CriticalPressureActive,
        RecoveryPending,
        Recovered,
        Cooldown
    }

    /// <summary>
    /// Defines the sustained-sample and cooldown policy for pressure alerts.
    /// </summary>
    internal sealed class SessionGuardPressurePolicyOptions
    {
        public const int DefaultCriticalSamples = 3;
        public const int DefaultRecoverySamples = 3;
        public static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(5);

        public SessionGuardPressurePolicyOptions(
            int requiredCriticalSamples,
            int requiredRecoverySamples,
            TimeSpan alertCooldown)
        {
            if (requiredCriticalSamples < 2 || requiredCriticalSamples > 60)
            {
                throw new ArgumentOutOfRangeException(
                    "requiredCriticalSamples",
                    "At least two and no more than 60 samples are required.");
            }

            if (requiredRecoverySamples < 2 || requiredRecoverySamples > 60)
            {
                throw new ArgumentOutOfRangeException(
                    "requiredRecoverySamples",
                    "At least two and no more than 60 samples are required.");
            }

            if (alertCooldown < TimeSpan.FromSeconds(1) ||
                alertCooldown > TimeSpan.FromHours(24))
            {
                throw new ArgumentOutOfRangeException("alertCooldown");
            }

            RequiredCriticalSamples = requiredCriticalSamples;
            RequiredRecoverySamples = requiredRecoverySamples;
            AlertCooldown = alertCooldown;
        }

        public int RequiredCriticalSamples { get; private set; }
        public int RequiredRecoverySamples { get; private set; }
        public TimeSpan AlertCooldown { get; private set; }

        public static SessionGuardPressurePolicyOptions CreateDefault()
        {
            return new SessionGuardPressurePolicyOptions(
                DefaultCriticalSamples,
                DefaultRecoverySamples,
                DefaultCooldown);
        }
    }

    /// <summary>
    /// Contains state that must be carried between pure pressure evaluations.
    /// </summary>
    internal sealed class SessionGuardPressureState
    {
        public int ConsecutiveCriticalSamples;
        public int ConsecutiveRecoverySamples;
        public bool CriticalAlertActive;
        public long NextAlertAllowedTimestamp;

        public SessionGuardPressureState Clone()
        {
            return new SessionGuardPressureState
            {
                ConsecutiveCriticalSamples = ConsecutiveCriticalSamples,
                ConsecutiveRecoverySamples = ConsecutiveRecoverySamples,
                CriticalAlertActive = CriticalAlertActive,
                NextAlertAllowedTimestamp = NextAlertAllowedTimestamp
            };
        }
    }

    internal sealed class SessionGuardPressureEvaluation
    {
        public SessionGuardPressureDecision Decision;
        public string Reason;
        public SessionGuardPressureState NextState;
        public long PhysicalCriticalThresholdBytes;
        public long PhysicalRecoveryThresholdBytes;
        public long CommitCriticalThresholdBytes;
        public long CommitRecoveryThresholdBytes;
    }

    /// <summary>
    /// Classifies memory and commit pressure without performing memory mutations.
    /// </summary>
    internal sealed class SessionGuardPressurePolicy
    {
        private const long Mebibyte = 1024L * 1024L;
        private const long Gibibyte = 1024L * Mebibyte;
        private readonly SessionGuardPressurePolicyOptions options;

        public SessionGuardPressurePolicy(SessionGuardPressurePolicyOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            this.options = options;
        }

        public SessionGuardPressureEvaluation Evaluate(
            SessionGuardSample sample,
            SessionGuardPressureState state,
            long nowTimestamp)
        {
            if (nowTimestamp < 0)
            {
                throw new ArgumentOutOfRangeException("nowTimestamp");
            }

            var next = state == null
                ? new SessionGuardPressureState()
                : state.Clone();

            if (sample == null || !sample.SystemMetricsAvailable)
            {
                next.ConsecutiveCriticalSamples = 0;
                next.ConsecutiveRecoverySamples = 0;
                return NewEvaluation(
                    SessionGuardPressureDecision.MetricsUnavailable,
                    "Windows memory metrics are unavailable.",
                    next,
                    sample);
            }

            long physicalCritical = GetPhysicalCriticalThreshold(
                sample.TotalPhysicalBytes);
            long physicalRecovery = GetPhysicalRecoveryThreshold(
                sample.TotalPhysicalBytes);
            long commitCritical = GetCommitCriticalThreshold(
                sample.CommitLimitBytes);
            long commitRecovery = GetCommitRecoveryThreshold(
                sample.CommitLimitBytes);

            bool critical =
                sample.AvailablePhysicalBytes <= physicalCritical ||
                sample.CommitHeadroomBytes <= commitCritical;
            bool recovered =
                sample.AvailablePhysicalBytes >= physicalRecovery &&
                sample.CommitHeadroomBytes >= commitRecovery;

            if (next.CriticalAlertActive)
            {
                next.ConsecutiveCriticalSamples = 0;
                if (!recovered)
                {
                    next.ConsecutiveRecoverySamples = 0;
                    return NewEvaluation(
                        SessionGuardPressureDecision.CriticalPressureActive,
                        "Pressure remains above the recovery boundary.",
                        next,
                        sample);
                }

                next.ConsecutiveRecoverySamples = IncrementSaturated(
                    next.ConsecutiveRecoverySamples);
                if (next.ConsecutiveRecoverySamples <
                    options.RequiredRecoverySamples)
                {
                    return NewEvaluation(
                        SessionGuardPressureDecision.RecoveryPending,
                        "Recovery must remain stable for consecutive samples.",
                        next,
                        sample);
                }

                next.CriticalAlertActive = false;
                next.ConsecutiveRecoverySamples = 0;
                return NewEvaluation(
                    SessionGuardPressureDecision.Recovered,
                    "Physical memory and commit headroom recovered.",
                    next,
                    sample);
            }

            next.ConsecutiveRecoverySamples = 0;
            if (!critical)
            {
                next.ConsecutiveCriticalSamples = 0;
                return NewEvaluation(
                    SessionGuardPressureDecision.Healthy,
                    "Physical memory and commit headroom are healthy.",
                    next,
                    sample);
            }

            next.ConsecutiveCriticalSamples = IncrementSaturated(
                next.ConsecutiveCriticalSamples);
            if (next.ConsecutiveCriticalSamples <
                options.RequiredCriticalSamples)
            {
                return NewEvaluation(
                    SessionGuardPressureDecision.ObservingPressure,
                    "A critical sample was observed and is awaiting confirmation.",
                    next,
                    sample);
            }

            if (next.NextAlertAllowedTimestamp > 0 &&
                nowTimestamp < next.NextAlertAllowedTimestamp)
            {
                return NewEvaluation(
                    SessionGuardPressureDecision.Cooldown,
                    "The alert cooldown prevents a duplicate notification.",
                    next,
                    sample);
            }

            next.CriticalAlertActive = true;
            next.ConsecutiveCriticalSamples = 0;
            next.NextAlertAllowedTimestamp = AddDurationSaturated(
                nowTimestamp,
                options.AlertCooldown);
            return NewEvaluation(
                SessionGuardPressureDecision.CriticalAlertRaised,
                "Sustained physical-memory or commit pressure was confirmed.",
                next,
                sample);
        }

        public static long GetPhysicalCriticalThreshold(long totalBytes)
        {
            return Clamp(
                totalBytes > 0 ? totalBytes / 20L : 0,
                512L * Mebibyte,
                1536L * Mebibyte);
        }

        public static long GetPhysicalRecoveryThreshold(long totalBytes)
        {
            return Clamp(
                totalBytes > 0 ? totalBytes / 10L : 0,
                1024L * Mebibyte,
                3L * Gibibyte);
        }

        public static long GetCommitCriticalThreshold(long commitLimitBytes)
        {
            return Clamp(
                commitLimitBytes > 0 ? commitLimitBytes / 20L : 0,
                512L * Mebibyte,
                2L * Gibibyte);
        }

        public static long GetCommitRecoveryThreshold(long commitLimitBytes)
        {
            return Clamp(
                commitLimitBytes > 0 ? commitLimitBytes / 10L : 0,
                1024L * Mebibyte,
                4L * Gibibyte);
        }

        private static SessionGuardPressureEvaluation NewEvaluation(
            SessionGuardPressureDecision decision,
            string reason,
            SessionGuardPressureState next,
            SessionGuardSample sample)
        {
            return new SessionGuardPressureEvaluation
            {
                Decision = decision,
                Reason = reason,
                NextState = next,
                PhysicalCriticalThresholdBytes = sample == null
                    ? 0
                    : GetPhysicalCriticalThreshold(sample.TotalPhysicalBytes),
                PhysicalRecoveryThresholdBytes = sample == null
                    ? 0
                    : GetPhysicalRecoveryThreshold(sample.TotalPhysicalBytes),
                CommitCriticalThresholdBytes = sample == null
                    ? 0
                    : GetCommitCriticalThreshold(sample.CommitLimitBytes),
                CommitRecoveryThresholdBytes = sample == null
                    ? 0
                    : GetCommitRecoveryThreshold(sample.CommitLimitBytes)
            };
        }

        private static int IncrementSaturated(int value)
        {
            return value == Int32.MaxValue ? value : value + 1;
        }

        private static long AddDurationSaturated(long timestamp, TimeSpan duration)
        {
            double scaledTicks = duration.TotalSeconds * Stopwatch.Frequency;
            long ticks = scaledTicks >= Int64.MaxValue
                ? Int64.MaxValue
                : (long)Math.Ceiling(scaledTicks);
            return timestamp > Int64.MaxValue - ticks
                ? Int64.MaxValue
                : timestamp + ticks;
        }

        private static long Clamp(long value, long minimum, long maximum)
        {
            return Math.Min(maximum, Math.Max(minimum, value));
        }
    }

    internal enum PagefileConfigurationKind
    {
        Unknown,
        SystemManaged,
        Disabled,
        Fixed
    }

    internal enum PagefileRecommendationCode
    {
        None,
        UnableToAssess,
        EnableSystemManaged,
        PreferSystemManaged,
        FreeSystemDriveSpace
    }

    /// <summary>
    /// Describes the observed page-file configuration. It contains no mutation API.
    /// </summary>
    internal sealed class PagefileConfigurationSnapshot
    {
        public PagefileConfigurationSnapshot(
            DateTime capturedUtc,
            PagefileConfigurationKind kind,
            long configuredBytes,
            long allocatedBytes,
            long installedPhysicalMemoryBytes,
            long systemDriveFreeBytes,
            long systemDriveTotalBytes,
            string unavailableReason)
        {
            EnsureNonNegative(configuredBytes, "configuredBytes");
            EnsureNonNegative(allocatedBytes, "allocatedBytes");
            EnsureNonNegative(
                installedPhysicalMemoryBytes,
                "installedPhysicalMemoryBytes");
            EnsureNonNegative(systemDriveFreeBytes, "systemDriveFreeBytes");
            EnsureNonNegative(systemDriveTotalBytes, "systemDriveTotalBytes");
            if (systemDriveTotalBytes > 0 &&
                systemDriveFreeBytes > systemDriveTotalBytes)
            {
                throw new ArgumentException(
                    "System-drive free space cannot exceed total space.");
            }

            CapturedUtc = capturedUtc.Kind == DateTimeKind.Utc
                ? capturedUtc
                : capturedUtc.ToUniversalTime();
            Kind = kind;
            ConfiguredBytes = configuredBytes;
            AllocatedBytes = allocatedBytes;
            InstalledPhysicalMemoryBytes = installedPhysicalMemoryBytes;
            SystemDriveFreeBytes = systemDriveFreeBytes;
            SystemDriveTotalBytes = systemDriveTotalBytes;
            UnavailableReason = unavailableReason ?? String.Empty;
        }

        public DateTime CapturedUtc { get; private set; }
        public PagefileConfigurationKind Kind { get; private set; }
        public long ConfiguredBytes { get; private set; }
        public long AllocatedBytes { get; private set; }
        public long InstalledPhysicalMemoryBytes { get; private set; }
        public long SystemDriveFreeBytes { get; private set; }
        public long SystemDriveTotalBytes { get; private set; }
        public string UnavailableReason { get; private set; }

        private static void EnsureNonNegative(long value, string parameterName)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }
    }

    internal sealed class PagefileAssessment
    {
        public PagefileRecommendationCode Recommendation;
        public bool RequiresAttention;
        public string Summary;
        public string RecommendedAction;
    }

    /// <summary>
    /// Produces read-only page-file guidance and never changes Windows settings.
    /// </summary>
    internal static class PagefileAdvisor
    {
        private const long Gibibyte = 1024L * 1024L * 1024L;

        public static PagefileAssessment Assess(
            PagefileConfigurationSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }

            if (snapshot.Kind == PagefileConfigurationKind.Unknown)
            {
                return NewAssessment(
                    PagefileRecommendationCode.UnableToAssess,
                    false,
                    "The page-file configuration could not be determined.",
                    "Open Windows virtual-memory settings and verify that the page file is system managed.");
            }

            if (snapshot.Kind == PagefileConfigurationKind.Disabled)
            {
                return NewAssessment(
                    PagefileRecommendationCode.EnableSystemManaged,
                    true,
                    "The Windows page file is disabled.",
                    "Enable a system-managed page file, then restart Windows.");
            }

            if (HasLowSystemDriveSpace(snapshot))
            {
                return NewAssessment(
                    PagefileRecommendationCode.FreeSystemDriveSpace,
                    true,
                    "The system drive has too little free space for reliable commit growth.",
                    "Free space on the system drive before a long gaming session.");
            }

            if (snapshot.Kind == PagefileConfigurationKind.Fixed)
            {
                return NewAssessment(
                    PagefileRecommendationCode.PreferSystemManaged,
                    true,
                    "A fixed-size page file can cap commit during asset-heavy scenes.",
                    "Prefer a system-managed page file unless a measured workload requires a fixed size.");
            }

            return NewAssessment(
                PagefileRecommendationCode.None,
                false,
                "The page file is system managed and system-drive space is adequate.",
                "No page-file change is recommended.");
        }

        private static bool HasLowSystemDriveSpace(
            PagefileConfigurationSnapshot snapshot)
        {
            if (snapshot.SystemDriveTotalBytes <= 0)
            {
                return false;
            }

            long percentageFloor = snapshot.SystemDriveTotalBytes / 20L;
            long requiredFree = Math.Max(5L * Gibibyte, percentageFloor);
            return snapshot.SystemDriveFreeBytes < requiredFree;
        }

        private static PagefileAssessment NewAssessment(
            PagefileRecommendationCode recommendation,
            bool requiresAttention,
            string summary,
            string recommendedAction)
        {
            return new PagefileAssessment
            {
                Recommendation = recommendation,
                RequiresAttention = requiresAttention,
                Summary = summary,
                RecommendedAction = recommendedAction
            };
        }
    }

    internal sealed class SessionGuardHeavySample
    {
        public SessionGuardHeavySample(
            DateTime capturedUtc,
            long monotonicTimestamp,
            PagefileConfigurationSnapshot pagefile)
        {
            if (monotonicTimestamp < 0)
            {
                throw new ArgumentOutOfRangeException("monotonicTimestamp");
            }

            CapturedUtc = capturedUtc.Kind == DateTimeKind.Utc
                ? capturedUtc
                : capturedUtc.ToUniversalTime();
            MonotonicTimestamp = monotonicTimestamp;
            Pagefile = pagefile;
        }

        public DateTime CapturedUtc { get; private set; }
        public long MonotonicTimestamp { get; private set; }
        public PagefileConfigurationSnapshot Pagefile { get; private set; }
    }

    /// <summary>
    /// Separates metrics acquisition from Session Guard scheduling and policy.
    /// </summary>
    internal interface ISessionGuardMetricsSource : IDisposable
    {
        SessionGuardSample CaptureCheap(SessionGuardTargetIdentity target);
        SessionGuardHeavySample CaptureHeavy();
    }

    /// <summary>
    /// Provides a deterministic test seam for UTC time, monotonic time, and delay.
    /// </summary>
    internal interface ISessionGuardClock
    {
        DateTime UtcNow { get; }
        long Timestamp { get; }
        Task Delay(TimeSpan delay, CancellationToken cancellationToken);
    }

    internal sealed class SystemSessionGuardClock : ISessionGuardClock
    {
        public DateTime UtcNow
        {
            get { return DateTime.UtcNow; }
        }

        public long Timestamp
        {
            get { return Stopwatch.GetTimestamp(); }
        }

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            return Task.Delay(delay, cancellationToken);
        }
    }

    /// <summary>
    /// Defines safe sampling cadences and a bounded history size.
    /// </summary>
    internal sealed class SessionGuardSamplerOptions
    {
        public SessionGuardSamplerOptions(
            TimeSpan cheapInterval,
            TimeSpan heavyInterval,
            int historyCapacity)
        {
            if (cheapInterval < TimeSpan.FromSeconds(1) ||
                cheapInterval > TimeSpan.FromSeconds(2))
            {
                throw new ArgumentOutOfRangeException(
                    "cheapInterval",
                    "Cheap sampling must run every one to two seconds.");
            }

            if (heavyInterval < TimeSpan.FromSeconds(10) ||
                heavyInterval > TimeSpan.FromHours(1))
            {
                throw new ArgumentOutOfRangeException(
                    "heavyInterval",
                    "Heavy sampling must run no more often than every ten seconds.");
            }

            if (historyCapacity < 2 || historyCapacity > 3600)
            {
                throw new ArgumentOutOfRangeException("historyCapacity");
            }

            CheapInterval = cheapInterval;
            HeavyInterval = heavyInterval;
            HistoryCapacity = historyCapacity;
        }

        public TimeSpan CheapInterval { get; private set; }
        public TimeSpan HeavyInterval { get; private set; }
        public int HistoryCapacity { get; private set; }

        public static SessionGuardSamplerOptions CreateDefault()
        {
            return new SessionGuardSamplerOptions(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(60),
                900);
        }
    }

    internal sealed class SessionGuardSampleEventArgs : EventArgs
    {
        public SessionGuardSampleEventArgs(SessionGuardSample sample)
        {
            if (sample == null)
            {
                throw new ArgumentNullException("sample");
            }

            Sample = sample;
        }

        public SessionGuardSample Sample { get; private set; }
    }

    internal sealed class SessionGuardHeavySampleEventArgs : EventArgs
    {
        public SessionGuardHeavySampleEventArgs(SessionGuardHeavySample sample)
        {
            if (sample == null)
            {
                throw new ArgumentNullException("sample");
            }

            Sample = sample;
        }

        public SessionGuardHeavySample Sample { get; private set; }
    }

    internal sealed class SessionGuardSamplingFaultEventArgs : EventArgs
    {
        public SessionGuardSamplingFaultEventArgs(bool heavySample, Exception error)
        {
            if (error == null)
            {
                throw new ArgumentNullException("error");
            }

            HeavySample = heavySample;
            Error = error;
        }

        public bool HeavySample { get; private set; }
        public Exception Error { get; private set; }
    }

    /// <summary>
    /// Runs one leak-free sequential sampling loop without overlapping expensive work.
    /// </summary>
    internal sealed class SessionGuardSampler : IDisposable
    {
        private readonly object syncRoot = new object();
        private readonly object publicationGate = new object();
        private readonly ISessionGuardMetricsSource metricsSource;
        private readonly ISessionGuardClock clock;
        private readonly SessionGuardSamplerOptions options;
        private readonly bool ownsMetricsSource;
        private readonly BoundedRingBuffer<SessionGuardSample> history;
        private CancellationTokenSource shutdown;
        private Task runningTask;
        private Task heavyTask;
        private bool disposed;
        private bool samplingActive;
        private bool publicationsEnabled;
        private bool metricsSourceDisposed;
        private int activeGeneration;
        private int publicationGeneration;
        private SessionGuardHeavySample latestHeavySample;

        public SessionGuardSampler(
            ISessionGuardMetricsSource metricsSource,
            ISessionGuardClock clock,
            SessionGuardSamplerOptions options,
            bool ownsMetricsSource)
        {
            if (metricsSource == null)
            {
                throw new ArgumentNullException("metricsSource");
            }
            if (clock == null)
            {
                throw new ArgumentNullException("clock");
            }
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            this.metricsSource = metricsSource;
            this.clock = clock;
            this.options = options;
            this.ownsMetricsSource = ownsMetricsSource;
            history = new BoundedRingBuffer<SessionGuardSample>(
                options.HistoryCapacity);
        }

        public event EventHandler<SessionGuardSampleEventArgs> SampleCaptured;
        public event EventHandler<SessionGuardHeavySampleEventArgs> HeavySampleCaptured;
        public event EventHandler<SessionGuardSamplingFaultEventArgs> SamplingFaulted;

        public SessionGuardSample[] GetHistorySnapshot()
        {
            ThrowIfDisposed();
            return history.Snapshot();
        }

        public SessionGuardHeavySample GetLatestHeavySample()
        {
            ThrowIfDisposed();
            lock (publicationGate)
            {
                return latestHeavySample;
            }
        }

        public Task StartAsync(
            SessionGuardTargetIdentity target,
            CancellationToken cancellationToken)
        {
            if (target == null)
            {
                throw new ArgumentNullException("target");
            }

            lock (publicationGate)
            {
                lock (syncRoot)
                {
                    ThrowIfDisposed();
                    if (runningTask != null && !runningTask.IsCompleted)
                    {
                        throw new InvalidOperationException(
                            "Session Guard sampling is already running.");
                    }

                    if (shutdown != null)
                    {
                        shutdown.Dispose();
                    }

                    shutdown = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                    activeGeneration = unchecked(activeGeneration + 1);
                    publicationGeneration = activeGeneration;
                    samplingActive = true;
                    publicationsEnabled = true;
                    runningTask = RunLoopAsync(
                        target,
                        activeGeneration,
                        shutdown.Token);
                    return runningTask;
                }
            }
        }

        public Task StopAsync()
        {
            Task task;
            lock (publicationGate)
            {
                publicationsEnabled = false;
                lock (syncRoot)
                {
                    samplingActive = false;
                    if (shutdown != null)
                    {
                        shutdown.Cancel();
                    }
                    task = runningTask;
                }
            }

            return task ?? Task.FromResult(0);
        }

        public void Dispose()
        {
            Task loopTask;
            Task expensiveTask;
            CancellationTokenSource tokenSource;
            lock (publicationGate)
            {
                publicationsEnabled = false;
                lock (syncRoot)
                {
                    if (disposed)
                    {
                        return;
                    }

                    disposed = true;
                    samplingActive = false;
                    if (shutdown != null)
                    {
                        shutdown.Cancel();
                    }
                    loopTask = runningTask;
                    expensiveTask = heavyTask;
                    tokenSource = shutdown;
                }

                SampleCaptured = null;
                HeavySampleCaptured = null;
                SamplingFaulted = null;
            }

            if (loopTask != null &&
                (!Task.CurrentId.HasValue ||
                 Task.CurrentId.Value != loopTask.Id))
            {
                try
                {
                    loopTask.Wait(TimeSpan.FromSeconds(3));
                }
                catch (AggregateException)
                {
                    // Sampling failures are reported through SamplingFaulted.
                    // Deferred cleanup still observes the completed task.
                }
            }

            ScheduleDeferredDisposal(
                loopTask,
                expensiveTask,
                tokenSource);
        }

        private async Task RunLoopAsync(
            SessionGuardTargetIdentity target,
            int generation,
            CancellationToken cancellationToken)
        {
            // Prevent a synchronous test clock from executing the loop while the
            // lifecycle lock in StartAsync is still held.
            await Task.Yield();
            long nextHeavyTimestamp = 0;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CaptureCheapSafely(target, generation);

                    long now = clock.Timestamp;
                    if (nextHeavyTimestamp == 0 || now >= nextHeavyTimestamp)
                    {
                        if (TryStartHeavyCapture(generation))
                        {
                            nextHeavyTimestamp = AddDurationSaturated(
                                now,
                                options.HeavyInterval);
                        }
                    }

                    await clock.Delay(
                        options.CheapInterval,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
            }
            finally
            {
                EndGeneration(generation);
            }
        }

        private void CaptureCheapSafely(
            SessionGuardTargetIdentity target,
            int generation)
        {
            try
            {
                SessionGuardSample sample = metricsSource.CaptureCheap(
                    target);
                if (sample == null)
                {
                    throw new InvalidOperationException(
                        "The metrics source returned a null cheap sample.");
                }

                PublishCheapSample(generation, sample);
            }
            catch (Exception error)
            {
                PublishSamplingFault(generation, false, error);
            }
        }

        private bool TryStartHeavyCapture(int generation)
        {
            lock (syncRoot)
            {
                if (disposed ||
                    !samplingActive ||
                    activeGeneration != generation ||
                    (heavyTask != null && !heavyTask.IsCompleted))
                {
                    return false;
                }

                heavyTask = Task.Run(delegate
                {
                    CaptureHeavySafely(generation);
                });
                return true;
            }
        }

        private void CaptureHeavySafely(int generation)
        {
            try
            {
                SessionGuardHeavySample sample = metricsSource.CaptureHeavy();
                if (sample == null)
                {
                    throw new InvalidOperationException(
                        "The metrics source returned a null heavy sample.");
                }

                PublishHeavySample(generation, sample);
            }
            catch (Exception error)
            {
                PublishSamplingFault(generation, true, error);
            }
        }

        private void PublishCheapSample(
            int generation,
            SessionGuardSample sample)
        {
            lock (publicationGate)
            {
                if (!CanPublish(generation))
                {
                    return;
                }

                history.Add(sample);
                EventHandler<SessionGuardSampleEventArgs> handler =
                    SampleCaptured;
                if (handler != null)
                {
                    handler(this, new SessionGuardSampleEventArgs(sample));
                }
            }
        }

        private void PublishHeavySample(
            int generation,
            SessionGuardHeavySample sample)
        {
            lock (publicationGate)
            {
                if (!CanPublish(generation))
                {
                    return;
                }

                latestHeavySample = sample;
                EventHandler<SessionGuardHeavySampleEventArgs> handler =
                    HeavySampleCaptured;
                if (handler != null)
                {
                    handler(
                        this,
                        new SessionGuardHeavySampleEventArgs(sample));
                }
            }
        }

        private void PublishSamplingFault(
            int generation,
            bool heavySample,
            Exception error)
        {
            lock (publicationGate)
            {
                if (!CanPublish(generation))
                {
                    return;
                }

                EventHandler<SessionGuardSamplingFaultEventArgs> handler =
                    SamplingFaulted;
                if (handler != null)
                {
                    handler(
                        this,
                        new SessionGuardSamplingFaultEventArgs(
                            heavySample,
                            error));
                }
            }
        }

        private bool CanPublish(int generation)
        {
            return !disposed &&
                publicationsEnabled &&
                publicationGeneration == generation;
        }

        private void EndGeneration(int generation)
        {
            lock (publicationGate)
            {
                if (publicationGeneration == generation)
                {
                    publicationsEnabled = false;
                }

                lock (syncRoot)
                {
                    if (activeGeneration == generation)
                    {
                        samplingActive = false;
                    }
                }
            }
        }

        private void ScheduleDeferredDisposal(
            Task loopTask,
            Task expensiveTask,
            CancellationTokenSource tokenSource)
        {
            var tasks = new List<Task>(2);
            if (loopTask != null)
            {
                tasks.Add(loopTask);
            }
            if (expensiveTask != null &&
                !Object.ReferenceEquals(expensiveTask, loopTask))
            {
                tasks.Add(expensiveTask);
            }

            bool allCompleted = true;
            foreach (Task task in tasks)
            {
                if (!task.IsCompleted)
                {
                    allCompleted = false;
                    break;
                }
            }
            if (tasks.Count == 0 || allCompleted)
            {
                foreach (Task task in tasks)
                {
                    ObserveCompletedTask(task);
                }
                FinalizeDisposal(tokenSource);
                return;
            }

            Task.Factory.ContinueWhenAll(
                tasks.ToArray(),
                completedTasks =>
                {
                    foreach (Task task in completedTasks)
                    {
                        ObserveCompletedTask(task);
                    }
                    FinalizeDisposal(tokenSource);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static void ObserveCompletedTask(Task task)
        {
            if (task != null && task.IsFaulted)
            {
                task.Exception.Handle(delegate { return true; });
            }
        }

        private void FinalizeDisposal(CancellationTokenSource tokenSource)
        {
            bool disposeSource = false;
            lock (syncRoot)
            {
                if (Object.ReferenceEquals(shutdown, tokenSource))
                {
                    shutdown = null;
                }
                runningTask = null;
                heavyTask = null;
                if (ownsMetricsSource && !metricsSourceDisposed)
                {
                    metricsSourceDisposed = true;
                    disposeSource = true;
                }
            }

            if (tokenSource != null)
            {
                tokenSource.Dispose();
            }
            if (disposeSource)
            {
                try
                {
                    metricsSource.Dispose();
                }
                catch (Exception error)
                {
                    Trace.WriteLine(
                        "Session Guard metrics source disposal failed: " +
                        error.GetType().Name + ".");
                }
            }
        }

        private static long AddDurationSaturated(long timestamp, TimeSpan duration)
        {
            double scaledTicks = duration.TotalSeconds * Stopwatch.Frequency;
            long ticks = scaledTicks >= Int64.MaxValue
                ? Int64.MaxValue
                : (long)Math.Ceiling(scaledTicks);
            return timestamp > Int64.MaxValue - ticks
                ? Int64.MaxValue
                : timestamp + ticks;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException("SessionGuardSampler");
            }
        }
    }

    /// <summary>
    /// Captures read-only Windows metrics for the selected process and the system.
    /// </summary>
    internal sealed class WindowsSessionGuardMetricsSource : ISessionGuardMetricsSource
    {
        private static readonly TimeSpan WmiEnumerationTimeout =
            TimeSpan.FromSeconds(5);

        [StructLayout(LayoutKind.Sequential)]
        private struct PerformanceInformationNative
        {
            public uint Size;
            public UIntPtr CommitTotal;
            public UIntPtr CommitLimit;
            public UIntPtr CommitPeak;
            public UIntPtr PhysicalTotal;
            public UIntPtr PhysicalAvailable;
            public UIntPtr SystemCache;
            public UIntPtr KernelTotal;
            public UIntPtr KernelPaged;
            public UIntPtr KernelNonpaged;
            public UIntPtr PageSize;
            public uint HandleCount;
            public uint ProcessCount;
            public uint ThreadCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCountersNative
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        private sealed class TargetMetricPoint
        {
            public int ProcessId;
            public DateTime ProcessStartUtc;
            public bool ExecutablePathVerified;
            public string ExecutablePath;
            public long Timestamp;
            public TimeSpan ProcessorTime;
            public ulong ReadBytes;
            public ulong WriteBytes;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPerformanceInfo(
            ref PerformanceInformationNative information,
            uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessIoCounters(
            IntPtr processHandle,
            out IoCountersNative counters);

        private readonly object syncRoot = new object();
        private TargetMetricPoint previousTarget;
        private bool disposed;

        public SessionGuardSample CaptureCheap(SessionGuardTargetIdentity target)
        {
            if (target == null)
            {
                throw new ArgumentNullException("target");
            }

            ThrowIfDisposed();
            DateTime capturedUtc = DateTime.UtcNow;
            long timestamp = Stopwatch.GetTimestamp();

            long totalPhysical = 0;
            long availablePhysical = 0;
            long committed = 0;
            long commitLimit = 0;
            bool systemAvailable = TryCaptureSystemMetrics(
                out totalPhysical,
                out availablePhysical,
                out committed,
                out commitLimit);

            long privateBytes = 0;
            long workingSet = 0;
            double cpuPercent = 0.0;
            long readDelta = 0;
            long writeDelta = 0;
            string targetUnavailableReason;
            bool targetAvailable = TryCaptureTargetMetrics(
                target,
                timestamp,
                out privateBytes,
                out workingSet,
                out cpuPercent,
                out readDelta,
                out writeDelta,
                out targetUnavailableReason);

            string reason = String.Empty;
            if (!systemAvailable && !targetAvailable)
            {
                reason = "System and selected-process metrics are unavailable.";
            }
            else if (!systemAvailable)
            {
                reason = "System memory metrics are unavailable.";
            }
            else if (!targetAvailable)
            {
                reason = targetUnavailableReason;
            }

            return new SessionGuardSample(
                capturedUtc,
                timestamp,
                systemAvailable,
                totalPhysical,
                availablePhysical,
                committed,
                commitLimit,
                targetAvailable,
                target.ProcessId,
                privateBytes,
                workingSet,
                cpuPercent,
                readDelta,
                writeDelta,
                reason);
        }

        public SessionGuardHeavySample CaptureHeavy()
        {
            ThrowIfDisposed();
            DateTime capturedUtc = DateTime.UtcNow;
            long timestamp = Stopwatch.GetTimestamp();
            return new SessionGuardHeavySample(
                capturedUtc,
                timestamp,
                CapturePagefileConfiguration(capturedUtc));
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                disposed = true;
                previousTarget = null;
            }
        }

        private bool TryCaptureTargetMetrics(
            SessionGuardTargetIdentity target,
            long timestamp,
            out long privateBytes,
            out long workingSet,
            out double cpuPercent,
            out long readDelta,
            out long writeDelta,
            out string unavailableReason)
        {
            privateBytes = 0;
            workingSet = 0;
            cpuPercent = 0.0;
            readDelta = 0;
            writeDelta = 0;
            unavailableReason = String.Empty;

            try
            {
                using (Process process = Process.GetProcessById(target.ProcessId))
                {
                    if (process.HasExited)
                    {
                        unavailableReason = "The selected process has exited.";
                        return false;
                    }

                    DateTime processStartUtc = process.StartTime.ToUniversalTime();
                    if (processStartUtc != target.ProcessStartTimeUtc)
                    {
                        ResetPreviousTarget();
                        unavailableReason =
                            "The selected PID was reused by a different process instance.";
                        return false;
                    }

                    TargetMetricPoint previous;
                    lock (syncRoot)
                    {
                        previous = previousTarget;
                    }
                    bool previousIdentityVerified =
                        previous != null &&
                        previous.ProcessId == target.ProcessId &&
                        previous.ProcessStartUtc == target.ProcessStartTimeUtc &&
                        previous.ExecutablePathVerified &&
                        String.Equals(
                            previous.ExecutablePath,
                            target.ExecutablePath,
                            StringComparison.OrdinalIgnoreCase);
                    if (target.HasExecutablePath && !previousIdentityVerified)
                    {
                        string actualExecutablePath = process.MainModule == null
                            ? String.Empty
                            : process.MainModule.FileName;
                        if (!target.Matches(
                                process.Id,
                                processStartUtc,
                                actualExecutablePath))
                        {
                            ResetPreviousTarget();
                            unavailableReason =
                                "The selected PID no longer matches the expected executable path.";
                            return false;
                        }
                    }

                    TimeSpan processorTime = process.TotalProcessorTime;
                    privateBytes = Math.Max(0L, process.PrivateMemorySize64);
                    workingSet = Math.Max(0L, process.WorkingSet64);

                    IoCountersNative io;
                    bool ioAvailable = GetProcessIoCounters(
                        process.Handle,
                        out io);
                    ulong readBytes = ioAvailable ? io.ReadTransferCount : 0UL;
                    ulong writeBytes = ioAvailable ? io.WriteTransferCount : 0UL;

                    lock (syncRoot)
                    {
                        previous = previousTarget;
                        if (previous != null &&
                            previous.ProcessId == target.ProcessId &&
                            previous.ProcessStartUtc == processStartUtc &&
                            timestamp > previous.Timestamp)
                        {
                            double elapsedSeconds =
                                (timestamp - previous.Timestamp) /
                                (double)Stopwatch.Frequency;
                            double processorSeconds =
                                (processorTime - previous.ProcessorTime).TotalSeconds;
                            if (elapsedSeconds > 0.0 && processorSeconds >= 0.0)
                            {
                                cpuPercent = ClampPercentage(
                                    processorSeconds /
                                    elapsedSeconds /
                                    Math.Max(1, Environment.ProcessorCount) *
                                    100.0);
                            }

                            if (ioAvailable)
                            {
                                readDelta = UnsignedDeltaToLong(
                                    readBytes,
                                    previous.ReadBytes);
                                writeDelta = UnsignedDeltaToLong(
                                    writeBytes,
                                    previous.WriteBytes);
                            }
                        }

                        previousTarget = new TargetMetricPoint
                        {
                            ProcessId = target.ProcessId,
                            ProcessStartUtc = processStartUtc,
                            ExecutablePathVerified = true,
                            ExecutablePath = target.ExecutablePath,
                            Timestamp = timestamp,
                            ProcessorTime = processorTime,
                            ReadBytes = readBytes,
                            WriteBytes = writeBytes
                        };
                    }
                }

                return true;
            }
            catch (Exception error)
            {
                ResetPreviousTarget();
                unavailableReason =
                    "The selected process cannot be queried: " +
                    error.GetType().Name + ".";
                return false;
            }
        }

        private void ResetPreviousTarget()
        {
            lock (syncRoot)
            {
                previousTarget = null;
            }
        }

        private static bool TryCaptureSystemMetrics(
            out long totalPhysical,
            out long availablePhysical,
            out long committed,
            out long commitLimit)
        {
            totalPhysical = 0;
            availablePhysical = 0;
            committed = 0;
            commitLimit = 0;

            var information = new PerformanceInformationNative();
            information.Size = (uint)Marshal.SizeOf(
                typeof(PerformanceInformationNative));
            try
            {
                if (!GetPerformanceInfo(ref information, information.Size))
                {
                    return false;
                }

                ulong pageSize = information.PageSize.ToUInt64();
                totalPhysical = PagesToBytes(
                    information.PhysicalTotal.ToUInt64(),
                    pageSize);
                availablePhysical = PagesToBytes(
                    information.PhysicalAvailable.ToUInt64(),
                    pageSize);
                committed = PagesToBytes(
                    information.CommitTotal.ToUInt64(),
                    pageSize);
                commitLimit = PagesToBytes(
                    information.CommitLimit.ToUInt64(),
                    pageSize);
                return totalPhysical > 0 &&
                    availablePhysical <= totalPhysical &&
                    commitLimit > 0;
            }
            catch
            {
                totalPhysical = 0;
                availablePhysical = 0;
                committed = 0;
                commitLimit = 0;
                return false;
            }
        }

        private static PagefileConfigurationSnapshot CapturePagefileConfiguration(
            DateTime capturedUtc)
        {
            PagefileConfigurationKind kind = PagefileConfigurationKind.Unknown;
            long configuredBytes = 0;
            long allocatedBytes = 0;
            long physicalBytes = 0;
            string reason = String.Empty;

            try
            {
                bool automatic = false;
                bool automaticKnown = false;
                using (var searcher = CreateTimedManagementSearcher(
                    "SELECT AutomaticManagedPagefile, TotalPhysicalMemory FROM Win32_ComputerSystem"))
                using (ManagementObjectCollection systems = searcher.Get())
                {
                    foreach (ManagementObject system in systems)
                    {
                        using (system)
                        {
                            automatic = Convert.ToBoolean(
                                system["AutomaticManagedPagefile"]);
                            automaticKnown = true;
                            physicalBytes = ConvertUnsignedToLong(
                                system["TotalPhysicalMemory"]);
                            break;
                        }
                    }
                }

                int settingsCount = 0;
                using (var searcher = CreateTimedManagementSearcher(
                    "SELECT InitialSize, MaximumSize FROM Win32_PageFileSetting"))
                using (ManagementObjectCollection settings = searcher.Get())
                {
                    foreach (ManagementObject setting in settings)
                    {
                        using (setting)
                        {
                            settingsCount++;
                            long maximumMib = ConvertUnsignedToLong(
                                setting["MaximumSize"]);
                            long initialMib = ConvertUnsignedToLong(
                                setting["InitialSize"]);
                            configuredBytes = AddSaturated(
                                configuredBytes,
                                MebibytesToBytes(Math.Max(
                                    maximumMib,
                                    initialMib)));
                        }
                    }
                }

                int usageCount = 0;
                using (var searcher = CreateTimedManagementSearcher(
                    "SELECT AllocatedBaseSize FROM Win32_PageFileUsage"))
                using (ManagementObjectCollection usages = searcher.Get())
                {
                    foreach (ManagementObject usage in usages)
                    {
                        using (usage)
                        {
                            usageCount++;
                            allocatedBytes = AddSaturated(
                                allocatedBytes,
                                MebibytesToBytes(ConvertUnsignedToLong(
                                    usage["AllocatedBaseSize"])));
                        }
                    }
                }

                if (!automaticKnown)
                {
                    reason = "Windows did not return page-file management state.";
                }
                else if (automatic)
                {
                    kind = PagefileConfigurationKind.SystemManaged;
                }
                else if (settingsCount > 0)
                {
                    kind = PagefileConfigurationKind.Fixed;
                }
                else if (usageCount == 0)
                {
                    kind = PagefileConfigurationKind.Disabled;
                }
                else
                {
                    reason = "The active page file has no matching configuration record.";
                }
            }
            catch (Exception error)
            {
                kind = PagefileConfigurationKind.Unknown;
                reason = error.GetType().Name;
            }

            long freeBytes = 0;
            long totalBytes = 0;
            TryGetSystemDriveSpace(out freeBytes, out totalBytes);
            return new PagefileConfigurationSnapshot(
                capturedUtc,
                kind,
                configuredBytes,
                allocatedBytes,
                physicalBytes,
                freeBytes,
                totalBytes,
                reason);
        }

        private static ManagementObjectSearcher CreateTimedManagementSearcher(
            string query)
        {
            var searcher = new ManagementObjectSearcher(query);
            searcher.Options.Timeout = WmiEnumerationTimeout;
            searcher.Options.ReturnImmediately = false;
            searcher.Options.Rewindable = false;
            return searcher;
        }

        private static void TryGetSystemDriveSpace(
            out long freeBytes,
            out long totalBytes)
        {
            freeBytes = 0;
            totalBytes = 0;
            try
            {
                string systemDirectory = Environment.GetFolderPath(
                    Environment.SpecialFolder.System);
                string root = Path.GetPathRoot(systemDirectory);
                if (String.IsNullOrWhiteSpace(root))
                {
                    return;
                }

                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                {
                    return;
                }

                freeBytes = Math.Max(0L, drive.AvailableFreeSpace);
                totalBytes = Math.Max(0L, drive.TotalSize);
            }
            catch
            {
                freeBytes = 0;
                totalBytes = 0;
            }
        }

        private static long PagesToBytes(ulong pages, ulong pageSize)
        {
            if (pageSize == 0)
            {
                return 0;
            }
            if (pages > (ulong)Int64.MaxValue / pageSize)
            {
                return Int64.MaxValue;
            }
            return (long)(pages * pageSize);
        }

        private static long MebibytesToBytes(long mebibytes)
        {
            const long bytesPerMebibyte = 1024L * 1024L;
            return mebibytes > Int64.MaxValue / bytesPerMebibyte
                ? Int64.MaxValue
                : mebibytes * bytesPerMebibyte;
        }

        private static long ConvertUnsignedToLong(object value)
        {
            if (value == null || value == DBNull.Value)
            {
                return 0;
            }

            try
            {
                ulong converted = Convert.ToUInt64(value);
                return converted > (ulong)Int64.MaxValue
                    ? Int64.MaxValue
                    : (long)converted;
            }
            catch
            {
                return 0;
            }
        }

        private static long AddSaturated(long left, long right)
        {
            return left > Int64.MaxValue - right
                ? Int64.MaxValue
                : left + right;
        }

        private static long UnsignedDeltaToLong(ulong current, ulong previous)
        {
            if (current < previous)
            {
                return 0;
            }
            ulong delta = current - previous;
            return delta > (ulong)Int64.MaxValue
                ? Int64.MaxValue
                : (long)delta;
        }

        private static double ClampPercentage(double value)
        {
            return Math.Min(100.0, Math.Max(0.0, value));
        }

        private void ThrowIfDisposed()
        {
            lock (syncRoot)
            {
                if (disposed)
                {
                    throw new ObjectDisposedException(
                        "WindowsSessionGuardMetricsSource");
                }
            }
        }
    }
}
