using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Boostix
{
    /// <summary>
    /// A point-in-time description of a process. The start time and executable
    /// path are deliberately captured together so a recycled PID cannot be
    /// mistaken for the game selected by the user.
    /// </summary>
    internal sealed class GameProcessSnapshot
    {
        internal GameProcessSnapshot(
            int processId,
            DateTime startTimeUtc,
            string executablePath,
            string processName,
            string windowTitle,
            bool hasVisibleMainWindow,
            int sessionId)
        {
            ProcessId = processId;
            StartTimeUtc = startTimeUtc.Kind == DateTimeKind.Utc
                ? startTimeUtc
                : startTimeUtc.ToUniversalTime();
            ExecutablePath = executablePath ?? string.Empty;
            ProcessName = processName ?? string.Empty;
            WindowTitle = windowTitle ?? string.Empty;
            HasVisibleMainWindow = hasVisibleMainWindow;
            SessionId = sessionId;
        }

        internal int ProcessId { get; private set; }
        internal DateTime StartTimeUtc { get; private set; }
        internal string ExecutablePath { get; private set; }
        internal string ProcessName { get; private set; }
        internal string WindowTitle { get; private set; }
        internal bool HasVisibleMainWindow { get; private set; }
        internal int SessionId { get; private set; }
    }

    /// <summary>
    /// Abstracts process enumeration so target identity rules can be tested
    /// without launching or terminating unrelated applications.
    /// </summary>
    internal interface IGameProcessCatalog
    {
        IList<GameProcessSnapshot> CaptureAll();

        bool TryCapture(
            int processId,
            out GameProcessSnapshot snapshot,
            out string error);
    }

    internal sealed class SystemGameProcessCatalog : IGameProcessCatalog
    {
        public IList<GameProcessSnapshot> CaptureAll()
        {
            List<GameProcessSnapshot> snapshots =
                new List<GameProcessSnapshot>();

            Process[] processes;
            try
            {
                processes = Process.GetProcesses();
            }
            catch
            {
                return snapshots;
            }

            foreach (Process process in processes)
            {
                using (process)
                {
                    GameProcessSnapshot snapshot;
                    string ignored;
                    if (TryCaptureProcess(process, out snapshot, out ignored))
                    {
                        snapshots.Add(snapshot);
                    }
                }
            }

            return snapshots;
        }

        public bool TryCapture(
            int processId,
            out GameProcessSnapshot snapshot,
            out string error)
        {
            snapshot = null;
            error = string.Empty;

            if (processId <= 0)
            {
                error = "The process identifier is invalid.";
                return false;
            }

            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    return TryCaptureProcess(process, out snapshot, out error);
                }
            }
            catch (ArgumentException)
            {
                error = "The selected process is no longer running.";
                return false;
            }
            catch (InvalidOperationException)
            {
                error = "The selected process is no longer available.";
                return false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                error = "Windows did not allow the selected process to be inspected.";
                return false;
            }
            catch
            {
                error = "The selected process could not be inspected.";
                return false;
            }
        }

        private static bool TryCaptureProcess(
            Process process,
            out GameProcessSnapshot snapshot,
            out string error)
        {
            snapshot = null;
            error = string.Empty;

            try
            {
                if (process.HasExited)
                {
                    error = "The selected process has already exited.";
                    return false;
                }

                DateTime startBefore = process.StartTime.ToUniversalTime();
                int processId = process.Id;
                int sessionId = process.SessionId;
                string processName = process.ProcessName ?? string.Empty;
                string executablePath = process.MainModule == null
                    ? string.Empty
                    : process.MainModule.FileName;
                IntPtr mainWindow = process.MainWindowHandle;
                string windowTitle = process.MainWindowTitle ?? string.Empty;

                process.Refresh();
                if (process.HasExited ||
                    process.StartTime.ToUniversalTime() != startBefore)
                {
                    error = "The selected process changed while it was inspected.";
                    return false;
                }

                snapshot = new GameProcessSnapshot(
                    processId,
                    startBefore,
                    executablePath,
                    processName,
                    windowTitle,
                    mainWindow != IntPtr.Zero,
                    sessionId);
                return true;
            }
            catch (InvalidOperationException)
            {
                error = "The selected process is no longer available.";
                return false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                error = "Windows did not allow the selected process to be inspected.";
                return false;
            }
            catch
            {
                error = "The selected process could not be inspected.";
                return false;
            }
        }
    }

    /// <summary>
    /// Immutable live target identity. PID alone is never sufficient: every
    /// resolution checks PID, UTC start time and normalized executable path.
    /// </summary>
    internal sealed class GameTargetIdentity
    {
        private readonly int processId;
        private readonly DateTime processStartTimeUtc;
        private readonly string executablePath;
        private readonly string normalizedExecutablePath;
        private readonly string processName;

        internal GameTargetIdentity(
            int processId,
            DateTime processStartTimeUtc,
            string executablePath,
            string normalizedExecutablePath,
            string processName)
        {
            this.processId = processId;
            this.processStartTimeUtc = processStartTimeUtc.Kind == DateTimeKind.Utc
                ? processStartTimeUtc
                : processStartTimeUtc.ToUniversalTime();
            this.executablePath = executablePath;
            this.normalizedExecutablePath = normalizedExecutablePath;
            this.processName = processName ?? string.Empty;
        }

        internal int ProcessId { get { return processId; } }
        internal DateTime ProcessStartTimeUtc { get { return processStartTimeUtc; } }
        internal string ExecutablePath { get { return executablePath; } }
        internal string NormalizedExecutablePath
        {
            get { return normalizedExecutablePath; }
        }
        internal string ProcessName { get { return processName; } }
    }

    internal sealed class GameTargetCandidate
    {
        internal GameTargetCandidate(
            GameTargetIdentity identity,
            string displayName,
            string windowTitle)
        {
            Identity = identity;
            DisplayName = displayName ?? string.Empty;
            WindowTitle = windowTitle ?? string.Empty;
        }

        internal GameTargetIdentity Identity { get; private set; }
        internal string DisplayName { get; private set; }
        internal string WindowTitle { get; private set; }
    }

    internal static class GameExecutablePath
    {
        internal static bool TryNormalize(
            string path,
            out string displayPath,
            out string comparisonPath,
            out string error)
        {
            displayPath = string.Empty;
            comparisonPath = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "The executable path is empty.";
                return false;
            }

            string candidate = path.Trim();
            if (candidate.Length >= 2 &&
                candidate[0] == '"' &&
                candidate[candidate.Length - 1] == '"')
            {
                candidate = candidate.Substring(1, candidate.Length - 2);
            }

            candidate = RemoveExtendedPathPrefix(candidate);

            if (!Path.IsPathRooted(candidate))
            {
                error = "The target must use an absolute executable path.";
                return false;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(candidate);
            }
            catch
            {
                error = "The executable path is invalid.";
                return false;
            }

            if (!Path.IsPathRooted(fullPath) ||
                !string.Equals(
                    Path.GetExtension(fullPath),
                    ".exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "The target must be a full path to an executable file.";
                return false;
            }

            displayPath = fullPath;
            comparisonPath = fullPath;
            return true;
        }

        internal static bool AreEquivalent(string left, string right)
        {
            string leftDisplay;
            string leftComparison;
            string leftError;
            string rightDisplay;
            string rightComparison;
            string rightError;

            return TryNormalize(
                    left,
                    out leftDisplay,
                    out leftComparison,
                    out leftError) &&
                TryNormalize(
                    right,
                    out rightDisplay,
                    out rightComparison,
                    out rightError) &&
                string.Equals(
                    leftComparison,
                    rightComparison,
                    StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsWithinDirectory(
            string executablePath,
            string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                return false;
            }

            string normalizedExecutable;
            string comparisonExecutable;
            string executableError;
            if (!TryNormalize(
                    executablePath,
                    out normalizedExecutable,
                    out comparisonExecutable,
                    out executableError))
            {
                return false;
            }

            string normalizedDirectory;
            try
            {
                normalizedDirectory = Path.GetFullPath(directoryPath.Trim())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
            }
            catch
            {
                return false;
            }

            return comparisonExecutable.StartsWith(
                normalizedDirectory,
                StringComparison.OrdinalIgnoreCase);
        }

        private static string RemoveExtendedPathPrefix(string path)
        {
            const string ExtendedUncPrefix = @"\\?\UNC\";
            const string ExtendedPrefix = @"\\?\";

            if (path.StartsWith(
                    ExtendedUncPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                return @"\\" + path.Substring(ExtendedUncPrefix.Length);
            }

            if (path.StartsWith(
                    ExtendedPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                return path.Substring(ExtendedPrefix.Length);
            }

            return path;
        }
    }

    /// <summary>
    /// Performs explicit target selection and safe rebinding. It never guesses
    /// a game from working-set size or another resource-usage heuristic.
    /// </summary>
    internal sealed class GameTargetService
    {
        private static readonly HashSet<string> SystemProcessNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "system",
                "idle",
                "registry",
                "smss",
                "csrss",
                "wininit",
                "services",
                "lsass",
                "svchost",
                "fontdrvhost",
                "dwm",
                "winlogon",
                "taskhostw",
                "explorer",
                "sihost",
                "shellexperiencehost",
                "startmenuexperiencehost",
                "searchhost",
                "applicationframehost"
            };

        private readonly IGameProcessCatalog catalog;
        private readonly string boostixExecutablePath;
        private readonly int ownProcessId;
        private readonly int currentSessionId;

        internal GameTargetService(string boostixExecutablePath)
            : this(
                new SystemGameProcessCatalog(),
                boostixExecutablePath,
                GetCurrentProcessId(),
                GetCurrentSessionId())
        {
        }

        internal GameTargetService(
            IGameProcessCatalog catalog,
            string boostixExecutablePath,
            int ownProcessId,
            int currentSessionId)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException("catalog");
            }

            this.catalog = catalog;
            this.ownProcessId = ownProcessId;
            this.currentSessionId = currentSessionId;

            string displayPath;
            string comparisonPath;
            string ignored;
            this.boostixExecutablePath = GameExecutablePath.TryNormalize(
                boostixExecutablePath,
                out displayPath,
                out comparisonPath,
                out ignored)
                ? displayPath
                : string.Empty;
        }

        /// <summary>
        /// Returns deterministic visible candidates for explicit user choice.
        /// No candidate is automatically promoted based on RAM, CPU or recency.
        /// </summary>
        internal ReadOnlyCollection<GameTargetCandidate> EnumerateCandidates()
        {
            List<GameTargetCandidate> candidates =
                new List<GameTargetCandidate>();
            IList<GameProcessSnapshot> snapshots = catalog.CaptureAll();

            if (snapshots != null)
            {
                foreach (GameProcessSnapshot snapshot in snapshots)
                {
                    GameTargetIdentity identity;
                    string ignored;
                    if (!TryCreateIdentity(
                            snapshot,
                            true,
                            out identity,
                            out ignored))
                    {
                        continue;
                    }

                    string displayName = string.IsNullOrWhiteSpace(snapshot.ProcessName)
                        ? Path.GetFileNameWithoutExtension(identity.ExecutablePath)
                        : snapshot.ProcessName;
                    candidates.Add(new GameTargetCandidate(
                        identity,
                        displayName,
                        snapshot.WindowTitle));
                }
            }

            candidates.Sort(delegate(
                GameTargetCandidate left,
                GameTargetCandidate right)
            {
                int byName = StringComparer.CurrentCultureIgnoreCase.Compare(
                    left.DisplayName,
                    right.DisplayName);
                if (byName != 0)
                {
                    return byName;
                }

                int byPath = StringComparer.OrdinalIgnoreCase.Compare(
                    left.Identity.ExecutablePath,
                    right.Identity.ExecutablePath);
                if (byPath != 0)
                {
                    return byPath;
                }

                return left.Identity.ProcessId.CompareTo(
                    right.Identity.ProcessId);
            });

            return new ReadOnlyCollection<GameTargetCandidate>(candidates);
        }

        internal bool TrySelect(
            int processId,
            out GameTargetIdentity identity,
            out string error)
        {
            identity = null;
            error = string.Empty;

            GameProcessSnapshot snapshot;
            if (!catalog.TryCapture(processId, out snapshot, out error))
            {
                return false;
            }

            return TryCreateIdentity(
                snapshot,
                true,
                out identity,
                out error);
        }

        /// <summary>
        /// Resolves a prior selection only while PID, start time and path still
        /// describe the exact same live process.
        /// </summary>
        internal bool TryResolve(
            GameTargetIdentity identity,
            out GameProcessSnapshot snapshot,
            out string error)
        {
            snapshot = null;
            error = string.Empty;

            if (identity == null)
            {
                error = "No game target is selected.";
                return false;
            }

            GameProcessSnapshot current;
            if (!catalog.TryCapture(identity.ProcessId, out current, out error))
            {
                return false;
            }

            if (current.StartTimeUtc != identity.ProcessStartTimeUtc)
            {
                error = "The selected process identifier was reused by another process.";
                return false;
            }

            if (!GameExecutablePath.AreEquivalent(
                    current.ExecutablePath,
                    identity.ExecutablePath))
            {
                error = "The executable behind the selected process changed.";
                return false;
            }

            GameTargetIdentity refreshed;
            // A selected game may temporarily recreate or hide its top-level
            // window while changing display mode. Live identity resolution is
            // therefore based on PID + start time + executable path; visibility
            // is required only for discovery and the initial explicit choice.
            if (!TryCreateIdentity(
                    current,
                    false,
                    out refreshed,
                    out error))
            {
                return false;
            }

            snapshot = current;
            return true;
        }

        /// <summary>
        /// Auto Boost is intentionally store-backed. A process can match only
        /// a profile the user explicitly saved with AutoBoost enabled.
        /// </summary>
        internal bool TryMatchSavedAutoBoostProfile(
            int processId,
            GameProfileStore profileStore,
            out GameTargetIdentity identity,
            out GameProfile profile,
            out string error)
        {
            identity = null;
            profile = null;
            error = string.Empty;

            if (profileStore == null)
            {
                error = "The game profile store is unavailable.";
                return false;
            }

            GameProcessSnapshot snapshot;
            if (!catalog.TryCapture(processId, out snapshot, out error))
            {
                return false;
            }

            GameTargetIdentity selected;
            if (!TryCreateIdentity(
                    snapshot,
                    true,
                    out selected,
                    out error))
            {
                return false;
            }

            GameProfile savedProfile;
            if (!profileStore.TryGetAutoBoostProfile(
                    selected.ExecutablePath,
                    out savedProfile))
            {
                error = "Auto Boost is not enabled in a saved profile for this game.";
                return false;
            }

            identity = selected;
            profile = savedProfile;
            return true;
        }

        private bool TryCreateIdentity(
            GameProcessSnapshot snapshot,
            bool requireVisibleMainWindow,
            out GameTargetIdentity identity,
            out string error)
        {
            identity = null;
            error = string.Empty;

            if (snapshot == null || snapshot.ProcessId <= 4)
            {
                error = "Windows system processes cannot be selected as a game.";
                return false;
            }

            if (snapshot.ProcessId == ownProcessId ||
                string.Equals(
                    snapshot.ProcessName,
                    "Boostix",
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "Boostix cannot select itself as the game target.";
                return false;
            }

            if (snapshot.SessionId != currentSessionId)
            {
                error = "Only applications in the current desktop session can be selected.";
                return false;
            }

            if (requireVisibleMainWindow && !snapshot.HasVisibleMainWindow)
            {
                error = "The selected process does not have a visible main window.";
                return false;
            }

            if (SystemProcessNames.Contains(snapshot.ProcessName ?? string.Empty))
            {
                error = "Windows shell and system processes cannot be selected as a game.";
                return false;
            }

            string displayPath;
            string comparisonPath;
            if (!GameExecutablePath.TryNormalize(
                    snapshot.ExecutablePath,
                    out displayPath,
                    out comparisonPath,
                    out error))
            {
                return false;
            }

            if (!File.Exists(displayPath))
            {
                error = "The selected executable no longer exists.";
                return false;
            }

            if (!string.IsNullOrEmpty(boostixExecutablePath) &&
                GameExecutablePath.AreEquivalent(
                    displayPath,
                    boostixExecutablePath))
            {
                error = "Boostix cannot select itself as the game target.";
                return false;
            }

            string windowsDirectory = Environment.GetFolderPath(
                Environment.SpecialFolder.Windows);
            if (GameExecutablePath.IsWithinDirectory(
                    displayPath,
                    windowsDirectory))
            {
                error = "Executables inside the Windows directory cannot be selected.";
                return false;
            }

            if (snapshot.StartTimeUtc == DateTime.MinValue ||
                snapshot.StartTimeUtc > DateTime.UtcNow.AddMinutes(1))
            {
                error = "The selected process start time is invalid.";
                return false;
            }

            identity = new GameTargetIdentity(
                snapshot.ProcessId,
                snapshot.StartTimeUtc,
                displayPath,
                comparisonPath,
                snapshot.ProcessName);
            return true;
        }

        private static int GetCurrentProcessId()
        {
            using (Process process = Process.GetCurrentProcess())
            {
                return process.Id;
            }
        }

        private static int GetCurrentSessionId()
        {
            using (Process process = Process.GetCurrentProcess())
            {
                return process.SessionId;
            }
        }
    }

    /// <summary>
    /// A user-created game profile. Profiles store a path, not a PID; live
    /// process identity is always recaptured and validated before a session.
    /// </summary>
    internal sealed class GameProfile
    {
        private readonly string executablePath;
        private readonly string normalizedExecutablePath;
        private readonly string displayName;
        private readonly bool autoBoost;
        private readonly DateTime savedUtc;

        private GameProfile(
            string executablePath,
            string normalizedExecutablePath,
            string displayName,
            bool autoBoost,
            DateTime savedUtc)
        {
            this.executablePath = executablePath;
            this.normalizedExecutablePath = normalizedExecutablePath;
            this.displayName = displayName;
            this.autoBoost = autoBoost;
            this.savedUtc = savedUtc.Kind == DateTimeKind.Utc
                ? savedUtc
                : savedUtc.ToUniversalTime();
        }

        internal string ExecutablePath { get { return executablePath; } }
        internal string NormalizedExecutablePath
        {
            get { return normalizedExecutablePath; }
        }
        internal string DisplayName { get { return displayName; } }
        internal bool AutoBoost { get { return autoBoost; } }
        internal DateTime SavedUtc { get { return savedUtc; } }

        internal static GameProfile CreateFromTarget(
            GameTargetIdentity target,
            string displayName,
            bool autoBoost)
        {
            if (target == null)
            {
                throw new ArgumentNullException("target");
            }

            string safeDisplayName = NormalizeDisplayName(
                displayName,
                target.ProcessName);
            return new GameProfile(
                target.ExecutablePath,
                target.NormalizedExecutablePath,
                safeDisplayName,
                autoBoost,
                DateTime.UtcNow);
        }

        internal static bool TryCreateStored(
            string executablePath,
            string displayName,
            bool autoBoost,
            DateTime savedUtc,
            out GameProfile profile)
        {
            profile = null;

            string normalizedDisplayPath;
            string normalizedComparisonPath;
            string ignored;
            if (!GameExecutablePath.TryNormalize(
                    executablePath,
                    out normalizedDisplayPath,
                    out normalizedComparisonPath,
                    out ignored) ||
                savedUtc == DateTime.MinValue ||
                savedUtc > DateTime.UtcNow.AddMinutes(5))
            {
                return false;
            }

            profile = new GameProfile(
                normalizedDisplayPath,
                normalizedComparisonPath,
                NormalizeDisplayName(
                    displayName,
                    Path.GetFileNameWithoutExtension(normalizedDisplayPath)),
                autoBoost,
                savedUtc);
            return true;
        }

        internal GameProfile WithAutoBoost(bool enabled)
        {
            return new GameProfile(
                executablePath,
                normalizedExecutablePath,
                displayName,
                enabled,
                DateTime.UtcNow);
        }

        private static string NormalizeDisplayName(
            string displayName,
            string fallback)
        {
            string result = string.IsNullOrWhiteSpace(displayName)
                ? (fallback ?? string.Empty).Trim()
                : displayName.Trim();
            if (result.Length == 0)
            {
                result = "Game";
            }
            if (result.Length > 160)
            {
                result = result.Substring(0, 160);
            }
            return result;
        }
    }

    internal sealed class GameProfileLoadResult
    {
        internal GameProfileLoadResult(
            IList<GameProfile> profiles,
            bool corrupt,
            string quarantinePath,
            string error)
        {
            Profiles = new ReadOnlyCollection<GameProfile>(
                new List<GameProfile>(profiles ?? new List<GameProfile>()));
            Corrupt = corrupt;
            QuarantinePath = quarantinePath ?? string.Empty;
            Error = error ?? string.Empty;
        }

        internal ReadOnlyCollection<GameProfile> Profiles { get; private set; }
        internal bool Corrupt { get; private set; }
        internal string QuarantinePath { get; private set; }
        internal string Error { get; private set; }
    }

    /// <summary>
    /// Versioned, bounded game-profile persistence with same-directory atomic
    /// replacement. Corrupt data is never partially trusted: it is quarantined
    /// when possible and the caller receives an empty profile list.
    /// </summary>
    internal sealed class GameProfileStore
    {
        private const string Header = "BOOSTIX-GAME-PROFILES\t1";
        private const int MaximumProfiles = 256;
        private const int MaximumFileBytes = 1024 * 1024;
        private const int MaximumEncodedFieldLength = 65536;

        private readonly string filePath;
        private readonly string trustedDirectory;

        internal GameProfileStore(string filePath)
            : this(filePath, GetProductionDirectory(), false)
        {
        }

        /// <summary>
        /// Test seam for isolated stores. The explicit root must itself be a
        /// strict descendant of the process TEMP directory; production callers
        /// always use the one-argument constructor and LocalAppData\Boostix.
        /// </summary>
        internal GameProfileStore(
            string filePath,
            string explicitlyAllowedTestRoot)
            : this(filePath, explicitlyAllowedTestRoot, true)
        {
        }

        private GameProfileStore(
            string filePath,
            string trustedDirectory,
            bool isExplicitTestRoot)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException(
                    "A game profile file path is required.",
                    "filePath");
            }

            if (string.IsNullOrWhiteSpace(trustedDirectory))
            {
                throw new ArgumentException(
                    "A trusted game profile directory is required.",
                    "trustedDirectory");
            }

            this.filePath = Path.GetFullPath(filePath.Trim());
            this.trustedDirectory = NormalizeDirectoryPath(trustedDirectory);
            if (string.IsNullOrWhiteSpace(Path.GetFileName(this.filePath)))
            {
                throw new ArgumentException(
                    "The game profile path must include a file name.",
                    "filePath");
            }

            if (isExplicitTestRoot)
            {
                string tempDirectory = NormalizeDirectoryPath(Path.GetTempPath());
                if (!IsStrictDescendantDirectory(
                        this.trustedDirectory,
                        tempDirectory))
                {
                    throw new ArgumentException(
                        "An explicit game profile test root must be inside TEMP.",
                        "trustedDirectory");
                }
            }

            ValidateDirectChildPath(this.filePath, this.trustedDirectory);
        }

        internal string FilePath { get { return filePath; } }

        internal GameProfileLoadResult Load()
        {
            try
            {
                EnsureTrustedFilePath(filePath);
                if (!File.Exists(filePath))
                {
                    return new GameProfileLoadResult(
                        new List<GameProfile>(),
                        false,
                        string.Empty,
                        string.Empty);
                }

                // Repeat immediately before metadata/read operations. This
                // prevents a pre-existing junction or file symlink from
                // redirecting profile I/O outside the trusted directory.
                EnsureTrustedFilePath(filePath);
                FileInfo info = new FileInfo(filePath);
                if (info.Length <= 0 || info.Length > MaximumFileBytes)
                {
                    throw new InvalidDataException(
                        "The profile file size is outside the allowed range.");
                }

                EnsureTrustedFilePath(filePath);
                string[] lines = File.ReadAllLines(
                    filePath,
                    new UTF8Encoding(false, true));
                if (lines.Length == 0 ||
                    !string.Equals(lines[0], Header, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The profile file header is not recognized.");
                }

                Dictionary<string, GameProfile> byPath =
                    new Dictionary<string, GameProfile>(
                        StringComparer.OrdinalIgnoreCase);

                for (int index = 1; index < lines.Length; index++)
                {
                    string line = lines[index];
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    string[] fields = line.Split('\t');
                    if (fields.Length != 5 ||
                        !string.Equals(fields[0], "P", StringComparison.Ordinal) ||
                        fields[1].Length > MaximumEncodedFieldLength ||
                        fields[2].Length > MaximumEncodedFieldLength)
                    {
                        throw new InvalidDataException(
                            "A game profile record is malformed.");
                    }

                    string executablePath = DecodeField(fields[1]);
                    string displayName = DecodeField(fields[2]);
                    bool autoBoost;
                    if (fields[3] == "1")
                    {
                        autoBoost = true;
                    }
                    else if (fields[3] == "0")
                    {
                        autoBoost = false;
                    }
                    else
                    {
                        throw new InvalidDataException(
                            "A game profile Auto Boost value is invalid.");
                    }

                    long savedTicks;
                    if (!long.TryParse(
                            fields[4],
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out savedTicks))
                    {
                        throw new InvalidDataException(
                            "A game profile timestamp is invalid.");
                    }

                    DateTime savedUtc;
                    try
                    {
                        savedUtc = new DateTime(savedTicks, DateTimeKind.Utc);
                    }
                    catch
                    {
                        throw new InvalidDataException(
                            "A game profile timestamp is outside the allowed range.");
                    }

                    GameProfile profile;
                    if (!GameProfile.TryCreateStored(
                            executablePath,
                            displayName,
                            autoBoost,
                            savedUtc,
                            out profile))
                    {
                        throw new InvalidDataException(
                            "A stored game profile is invalid.");
                    }

                    if (byPath.ContainsKey(profile.NormalizedExecutablePath))
                    {
                        throw new InvalidDataException(
                            "The profile file contains duplicate executable paths.");
                    }

                    byPath.Add(profile.NormalizedExecutablePath, profile);
                    if (byPath.Count > MaximumProfiles)
                    {
                        throw new InvalidDataException(
                            "The profile file contains too many records.");
                    }
                }

                List<GameProfile> profiles = byPath.Values.ToList();
                SortProfiles(profiles);
                return new GameProfileLoadResult(
                    profiles,
                    false,
                    string.Empty,
                    string.Empty);
            }
            catch (InvalidDataException exception)
            {
                string quarantinePath = TryQuarantineCorruptFile();
                return new GameProfileLoadResult(
                    new List<GameProfile>(),
                    true,
                    quarantinePath,
                    "Saved game profiles were invalid and were ignored: " +
                    exception.Message);
            }
            catch (DecoderFallbackException)
            {
                string quarantinePath = TryQuarantineCorruptFile();
                return new GameProfileLoadResult(
                    new List<GameProfile>(),
                    true,
                    quarantinePath,
                    "Saved game profiles contained invalid UTF-8 data and were ignored.");
            }
            catch
            {
                return new GameProfileLoadResult(
                    new List<GameProfile>(),
                    true,
                    string.Empty,
                    "Saved game profiles could not be read safely and were ignored.");
            }
        }

        internal GameProfile Upsert(
            GameTargetIdentity target,
            string displayName,
            bool autoBoost)
        {
            GameProfile profile = GameProfile.CreateFromTarget(
                target,
                displayName,
                autoBoost);
            GameProfileLoadResult loaded = Load();
            ThrowIfUnreadableStoreWouldBeOverwritten(loaded);
            List<GameProfile> profiles = loaded.Profiles.ToList();
            profiles.RemoveAll(delegate(GameProfile existing)
            {
                return string.Equals(
                    existing.NormalizedExecutablePath,
                    profile.NormalizedExecutablePath,
                    StringComparison.OrdinalIgnoreCase);
            });
            profiles.Add(profile);
            Save(profiles);
            return profile;
        }

        internal bool Remove(string executablePath)
        {
            GameProfileLoadResult loaded = Load();
            ThrowIfUnreadableStoreWouldBeOverwritten(loaded);
            List<GameProfile> profiles = loaded.Profiles.ToList();
            int removed = profiles.RemoveAll(delegate(GameProfile profile)
            {
                return GameExecutablePath.AreEquivalent(
                    profile.ExecutablePath,
                    executablePath);
            });

            if (removed <= 0)
            {
                return false;
            }

            Save(profiles);
            return true;
        }

        /// <summary>
        /// Changes Auto Boost only for an already saved profile. The executable
        /// does not need to be running or currently installed, which lets the
        /// Profiles UI manage offline entries without weakening explicit opt-in.
        /// </summary>
        internal bool SetAutoBoost(string executablePath, bool enabled)
        {
            string displayPath;
            string comparisonPath;
            string ignored;
            if (!GameExecutablePath.TryNormalize(
                    executablePath,
                    out displayPath,
                    out comparisonPath,
                    out ignored))
            {
                return false;
            }

            GameProfileLoadResult loaded = Load();
            ThrowIfUnreadableStoreWouldBeOverwritten(loaded);
            if (loaded.Corrupt)
            {
                // A quarantined/corrupt store contains no trustworthy profile
                // whose consent may be changed implicitly.
                return false;
            }

            List<GameProfile> profiles = loaded.Profiles.ToList();
            int profileIndex = profiles.FindIndex(delegate(GameProfile profile)
            {
                return string.Equals(
                    profile.NormalizedExecutablePath,
                    comparisonPath,
                    StringComparison.OrdinalIgnoreCase);
            });
            if (profileIndex < 0)
            {
                return false;
            }

            if (profiles[profileIndex].AutoBoost == enabled)
            {
                return true;
            }

            profiles[profileIndex] = profiles[profileIndex].WithAutoBoost(enabled);
            Save(profiles);
            return true;
        }

        internal bool TryGetAutoBoostProfile(
            string executablePath,
            out GameProfile profile)
        {
            profile = null;
            GameProfileLoadResult loaded = Load();
            if (loaded.Corrupt)
            {
                return false;
            }

            foreach (GameProfile candidate in loaded.Profiles)
            {
                if (candidate.AutoBoost &&
                    GameExecutablePath.AreEquivalent(
                        candidate.ExecutablePath,
                        executablePath))
                {
                    profile = candidate;
                    return true;
                }
            }

            return false;
        }

        internal void Save(IEnumerable<GameProfile> profiles)
        {
            if (profiles == null)
            {
                throw new ArgumentNullException("profiles");
            }

            Dictionary<string, GameProfile> byPath =
                new Dictionary<string, GameProfile>(
                    StringComparer.OrdinalIgnoreCase);
            foreach (GameProfile profile in profiles)
            {
                if (profile == null)
                {
                    throw new InvalidDataException(
                        "A null game profile cannot be saved.");
                }
                byPath[profile.NormalizedExecutablePath] = profile;
                if (byPath.Count > MaximumProfiles)
                {
                    throw new InvalidDataException(
                        "Too many game profiles were supplied.");
                }
            }

            List<GameProfile> ordered = byPath.Values.ToList();
            SortProfiles(ordered);

            StringBuilder text = new StringBuilder();
            text.AppendLine(Header);
            foreach (GameProfile profile in ordered)
            {
                text.Append("P\t");
                text.Append(EncodeField(profile.ExecutablePath));
                text.Append('\t');
                text.Append(EncodeField(profile.DisplayName));
                text.Append('\t');
                text.Append(profile.AutoBoost ? "1" : "0");
                text.Append('\t');
                text.Append(profile.SavedUtc.Ticks.ToString(
                    CultureInfo.InvariantCulture));
                text.AppendLine();
            }

            AtomicWriteUtf8(text.ToString());
        }

        private static void SortProfiles(List<GameProfile> profiles)
        {
            profiles.Sort(delegate(GameProfile left, GameProfile right)
            {
                int byName = StringComparer.CurrentCultureIgnoreCase.Compare(
                    left.DisplayName,
                    right.DisplayName);
                if (byName != 0)
                {
                    return byName;
                }
                return StringComparer.OrdinalIgnoreCase.Compare(
                    left.ExecutablePath,
                    right.ExecutablePath);
            });
        }

        private void AtomicWriteUtf8(string content)
        {
            string directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException(
                    "The game profile directory could not be determined.");
            }

            EnsureTrustedDirectoryForOperation(true);
            string temporaryPath = Path.Combine(
                directory,
                "." + Path.GetFileName(filePath) + "." +
                Guid.NewGuid().ToString("N") + ".tmp");
            string backupPath = Path.Combine(
                directory,
                "." + Path.GetFileName(filePath) + "." +
                Guid.NewGuid().ToString("N") + ".bak");

            ValidateDirectChildPath(temporaryPath, trustedDirectory);
            ValidateDirectChildPath(backupPath, trustedDirectory);

            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(content);
                if (bytes.Length > MaximumFileBytes)
                {
                    throw new InvalidDataException(
                        "The profile file would exceed its size limit.");
                }

                EnsureTrustedFilePath(temporaryPath);
                using (FileStream stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                EnsureTrustedFilePath(filePath);
                if (File.Exists(filePath))
                {
                    // The directory, source, destination and backup are all
                    // revalidated immediately before the atomic replacement.
                    EnsureTrustedFilePath(temporaryPath);
                    EnsureTrustedFilePath(filePath);
                    EnsureTrustedFilePath(backupPath);
                    File.Replace(
                        temporaryPath,
                        filePath,
                        backupPath,
                        true);
                    TryDelete(backupPath);
                }
                else
                {
                    EnsureTrustedFilePath(temporaryPath);
                    EnsureTrustedFilePath(filePath);
                    File.Move(temporaryPath, filePath);
                }
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }

        private string TryQuarantineCorruptFile()
        {
            try
            {
                EnsureTrustedFilePath(filePath);
                if (!File.Exists(filePath))
                {
                    return string.Empty;
                }

                string quarantinePath = filePath + ".corrupt-" +
                    DateTime.UtcNow.ToString(
                        "yyyyMMddHHmmssfff",
                        CultureInfo.InvariantCulture) +
                    "-" + Guid.NewGuid().ToString("N");
                ValidateDirectChildPath(quarantinePath, trustedDirectory);

                // Quarantine is a mutation too: repeat all chain/file checks
                // directly before Move and never follow a reparse target.
                EnsureTrustedFilePath(filePath);
                EnsureTrustedFilePath(quarantinePath);
                File.Move(filePath, quarantinePath);
                return quarantinePath;
            }
            catch
            {
                return string.Empty;
            }
        }

        private void ThrowIfUnreadableStoreWouldBeOverwritten(
            GameProfileLoadResult loaded)
        {
            if (loaded != null &&
                loaded.Corrupt &&
                string.IsNullOrEmpty(loaded.QuarantinePath))
            {
                throw new IOException(
                    "The existing game profile store could not be read safely.");
            }
        }

        private static string GetProductionDirectory()
        {
            string localApplicationData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localApplicationData))
            {
                throw new InvalidOperationException(
                    "The LocalApplicationData directory is unavailable.");
            }

            return NormalizeDirectoryPath(Path.Combine(
                localApplicationData,
                "Boostix"));
        }

        private void EnsureTrustedDirectoryForOperation(bool createIfMissing)
        {
            EnsureNoReparsePointsInExistingChain(trustedDirectory, true);
            if (createIfMissing && !Directory.Exists(trustedDirectory))
            {
                Directory.CreateDirectory(trustedDirectory);
                // A directory can be redirected between the preflight and the
                // create/open. Never proceed until the resulting chain is
                // checked again.
                EnsureNoReparsePointsInExistingChain(trustedDirectory, true);
            }
        }

        private void EnsureTrustedFilePath(string candidatePath)
        {
            ValidateDirectChildPath(candidatePath, trustedDirectory);
            EnsureTrustedDirectoryForOperation(false);
            EnsureNoReparsePointsInExistingChain(candidatePath, false);
        }

        private static void ValidateDirectChildPath(
            string candidatePath,
            string trustedDirectory)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) ||
                string.IsNullOrWhiteSpace(trustedDirectory) ||
                !Path.IsPathRooted(candidatePath) ||
                !Path.IsPathRooted(trustedDirectory))
            {
                throw new ArgumentException(
                    "A rooted trusted profile path is required.");
            }

            string fullCandidate = Path.GetFullPath(candidatePath.Trim());
            string parent = Path.GetDirectoryName(fullCandidate);
            if (string.IsNullOrWhiteSpace(parent) ||
                !string.Equals(
                    NormalizeDirectoryPath(parent),
                    NormalizeDirectoryPath(trustedDirectory),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException(
                    "The game profile file must be a direct child of its trusted directory.");
            }

            string fileName = Path.GetFileName(fullCandidate);
            if (string.IsNullOrWhiteSpace(fileName) ||
                !string.Equals(fileName, fileName.Trim(), StringComparison.Ordinal) ||
                fileName.EndsWith(".", StringComparison.Ordinal) ||
                fileName.IndexOf(':') >= 0 ||
                fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException(
                    "The game profile file name is not safe.",
                    "candidatePath");
            }
        }

        private static string NormalizeDirectoryPath(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) ||
                !Path.IsPathRooted(directoryPath))
            {
                throw new ArgumentException(
                    "A rooted directory path is required.",
                    "directoryPath");
            }

            string fullPath = Path.GetFullPath(directoryPath.Trim());
            string root = Path.GetPathRoot(fullPath);
            if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            return fullPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }

        private static bool IsStrictDescendantDirectory(
            string candidateDirectory,
            string parentDirectory)
        {
            string candidate = NormalizeDirectoryPath(candidateDirectory);
            string parent = NormalizeDirectoryPath(parentDirectory);
            if (string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string prefix = parent.EndsWith(
                    Path.DirectorySeparatorChar.ToString(),
                    StringComparison.Ordinal)
                ? parent
                : parent + Path.DirectorySeparatorChar;
            return candidate.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureNoReparsePointsInExistingChain(
            string path,
            bool expectDirectoryLeaf)
        {
            string fullPath = Path.GetFullPath(path);
            string root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new IOException("The profile path has no trusted root.");
            }

            List<string> chain = new List<string>();
            chain.Add(root);
            string relative = fullPath.Substring(root.Length);
            string[] segments = relative.Split(
                new char[]
                {
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar
                },
                StringSplitOptions.RemoveEmptyEntries);
            string current = root;
            foreach (string segment in segments)
            {
                current = Path.Combine(current, segment);
                chain.Add(current);
            }

            for (int index = 0; index < chain.Count; index++)
            {
                FileAttributes attributes;
                if (!TryGetAttributes(chain[index], out attributes))
                {
                    // Descendants cannot exist when an ancestor is absent.
                    break;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        "The game profile path contains a reparse point.");
                }

                bool isDirectory =
                    (attributes & FileAttributes.Directory) != 0;
                bool isLeaf = index == chain.Count - 1;
                if ((!isLeaf && !isDirectory) ||
                    (isLeaf && expectDirectoryLeaf && !isDirectory) ||
                    (isLeaf && !expectDirectoryLeaf && isDirectory))
                {
                    throw new IOException(
                        "The game profile path has an unexpected file type.");
                }
            }
        }

        private static bool TryGetAttributes(
            string path,
            out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException)
            {
                attributes = default(FileAttributes);
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                attributes = default(FileAttributes);
                return false;
            }
        }

        private static string EncodeField(string value)
        {
            return Convert.ToBase64String(
                new UTF8Encoding(false).GetBytes(value ?? string.Empty));
        }

        private static string DecodeField(string value)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(value);
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch
            {
                throw new InvalidDataException(
                    "A game profile text field is not valid UTF-8 data.");
            }
        }

        private void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path))
                {
                    ValidateDirectChildPath(path, trustedDirectory);
                    EnsureTrustedFilePath(path);
                    if (File.Exists(path))
                    {
                        EnsureTrustedFilePath(path);
                        File.Delete(path);
                    }
                }
            }
            catch
            {
                // A stale same-directory backup is recoverable and must not
                // turn a successfully committed profile save into data loss.
            }
        }
    }
}
