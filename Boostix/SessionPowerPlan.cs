using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace Boostix
{
    internal enum SessionPowerPlanAction
    {
        None,
        Start,
        Stop,
        CrashRecovery
    }

    internal enum SessionPowerPlanStatus
    {
        Activated,
        AlreadyActive,
        Restored,
        Recovered,
        AlreadyStopped,
        NoRecoveryNeeded,
        ExternalOverridePreserved,
        LiveSessionPreserved,
        RecoveryRequired,
        SessionMismatch,
        UnsupportedPlatform,
        SkippedOnBattery,
        PowerSourceUnavailable,
        InvalidSession,
        TrustedStateMissing,
        TrustedStateRejected,
        MarkerRejected,
        MarkerWriteFailed,
        MarkerDeleteFailed,
        ActiveSchemeQueryFailed,
        ActivationFailed,
        RestoreFailed,
        CommandTimedOut,
        DependencyFailure
    }

    internal sealed class SessionPowerPlanOperationResult
    {
        private SessionPowerPlanOperationResult(
            SessionPowerPlanAction action,
            SessionPowerPlanStatus status,
            bool changed,
            string detail,
            Guid? boostPlanGuid,
            Guid? previousPlanGuid)
        {
            Action = action;
            Status = status;
            Changed = changed;
            Detail = detail ?? string.Empty;
            BoostPlanGuid = boostPlanGuid;
            PreviousPlanGuid = previousPlanGuid;
        }

        public SessionPowerPlanAction Action { get; private set; }
        public SessionPowerPlanStatus Status { get; private set; }
        public bool Changed { get; private set; }
        public string Detail { get; private set; }
        public Guid? BoostPlanGuid { get; private set; }
        public Guid? PreviousPlanGuid { get; private set; }

        internal static SessionPowerPlanOperationResult Create(
            SessionPowerPlanAction action,
            SessionPowerPlanStatus status,
            bool changed,
            string detail,
            Guid? boostPlanGuid,
            Guid? previousPlanGuid)
        {
            return new SessionPowerPlanOperationResult(
                action,
                status,
                changed,
                detail,
                boostPlanGuid,
                previousPlanGuid);
        }
    }

    internal enum SessionPowerSource
    {
        Unknown,
        AlternatingCurrent,
        Battery
    }

    internal enum SessionPowerPlanCommand
    {
        GetActiveScheme,
        SetActiveScheme
    }

    internal enum SessionPowerPlanCommandStatus
    {
        Succeeded,
        Failed,
        TimedOut,
        UnsupportedPlatform,
        InvalidRequest
    }

    internal sealed class SessionPowerPlanCommandResult
    {
        public SessionPowerPlanCommandStatus Status { get; private set; }
        public int ExitCode { get; private set; }
        public string StandardOutput { get; private set; }
        public string StandardError { get; private set; }

        public static SessionPowerPlanCommandResult Create(
            SessionPowerPlanCommandStatus status,
            int exitCode,
            string standardOutput,
            string standardError)
        {
            return new SessionPowerPlanCommandResult
            {
                Status = status,
                ExitCode = exitCode,
                StandardOutput = standardOutput ?? string.Empty,
                StandardError = standardError ?? string.Empty
            };
        }
    }

    internal enum SessionPowerPlanStateStatus
    {
        Valid,
        Missing,
        Corrupt,
        UntrustedPath,
        UntrustedOwner,
        AccessDenied,
        IoFailure
    }

    internal sealed class SessionPowerPlanStateRead<T> where T : class
    {
        public SessionPowerPlanStateStatus Status { get; private set; }
        public T Value { get; private set; }
        public string Detail { get; private set; }

        public static SessionPowerPlanStateRead<T> Create(
            SessionPowerPlanStateStatus status,
            T value,
            string detail)
        {
            return new SessionPowerPlanStateRead<T>
            {
                Status = status,
                Value = value,
                Detail = detail ?? string.Empty
            };
        }
    }

    internal sealed class SessionPowerPlanConfiguration
    {
        public const string RequiredPlanName = "Boostix Performance";

        public SessionPowerPlanConfiguration(Guid planGuid, string planName)
        {
            if (planGuid == Guid.Empty)
            {
                throw new ArgumentException("A non-empty power-plan GUID is required.", "planGuid");
            }
            if (!string.Equals(
                    planName,
                    RequiredPlanName,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The trusted plan name must be exactly Boostix Performance.",
                    "planName");
            }

            PlanGuid = planGuid;
            PlanName = planName;
        }

        public Guid PlanGuid { get; private set; }
        public string PlanName { get; private set; }
    }

    internal sealed class SessionPowerPlanMarker
    {
        public SessionPowerPlanMarker(
            Guid sessionId,
            int ownerProcessId,
            DateTime ownerProcessStartTimeUtc,
            Guid boostPlanGuid,
            Guid previousPlanGuid,
            DateTime createdUtc)
        {
            if (sessionId == Guid.Empty)
            {
                throw new ArgumentException("A non-empty session ID is required.", "sessionId");
            }
            if (ownerProcessId <= 0)
            {
                throw new ArgumentOutOfRangeException("ownerProcessId");
            }
            if (ownerProcessStartTimeUtc.Kind != DateTimeKind.Utc ||
                ownerProcessStartTimeUtc == DateTime.MinValue)
            {
                throw new ArgumentException(
                    "The owner start time must be a valid UTC value.",
                    "ownerProcessStartTimeUtc");
            }
            if (boostPlanGuid == Guid.Empty || previousPlanGuid == Guid.Empty)
            {
                throw new ArgumentException("Power-plan GUIDs must be non-empty.");
            }
            if (boostPlanGuid == previousPlanGuid)
            {
                throw new ArgumentException(
                    "The previous plan cannot be the Boostix plan.",
                    "previousPlanGuid");
            }
            if (createdUtc.Kind != DateTimeKind.Utc || createdUtc == DateTime.MinValue)
            {
                throw new ArgumentException(
                    "The marker creation time must be a valid UTC value.",
                    "createdUtc");
            }

            SessionId = sessionId;
            OwnerProcessId = ownerProcessId;
            OwnerProcessStartTimeUtc = ownerProcessStartTimeUtc;
            BoostPlanGuid = boostPlanGuid;
            PreviousPlanGuid = previousPlanGuid;
            CreatedUtc = createdUtc;
        }

        public Guid SessionId { get; private set; }
        public int OwnerProcessId { get; private set; }
        public DateTime OwnerProcessStartTimeUtc { get; private set; }
        public Guid BoostPlanGuid { get; private set; }
        public Guid PreviousPlanGuid { get; private set; }
        public DateTime CreatedUtc { get; private set; }
    }

    internal interface ISessionPowerPlanPlatform
    {
        bool IsWindows { get; }
        SessionPowerSource GetPowerSource();
        int CurrentProcessId { get; }
        DateTime CurrentProcessStartTimeUtc { get; }
        DateTime UtcNow { get; }
        bool IsProcessInstanceAlive(int processId, DateTime processStartTimeUtc);
    }

    internal interface ISessionPowerPlanCommandRunner
    {
        SessionPowerPlanCommandResult Run(
            SessionPowerPlanCommand command,
            Guid? schemeGuid,
            TimeSpan timeout);
    }

    internal interface ISessionPowerPlanStateStore
    {
        SessionPowerPlanStateRead<SessionPowerPlanConfiguration>
            ReadTrustedConfiguration();

        SessionPowerPlanStateRead<SessionPowerPlanMarker> ReadMarker();

        SessionPowerPlanStateStatus WriteMarker(
            SessionPowerPlanMarker marker,
            out string detail);

        SessionPowerPlanStateStatus DeleteMarker(out string detail);
    }

    /// <summary>
    /// Applies one pre-provisioned Boostix power plan only for one Boost
    /// session, and restores the exact plan observed before activation.
    /// </summary>
    internal sealed class SessionPowerPlanManager
    {
        private static readonly TimeSpan MinimumCommandTimeout =
            TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan MaximumCommandTimeout =
            TimeSpan.FromSeconds(10);

        private readonly object syncRoot = new object();
        private readonly ISessionPowerPlanPlatform platform;
        private readonly ISessionPowerPlanCommandRunner runner;
        private readonly ISessionPowerPlanStateStore stateStore;
        private readonly TimeSpan commandTimeout;

        public SessionPowerPlanManager(
            ISessionPowerPlanPlatform platform,
            ISessionPowerPlanCommandRunner runner,
            ISessionPowerPlanStateStore stateStore,
            TimeSpan commandTimeout)
        {
            if (platform == null)
            {
                throw new ArgumentNullException("platform");
            }
            if (runner == null)
            {
                throw new ArgumentNullException("runner");
            }
            if (stateStore == null)
            {
                throw new ArgumentNullException("stateStore");
            }
            if (commandTimeout < MinimumCommandTimeout ||
                commandTimeout > MaximumCommandTimeout)
            {
                throw new ArgumentOutOfRangeException(
                    "commandTimeout",
                    "The powercfg timeout must be between 100 ms and 10 seconds.");
            }

            this.platform = platform;
            this.runner = runner;
            this.stateStore = stateStore;
            this.commandTimeout = commandTimeout;
        }

        /// <summary>
        /// Activates the trusted Boostix Performance plan for a new session.
        /// Call this from a worker thread after crash recovery has completed.
        /// </summary>
        public SessionPowerPlanOperationResult Start(Guid sessionId)
        {
            lock (syncRoot)
            {
                try
                {
                    return StartCore(sessionId);
                }
                catch (Exception exception)
                {
                    return UnexpectedFailure(SessionPowerPlanAction.Start, exception);
                }
            }
        }

        /// <summary>
        /// Restores the previously active plan if this session still owns the
        /// active Boostix plan. An external override is preserved.
        /// </summary>
        public SessionPowerPlanOperationResult Stop(Guid sessionId)
        {
            lock (syncRoot)
            {
                try
                {
                    return StopCore(sessionId);
                }
                catch (Exception exception)
                {
                    return UnexpectedFailure(SessionPowerPlanAction.Stop, exception);
                }
            }
        }

        /// <summary>
        /// Reconciles a marker left by a crashed process. A marker owned by a
        /// still-running exact PID/start-time pair is never disturbed.
        /// </summary>
        public SessionPowerPlanOperationResult RecoverOnStartup()
        {
            lock (syncRoot)
            {
                try
                {
                    return RecoverCore();
                }
                catch (Exception exception)
                {
                    return UnexpectedFailure(
                        SessionPowerPlanAction.CrashRecovery,
                        exception);
                }
            }
        }

        private SessionPowerPlanOperationResult StartCore(Guid sessionId)
        {
            const SessionPowerPlanAction action = SessionPowerPlanAction.Start;
            if (sessionId == Guid.Empty)
            {
                return Result(action, SessionPowerPlanStatus.InvalidSession, false,
                    "A non-empty session ID is required.", null, null);
            }
            SessionPowerPlanOperationResult platformFailure = CheckPlatform(action);
            if (platformFailure != null)
            {
                return platformFailure;
            }

            SessionPowerSource source = platform.GetPowerSource();
            if (source == SessionPowerSource.Battery)
            {
                return Result(action, SessionPowerPlanStatus.SkippedOnBattery, false,
                    "The Boostix plan is activated only while connected to AC power.",
                    null,
                    null);
            }
            if (source != SessionPowerSource.AlternatingCurrent)
            {
                return Result(action, SessionPowerPlanStatus.PowerSourceUnavailable,
                    false, "The current power source could not be verified.", null, null);
            }

            SessionPowerPlanConfiguration configuration;
            SessionPowerPlanOperationResult configurationFailure =
                ReadConfiguration(action, out configuration);
            if (configurationFailure != null)
            {
                return configurationFailure;
            }

            SessionPowerPlanMarker existingMarker;
            bool markerExists;
            SessionPowerPlanOperationResult markerFailure =
                ReadMarker(action, out existingMarker, out markerExists);
            if (markerFailure != null)
            {
                return markerFailure;
            }
            if (markerExists)
            {
                if (!MarkerMatchesConfiguration(existingMarker, configuration))
                {
                    return Result(action, SessionPowerPlanStatus.MarkerRejected, false,
                        "The session marker does not match trusted plan state.",
                        configuration.PlanGuid,
                        null);
                }

                if (MarkerBelongsToCurrentSession(existingMarker, sessionId))
                {
                    Guid activeForExisting;
                    SessionPowerPlanOperationResult queryFailure;
                    if (!TryGetActiveScheme(action, out activeForExisting, out queryFailure))
                    {
                        return queryFailure;
                    }
                    if (activeForExisting == configuration.PlanGuid)
                    {
                        return Result(action, SessionPowerPlanStatus.AlreadyActive, false,
                            "This session already owns the active Boostix plan.",
                            configuration.PlanGuid,
                            existingMarker.PreviousPlanGuid);
                    }

                    SessionPowerPlanOperationResult deleteFailure =
                        DeleteMarker(action, configuration, existingMarker);
                    return deleteFailure ?? Result(
                        action,
                        SessionPowerPlanStatus.ExternalOverridePreserved,
                        false,
                        "Another component changed the active plan; Boostix did not override it.",
                        configuration.PlanGuid,
                        existingMarker.PreviousPlanGuid);
                }

                if (platform.IsProcessInstanceAlive(
                        existingMarker.OwnerProcessId,
                        existingMarker.OwnerProcessStartTimeUtc))
                {
                    return Result(action, SessionPowerPlanStatus.LiveSessionPreserved,
                        false, "Another live Boostix session owns the marker.",
                        configuration.PlanGuid, existingMarker.PreviousPlanGuid);
                }
                return Result(action, SessionPowerPlanStatus.RecoveryRequired, false,
                    "A stale session marker must be recovered before activation.",
                    configuration.PlanGuid, existingMarker.PreviousPlanGuid);
            }

            Guid previousPlanGuid;
            SessionPowerPlanOperationResult activeQueryFailure;
            if (!TryGetActiveScheme(action, out previousPlanGuid, out activeQueryFailure))
            {
                return activeQueryFailure;
            }
            if (previousPlanGuid == configuration.PlanGuid)
            {
                return Result(action, SessionPowerPlanStatus.AlreadyActive, false,
                    "Boostix Performance was already active and was not claimed.",
                    configuration.PlanGuid, previousPlanGuid);
            }

            // Re-check immediately before writing ownership and changing state.
            // An unplug race therefore fails closed without invoking powercfg.
            source = platform.GetPowerSource();
            if (source == SessionPowerSource.Battery)
            {
                return Result(action, SessionPowerPlanStatus.SkippedOnBattery, false,
                    "AC power was disconnected before activation.",
                    configuration.PlanGuid, previousPlanGuid);
            }
            if (source != SessionPowerSource.AlternatingCurrent)
            {
                return Result(action, SessionPowerPlanStatus.PowerSourceUnavailable,
                    false, "The power source changed before activation.",
                    configuration.PlanGuid, previousPlanGuid);
            }

            var marker = new SessionPowerPlanMarker(
                sessionId,
                platform.CurrentProcessId,
                EnsureUtc(platform.CurrentProcessStartTimeUtc),
                configuration.PlanGuid,
                previousPlanGuid,
                EnsureUtc(platform.UtcNow));
            string writeDetail;
            SessionPowerPlanStateStatus writeStatus =
                stateStore.WriteMarker(marker, out writeDetail);
            if (writeStatus != SessionPowerPlanStateStatus.Valid)
            {
                return Result(action, SessionPowerPlanStatus.MarkerWriteFailed, false,
                    StateDetail(writeStatus, writeDetail), configuration.PlanGuid,
                    previousPlanGuid);
            }

            SessionPowerPlanCommandResult activation = runner.Run(
                SessionPowerPlanCommand.SetActiveScheme,
                configuration.PlanGuid,
                commandTimeout);
            if (activation == null ||
                activation.Status != SessionPowerPlanCommandStatus.Succeeded)
            {
                SessionPowerPlanStatus status = activation != null &&
                    activation.Status == SessionPowerPlanCommandStatus.TimedOut
                        ? SessionPowerPlanStatus.CommandTimedOut
                        : SessionPowerPlanStatus.ActivationFailed;
                CleanupFailedActivation(configuration, marker);
                return Result(action, status, false,
                    CommandDetail("Power-plan activation failed", activation),
                    configuration.PlanGuid, previousPlanGuid);
            }

            Guid verifiedActive;
            SessionPowerPlanOperationResult verificationFailure;
            if (!TryGetActiveScheme(action, out verifiedActive, out verificationFailure))
            {
                // Keep the marker: the next launch can safely determine whether
                // activation happened before the verification failure.
                return verificationFailure;
            }
            if (verifiedActive != configuration.PlanGuid)
            {
                SessionPowerPlanOperationResult deleteFailure =
                    DeleteMarker(action, configuration, marker);
                return deleteFailure ?? Result(
                    action,
                    SessionPowerPlanStatus.ExternalOverridePreserved,
                    false,
                    "The active plan changed during activation; the external choice was preserved.",
                    configuration.PlanGuid,
                    previousPlanGuid);
            }

            return Result(action, SessionPowerPlanStatus.Activated, true,
                "Boostix Performance is active for this session.",
                configuration.PlanGuid, previousPlanGuid);
        }

        private SessionPowerPlanOperationResult StopCore(Guid sessionId)
        {
            const SessionPowerPlanAction action = SessionPowerPlanAction.Stop;
            if (sessionId == Guid.Empty)
            {
                return Result(action, SessionPowerPlanStatus.InvalidSession, false,
                    "A non-empty session ID is required.", null, null);
            }
            SessionPowerPlanOperationResult platformFailure = CheckPlatform(action);
            if (platformFailure != null)
            {
                return platformFailure;
            }

            SessionPowerPlanConfiguration configuration;
            SessionPowerPlanOperationResult configurationFailure =
                ReadConfiguration(action, out configuration);
            if (configurationFailure != null)
            {
                return configurationFailure;
            }

            SessionPowerPlanMarker marker;
            bool markerExists;
            SessionPowerPlanOperationResult markerFailure =
                ReadMarker(action, out marker, out markerExists);
            if (markerFailure != null)
            {
                return markerFailure;
            }
            if (!markerExists)
            {
                return Result(action, SessionPowerPlanStatus.AlreadyStopped, false,
                    "No session power-plan marker exists.", configuration.PlanGuid, null);
            }
            if (!MarkerMatchesConfiguration(marker, configuration))
            {
                return Result(action, SessionPowerPlanStatus.MarkerRejected, false,
                    "The marker does not match trusted plan state.",
                    configuration.PlanGuid, null);
            }
            if (!MarkerBelongsToCurrentSession(marker, sessionId))
            {
                return Result(action, SessionPowerPlanStatus.SessionMismatch, false,
                    "The marker belongs to another process instance or session.",
                    configuration.PlanGuid, marker.PreviousPlanGuid);
            }

            return RestoreOwnedMarker(action, configuration, marker, false);
        }

        private SessionPowerPlanOperationResult RecoverCore()
        {
            const SessionPowerPlanAction action =
                SessionPowerPlanAction.CrashRecovery;
            SessionPowerPlanOperationResult platformFailure = CheckPlatform(action);
            if (platformFailure != null)
            {
                return platformFailure;
            }

            SessionPowerPlanConfiguration configuration;
            SessionPowerPlanOperationResult configurationFailure =
                ReadConfiguration(action, out configuration);
            if (configurationFailure != null)
            {
                return configurationFailure;
            }

            SessionPowerPlanMarker marker;
            bool markerExists;
            SessionPowerPlanOperationResult markerFailure =
                ReadMarker(action, out marker, out markerExists);
            if (markerFailure != null)
            {
                return markerFailure;
            }
            if (!markerExists)
            {
                return Result(action, SessionPowerPlanStatus.NoRecoveryNeeded,
                    false, "No crash-recovery marker exists.",
                    configuration.PlanGuid, null);
            }
            if (!MarkerMatchesConfiguration(marker, configuration))
            {
                return Result(action, SessionPowerPlanStatus.MarkerRejected, false,
                    "The recovery marker does not match trusted plan state.",
                    configuration.PlanGuid, null);
            }
            if (platform.IsProcessInstanceAlive(
                    marker.OwnerProcessId,
                    marker.OwnerProcessStartTimeUtc))
            {
                return Result(action, SessionPowerPlanStatus.LiveSessionPreserved,
                    false, "The exact process instance that owns the marker is still alive.",
                    configuration.PlanGuid, marker.PreviousPlanGuid);
            }

            return RestoreOwnedMarker(action, configuration, marker, true);
        }

        private SessionPowerPlanOperationResult RestoreOwnedMarker(
            SessionPowerPlanAction action,
            SessionPowerPlanConfiguration configuration,
            SessionPowerPlanMarker marker,
            bool recovery)
        {
            Guid activeGuid;
            SessionPowerPlanOperationResult queryFailure;
            if (!TryGetActiveScheme(action, out activeGuid, out queryFailure))
            {
                return queryFailure;
            }
            if (activeGuid != marker.BoostPlanGuid)
            {
                SessionPowerPlanOperationResult deleteFailure =
                    DeleteMarker(action, configuration, marker);
                return deleteFailure ?? Result(
                    action,
                    SessionPowerPlanStatus.ExternalOverridePreserved,
                    false,
                    "The active plan was changed externally and was not overwritten.",
                    configuration.PlanGuid,
                    marker.PreviousPlanGuid);
            }

            SessionPowerPlanCommandResult restore = runner.Run(
                SessionPowerPlanCommand.SetActiveScheme,
                marker.PreviousPlanGuid,
                commandTimeout);
            if (restore == null ||
                restore.Status != SessionPowerPlanCommandStatus.Succeeded)
            {
                return Result(
                    action,
                    restore != null &&
                        restore.Status == SessionPowerPlanCommandStatus.TimedOut
                            ? SessionPowerPlanStatus.CommandTimedOut
                            : SessionPowerPlanStatus.RestoreFailed,
                    false,
                    CommandDetail("Power-plan restore failed", restore),
                    configuration.PlanGuid,
                    marker.PreviousPlanGuid);
            }

            Guid verifiedActive;
            SessionPowerPlanOperationResult verificationFailure;
            if (!TryGetActiveScheme(action, out verifiedActive, out verificationFailure))
            {
                return verificationFailure;
            }
            if (verifiedActive == marker.BoostPlanGuid)
            {
                return Result(action, SessionPowerPlanStatus.RestoreFailed, false,
                    "The Boostix plan remained active after the restore command.",
                    configuration.PlanGuid, marker.PreviousPlanGuid);
            }
            if (verifiedActive != marker.PreviousPlanGuid)
            {
                SessionPowerPlanOperationResult deleteFailure =
                    DeleteMarker(action, configuration, marker);
                return deleteFailure ?? Result(
                    action,
                    SessionPowerPlanStatus.ExternalOverridePreserved,
                    false,
                    "Another component selected a different plan during restore.",
                    configuration.PlanGuid,
                    marker.PreviousPlanGuid);
            }

            SessionPowerPlanOperationResult markerDeleteFailure =
                DeleteMarker(action, configuration, marker);
            if (markerDeleteFailure != null)
            {
                return markerDeleteFailure;
            }
            return Result(
                action,
                recovery
                    ? SessionPowerPlanStatus.Recovered
                    : SessionPowerPlanStatus.Restored,
                true,
                recovery
                    ? "The pre-crash power plan was restored."
                    : "The pre-session power plan was restored.",
                configuration.PlanGuid,
                marker.PreviousPlanGuid);
        }

        private SessionPowerPlanOperationResult CheckPlatform(
            SessionPowerPlanAction action)
        {
            return platform.IsWindows
                ? null
                : Result(action, SessionPowerPlanStatus.UnsupportedPlatform, false,
                    "Session power-plan management is available only on Windows.",
                    null, null);
        }

        private SessionPowerPlanOperationResult ReadConfiguration(
            SessionPowerPlanAction action,
            out SessionPowerPlanConfiguration configuration)
        {
            configuration = null;
            SessionPowerPlanStateRead<SessionPowerPlanConfiguration> read =
                stateStore.ReadTrustedConfiguration();
            if (read == null)
            {
                return Result(action, SessionPowerPlanStatus.TrustedStateRejected,
                    false, "The trusted-state store returned no result.", null, null);
            }
            if (read.Status == SessionPowerPlanStateStatus.Missing)
            {
                return Result(action, SessionPowerPlanStatus.TrustedStateMissing,
                    false, StateDetail(read.Status, read.Detail), null, null);
            }
            if (read.Status != SessionPowerPlanStateStatus.Valid || read.Value == null ||
                read.Value.PlanGuid == Guid.Empty ||
                !string.Equals(
                    read.Value.PlanName,
                    SessionPowerPlanConfiguration.RequiredPlanName,
                    StringComparison.Ordinal))
            {
                return Result(action, SessionPowerPlanStatus.TrustedStateRejected,
                    false, StateDetail(read.Status, read.Detail), null, null);
            }
            configuration = read.Value;
            return null;
        }

        private SessionPowerPlanOperationResult ReadMarker(
            SessionPowerPlanAction action,
            out SessionPowerPlanMarker marker,
            out bool exists)
        {
            marker = null;
            exists = false;
            SessionPowerPlanStateRead<SessionPowerPlanMarker> read =
                stateStore.ReadMarker();
            if (read == null)
            {
                return Result(action, SessionPowerPlanStatus.MarkerRejected, false,
                    "The marker store returned no result.", null, null);
            }
            if (read.Status == SessionPowerPlanStateStatus.Missing)
            {
                return null;
            }
            if (read.Status != SessionPowerPlanStateStatus.Valid || read.Value == null)
            {
                return Result(action, SessionPowerPlanStatus.MarkerRejected, false,
                    StateDetail(read.Status, read.Detail), null, null);
            }
            marker = read.Value;
            exists = true;
            return null;
        }

        private bool TryGetActiveScheme(
            SessionPowerPlanAction action,
            out Guid activeGuid,
            out SessionPowerPlanOperationResult failure)
        {
            activeGuid = Guid.Empty;
            failure = null;
            SessionPowerPlanCommandResult command = runner.Run(
                SessionPowerPlanCommand.GetActiveScheme,
                null,
                commandTimeout);
            if (command == null ||
                command.Status != SessionPowerPlanCommandStatus.Succeeded)
            {
                failure = Result(
                    action,
                    command != null &&
                        command.Status == SessionPowerPlanCommandStatus.TimedOut
                            ? SessionPowerPlanStatus.CommandTimedOut
                            : SessionPowerPlanStatus.ActiveSchemeQueryFailed,
                    false,
                    CommandDetail("Active power-plan query failed", command),
                    null,
                    null);
                return false;
            }
            if (!SessionPowerPlanOutputParser.TryParseActiveScheme(
                    command.StandardOutput,
                    out activeGuid))
            {
                failure = Result(action,
                    SessionPowerPlanStatus.ActiveSchemeQueryFailed,
                    false,
                    "powercfg returned no single valid active-scheme GUID.",
                    null,
                    null);
                return false;
            }
            return true;
        }

        private void CleanupFailedActivation(
            SessionPowerPlanConfiguration configuration,
            SessionPowerPlanMarker marker)
        {
            try
            {
                Guid active;
                SessionPowerPlanOperationResult ignored;
                if (!TryGetActiveScheme(
                        SessionPowerPlanAction.Start,
                        out active,
                        out ignored))
                {
                    return;
                }
                if (active == marker.BoostPlanGuid)
                {
                    SessionPowerPlanCommandResult restore = runner.Run(
                        SessionPowerPlanCommand.SetActiveScheme,
                        marker.PreviousPlanGuid,
                        commandTimeout);
                    if (restore == null ||
                        restore.Status != SessionPowerPlanCommandStatus.Succeeded)
                    {
                        return;
                    }
                    Guid verified;
                    if (!TryGetActiveScheme(
                            SessionPowerPlanAction.Start,
                            out verified,
                            out ignored) ||
                        verified != marker.PreviousPlanGuid)
                    {
                        return;
                    }
                }
                string detail;
                stateStore.DeleteMarker(out detail);
            }
            catch
            {
                // The marker intentionally remains for next-launch recovery.
            }
        }

        private SessionPowerPlanOperationResult DeleteMarker(
            SessionPowerPlanAction action,
            SessionPowerPlanConfiguration configuration,
            SessionPowerPlanMarker marker)
        {
            string detail;
            SessionPowerPlanStateStatus status = stateStore.DeleteMarker(out detail);
            if (status == SessionPowerPlanStateStatus.Valid ||
                status == SessionPowerPlanStateStatus.Missing)
            {
                return null;
            }
            return Result(action, SessionPowerPlanStatus.MarkerDeleteFailed, false,
                StateDetail(status, detail), configuration.PlanGuid,
                marker.PreviousPlanGuid);
        }

        private bool MarkerBelongsToCurrentSession(
            SessionPowerPlanMarker marker,
            Guid sessionId)
        {
            return marker.SessionId == sessionId &&
                marker.OwnerProcessId == platform.CurrentProcessId &&
                marker.OwnerProcessStartTimeUtc ==
                    EnsureUtc(platform.CurrentProcessStartTimeUtc);
        }

        private static bool MarkerMatchesConfiguration(
            SessionPowerPlanMarker marker,
            SessionPowerPlanConfiguration configuration)
        {
            return marker != null && configuration != null &&
                marker.BoostPlanGuid == configuration.PlanGuid &&
                marker.SessionId != Guid.Empty &&
                marker.OwnerProcessId > 0 &&
                marker.OwnerProcessStartTimeUtc.Kind == DateTimeKind.Utc &&
                marker.PreviousPlanGuid != Guid.Empty &&
                marker.PreviousPlanGuid != marker.BoostPlanGuid &&
                marker.CreatedUtc.Kind == DateTimeKind.Utc;
        }

        private static DateTime EnsureUtc(DateTime value)
        {
            if (value == DateTime.MinValue || value == DateTime.MaxValue)
            {
                throw new ArgumentOutOfRangeException("value");
            }
            return value.Kind == DateTimeKind.Utc
                ? value
                : value.ToUniversalTime();
        }

        private static string StateDetail(
            SessionPowerPlanStateStatus status,
            string detail)
        {
            return status.ToString() +
                (string.IsNullOrWhiteSpace(detail) ? string.Empty : ": " + detail);
        }

        private static string CommandDetail(
            string prefix,
            SessionPowerPlanCommandResult command)
        {
            if (command == null)
            {
                return prefix + ": no command result.";
            }
            string error = (command.StandardError ?? string.Empty).Trim();
            if (error.Length > 512)
            {
                error = error.Substring(0, 512);
            }
            return prefix + ": " + command.Status +
                (string.IsNullOrWhiteSpace(error) ? string.Empty : " - " + error);
        }

        private static SessionPowerPlanOperationResult UnexpectedFailure(
            SessionPowerPlanAction action,
            Exception exception)
        {
            return Result(action, SessionPowerPlanStatus.DependencyFailure, false,
                exception == null
                    ? "An unexpected dependency failure occurred."
                    : exception.GetType().Name + ": " + exception.Message,
                null,
                null);
        }

        private static SessionPowerPlanOperationResult Result(
            SessionPowerPlanAction action,
            SessionPowerPlanStatus status,
            bool changed,
            string detail,
            Guid? boostPlanGuid,
            Guid? previousPlanGuid)
        {
            return SessionPowerPlanOperationResult.Create(
                action,
                status,
                changed,
                detail,
                boostPlanGuid,
                previousPlanGuid);
        }
    }

    internal static class SessionPowerPlanOutputParser
    {
        public static bool TryParseActiveScheme(string output, out Guid schemeGuid)
        {
            schemeGuid = Guid.Empty;
            if (string.IsNullOrWhiteSpace(output) || output.Length > 32768)
            {
                return false;
            }

            Guid found = Guid.Empty;
            int matches = 0;
            for (int index = 0; index <= output.Length - 36; index++)
            {
                Guid candidate;
                if (Guid.TryParseExact(output.Substring(index, 36), "D", out candidate) &&
                    candidate != Guid.Empty)
                {
                    if (found != Guid.Empty && candidate != found)
                    {
                        return false;
                    }
                    found = candidate;
                    matches++;
                    index += 35;
                }
            }
            if (matches != 1 || found == Guid.Empty)
            {
                return false;
            }
            schemeGuid = found;
            return true;
        }
    }

    /// <summary>
    /// Windows-only runner whose command surface is limited to querying and
    /// selecting an existing power scheme. It cannot mutate scheme settings.
    /// </summary>
    internal sealed class WindowsPowerCfgRunner : ISessionPowerPlanCommandRunner
    {
        private static readonly TimeSpan MinimumTimeout =
            TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan MaximumTimeout = TimeSpan.FromSeconds(10);

        public SessionPowerPlanCommandResult Run(
            SessionPowerPlanCommand command,
            Guid? schemeGuid,
            TimeSpan timeout)
        {
            if (timeout < MinimumTimeout || timeout > MaximumTimeout)
            {
                return SessionPowerPlanCommandResult.Create(
                    SessionPowerPlanCommandStatus.InvalidRequest,
                    -1,
                    string.Empty,
                    "The timeout is outside the safe 100 ms to 10 second range.");
            }
            bool getActive = command == SessionPowerPlanCommand.GetActiveScheme;
            bool setActive = command == SessionPowerPlanCommand.SetActiveScheme;
            if ((!getActive && !setActive) ||
                (getActive && schemeGuid.HasValue) ||
                (setActive && (!schemeGuid.HasValue || schemeGuid.Value == Guid.Empty)))
            {
                return SessionPowerPlanCommandResult.Create(
                    SessionPowerPlanCommandStatus.InvalidRequest,
                    -1,
                    string.Empty,
                    "The powercfg command request is invalid.");
            }
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                return SessionPowerPlanCommandResult.Create(
                    SessionPowerPlanCommandStatus.UnsupportedPlatform,
                    -1,
                    string.Empty,
                    "powercfg is available only on Windows.");
            }

            string systemDirectory = Path.GetFullPath(Environment.SystemDirectory);
            string executable = Path.GetFullPath(Path.Combine(
                systemDirectory,
                "powercfg.exe"));
            if (!IsDirectChild(systemDirectory, executable) ||
                !File.Exists(executable) ||
                (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
            {
                return SessionPowerPlanCommandResult.Create(
                    SessionPowerPlanCommandStatus.Failed,
                    -1,
                    string.Empty,
                    "The trusted system powercfg executable is unavailable.");
            }

            string arguments = getActive
                ? "/getactivescheme"
                : "/setactive " + schemeGuid.Value.ToString("D");
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                Encoding encoding = Encoding.GetEncoding(
                    CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
                startInfo.StandardOutputEncoding = encoding;
                startInfo.StandardErrorEncoding = encoding;
            }
            catch
            {
                // The framework default is a safe fallback for GUID parsing.
            }

            using (var process = new Process())
            {
                process.StartInfo = startInfo;
                try
                {
                    if (!process.Start())
                    {
                        return SessionPowerPlanCommandResult.Create(
                            SessionPowerPlanCommandStatus.Failed,
                            -1,
                            string.Empty,
                            "powercfg did not start.");
                    }
                    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> errorTask = process.StandardError.ReadToEndAsync();
                    int timeoutMilliseconds = (int)Math.Ceiling(timeout.TotalMilliseconds);
                    if (!process.WaitForExit(timeoutMilliseconds))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }
                        try
                        {
                            process.WaitForExit(1000);
                        }
                        catch
                        {
                        }
                        return SessionPowerPlanCommandResult.Create(
                            SessionPowerPlanCommandStatus.TimedOut,
                            -1,
                            CompletedText(outputTask),
                            CompletedText(errorTask));
                    }

                    Task.WaitAll(new Task[] { outputTask, errorTask }, 1000);
                    string standardOutput = CompletedText(outputTask);
                    string standardError = CompletedText(errorTask);
                    return SessionPowerPlanCommandResult.Create(
                        process.ExitCode == 0
                            ? SessionPowerPlanCommandStatus.Succeeded
                            : SessionPowerPlanCommandStatus.Failed,
                        process.ExitCode,
                        standardOutput,
                        standardError);
                }
                catch (Exception exception)
                {
                    return SessionPowerPlanCommandResult.Create(
                        SessionPowerPlanCommandStatus.Failed,
                        -1,
                        string.Empty,
                        exception.GetType().Name + ": " + exception.Message);
                }
            }
        }

        private static string CompletedText(Task<string> task)
        {
            return task != null && task.Status == TaskStatus.RanToCompletion
                ? task.Result
                : string.Empty;
        }

        private static bool IsDirectChild(string parent, string child)
        {
            string parentFull = Path.GetFullPath(parent).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string childFull = Path.GetFullPath(child);
            if (!childFull.StartsWith(parentFull, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string relative = childFull.Substring(parentFull.Length);
            return relative.Length > 0 &&
                relative.IndexOf(Path.DirectorySeparatorChar) < 0 &&
                relative.IndexOf(Path.AltDirectorySeparatorChar) < 0;
        }
    }

    internal sealed class WindowsSessionPowerPlanPlatform : ISessionPowerPlanPlatform
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerStatus
        {
            public byte AcLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

        public bool IsWindows
        {
            get { return Environment.OSVersion.Platform == PlatformID.Win32NT; }
        }

        public SessionPowerSource GetPowerSource()
        {
            if (!IsWindows)
            {
                return SessionPowerSource.Unknown;
            }
            SystemPowerStatus status;
            if (!GetSystemPowerStatus(out status))
            {
                return SessionPowerSource.Unknown;
            }
            if (status.AcLineStatus == 1)
            {
                return SessionPowerSource.AlternatingCurrent;
            }
            return status.AcLineStatus == 0
                ? SessionPowerSource.Battery
                : SessionPowerSource.Unknown;
        }

        public int CurrentProcessId
        {
            get
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    return process.Id;
                }
            }
        }

        public DateTime CurrentProcessStartTimeUtc
        {
            get
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    return process.StartTime.ToUniversalTime();
                }
            }
        }

        public DateTime UtcNow
        {
            get { return DateTime.UtcNow; }
        }

        public bool IsProcessInstanceAlive(
            int processId,
            DateTime processStartTimeUtc)
        {
            if (processId <= 0 || processStartTimeUtc.Kind != DateTimeKind.Utc)
            {
                return false;
            }
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    return !process.HasExited &&
                        process.StartTime.ToUniversalTime() == processStartTimeUtc;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Access denied cannot safely be interpreted as a dead owner.
                return true;
            }
        }
    }

    /// <summary>
    /// Strict JSON codec for the two small, flat control files. Unknown or
    /// duplicate fields and non-canonical GUID/date values are rejected.
    /// </summary>
    internal static class SessionPowerPlanStateJson
    {
        private const int CurrentVersion = 1;

        public static string SerializeConfiguration(
            SessionPowerPlanConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException("configuration");
            }
            return "{\"version\":1,\"planName\":" +
                Quote(configuration.PlanName) +
                ",\"planGuid\":" + Quote(configuration.PlanGuid.ToString("D")) + "}";
        }

        public static string SerializeMarker(SessionPowerPlanMarker marker)
        {
            if (marker == null)
            {
                throw new ArgumentNullException("marker");
            }
            return "{\"version\":1" +
                ",\"sessionId\":" + Quote(marker.SessionId.ToString("D")) +
                ",\"ownerProcessId\":" + marker.OwnerProcessId.ToString(
                    CultureInfo.InvariantCulture) +
                ",\"ownerProcessStartUtc\":" + Quote(
                    marker.OwnerProcessStartTimeUtc.ToString(
                        "o", CultureInfo.InvariantCulture)) +
                ",\"boostPlanGuid\":" + Quote(marker.BoostPlanGuid.ToString("D")) +
                ",\"previousPlanGuid\":" + Quote(
                    marker.PreviousPlanGuid.ToString("D")) +
                ",\"createdUtc\":" + Quote(
                    marker.CreatedUtc.ToString("o", CultureInfo.InvariantCulture)) + "}";
        }

        public static bool TryDeserializeConfiguration(
            string json,
            out SessionPowerPlanConfiguration configuration,
            out string error)
        {
            configuration = null;
            error = string.Empty;
            Dictionary<string, string> values;
            if (!StrictFlatJson.TryParse(json, out values, out error) ||
                !HasExactKeys(values, "version", "planName", "planGuid"))
            {
                error = string.IsNullOrWhiteSpace(error)
                    ? "The trusted state has unexpected fields."
                    : error;
                return false;
            }
            int version;
            Guid planGuid;
            if (!int.TryParse(values["version"], NumberStyles.None,
                    CultureInfo.InvariantCulture, out version) ||
                version != CurrentVersion ||
                !Guid.TryParseExact(values["planGuid"], "D", out planGuid) ||
                planGuid == Guid.Empty ||
                !string.Equals(values["planName"],
                    SessionPowerPlanConfiguration.RequiredPlanName,
                    StringComparison.Ordinal))
            {
                error = "The trusted power-plan identity is invalid.";
                return false;
            }
            try
            {
                configuration = new SessionPowerPlanConfiguration(
                    planGuid,
                    values["planName"]);
                return true;
            }
            catch (ArgumentException exception)
            {
                error = exception.Message;
                return false;
            }
        }

        public static bool TryDeserializeMarker(
            string json,
            out SessionPowerPlanMarker marker,
            out string error)
        {
            marker = null;
            error = string.Empty;
            Dictionary<string, string> values;
            if (!StrictFlatJson.TryParse(json, out values, out error) ||
                !HasExactKeys(
                    values,
                    "version",
                    "sessionId",
                    "ownerProcessId",
                    "ownerProcessStartUtc",
                    "boostPlanGuid",
                    "previousPlanGuid",
                    "createdUtc"))
            {
                error = string.IsNullOrWhiteSpace(error)
                    ? "The marker has unexpected fields."
                    : error;
                return false;
            }

            int version;
            int ownerProcessId;
            Guid sessionId;
            Guid boostPlanGuid;
            Guid previousPlanGuid;
            DateTime ownerStartUtc;
            DateTime createdUtc;
            if (!int.TryParse(values["version"], NumberStyles.None,
                    CultureInfo.InvariantCulture, out version) ||
                version != CurrentVersion ||
                !int.TryParse(values["ownerProcessId"], NumberStyles.None,
                    CultureInfo.InvariantCulture, out ownerProcessId) ||
                ownerProcessId <= 0 ||
                !Guid.TryParseExact(values["sessionId"], "D", out sessionId) ||
                sessionId == Guid.Empty ||
                !Guid.TryParseExact(values["boostPlanGuid"], "D", out boostPlanGuid) ||
                boostPlanGuid == Guid.Empty ||
                !Guid.TryParseExact(
                    values["previousPlanGuid"], "D", out previousPlanGuid) ||
                previousPlanGuid == Guid.Empty ||
                previousPlanGuid == boostPlanGuid ||
                !TryParseUtc(values["ownerProcessStartUtc"], out ownerStartUtc) ||
                !TryParseUtc(values["createdUtc"], out createdUtc))
            {
                error = "The session marker contains invalid identity data.";
                return false;
            }
            try
            {
                marker = new SessionPowerPlanMarker(
                    sessionId,
                    ownerProcessId,
                    ownerStartUtc,
                    boostPlanGuid,
                    previousPlanGuid,
                    createdUtc);
                return true;
            }
            catch (ArgumentException exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static bool TryParseUtc(string value, out DateTime parsed)
        {
            return DateTime.TryParseExact(
                    value,
                    "o",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out parsed) &&
                parsed.Kind == DateTimeKind.Utc &&
                parsed != DateTime.MinValue &&
                parsed != DateTime.MaxValue;
        }

        private static bool HasExactKeys(
            Dictionary<string, string> values,
            params string[] expected)
        {
            if (values == null || values.Count != expected.Length)
            {
                return false;
            }
            foreach (string key in expected)
            {
                if (!values.ContainsKey(key))
                {
                    return false;
                }
            }
            return true;
        }

        private static string Quote(string value)
        {
            if (value == null)
            {
                throw new ArgumentNullException("value");
            }
            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            foreach (char character in value)
            {
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < 0x20)
                        {
                            builder.Append("\\u");
                            builder.Append(((int)character).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(character);
                        }
                        break;
                }
            }
            builder.Append('"');
            return builder.ToString();
        }
    }

    internal static class StrictFlatJson
    {
        public static bool TryParse(
            string json,
            out Dictionary<string, string> values,
            out string error)
        {
            values = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(json) || json.Length > 16384)
            {
                error = "JSON is empty or too large.";
                return false;
            }
            var parser = new Parser(json);
            return parser.TryParse(out values, out error);
        }

        private sealed class Parser
        {
            private readonly string input;
            private int position;

            public Parser(string input)
            {
                this.input = input;
            }

            public bool TryParse(
                out Dictionary<string, string> values,
                out string error)
            {
                values = new Dictionary<string, string>(StringComparer.Ordinal);
                error = string.Empty;
                SkipWhitespace();
                if (!Consume('{'))
                {
                    return Fail(out values, out error, "JSON must start with an object.");
                }
                SkipWhitespace();
                if (Consume('}'))
                {
                    SkipWhitespace();
                    return position == input.Length ||
                        Fail(out values, out error, "Trailing JSON data is forbidden.");
                }

                while (position < input.Length)
                {
                    string key;
                    if (!TryReadString(out key, out error) ||
                        string.IsNullOrEmpty(key) || values.ContainsKey(key))
                    {
                        if (string.IsNullOrEmpty(error))
                        {
                            error = "JSON contains an empty or duplicate key.";
                        }
                        values = null;
                        return false;
                    }
                    SkipWhitespace();
                    if (!Consume(':'))
                    {
                        return Fail(out values, out error, "JSON key lacks a value.");
                    }
                    SkipWhitespace();
                    string value;
                    if (Peek() == '"')
                    {
                        if (!TryReadString(out value, out error))
                        {
                            values = null;
                            return false;
                        }
                    }
                    else if (!TryReadInteger(out value))
                    {
                        return Fail(out values, out error,
                            "Only strings and integer values are accepted.");
                    }
                    values.Add(key, value);
                    SkipWhitespace();
                    if (Consume('}'))
                    {
                        SkipWhitespace();
                        if (position != input.Length)
                        {
                            return Fail(out values, out error,
                                "Trailing JSON data is forbidden.");
                        }
                        return true;
                    }
                    if (!Consume(','))
                    {
                        return Fail(out values, out error,
                            "JSON object fields must be comma-separated.");
                    }
                    SkipWhitespace();
                }
                return Fail(out values, out error, "JSON object is incomplete.");
            }

            private bool TryReadString(out string value, out string error)
            {
                value = null;
                error = string.Empty;
                if (!Consume('"'))
                {
                    error = "A JSON string was expected.";
                    return false;
                }
                var builder = new StringBuilder();
                while (position < input.Length)
                {
                    char character = input[position++];
                    if (character == '"')
                    {
                        value = builder.ToString();
                        return true;
                    }
                    if (character < 0x20)
                    {
                        error = "Unescaped control characters are forbidden.";
                        return false;
                    }
                    if (character != '\\')
                    {
                        builder.Append(character);
                        continue;
                    }
                    if (position >= input.Length)
                    {
                        error = "JSON escape sequence is incomplete.";
                        return false;
                    }
                    char escape = input[position++];
                    switch (escape)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (position + 4 > input.Length)
                            {
                                error = "JSON unicode escape is incomplete.";
                                return false;
                            }
                            int codePoint;
                            if (!int.TryParse(
                                    input.Substring(position, 4),
                                    NumberStyles.AllowHexSpecifier,
                                    CultureInfo.InvariantCulture,
                                    out codePoint))
                            {
                                error = "JSON unicode escape is invalid.";
                                return false;
                            }
                            builder.Append((char)codePoint);
                            position += 4;
                            break;
                        default:
                            error = "JSON escape sequence is invalid.";
                            return false;
                    }
                }
                error = "JSON string is incomplete.";
                return false;
            }

            private bool TryReadInteger(out string value)
            {
                int start = position;
                if (Peek() == '-')
                {
                    position++;
                }
                int digits = 0;
                while (char.IsDigit(Peek()))
                {
                    position++;
                    digits++;
                }
                if (digits == 0)
                {
                    position = start;
                    value = null;
                    return false;
                }
                value = input.Substring(start, position - start);
                return true;
            }

            private char Peek()
            {
                return position < input.Length ? input[position] : '\0';
            }

            private bool Consume(char expected)
            {
                if (Peek() != expected)
                {
                    return false;
                }
                position++;
                return true;
            }

            private void SkipWhitespace()
            {
                while (position < input.Length &&
                    (input[position] == ' ' || input[position] == '\t' ||
                     input[position] == '\r' || input[position] == '\n'))
                {
                    position++;
                }
            }

            private static bool Fail(
                out Dictionary<string, string> values,
                out string error,
                string message)
            {
                values = null;
                error = message;
                return false;
            }
        }
    }

    /// <summary>
    /// Reads a privileged trusted-plan file and a per-user crash marker. Every
    /// existing path is checked for redirects, owner, and writable ACL entries.
    /// </summary>
    internal sealed class SecureSessionPowerPlanStateStore :
        ISessionPowerPlanStateStore
    {
        private const int MaximumStateBytes = 16384;
        private const string TrustedFileName = "trusted-plan.json";
        private const string MarkerFileName = "active-session.json";
        private const string TrustedInstallerSid =
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

        private readonly string trustedStatePath;
        private readonly string markerPath;
        private readonly SecurityIdentifier currentUserSid;

        public SecureSessionPowerPlanStateStore(
            string trustedStatePath,
            string markerPath)
        {
            this.trustedStatePath = NormalizeControlPath(
                trustedStatePath,
                TrustedFileName);
            this.markerPath = NormalizeControlPath(markerPath, MarkerFileName);
            if (string.Equals(this.trustedStatePath, this.markerPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Trusted state and marker paths must differ.");
            }
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                currentUserSid = identity.User;
            }
            if (currentUserSid == null)
            {
                throw new SecurityException("The current Windows SID is unavailable.");
            }
        }

        public static SecureSessionPowerPlanStateStore CreateDefault()
        {
            string common = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);
            string local = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(common) || string.IsNullOrWhiteSpace(local))
            {
                throw new DirectoryNotFoundException(
                    "ProgramData or LocalApplicationData is unavailable.");
            }
            string markerDirectory = Path.Combine(
                local,
                "Boostix",
                "SessionPowerPlan");
            EnsureLocalMarkerDirectory(local, markerDirectory);
            return new SecureSessionPowerPlanStateStore(
                Path.Combine(common, "Boostix", "SessionPowerPlan", TrustedFileName),
                Path.Combine(markerDirectory, MarkerFileName));
        }

        private static void EnsureLocalMarkerDirectory(
            string localApplicationData,
            string markerDirectory)
        {
            string root = Path.GetFullPath(localApplicationData).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string target = Path.GetFullPath(markerDirectory).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (IsUncPath(root) || IsUncPath(target) ||
                !target.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityException(
                    "The session marker directory escaped LocalApplicationData.");
            }

            string current = root;
            string relative = target.Substring(root.Length + 1);
            foreach (string segment in relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (Directory.Exists(current) &&
                    (new DirectoryInfo(current).Attributes &
                        FileAttributes.ReparsePoint) != 0)
                {
                    throw new SecurityException(
                        "The session marker path contains a reparse point.");
                }
                current = Path.Combine(current, segment);
                if (File.Exists(current))
                {
                    throw new SecurityException(
                        "A file blocks the session marker directory.");
                }
                Directory.CreateDirectory(current);
                if ((new DirectoryInfo(current).Attributes &
                    FileAttributes.ReparsePoint) != 0)
                {
                    throw new SecurityException(
                        "The session marker path contains a reparse point.");
                }
            }
        }

        public SessionPowerPlanStateRead<SessionPowerPlanConfiguration>
            ReadTrustedConfiguration()
        {
            string payload;
            string detail;
            SessionPowerPlanStateStatus status = ReadControlFile(
                trustedStatePath,
                true,
                out payload,
                out detail);
            if (status != SessionPowerPlanStateStatus.Valid)
            {
                return SessionPowerPlanStateRead<SessionPowerPlanConfiguration>.Create(
                    status, null, detail);
            }
            SessionPowerPlanConfiguration configuration;
            string parseError;
            if (!SessionPowerPlanStateJson.TryDeserializeConfiguration(
                    payload,
                    out configuration,
                    out parseError))
            {
                return SessionPowerPlanStateRead<SessionPowerPlanConfiguration>.Create(
                    SessionPowerPlanStateStatus.Corrupt, null, parseError);
            }
            return SessionPowerPlanStateRead<SessionPowerPlanConfiguration>.Create(
                SessionPowerPlanStateStatus.Valid, configuration, string.Empty);
        }

        public SessionPowerPlanStateRead<SessionPowerPlanMarker> ReadMarker()
        {
            string payload;
            string detail;
            SessionPowerPlanStateStatus status = ReadControlFile(
                markerPath,
                false,
                out payload,
                out detail);
            if (status != SessionPowerPlanStateStatus.Valid)
            {
                return SessionPowerPlanStateRead<SessionPowerPlanMarker>.Create(
                    status, null, detail);
            }
            SessionPowerPlanMarker marker;
            string parseError;
            if (!SessionPowerPlanStateJson.TryDeserializeMarker(
                    payload,
                    out marker,
                    out parseError))
            {
                return SessionPowerPlanStateRead<SessionPowerPlanMarker>.Create(
                    SessionPowerPlanStateStatus.Corrupt, null, parseError);
            }
            return SessionPowerPlanStateRead<SessionPowerPlanMarker>.Create(
                SessionPowerPlanStateStatus.Valid, marker, string.Empty);
        }

        public SessionPowerPlanStateStatus WriteMarker(
            SessionPowerPlanMarker marker,
            out string detail)
        {
            detail = string.Empty;
            if (marker == null)
            {
                detail = "The marker is null.";
                return SessionPowerPlanStateStatus.Corrupt;
            }
            string temporaryPath = null;
            try
            {
                string directory = Path.GetDirectoryName(markerPath);
                ValidateDirectory(directory, false);
                if (File.Exists(markerPath))
                {
                    detail = "A session marker already exists.";
                    return SessionPowerPlanStateStatus.IoFailure;
                }
                string payload = SessionPowerPlanStateJson.SerializeMarker(marker);
                byte[] bytes = new UTF8Encoding(false, true).GetBytes(payload);
                if (bytes.Length <= 0 || bytes.Length > MaximumStateBytes)
                {
                    detail = "The marker payload size is invalid.";
                    return SessionPowerPlanStateStatus.Corrupt;
                }

                temporaryPath = Path.Combine(
                    directory,
                    ".active-session." + Guid.NewGuid().ToString("N") + ".tmp");
                if (!IsDirectChild(directory, temporaryPath))
                {
                    throw new StateTrustException(
                        SessionPowerPlanStateStatus.UntrustedPath,
                        "The temporary marker escaped its directory.");
                }
                using (var output = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                {
                    output.Write(bytes, 0, bytes.Length);
                    output.Flush(true);
                }
                ValidateFile(temporaryPath, false);
                File.Move(temporaryPath, markerPath);
                temporaryPath = null;
                ValidateFile(markerPath, false);
                return SessionPowerPlanStateStatus.Valid;
            }
            catch (Exception exception)
            {
                return ClassifyException(exception, out detail);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(temporaryPath))
                {
                    try
                    {
                        if (File.Exists(temporaryPath) &&
                            (File.GetAttributes(temporaryPath) &
                                FileAttributes.ReparsePoint) == 0)
                        {
                            File.Delete(temporaryPath);
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        public SessionPowerPlanStateStatus DeleteMarker(out string detail)
        {
            detail = string.Empty;
            try
            {
                if (!File.Exists(markerPath))
                {
                    return SessionPowerPlanStateStatus.Missing;
                }
                ValidateFile(markerPath, false);
                File.Delete(markerPath);
                return SessionPowerPlanStateStatus.Valid;
            }
            catch (Exception exception)
            {
                return ClassifyException(exception, out detail);
            }
        }

        private SessionPowerPlanStateStatus ReadControlFile(
            string path,
            bool privilegedOnly,
            out string payload,
            out string detail)
        {
            payload = null;
            detail = string.Empty;
            try
            {
                if (!File.Exists(path))
                {
                    return SessionPowerPlanStateStatus.Missing;
                }
                ValidateFile(path, privilegedOnly);
                byte[] bytes;
                using (var input = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.SequentialScan))
                {
                    if (input.Length <= 0 || input.Length > MaximumStateBytes)
                    {
                        return SessionPowerPlanStateStatus.Corrupt;
                    }
                    bytes = new byte[(int)input.Length];
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int read = input.Read(bytes, offset, bytes.Length - offset);
                        if (read <= 0)
                        {
                            throw new EndOfStreamException();
                        }
                        offset += read;
                    }
                }
                payload = new UTF8Encoding(false, true).GetString(bytes);
                return SessionPowerPlanStateStatus.Valid;
            }
            catch (Exception exception)
            {
                return ClassifyException(exception, out detail);
            }
        }

        private void ValidateFile(string path, bool privilegedOnly)
        {
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            if (!IsDirectChild(directory, fullPath) ||
                (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new StateTrustException(
                    SessionPowerPlanStateStatus.UntrustedPath,
                    "A state file is redirected or escaped its directory.");
            }
            ValidateDirectory(directory, privilegedOnly);
            FileSecurity security = File.GetAccessControl(
                fullPath,
                AccessControlSections.Access | AccessControlSections.Owner);
            ValidateSecurity(security, privilegedOnly, "state file");
        }

        private void ValidateDirectory(string path, bool privilegedOnly)
        {
            string fullPath = Path.GetFullPath(path);
            if (!Path.IsPathRooted(fullPath) || IsUncPath(fullPath) ||
                !Directory.Exists(fullPath))
            {
                throw new StateTrustException(
                    SessionPowerPlanStateStatus.UntrustedPath,
                    "The state directory is missing, relative, or remote.");
            }
            DirectoryInfo current = new DirectoryInfo(fullPath);
            while (current != null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new StateTrustException(
                        SessionPowerPlanStateStatus.UntrustedPath,
                        "The state path contains a reparse point.");
                }
                current = current.Parent;
            }
            DirectorySecurity security = Directory.GetAccessControl(
                fullPath,
                AccessControlSections.Access | AccessControlSections.Owner);
            ValidateSecurity(security, privilegedOnly, "state directory");
        }

        private void ValidateSecurity(
            FileSystemSecurity security,
            bool privilegedOnly,
            string description)
        {
            SecurityIdentifier owner = security.GetOwner(
                typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (!IsAllowedIdentity(owner, privilegedOnly))
            {
                throw new StateTrustException(
                    SessionPowerPlanStateStatus.UntrustedOwner,
                    "The " + description + " has an unexpected owner.");
            }

            const FileSystemRights writeRights =
                FileSystemRights.WriteData |
                FileSystemRights.CreateFiles |
                FileSystemRights.AppendData |
                FileSystemRights.CreateDirectories |
                FileSystemRights.WriteAttributes |
                FileSystemRights.WriteExtendedAttributes |
                FileSystemRights.Delete |
                FileSystemRights.DeleteSubdirectoriesAndFiles |
                FileSystemRights.ChangePermissions |
                FileSystemRights.TakeOwnership;
            AuthorizationRuleCollection rules = security.GetAccessRules(
                true,
                true,
                typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
            {
                SecurityIdentifier identity =
                    rule.IdentityReference as SecurityIdentifier;
                if (rule.AccessControlType == AccessControlType.Allow &&
                    (rule.FileSystemRights & writeRights) != 0 &&
                    !IsAllowedIdentity(identity, privilegedOnly))
                {
                    throw new StateTrustException(
                        SessionPowerPlanStateStatus.UntrustedOwner,
                        "An untrusted identity can modify the " + description + ".");
                }
            }
        }

        private bool IsAllowedIdentity(
            SecurityIdentifier identity,
            bool privilegedOnly)
        {
            if (identity == null)
            {
                return false;
            }
            if (identity.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
                identity.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
                string.Equals(identity.Value, TrustedInstallerSid,
                    StringComparison.Ordinal))
            {
                return true;
            }
            return !privilegedOnly && identity.Equals(currentUserSid);
        }

        private static string NormalizeControlPath(
            string path,
            string expectedFileName)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            {
                throw new ArgumentException("A rooted control-file path is required.", "path");
            }
            string fullPath = Path.GetFullPath(path);
            if (IsUncPath(fullPath) ||
                fullPath.IndexOf(':', 3) >= 0 ||
                !string.Equals(Path.GetFileName(fullPath), expectedFileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityException("The control-file path is not trusted.");
            }
            return fullPath;
        }

        private static bool IsDirectChild(string parent, string child)
        {
            string parentFull = Path.GetFullPath(parent).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string childFull = Path.GetFullPath(child);
            if (!childFull.StartsWith(parentFull, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string relative = childFull.Substring(parentFull.Length);
            return relative.Length > 0 &&
                relative.IndexOf(Path.DirectorySeparatorChar) < 0 &&
                relative.IndexOf(Path.AltDirectorySeparatorChar) < 0;
        }

        private static bool IsUncPath(string path)
        {
            return path.StartsWith("\\\\", StringComparison.Ordinal) ||
                path.StartsWith("//", StringComparison.Ordinal);
        }

        private static SessionPowerPlanStateStatus ClassifyException(
            Exception exception,
            out string detail)
        {
            detail = exception.GetType().Name + ": " + exception.Message;
            StateTrustException trust = exception as StateTrustException;
            if (trust != null)
            {
                return trust.Status;
            }
            if (exception is UnauthorizedAccessException ||
                exception is SecurityException)
            {
                return SessionPowerPlanStateStatus.AccessDenied;
            }
            if (exception is DecoderFallbackException ||
                exception is InvalidDataException ||
                exception is EndOfStreamException ||
                exception is FormatException)
            {
                return SessionPowerPlanStateStatus.Corrupt;
            }
            return SessionPowerPlanStateStatus.IoFailure;
        }

        private sealed class StateTrustException : Exception
        {
            public StateTrustException(
                SessionPowerPlanStateStatus status,
                string message)
                : base(message)
            {
                Status = status;
            }

            public SessionPowerPlanStateStatus Status { get; private set; }
        }
    }
}
