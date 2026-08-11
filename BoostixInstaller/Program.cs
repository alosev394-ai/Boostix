using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Boostix.Branding;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

#if LEGACY_UPDATE_BRIDGE
// Transport-only bridge for the strict FileVersionInfo check in installed
// 1.8.x updaters. The UI, payload, paths, shortcuts, and installed product are
// still Boostix. Remove this build after the 1.8.x support window closes.
[assembly: AssemblyTitle("Boostix Update Compatibility Bridge")]
[assembly: AssemblyDescription("Compatibility update bridge for Boostix")]
[assembly: AssemblyProduct("Majestic Boost")]
#else
[assembly: AssemblyTitle(ProductBrand.ProductName + " Setup")]
[assembly: AssemblyDescription("Installer for " + ProductBrand.ProductName)]
[assembly: AssemblyProduct(ProductBrand.ProductName)]
#endif
[assembly: AssemblyCompany(ProductBrand.CompanyName)]
[assembly: AssemblyCopyright("© " + ProductBrand.CompanyName)]
[assembly: AssemblyVersion(ProductBrand.AssemblyVersion)]
[assembly: AssemblyFileVersion(ProductBrand.AssemblyVersion)]

namespace BoostixSetup
{
    internal static class Program
    {
        private const string SetupMutexName = @"Global\SilasSuspect.Boostix.Setup";
        private const uint LoadLibrarySearchSystem32 = 0x00000800;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDefaultDllDirectories(
            uint directoryFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDllDirectory(string path);

        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                if (!HardenNativeDllSearch())
                {
                    MessageBox.Show(
                        "Boostix Setup не смог безопасно подготовить запуск. " +
                        "Установите актуальные обновления Windows и повторите попытку.",
                        "Boostix Setup",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    Environment.ExitCode = 3;
                    return;
                }
                InstallerDiagnostics.Initialize(args);
                MainCore(args);
            }
            catch (Exception exception)
            {
                InstallerDiagnostics.Write("Unhandled setup failure.", exception);
                Environment.ExitCode = 1;
                if (!IsSilentInvocation(args))
                {
                    MessageBox.Show(
                        "Boostix Setup не смог завершить операцию.\r\n\r\n" +
                        exception.Message + "\r\n\r\nЖурнал: " +
                        InstallerDiagnostics.LogPath,
                        "Boostix Setup",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
        }

        private static void MainCore(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (InstallerEngine.TryRunUpdateRecoveryWatchdog(args))
            {
                return;
            }
            bool scheduleCleanup = false;

            try
            {
                using (var setupMutex = new System.Threading.Mutex(false, SetupMutexName))
                {
                    bool ownsMutex = false;
                    try
                    {
                        try
                        {
                            ownsMutex = setupMutex.WaitOne(0, false);
                        }
                        catch (System.Threading.AbandonedMutexException)
                        {
                            ownsMutex = true;
                        }

                        if (!ownsMutex)
                        {
                            MessageBox.Show(
                                "Установка или удаление Boostix уже выполняется. Дождитесь завершения открытого установщика.",
                                "Boostix Setup",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                            Environment.ExitCode = 2;
                            return;
                        }

                        scheduleCleanup = true;
                        Run(args);
                    }
                    finally
                    {
                        if (ownsMutex)
                        {
                            setupMutex.ReleaseMutex();
                        }
                    }
                }
            }
            finally
            {
                if (scheduleCleanup)
                {
                    InstallerEngine.ScheduleUpdateSourceCleanupIfNeeded();
                }
            }
        }

        private static bool IsSilentInvocation(string[] args)
        {
            foreach (string argument in args ?? new string[0])
            {
                if (string.Equals(argument, "/quiet", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "/silent", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HardenNativeDllSearch()
        {
            try
            {
                return SetDllDirectory(string.Empty) &&
                    SetDefaultDllDirectories(LoadLibrarySearchSystem32);
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        private static void Run(string[] args)
        {
            bool uninstall = string.Equals(
                Path.GetFullPath(Application.ExecutablePath),
                Path.GetFullPath(InstallerEngine.UninstallerExe),
                StringComparison.OrdinalIgnoreCase);
            bool quiet = false;
            bool silentInstall = false;
            bool launchAfterInstall = false;
            bool updateUi = false;
            bool demoUpdateUi = false;
            foreach (string argument in args)
            {
                if (string.Equals(argument, "/uninstall", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "-uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    uninstall = true;
                }
                else if (string.Equals(argument, "/quiet", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "/silent", StringComparison.OrdinalIgnoreCase))
                {
                    quiet = true;
                    silentInstall = true;
                }
                else if (string.Equals(argument, "/launch", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "-launch", StringComparison.OrdinalIgnoreCase))
                {
                    launchAfterInstall = true;
                }
                else if (string.Equals(argument, "/updateui", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "-updateui", StringComparison.OrdinalIgnoreCase))
                {
                    updateUi = true;
                }
                else if (string.Equals(argument, "/demo-updateui", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "-demo-updateui", StringComparison.OrdinalIgnoreCase))
                {
                    updateUi = true;
                    demoUpdateUi = true;
                }
            }

            if (uninstall)
            {
                try
                {
                    InstallerEngine.Uninstall(quiet);
                }
                catch
                {
                    Environment.ExitCode = 1;
                    if (!quiet)
                    {
                        throw;
                    }
                }
                return;
            }

            if (silentInstall && !updateUi)
            {
                try
                {
                    InstallerEngine.Install(InstallerEngine.GetDesktopShortcutPreference());
                    if (launchAfterInstall)
                    {
                        InstallerEngine.LaunchInstalledApplication();
                    }
                    Environment.ExitCode = 0;
                }
                catch (Exception exception)
                {
                    InstallerDiagnostics.Write("Silent installation failed.", exception);
                    Environment.ExitCode = 1;
                }
                return;
            }

            Application.Run(updateUi ? (Form)new UpdateProgressForm(demoUpdateUi) : new InstallerForm());
        }
    }

    internal static class InstallerDiagnostics
    {
        private const long MaximumLogBytes = 2L * 1024L * 1024L;
        private static readonly object Sync = new object();
        private static readonly string CorrelationId =
            Guid.NewGuid().ToString("N");
        private static string logPath;
        private static bool logInitializationAttempted;

        public static string LogPath
        {
            get
            {
                EnsureLogPath();
                return string.IsNullOrWhiteSpace(logPath)
                    ? "file logging disabled"
                    : logPath;
            }
        }

        public static void Initialize(string[] args)
        {
            EnsureLogPath();
            string sid = "unknown";
            try
            {
                SecurityIdentifier identity = WindowsIdentity.GetCurrent().User;
                if (identity != null)
                {
                    sid = identity.Value;
                }
            }
            catch
            {
            }
            Write(
                "Setup started. Version=" + ProductBrand.ProductVersion +
                "; OS=" + Environment.OSVersion.VersionString +
                "; CLR=" + Environment.Version +
                "; OS64=" + Environment.Is64BitOperatingSystem +
                "; Process64=" + Environment.Is64BitProcess +
                "; SID=" + sid +
                "; Mode=" + DescribeInvocationMode(args) +
                "; ArgCount=" + (args == null ? 0 : args.Length).ToString(
                    CultureInfo.InvariantCulture));
        }

        public static void Write(string message)
        {
            Write(message, null);
        }

        public static void Write(string message, Exception exception)
        {
            try
            {
                EnsureLogPath();
                var builder = new StringBuilder();
                builder.Append(DateTime.UtcNow.ToString(
                    "O",
                    CultureInfo.InvariantCulture));
                builder.Append(" [").Append(CorrelationId).Append("] ");
                builder.Append(SanitizeLogValue(message));
                Exception current = exception;
                int depth = 0;
                while (current != null && depth < 8)
                {
                    builder.Append(" | ")
                        .Append(current.GetType().FullName)
                        .Append(" HRESULT=0x")
                        .Append(current.HResult.ToString("X8", CultureInfo.InvariantCulture))
                        .Append(": ")
                        .Append(SanitizeLogValue(current.Message));
                    current = current.InnerException;
                    depth++;
                }
                lock (Sync)
                {
                    if (!string.IsNullOrWhiteSpace(logPath))
                    {
                        RotateIfNeeded();
                        File.AppendAllText(
                            logPath,
                            builder.AppendLine().ToString(),
                            new UTF8Encoding(false));
                    }
                }
                Trace.WriteLine(builder.ToString());
            }
            catch
            {
                // Logging must never become another reason for setup to fail.
            }
        }

        private static void EnsureLogPath()
        {
            if (logInitializationAttempted)
            {
                return;
            }
            lock (Sync)
            {
                if (logInitializationAttempted)
                {
                    return;
                }
                logInitializationAttempted = true;
                try
                {
                    string commonData = Path.GetFullPath(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.CommonApplicationData));
                    if (string.IsNullOrWhiteSpace(commonData) ||
                        !Directory.Exists(commonData) ||
                        !IsPathFreeOfReparsePoints(commonData))
                    {
                        throw new IOException(
                            "The ProgramData root is unavailable or redirected.");
                    }
                    string productDirectory = Path.GetFullPath(Path.Combine(
                        commonData,
                        ProductBrand.DataDirectoryName));
                    string directory = Path.GetFullPath(Path.Combine(
                        productDirectory,
                        "Logs"));
                    if (!IsDirectChild(commonData, productDirectory) ||
                        !IsDirectChild(productDirectory, directory))
                    {
                        throw new IOException(
                            "The setup log directory is outside ProgramData.");
                    }
                    EnsureProtectedLogDirectory(productDirectory);
                    EnsureProtectedLogDirectory(directory);
                    string candidate = Path.Combine(
                        directory,
                        "setup-" + CorrelationId + ".log");
                    using (var created = new FileStream(
                        candidate,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.Read,
                        4096,
                        FileOptions.WriteThrough))
                    {
                    }
                    if ((File.GetAttributes(candidate) &
                         FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException(
                            "The setup log file is redirected.");
                    }
                    logPath = candidate;
                }
                catch (Exception exception)
                {
                    logPath = null;
                    Trace.WriteLine(
                        "Boostix file logging is disabled: " +
                        SanitizeLogValue(exception.Message));
                }
            }
        }

        private static void EnsureProtectedLogDirectory(string path)
        {
            var administrators = new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                null);
            var localSystem = new SecurityIdentifier(
                WellKnownSidType.LocalSystemSid,
                null);
            var authenticatedUsers = new SecurityIdentifier(
                WellKnownSidType.AuthenticatedUserSid,
                null);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(administrators);
            const InheritanceFlags inheritance =
                InheritanceFlags.ContainerInherit |
                InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(
                administrators,
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                localSystem,
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                authenticatedUsers,
                FileSystemRights.ReadAndExecute,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));

            if (File.Exists(path))
            {
                throw new IOException(
                    "A protected setup log directory is occupied by a file.");
            }
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path, security);
            }
            var directory = new DirectoryInfo(path);
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "A protected setup log directory is redirected.");
            }
            directory.SetAccessControl(security);
            DirectorySecurity actual = directory.GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner);
            SecurityIdentifier owner = actual.GetOwner(
                typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner == null ||
                !owner.Equals(administrators) ||
                !actual.AreAccessRulesProtected)
            {
                throw new System.Security.SecurityException(
                    "The protected setup log directory ACL was not applied.");
            }
        }

        private static bool IsDirectChild(string parent, string child)
        {
            string parentFull = Path.GetFullPath(parent).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            string childFull = Path.GetFullPath(child).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (!childFull.StartsWith(
                    parentFull,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string relative = childFull.Substring(parentFull.Length);
            return relative.Length != 0 &&
                relative.IndexOf(Path.DirectorySeparatorChar) < 0 &&
                relative.IndexOf(Path.AltDirectorySeparatorChar) < 0;
        }

        private static bool IsPathFreeOfReparsePoints(string path)
        {
            DirectoryInfo current = new DirectoryInfo(Path.GetFullPath(path));
            while (current != null)
            {
                if (!current.Exists ||
                    (current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }
                current = current.Parent;
            }
            return true;
        }

        private static string DescribeInvocationMode(string[] args)
        {
            var modes = new List<string>();
            foreach (string argument in args ?? new string[0])
            {
                string value = (argument ?? string.Empty).Trim();
                if (string.Equals(value, "/uninstall", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "-uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    modes.Add("uninstall");
                }
                else if (string.Equals(value, "/quiet", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "/silent", StringComparison.OrdinalIgnoreCase))
                {
                    modes.Add("quiet");
                }
                else if (string.Equals(value, "/launch", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "-launch", StringComparison.OrdinalIgnoreCase))
                {
                    modes.Add("launch");
                }
                else if (string.Equals(value, "/updateui", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "-updateui", StringComparison.OrdinalIgnoreCase))
                {
                    modes.Add("update-ui");
                }
                else if (string.Equals(value, "/demo-updateui", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "-demo-updateui", StringComparison.OrdinalIgnoreCase))
                {
                    modes.Add("demo-update-ui");
                }
                else if (string.Equals(value, "/update-recovery", StringComparison.OrdinalIgnoreCase))
                {
                    modes.Add("update-recovery");
                }
            }
            return modes.Count == 0
                ? "interactive-install"
                : string.Join(",", modes.ToArray());
        }

        private static string SanitizeLogValue(string value)
        {
            string sanitized = (value ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\0', ' ');
            return sanitized.Length <= 1024
                ? sanitized
                : sanitized.Substring(0, 1024);
        }

        private static void RotateIfNeeded()
        {
            if (!File.Exists(logPath) ||
                new FileInfo(logPath).Length < MaximumLogBytes)
            {
                return;
            }
            string previous = logPath + ".previous";
            try
            {
                if (File.Exists(previous))
                {
                    File.Delete(previous);
                }
                File.Move(logPath, previous);
                using (var created = new FileStream(
                    logPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    4096,
                    FileOptions.WriteThrough))
                {
                }
            }
            catch
            {
                // If rotation is temporarily blocked, retain the current log.
            }
        }
    }

    internal static class InstallerEngine
    {
        public const string ProductName = ProductBrand.ProductName;
        public const string ProductVersion = ProductBrand.ProductVersion;

        private static RegistryView MachineRegistryView
        {
            get
            {
                return Environment.Is64BitOperatingSystem
                    ? RegistryView.Registry64
                    : RegistryView.Registry32;
            }
        }

        private static string GetMachineProgramFilesDirectory()
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine,
                    MachineRegistryView))
                using (RegistryKey currentVersion = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion",
                    false))
                {
                    string configured = currentVersion == null
                        ? null
                        : currentVersion.GetValue(
                            "ProgramFilesDir",
                            null,
                            RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    if (!string.IsNullOrWhiteSpace(configured) &&
                        Path.IsPathRooted(configured))
                    {
                        return Path.GetFullPath(configured);
                    }
                }
            }
            catch (Exception exception)
            {
                InstallerDiagnostics.Write(
                    "Could not read the machine Program Files directory; using the Windows fallback.",
                    exception);
            }
            return Path.GetFullPath(Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles));
        }

        private static bool TryResolveOptionalShortcutRoot(
            Environment.SpecialFolder folder,
            string purpose,
            out string directory)
        {
            string candidate = null;
            try
            {
                candidate = Environment.GetFolderPath(folder);
            }
            catch (Exception exception)
            {
                InstallerDiagnostics.Write(
                    "Optional " + purpose +
                    " shortcut root could not be resolved.",
                    exception);
                directory = null;
                return false;
            }
            return TryValidateOptionalShortcutRoot(
                candidate,
                purpose,
                out directory);
        }

        private static bool TryValidateOptionalShortcutRoot(
            string candidate,
            string purpose,
            out string directory)
        {
            directory = null;
            try
            {
                if (string.IsNullOrWhiteSpace(candidate) ||
                    !Path.IsPathRooted(candidate))
                {
                    throw new DirectoryNotFoundException(
                        "Windows did not provide an absolute shortcut directory.");
                }
                string fullPath = Path.GetFullPath(candidate);
                if (!Directory.Exists(fullPath) ||
                    !IsPathFreeOfReparsePoints(fullPath))
                {
                    throw new IOException(
                        "The optional shortcut directory is unavailable or redirected.");
                }
                directory = fullPath;
                return true;
            }
            catch (Exception exception)
            {
                InstallerDiagnostics.Write(
                    "Optional " + (purpose ?? "shell") +
                    " shortcuts will be skipped.",
                    exception);
                directory = null;
                return false;
            }
        }

        public static readonly string InstallDirectory = Path.Combine(
            GetMachineProgramFilesDirectory(),
            ProductBrand.InstallDirectoryName);
        public static readonly string InstalledExe = Path.Combine(InstallDirectory, "Boostix.exe");
        public static readonly string InstalledBoostSessionScript = Path.Combine(InstallDirectory, "Boost-Session.ps1");
        public static readonly string InstalledMaxFpsApplyScript = Path.Combine(InstallDirectory, "MaxFPS-Apply.ps1");
        public static readonly string InstalledMaxFpsRestoreScript = Path.Combine(InstallDirectory, "MaxFPS-Restore.ps1");
        public static readonly string PresentMonDirectory = Path.Combine(InstallDirectory, "Tools", "PresentMon");
        public static readonly string InstalledPresentMon = Path.Combine(PresentMonDirectory, "PresentMon.exe");
        public static readonly string InstalledPresentMonLicense = Path.Combine(PresentMonDirectory, "LICENSE.txt");
        public static readonly string InstalledPresentMonThirdParty = Path.Combine(PresentMonDirectory, "THIRD_PARTY.txt");
        public static readonly string UninstallerExe = Path.Combine(InstallDirectory, "Uninstall.exe");

        private const string UninstallRegistryPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Boostix";
        private const string AppPathsRegistryPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Boostix.exe";
        private const string LegacyUninstallRegistryPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MajesticBoost";
        private const string LegacyAppPathsRegistryPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\MajesticBoost.exe";
        private static readonly string LegacyInstallDirectory = Path.Combine(
            GetMachineProgramFilesDirectory(),
            ProductBrand.LegacyInstallDirectoryName);
        private static readonly string LegacyInstalledExe = Path.Combine(
            LegacyInstallDirectory,
            "MajesticBoost.exe");
        private const AccessControlSections CaptureSecuritySections =
            AccessControlSections.Access |
            AccessControlSections.Owner |
            AccessControlSections.Group;
        private const int OptimizationStatePointerMaximumBytes = 4096;
        private const int OptimizationStateMaximumBytes = 2 * 1024 * 1024;
        private const string UpdateRollbackDirectoryName = "UpdateRollback";
        private const string UpdateStateFileName = "state.dat";
        private const string UpdateFileManifestName = "files.manifest";
#if LEGACY_UPDATE_BRIDGE
        private const string CanonicalSetupResourceName =
            "Boostix.CanonicalSetup.exe";
#endif
        private const string UpdateRegistrationSnapshotName = "registration.snapshot";
        private const string UpdateHealthRequestName = "health.request";
        private const string UpdateReadySignalName = "ready.signal";
        private const string UpdateRecoveryExecutableName = "recovery.exe";
        private const int UpdateControlFileMaximumBytes = 4096;
        private const int UpdateSnapshotMaximumFiles = 512;
        private const long UpdateSnapshotMaximumBytes = 536870912L;
        private const int UpdateHealthTimeoutMilliseconds = 120000;
        private const int UpdateHealthPollMilliseconds = 100;
        private const int UpdateRecoveryParentWaitMilliseconds = 1800000;
        private const string UpdateHealthProbeArgument = "--update-health-probe";
        private const string UpdateTransactionArgument = "--update-transaction";
        private const string UpdateHealthTokenArgument = "--update-health-token";
        private const string UpdateHealthOwnerArgument = "--update-health-owner";
        private const int SafeDeletionMaximumEntries = 100000;
        private const int SafeDeletionMaximumDepth = 64;
        private const uint DeleteAccess = 0x00010000;
        private const uint FileReadAttributesAccess = 0x00000080;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint FileShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const int FileDispositionInformationClass = 4;
        private const int MoveFileDelayUntilReboot = 0x00000004;
        private static readonly string[] UninstallProductDirectoryNames =
        {
            "Boostix",
            "Majestic Boost"
        };
        private static readonly string[] UninstallLocalDataDirectoryNames =
        {
            "Boostix",
            "MajesticBoost"
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInformation
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool DeleteFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        private sealed class SafeDeletionEntry
        {
            public string Path;
            public bool IsDirectory;
            public int Depth;
        }

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class ShellLink
        {
        }

        [ComImport]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath(
                [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
                int maximumPath,
                IntPtr findData,
                uint flags);
            void GetIDList(out IntPtr itemIdList);
            void SetIDList(IntPtr itemIdList);
            void GetDescription(
                [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description,
                int maximumName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
            void GetWorkingDirectory(
                [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory,
                int maximumPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
            void GetArguments(
                [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments,
                int maximumPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCommand);
            void SetShowCmd(int showCommand);
            void GetIconLocation(
                [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath,
                int maximumPath,
                out int iconIndex);
            void SetIconLocation(
                [MarshalAs(UnmanagedType.LPWStr)] string iconPath,
                int iconIndex);
            void SetRelativePath(
                [MarshalAs(UnmanagedType.LPWStr)] string relativePath,
                uint reserved);
            void Resolve(IntPtr windowHandle, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            StringBuilder path,
            uint pathLength,
            uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            int fileInformationClass,
            ref FileDispositionInformation fileInformation,
            uint bufferSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(
            string existingFileName,
            string newFileName,
            int flags);

        private enum UpdateRollbackStatus
        {
            Preparing,
            Prepared,
            Installing,
            AwaitingReady,
            RollingBack,
            RolledBack,
            Committed
        }

        private enum PreviousInstallationKind
        {
            Boostix,
            Legacy
        }

        private sealed class UpdateRollbackState
        {
            public string TransactionId;
            public UpdateRollbackStatus Status;
            public string PreviousVersion;
            public string ExpectedVersion;
            public string ExpectedSid;
            public PreviousInstallationKind PreviousInstallation;
        }

        private sealed class UpdateRollbackTransaction
        {
            public string Id;
            public string RootDirectory;
            public string RecoveryExecutablePath;
            public Process RecoveryWatchdog;
            public UpdateRollbackState State;
            public PostInstallRegistrationSnapshot Registration;
        }

        private sealed class SnapshotFileEntry
        {
            public string RelativePath;
            public long Length;
            public string Sha256;
        }

        private sealed class UpdateRolledBackException : InvalidOperationException
        {
            public UpdateRolledBackException(string message)
                : base(message)
            {
            }
        }

        public static void Install(bool createDesktopShortcut, Action<int, string> progress = null)
        {
            InstallerDiagnostics.Write(
                "Install requested. DesktopShortcut=" + createDesktopShortcut + ".");
            using (IDisposable systemTransactionGuard =
                AcquireSystemTransactionGuard("установку или обновление"))
            {
                RecoverInterruptedUpdateTransactions(false);
                bool boostixInstalled = File.Exists(InstalledExe);
                bool legacyInstalled = File.Exists(LegacyInstalledExe);
                bool boostixRollbackEligible = boostixInstalled &&
                    IsInstalledExecutableRollbackEligible(
                        InstalledExe,
                        false);
                // An interrupted older migration can leave a damaged Boostix.exe
                // beside the still-working legacy installation. Prefer that valid
                // legacy copy as the durable rollback source instead of discarding
                // it through the non-health-checked repair path.
                bool legacyRollbackEligible = !boostixRollbackEligible &&
                    legacyInstalled &&
                    IsInstalledExecutableRollbackEligible(
                        LegacyInstalledExe,
                        true);
                bool legacyMigration = legacyRollbackEligible;
                bool rollbackEligible = boostixRollbackEligible ||
                    legacyRollbackEligible;
                if (rollbackEligible)
                {
                    InstallUpdateWithHealthRollback(
                        createDesktopShortcut,
                        progress,
                        legacyMigration);
                }
                else
                {
                    if (boostixInstalled || legacyInstalled)
                    {
                        InstallerDiagnostics.Write(
                            "The previous executable is damaged or has an unexpected identity; " +
                            "using the transactional repair path without executing it.");
                    }
                    InstallWithSystemTransactionGuard(createDesktopShortcut, progress);
                }
                CleanupLegacyInstallationAfterSuccess();
            }
            InstallerDiagnostics.Write("Install completed successfully.");
        }

        private static void CleanupLegacyInstallationAfterSuccess()
        {
            string desktopDirectory;
            if (TryResolveOptionalShortcutRoot(
                    Environment.SpecialFolder.CommonDesktopDirectory,
                    "desktop",
                    out desktopDirectory))
            {
                TryDeleteShortcut(Path.Combine(
                    desktopDirectory,
                    ProductBrand.LegacyInstallDirectoryName + ".lnk"));
            }
            TryLegacyCleanup(delegate
            {
                DeleteIfExists(Path.Combine(InstallDirectory, "Game-Boost.ps1"));
            });

            string commonPrograms;
            if (TryResolveOptionalShortcutRoot(
                    Environment.SpecialFolder.CommonPrograms,
                    "Start Menu",
                    out commonPrograms))
            {
                TryDeleteOptionalShortcutDirectory(
                    commonPrograms,
                    Path.Combine(commonPrograms, ProductBrand.LegacyInstallDirectoryName),
                    UninstallProductDirectoryNames);
            }

            TryLegacyCleanup(delegate
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine,
                    MachineRegistryView))
                {
                    baseKey.DeleteSubKeyTree(LegacyUninstallRegistryPath, false);
                    baseKey.DeleteSubKeyTree(LegacyAppPathsRegistryPath, false);
                }
            });

            if (Directory.Exists(LegacyInstallDirectory))
            {
                TryLegacyCleanup(delegate
                {
                    DeleteAllowlistedDirectoryTree(
                        GetMachineProgramFilesDirectory(),
                        LegacyInstallDirectory,
                        UninstallProductDirectoryNames,
                        null);
                });
            }
        }

        private static void TryLegacyCleanup(Action cleanup)
        {
            try
            {
                cleanup();
            }
            catch (Exception exception)
            {
                Trace.WriteLine(
                    "Boostix legacy cleanup deferred: " + exception.Message);
            }
        }

        private static void InstallUpdateWithHealthRollback(
            bool createDesktopShortcut,
            Action<int, string> progress,
            bool legacyMigration)
        {
            StopInstalledApplication();
            ReportProgress(progress, 1, "Сохранение предыдущей версии");
            UpdateRollbackTransaction transaction =
                CreateUpdateRollbackTransaction(legacyMigration);
            Process watchdog = null;
            bool committed = false;
            try
            {
                watchdog = transaction.RecoveryWatchdog;
                if (watchdog == null)
                {
                    throw new InvalidOperationException(
                        "The update recovery watchdog is unavailable.");
                }
                SetUpdateRollbackStatus(transaction, UpdateRollbackStatus.Installing);
                Action<int, string> boundedProgress = progress == null
                    ? null
                    : new Action<int, string>(delegate(int percent, string stage)
                    {
                        ReportProgress(
                            progress,
                            Math.Min(90, Math.Max(2, percent * 9 / 10)),
                            stage);
                    });
                InstallWithSystemTransactionGuard(
                    createDesktopShortcut,
                    boundedProgress);

                ReportProgress(progress, 92, "Проверка запуска новой версии");
                string token = CreateCryptographicToken();
                WriteHealthRequest(transaction, token);
                SetUpdateRollbackStatus(transaction, UpdateRollbackStatus.AwaitingReady);
                if (!LaunchAndWaitForUpdateHealth(transaction, token))
                {
                    SetUpdateRollbackStatus(transaction, UpdateRollbackStatus.RollingBack);
                    RestoreUpdateRollbackTransaction(transaction);
                    SetUpdateRollbackStatus(transaction, UpdateRollbackStatus.RolledBack);
                    TryDeleteUpdateTransaction(transaction.RootDirectory);
                    LaunchPreviousInstalledApplication(transaction.State);
                    throw new UpdateRolledBackException(
                        "Новая версия не подтвердила готовность. " +
                        "Предыдущая версия автоматически восстановлена и запущена.");
                }

                SetUpdateRollbackStatus(transaction, UpdateRollbackStatus.Committed);
                committed = true;
                TryDeleteUpdateTransaction(transaction.RootDirectory);
                ReportProgress(progress, 100, "Обновление проверено");
            }
            catch
            {
                if (!committed &&
                    transaction.State.Status != UpdateRollbackStatus.RolledBack)
                {
                    try
                    {
                        SetUpdateRollbackStatus(
                            transaction,
                            UpdateRollbackStatus.RollingBack);
                        RestoreUpdateRollbackTransaction(transaction);
                        SetUpdateRollbackStatus(
                            transaction,
                            UpdateRollbackStatus.RolledBack);
                        TryDeleteUpdateTransaction(transaction.RootDirectory);
                        LaunchPreviousInstalledApplication(transaction.State);
                    }
                    catch
                    {
                        // Keep the protected snapshot and RollingBack marker. The
                        // watchdog or the next installer invocation will retry.
                    }
                }
                throw;
            }
            finally
            {
                if (watchdog != null)
                {
                    watchdog.Dispose();
                }
                else if (!string.IsNullOrWhiteSpace(
                    transaction.RecoveryExecutablePath))
                {
                    TryDeleteIfExists(
                        transaction.RecoveryExecutablePath);
                }
            }
        }

        private static void InstallWithSystemTransactionGuard(
            bool createDesktopShortcut,
            Action<int, string> progress)
        {
            ReportProgress(progress, 0, "Подготовка обновления");
            EnsureInstallIsNotDowngrade();
            Directory.CreateDirectory(InstallDirectory);
            ValidateDirectoryTreeWithoutReparse(InstallDirectory);
            EnsureSufficientDiskSpace();
            ReportProgress(progress, 5, "Подготовка папки установки");

            InstallPayloadsAtomically(progress, delegate
            {
            PostInstallRegistrationSnapshot registrationSnapshot =
                CapturePostInstallRegistration();
            try
            {
            ReportProgress(progress, 76, "Обновление компонентов удаления");

            if (registrationSnapshot.StartMenuShortcut != null)
            {
                TryCreateShortcut(
                    registrationSnapshot.StartMenuShortcut.Path,
                    InstalledExe,
                    InstallDirectory,
                    "Boostix performance session utility.");
            }
            ReportProgress(progress, 82, "Обновление ярлыков");

            if (registrationSnapshot.DesktopShortcut != null &&
                createDesktopShortcut)
            {
                TryCreateShortcut(
                    registrationSnapshot.DesktopShortcut.Path,
                    InstalledExe,
                    InstallDirectory,
                    "Boostix performance session utility.");
            }
            else if (registrationSnapshot.DesktopShortcut != null)
            {
                TryDeleteShortcut(
                    registrationSnapshot.DesktopShortcut.Path);
            }
            ReportProgress(progress, 87, "Сохранение параметров установки");

            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, MachineRegistryView))
            using (RegistryKey uninstall = baseKey.CreateSubKey(UninstallRegistryPath))
            {
                uninstall.SetValue("DisplayName", ProductName, RegistryValueKind.String);
                uninstall.SetValue("DisplayVersion", ProductVersion, RegistryValueKind.String);
                uninstall.SetValue("Publisher", "Silas Suspect", RegistryValueKind.String);
                uninstall.SetValue("InstallLocation", InstallDirectory, RegistryValueKind.String);
                uninstall.SetValue("DisplayIcon", InstalledExe + ",0", RegistryValueKind.String);
                uninstall.SetValue("UninstallString", Quote(UninstallerExe) + " /uninstall", RegistryValueKind.String);
                uninstall.SetValue("QuietUninstallString", Quote(UninstallerExe) + " /uninstall /quiet", RegistryValueKind.String);
                uninstall.SetValue("NoModify", 1, RegistryValueKind.DWord);
                uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                uninstall.SetValue("EstimatedSize", CalculateEstimatedSizeKb(), RegistryValueKind.DWord);
                uninstall.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), RegistryValueKind.String);
            }
            ReportProgress(progress, 94, "Регистрация новой версии");

            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, MachineRegistryView))
            using (RegistryKey appPath = baseKey.CreateSubKey(AppPathsRegistryPath))
            {
                appPath.SetValue(string.Empty, InstalledExe, RegistryValueKind.String);
                appPath.SetValue("Path", InstallDirectory, RegistryValueKind.String);
            }
            ReportProgress(progress, 100, "Обновление установлено");
            }
            catch (Exception registrationException)
            {
                try
                {
                    RestorePostInstallRegistration(registrationSnapshot);
                }
                catch (Exception compensationException)
                {
                    throw new AggregateException(
                        "Installation registration failed and its previous state could not be restored completely.",
                        registrationException,
                        compensationException);
                }
                throw;
            }
            });
        }

        public static bool GetDesktopShortcutPreference()
        {
            if (!File.Exists(InstalledExe) && !File.Exists(LegacyInstalledExe))
            {
                return true;
            }

            string shortcutName = File.Exists(InstalledExe)
                ? ProductName
                : ProductBrand.LegacyInstallDirectoryName;
            string desktopDirectory;
            if (!TryResolveOptionalShortcutRoot(
                    Environment.SpecialFolder.CommonDesktopDirectory,
                    "desktop",
                    out desktopDirectory))
            {
                return false;
            }
            string shortcutPath = Path.Combine(
                desktopDirectory,
                shortcutName + ".lnk");
            if (!File.Exists(shortcutPath) ||
                Directory.Exists(shortcutPath))
            {
                return false;
            }
            try
            {
                ValidateFileNotReparse(shortcutPath);
                return true;
            }
            catch (Exception exception)
            {
                InstallerDiagnostics.Write(
                    "The optional desktop shortcut preference could not be read.",
                    exception);
                return false;
            }
        }

        public static void ScheduleUpdateSourceCleanupIfNeeded()
        {
            try
            {
                string executablePath = Path.GetFullPath(Application.ExecutablePath);
                string directoryPath = Path.GetFullPath(Path.GetDirectoryName(executablePath));
                string directoryName = Path.GetFileName(directoryPath);
                string executableName = Path.GetFileName(executablePath);
                if (!Regex.IsMatch(directoryName, @"^(?:Boostix|MajesticBoost)\.Update\.[0-9a-f]{32}$", RegexOptions.IgnoreCase) ||
                    !Regex.IsMatch(executableName, @"^(?:Boostix|MajesticBoost)-Setup-[0-9]+\.[0-9]+\.[0-9]+\.exe$", RegexOptions.IgnoreCase))
                {
                    return;
                }

                DirectoryInfo directory = new DirectoryInfo(directoryPath);
                if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return;
                }

                int processId = Process.GetCurrentProcess().Id;
                string encodedExecutable = Convert.ToBase64String(Encoding.UTF8.GetBytes(executablePath));
                string encodedDirectory = Convert.ToBase64String(Encoding.UTF8.GetBytes(directoryPath));
                string cleanupCommand =
                    "$ErrorActionPreference='SilentlyContinue';" +
                    "Wait-Process -Id " + processId.ToString(CultureInfo.InvariantCulture) +
                    " -ErrorAction SilentlyContinue;" +
                    "$e=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encodedExecutable + "'));" +
                    "$d=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encodedDirectory + "'));" +
                    "if([IO.Path]::GetFileName($d) -match '^(?:Boostix|MajesticBoost)\\.Update\\.[0-9a-f]{32}$'){" +
                    "$i=Get-Item -LiteralPath $d -Force -ErrorAction SilentlyContinue;" +
                    "if($i -and -not ($i.Attributes -band [IO.FileAttributes]::ReparsePoint)){" +
                    "[IO.File]::Delete($e);" +
                    "if([IO.Directory]::Exists($d) -and [IO.Directory]::GetFileSystemEntries($d).Length -eq 0){[IO.Directory]::Delete($d,$false)}" +
                    "}}";
                string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(cleanupCommand));
                var cleanupInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(
                        Environment.SystemDirectory,
                        @"WindowsPowerShell\v1.0\powershell.exe"),
                    Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encodedCommand,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process cleanupProcess = Process.Start(cleanupInfo);
                if (cleanupProcess != null)
                {
                    cleanupProcess.Dispose();
                }
            }
            catch
            {
                // A stale temporary setup is harmless and can be removed later.
            }
        }

        public static bool TryRunUpdateRecoveryWatchdog(string[] arguments)
        {
            string transactionId;
            int parentProcessId;
            long parentStartTimeUtcTicks;
            bool hasExactParentIdentity;
            bool recoveryRequested = false;
            foreach (string argument in arguments ?? new string[0])
            {
                if (string.Equals(
                        argument,
                        "/update-recovery",
                        StringComparison.OrdinalIgnoreCase))
                {
                    recoveryRequested = true;
                    break;
                }
            }
            if (!TryParseRecoveryArguments(
                    arguments,
                    out transactionId,
                    out parentProcessId,
                    out parentStartTimeUtcTicks,
                    out hasExactParentIdentity))
            {
                return recoveryRequested;
            }

            try
            {
                WaitForExactRecoveryParent(
                    parentProcessId,
                    parentStartTimeUtcTicks,
                    hasExactParentIdentity,
                    UpdateRecoveryParentWaitMilliseconds);

                IDisposable guard = null;
                Stopwatch guardTimer = Stopwatch.StartNew();
                while (guard == null && guardTimer.ElapsedMilliseconds < 60000)
                {
                    try
                    {
                        guard = AcquireSystemTransactionGuard(
                            "автоматическое восстановление обновления");
                    }
                    catch (InvalidOperationException)
                    {
                        System.Threading.Thread.Sleep(250);
                    }
                }
                if (guard == null)
                {
                    throw new IOException(
                        "The recovery watchdog could not acquire the system transaction lock.");
                }
                using (guard)
                {
                    RecoverOneUpdateTransaction(
                        ResolveUpdateTransactionDirectory(transactionId),
                        true);
                }
            }
            catch (Exception exception)
            {
                TryLogRecoveryFailure(transactionId, exception);
            }
            finally
            {
                ScheduleRecoveryExecutableSelfDelete();
            }
            return true;
        }

        private static bool TryParseRecoveryArguments(
            string[] arguments,
            out string transactionId,
            out int parentProcessId,
            out long parentStartTimeUtcTicks,
            out bool hasExactParentIdentity)
        {
            transactionId = null;
            parentProcessId = 0;
            parentStartTimeUtcTicks = 0L;
            hasExactParentIdentity = false;
            string[] values = arguments ?? new string[0];
            if ((values.Length != 3 && values.Length != 4) ||
                !string.Equals(
                    values[0],
                    "/update-recovery",
                    StringComparison.OrdinalIgnoreCase) ||
                !IsLowerHex(values[1], 32) ||
                !int.TryParse(
                    values[2],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out parentProcessId) ||
                parentProcessId <= 0)
            {
                return false;
            }

            if (values.Length == 4)
            {
                if (!long.TryParse(
                        values[3],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out parentStartTimeUtcTicks) ||
                    parentStartTimeUtcTicks <= DateTime.MinValue.Ticks ||
                    parentStartTimeUtcTicks > DateTime.MaxValue.Ticks ||
                    !string.Equals(
                        values[3],
                        parentStartTimeUtcTicks.ToString(
                            CultureInfo.InvariantCulture),
                        StringComparison.Ordinal))
                {
                    parentStartTimeUtcTicks = 0L;
                    return false;
                }
                hasExactParentIdentity = true;
            }

            transactionId = values[1];
            return true;
        }

        /// <summary>
        /// Waits only for the exact process instance that launched this
        /// watchdog. Legacy three-argument invocations remain parseable, but
        /// cannot prove identity and therefore skip the PID wait; the protected
        /// system-transaction guard below remains the serialization boundary.
        /// </summary>
        private static bool WaitForExactRecoveryParent(
            int parentProcessId,
            long parentStartTimeUtcTicks,
            bool hasExactParentIdentity,
            int maximumWaitMilliseconds)
        {
            if (!hasExactParentIdentity ||
                parentProcessId <= 0 ||
                maximumWaitMilliseconds < 0)
            {
                return false;
            }

            try
            {
                using (Process parent = Process.GetProcessById(parentProcessId))
                {
                    if (!IsExactRecoveryParent(
                            parent,
                            parentProcessId,
                            parentStartTimeUtcTicks))
                    {
                        return false;
                    }

                    parent.WaitForExit(maximumWaitMilliseconds);
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // The exact parent already exited; a reused PID is not awaited.
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Failure to inspect identity must never degrade to PID-only wait.
                return false;
            }
        }

        private static bool IsExactRecoveryParent(
            Process parent,
            int expectedProcessId,
            long expectedStartTimeUtcTicks)
        {
            if (parent == null || expectedProcessId <= 0)
            {
                return false;
            }

            try
            {
                if (parent.Id != expectedProcessId || parent.HasExited)
                {
                    return false;
                }

                long observedStartTimeUtcTicks =
                    parent.StartTime.ToUniversalTime().Ticks;
                parent.Refresh();
                return !parent.HasExited &&
                    parent.Id == expectedProcessId &&
                    observedStartTimeUtcTicks == expectedStartTimeUtcTicks;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        private static UpdateRollbackTransaction CreateUpdateRollbackTransaction(
            bool legacyMigration)
        {
            string rollbackRoot = EnsureSecureUpdateRollbackRoot();
            string transactionId = Guid.NewGuid().ToString("N");
            string transactionDirectory = Path.Combine(
                rollbackRoot,
                transactionId);
            CreateSecureDirectory(transactionDirectory);
            ValidateSecureDirectory(transactionDirectory);

            UpdateRollbackTransaction transaction = null;
            try
            {
                PreviousInstallationKind previousInstallation =
                    legacyMigration
                        ? PreviousInstallationKind.Legacy
                        : PreviousInstallationKind.Boostix;
                string previousExecutable = legacyMigration
                    ? LegacyInstalledExe
                    : InstalledExe;
                string previousInstallDirectory = legacyMigration
                    ? LegacyInstallDirectory
                    : InstallDirectory;
                transaction = new UpdateRollbackTransaction
                {
                    Id = transactionId,
                    RootDirectory = transactionDirectory,
                    State = new UpdateRollbackState
                    {
                        TransactionId = transactionId,
                        Status = UpdateRollbackStatus.Preparing,
                        PreviousVersion = ReadInstalledVersion(
                            previousExecutable),
                        ExpectedVersion = ProductVersion + ".0",
                        ExpectedSid = WindowsIdentity.GetCurrent().User.Value,
                        PreviousInstallation = previousInstallation
                    }
                };
                WriteUpdateState(transaction);
                transaction.RecoveryExecutablePath = Path.Combine(
                    rollbackRoot,
                    "." + transactionId + "-" + UpdateRecoveryExecutableName);
                File.Copy(
                    Application.ExecutablePath,
                    transaction.RecoveryExecutablePath,
                    false);
                ValidateFileNotReparse(transaction.RecoveryExecutablePath);
                transaction.RecoveryWatchdog =
                    StartUpdateRecoveryWatchdog(transaction);
                string filesDirectory = Path.Combine(
                    transactionDirectory,
                    "snapshot",
                    "files");
                Directory.CreateDirectory(Path.GetDirectoryName(filesDirectory));
                Directory.CreateDirectory(filesDirectory);
                ValidateDirectoryTreeWithoutReparse(
                    Path.Combine(transactionDirectory, "snapshot"));
                CreateFileSnapshot(
                    previousInstallDirectory,
                    filesDirectory,
                    Path.Combine(
                        transactionDirectory,
                        UpdateFileManifestName));

                transaction.Registration = CapturePostInstallRegistration();
                WriteRegistrationSnapshot(
                    transaction.Registration,
                    Path.Combine(
                        transactionDirectory,
                        UpdateRegistrationSnapshotName));
                ValidateUpdateSnapshot(transactionDirectory);
                SetUpdateRollbackStatus(
                    transaction,
                    UpdateRollbackStatus.Prepared);
                return transaction;
            }
            catch
            {
                TryDeleteUpdateTransaction(transactionDirectory);
                if (transaction != null &&
                    transaction.RecoveryWatchdog != null)
                {
                    transaction.RecoveryWatchdog.Dispose();
                }
                if (transaction != null &&
                    !string.IsNullOrWhiteSpace(
                        transaction.RecoveryExecutablePath))
                {
                    TryDeleteIfExists(
                        transaction.RecoveryExecutablePath);
                }
                throw;
            }
        }

        private static string ReadInstalledVersion(string installedExecutable)
        {
            if (string.IsNullOrWhiteSpace(installedExecutable) ||
                !File.Exists(installedExecutable))
            {
                throw new FileNotFoundException(
                    "The previous application executable is unavailable.",
                    installedExecutable);
            }
            ValidateFileNotReparse(installedExecutable);
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(
                installedExecutable);
            string value = (version.FileVersion ?? string.Empty).Trim();
            Version parsed;
            if (!Version.TryParse(value, out parsed))
            {
                throw new InvalidDataException(
                    "The previous application version is invalid.");
            }
            return parsed.ToString(4);
        }

        private static bool IsInstalledExecutableRollbackEligible(
            string installedExecutable,
            bool legacyInstallation)
        {
            try
            {
                string installedVersion = ReadInstalledVersion(
                    installedExecutable);
                FileVersionInfo version = FileVersionInfo.GetVersionInfo(
                    installedExecutable);
                bool productMatches = legacyInstallation
                    ? string.Equals(
                        version.ProductName,
                        ProductBrand.LegacyInstallDirectoryName,
                        StringComparison.Ordinal)
                    : string.Equals(
                          version.ProductName,
                          ProductName,
                          StringComparison.Ordinal) ||
                      string.Equals(
                          version.ProductName,
                          ProductBrand.LegacyInstallDirectoryName,
                          StringComparison.Ordinal);
                if (!productMatches)
                {
                    InstallerDiagnostics.Write(
                        "The previous executable product identity is unexpected; " +
                        "a transactional repair will be used.");
                    return false;
                }
                InstallerDiagnostics.Write(
                    "The previous executable is eligible for health-checked rollback. " +
                    "Version=" + installedVersion + ".");
                return true;
            }
            catch (Exception exception)
            {
                if (!(exception is IOException) &&
                    !(exception is InvalidDataException) &&
                    !(exception is UnauthorizedAccessException) &&
                    !(exception is System.Security.SecurityException) &&
                    !(exception is ArgumentException) &&
                    !(exception is NotSupportedException))
                {
                    throw;
                }
                InstallerDiagnostics.Write(
                    "The previous executable cannot be used for a reliable rollback; " +
                    "a transactional repair will be used.",
                    exception);
                return false;
            }
        }

        private static string EnsureSecureUpdateRollbackRoot()
        {
            string commonData = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(commonData) ||
                !Path.IsPathRooted(commonData))
            {
                throw new DirectoryNotFoundException(
                    "ProgramData is unavailable for update recovery.");
            }
            string commonRoot = Path.GetFullPath(commonData);
            ValidateDirectoryNoReparse(commonRoot, true);
            string productRoot = Path.GetFullPath(Path.Combine(
                commonRoot,
                ProductBrand.DataDirectoryName));
            if (!IsDirectChildPath(commonRoot, productRoot))
            {
                throw new IOException(
                    "The update recovery product root escaped ProgramData.");
            }
            if (File.Exists(productRoot))
            {
                throw new IOException(
                    "A file occupies the update recovery product root.");
            }
            if (!Directory.Exists(productRoot))
            {
                Directory.CreateDirectory(productRoot);
            }
            ValidateDirectoryNoReparse(productRoot, true);

            string rollbackRoot = Path.GetFullPath(Path.Combine(
                productRoot,
                UpdateRollbackDirectoryName));
            if (!IsDirectChildPath(productRoot, rollbackRoot))
            {
                throw new IOException(
                    "The update recovery root escaped its protected parent.");
            }
            if (!Directory.Exists(rollbackRoot))
            {
                CreateSecureDirectory(rollbackRoot);
            }
            ValidateSecureDirectory(rollbackRoot);
            return rollbackRoot;
        }

        private static string ResolveUpdateTransactionDirectory(
            string transactionId)
        {
            if (!IsLowerHex(transactionId, 32))
            {
                throw new InvalidDataException(
                    "The update transaction identifier is invalid.");
            }
            string rollbackRoot = EnsureSecureUpdateRollbackRoot();
            string transactionDirectory = Path.GetFullPath(Path.Combine(
                rollbackRoot,
                transactionId));
            if (!IsDirectChildPath(rollbackRoot, transactionDirectory))
            {
                throw new IOException(
                    "The update transaction escaped the rollback root.");
            }
            return transactionDirectory;
        }

        private static DirectorySecurity CreateUpdateRollbackSecurity()
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            var administrators = new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                null);
            var system = new SecurityIdentifier(
                WellKnownSidType.LocalSystemSid,
                null);
            security.SetOwner(administrators);
            security.SetGroup(administrators);
            const InheritanceFlags inheritance =
                InheritanceFlags.ContainerInherit |
                InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(
                administrators,
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                system,
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            return security;
        }

        private static void CreateSecureDirectory(string path)
        {
            if (File.Exists(path))
            {
                throw new IOException(
                    "A file occupies a protected update recovery path.");
            }
            if (Directory.Exists(path))
            {
                throw new IOException(
                    "The protected update recovery directory already exists.");
            }
            Directory.CreateDirectory(path, CreateUpdateRollbackSecurity());
            ValidateSecureDirectory(path);
        }

        private static void ValidateSecureDirectory(string path)
        {
            ValidateDirectoryNoReparse(path, true);
            DirectorySecurity security = Directory.GetAccessControl(
                path,
                AccessControlSections.Access |
                AccessControlSections.Owner);
            if (!security.AreAccessRulesProtected)
            {
                throw new System.Security.SecurityException(
                    "The update recovery directory inherits permissions.");
            }
            SecurityIdentifier owner = security.GetOwner(
                typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (!IsPrivilegedSid(owner))
            {
                throw new System.Security.SecurityException(
                    "The update recovery directory has an unexpected owner.");
            }

            AuthorizationRuleCollection rules = security.GetAccessRules(
                true,
                true,
                typeof(SecurityIdentifier));
            const FileSystemRights writes =
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
            foreach (FileSystemAccessRule rule in rules)
            {
                SecurityIdentifier identity =
                    rule.IdentityReference as SecurityIdentifier;
                if (rule.AccessControlType == AccessControlType.Allow &&
                    (rule.FileSystemRights & writes) != 0 &&
                    !IsPrivilegedSid(identity))
                {
                    throw new System.Security.SecurityException(
                        "A non-privileged identity can modify update recovery data.");
                }
            }
        }

        private static bool IsPrivilegedSid(SecurityIdentifier sid)
        {
            return sid != null &&
                (sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
                 sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        }

        private static void ValidateDirectoryNoReparse(
            string path,
            bool required)
        {
            if (!Directory.Exists(path))
            {
                if (File.Exists(path) || required)
                {
                    throw new DirectoryNotFoundException(
                        "A protected update directory is unavailable.");
                }
                return;
            }
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "A protected update directory is a reparse point.");
            }
        }

        private static void ValidateDirectoryTreeWithoutReparse(string root)
        {
            EnumerateFilesSafely(root);
        }

        private static List<string> EnumerateFilesSafely(string root)
        {
            string fullRoot = Path.GetFullPath(root);
            ValidateDirectoryNoReparse(fullRoot, true);
            var files = new List<string>();
            var pending = new Stack<string>();
            pending.Push(fullRoot);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                ValidateDirectoryNoReparse(current, true);
                foreach (string childDirectory in Directory.GetDirectories(
                    current,
                    "*",
                    SearchOption.TopDirectoryOnly))
                {
                    ValidateDirectoryNoReparse(childDirectory, true);
                    pending.Push(childDirectory);
                }
                foreach (string childFile in Directory.GetFiles(
                    current,
                    "*",
                    SearchOption.TopDirectoryOnly))
                {
                    ValidateFileNotReparse(childFile);
                    files.Add(childFile);
                }
            }
            return files;
        }

        private static void ValidateFileNotReparse(string path)
        {
            if (!File.Exists(path) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "A protected update file is missing or redirected.");
            }
        }

        private static void SetUpdateRollbackStatus(
            UpdateRollbackTransaction transaction,
            UpdateRollbackStatus status)
        {
            transaction.State.Status = status;
            WriteUpdateState(transaction);
        }

        private static void WriteUpdateState(UpdateRollbackTransaction transaction)
        {
            string content =
                "Format=2\n" +
                "Transaction=" + transaction.State.TransactionId + "\n" +
                "Status=" + transaction.State.Status + "\n" +
                "PreviousVersion=" + transaction.State.PreviousVersion + "\n" +
                "ExpectedVersion=" + transaction.State.ExpectedVersion + "\n" +
                "ExpectedSid=" + transaction.State.ExpectedSid + "\n" +
                "PreviousInstallation=" +
                    transaction.State.PreviousInstallation + "\n";
            WriteTextAtomically(
                Path.Combine(
                    transaction.RootDirectory,
                    UpdateStateFileName),
                content);
        }

        private static UpdateRollbackState ReadUpdateState(
            string transactionDirectory)
        {
            Dictionary<string, string> values = ReadControlFile(
                Path.Combine(transactionDirectory, UpdateStateFileName),
                UpdateControlFileMaximumBytes);
            string format;
            string transactionId;
            string statusText;
            string previousVersion;
            string expectedVersion;
            string expectedSid;
            string previousInstallationText;
            UpdateRollbackStatus status;
            PreviousInstallationKind previousInstallation =
                PreviousInstallationKind.Boostix;
            Version previousParsed;
            Version expectedParsed;
            SecurityIdentifier sid;
            bool format1 = values.TryGetValue("Format", out format) &&
                string.Equals(format, "1", StringComparison.Ordinal);
            bool format2 = values.TryGetValue("Format", out format) &&
                string.Equals(format, "2", StringComparison.Ordinal);
            bool previousInstallationValid = format1 ||
                (format2 &&
                 values.TryGetValue(
                     "PreviousInstallation",
                     out previousInstallationText) &&
                 Enum.TryParse(
                     previousInstallationText,
                     false,
                     out previousInstallation) &&
                 Enum.IsDefined(
                     typeof(PreviousInstallationKind),
                     previousInstallation) &&
                 string.Equals(
                     previousInstallationText,
                     previousInstallation.ToString(),
                     StringComparison.Ordinal));
            if ((!format1 && !format2) ||
                (format1 && values.Count != 6) ||
                (format2 && values.Count != 7) ||
                !previousInstallationValid ||
                !values.TryGetValue("Format", out format) ||
                !values.TryGetValue("Transaction", out transactionId) ||
                !values.TryGetValue("Status", out statusText) ||
                !values.TryGetValue("PreviousVersion", out previousVersion) ||
                !values.TryGetValue("ExpectedVersion", out expectedVersion) ||
                !values.TryGetValue("ExpectedSid", out expectedSid) ||
                !IsLowerHex(transactionId, 32) ||
                !Enum.TryParse(statusText, false, out status) ||
                !Enum.IsDefined(typeof(UpdateRollbackStatus), status) ||
                !string.Equals(
                    statusText,
                    status.ToString(),
                    StringComparison.Ordinal) ||
                !Version.TryParse(previousVersion, out previousParsed) ||
                !Version.TryParse(expectedVersion, out expectedParsed) ||
                previousParsed.Revision < 0 ||
                expectedParsed.Revision < 0 ||
                !string.Equals(
                    previousParsed.ToString(4),
                    previousVersion,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    expectedParsed.ToString(4),
                    expectedVersion,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The update rollback state is invalid.");
            }
            try
            {
                sid = new SecurityIdentifier(expectedSid);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    "The update rollback owner SID is invalid.",
                    exception);
            }
            if (!string.Equals(
                    sid.Value,
                    expectedSid,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    Path.GetFileName(transactionDirectory),
                    transactionId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The update rollback state does not match its directory.");
            }
            return new UpdateRollbackState
            {
                TransactionId = transactionId,
                Status = status,
                PreviousVersion = previousVersion,
                ExpectedVersion = expectedVersion,
                ExpectedSid = expectedSid,
                PreviousInstallation = previousInstallation
            };
        }

        private static void WriteTextAtomically(string path, string content)
        {
            string directory = Path.GetDirectoryName(path);
            string temporary = Path.Combine(
                directory,
                ".control-" + Guid.NewGuid().ToString("N") + ".tmp");
            string discarded = Path.Combine(
                directory,
                ".control-" + Guid.NewGuid().ToString("N") + ".old");
            try
            {
                using (var output = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                using (var writer = new StreamWriter(
                    output,
                    new UTF8Encoding(false)))
                {
                    writer.Write(content);
                    writer.Flush();
                    output.Flush(true);
                }
                if (File.Exists(path))
                {
                    ExecuteFileMutationWithRetry(
                        delegate { File.Replace(temporary, path, discarded, true); },
                        "replace installer control file");
                    DeleteIfExists(discarded);
                }
                else
                {
                    ExecuteFileMutationWithRetry(
                        delegate { File.Move(temporary, path); },
                        "publish installer control file");
                }
            }
            finally
            {
                TryDeleteIfExists(temporary);
                TryDeleteIfExists(discarded);
            }
        }

        private static Dictionary<string, string> ReadControlFile(
            string path,
            int maximumBytes)
        {
            ValidateFileNotReparse(path);
            byte[] bytes;
            using (var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                if (input.Length <= 0 || input.Length > maximumBytes)
                {
                    throw new InvalidDataException(
                        "An update control file has an invalid size.");
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
            string text = new UTF8Encoding(false, true).GetString(bytes);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.Length == 0)
                {
                    continue;
                }
                int separator = line.IndexOf('=');
                if (separator <= 0 || separator == line.Length - 1)
                {
                    throw new InvalidDataException(
                        "An update control file is malformed.");
                }
                string key = line.Substring(0, separator);
                if (result.ContainsKey(key))
                {
                    throw new InvalidDataException(
                        "An update control file contains a duplicate field.");
                }
                result.Add(key, line.Substring(separator + 1));
            }
            return result;
        }

        private static void CreateFileSnapshot(
            string sourceDirectory,
            string snapshotDirectory,
            string manifestPath)
        {
            string sourceRoot = Path.GetFullPath(sourceDirectory);
            string snapshotRoot = Path.GetFullPath(snapshotDirectory);
            ValidateDirectoryTreeWithoutReparse(sourceRoot);
            ValidateDirectoryTreeWithoutReparse(snapshotRoot);

            var entries = new List<SnapshotFileEntry>();
            long totalBytes = 0;
            foreach (string sourcePath in EnumerateFilesSafely(sourceRoot))
            {
                ValidateFileNotReparse(sourcePath);
                string relativePath = GetSafeRelativePath(sourceRoot, sourcePath);
                string destinationPath = ResolveSnapshotFile(
                    snapshotRoot,
                    relativePath);
                string destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (!Directory.Exists(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }
                ValidateDirectoryTreeWithoutReparse(snapshotRoot);
                var sourceInfo = new FileInfo(sourcePath);
                totalBytes += sourceInfo.Length;
                if (entries.Count >= UpdateSnapshotMaximumFiles ||
                    totalBytes > UpdateSnapshotMaximumBytes)
                {
                    throw new InvalidDataException(
                        "The previous installation is too large to snapshot safely.");
                }
                File.Copy(sourcePath, destinationPath, false);
                ValidateFileNotReparse(destinationPath);
                var entry = new SnapshotFileEntry
                {
                    RelativePath = relativePath,
                    Length = sourceInfo.Length,
                    Sha256 = ComputeFileSha256(destinationPath)
                };
                entries.Add(entry);
            }
            if (entries.Count == 0)
            {
                throw new InvalidDataException(
                    "The previous installation snapshot is empty.");
            }
            entries.Sort(delegate(SnapshotFileEntry left, SnapshotFileEntry right)
            {
                return StringComparer.OrdinalIgnoreCase.Compare(
                    left.RelativePath,
                    right.RelativePath);
            });
            WriteFileManifest(manifestPath, entries);
        }

        private static string GetSafeRelativePath(string root, string path)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(
                    fullRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "A snapshot file escaped the installation directory.");
            }
            string relative = fullPath.Substring(fullRoot.Length);
            ValidateRelativeSnapshotPath(relative);
            return relative;
        }

        private static void ValidateRelativeSnapshotPath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) ||
                relativePath.Length > 512 ||
                Path.IsPathRooted(relativePath) ||
                relativePath.IndexOf(':') >= 0 ||
                relativePath.IndexOf('\0') >= 0)
            {
                throw new InvalidDataException(
                    "A snapshot contains an invalid relative path.");
            }
            string normalized = relativePath.Replace(
                Path.AltDirectorySeparatorChar,
                Path.DirectorySeparatorChar);
            foreach (string part in normalized.Split(Path.DirectorySeparatorChar))
            {
                if (part.Length == 0 ||
                    string.Equals(part, ".", StringComparison.Ordinal) ||
                    string.Equals(part, "..", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "A snapshot contains path traversal.");
                }
            }
        }

        private static string ResolveSnapshotFile(
            string snapshotRoot,
            string relativePath)
        {
            ValidateRelativeSnapshotPath(relativePath);
            string root = Path.GetFullPath(snapshotRoot).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string candidate = Path.GetFullPath(Path.Combine(root, relativePath));
            string prefix = root + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "A snapshot path escaped its root.");
            }
            return candidate;
        }

        private static void WriteFileManifest(
            string path,
            IList<SnapshotFileEntry> entries)
        {
            var text = new StringBuilder("BoostixFiles1\n");
            foreach (SnapshotFileEntry entry in entries)
            {
                text.Append(Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(entry.RelativePath)));
                text.Append('|');
                text.Append(entry.Length.ToString(CultureInfo.InvariantCulture));
                text.Append('|');
                text.Append(entry.Sha256);
                text.Append('\n');
            }
            WriteTextAtomically(path, text.ToString());
        }

        private static List<SnapshotFileEntry> ReadFileManifest(string path)
        {
            ValidateFileNotReparse(path);
            FileInfo info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > 262144)
            {
                throw new InvalidDataException(
                    "The update file manifest has an invalid size.");
            }
            string text = File.ReadAllText(path, new UTF8Encoding(false, true));
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length < 3 ||
                !string.Equals(
                    lines[0],
                    "BoostixFiles1",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The update file manifest is invalid.");
            }
            var entries = new List<SnapshotFileEntry>();
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;
            for (int index = 1; index < lines.Length; index++)
            {
                if (lines[index].Length == 0)
                {
                    continue;
                }
                string[] fields = lines[index].Split('|');
                if (fields.Length != 3)
                {
                    throw new InvalidDataException(
                        "The update file manifest entry is invalid.");
                }
                long length;
                byte[] relativeBytes;
                try
                {
                    relativeBytes = Convert.FromBase64String(fields[0]);
                }
                catch (FormatException exception)
                {
                    throw new InvalidDataException(
                        "The update file manifest path is invalid.",
                        exception);
                }
                string relativePath =
                    new UTF8Encoding(false, true).GetString(relativeBytes);
                if (!long.TryParse(
                        fields[1],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out length) ||
                    length < 0 ||
                    !IsLowerHex(fields[2], 64))
                {
                    throw new InvalidDataException(
                        "The update file manifest entry is invalid.");
                }
                ValidateRelativeSnapshotPath(relativePath);
                if (!unique.Add(relativePath))
                {
                    throw new InvalidDataException(
                        "The update file manifest contains a duplicate path.");
                }
                totalBytes += length;
                if (entries.Count >= UpdateSnapshotMaximumFiles ||
                    totalBytes > UpdateSnapshotMaximumBytes)
                {
                    throw new InvalidDataException(
                        "The update file manifest exceeds its limits.");
                }
                entries.Add(new SnapshotFileEntry
                {
                    RelativePath = relativePath,
                    Length = length,
                    Sha256 = fields[2]
                });
            }
            if (entries.Count == 0)
            {
                throw new InvalidDataException(
                    "The update file manifest is empty.");
            }
            return entries;
        }

        private static List<SnapshotFileEntry> ValidateUpdateSnapshot(
            string transactionDirectory)
        {
            ValidateSecureDirectory(transactionDirectory);
            string snapshotRoot = Path.Combine(
                transactionDirectory,
                "snapshot",
                "files");
            List<SnapshotFileEntry> entries = ValidateFileSnapshotAtPaths(
                snapshotRoot,
                Path.Combine(
                    transactionDirectory,
                    UpdateFileManifestName));
            ValidateFileNotReparse(Path.Combine(
                transactionDirectory,
                UpdateRegistrationSnapshotName));
            return entries;
        }

        private static List<SnapshotFileEntry> ValidateFileSnapshotAtPaths(
            string snapshotRoot,
            string manifestPath)
        {
            snapshotRoot = Path.GetFullPath(snapshotRoot);
            ValidateDirectoryTreeWithoutReparse(snapshotRoot);
            List<SnapshotFileEntry> entries = ReadFileManifest(manifestPath);
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SnapshotFileEntry entry in entries)
            {
                string path = ResolveSnapshotFile(
                    snapshotRoot,
                    entry.RelativePath);
                ValidateFileNotReparse(path);
                var info = new FileInfo(path);
                if (info.Length != entry.Length ||
                    !FixedTimeEquals(
                        ComputeFileSha256(path),
                        entry.Sha256))
                {
                    throw new InvalidDataException(
                        "The previous installation snapshot is corrupt.");
                }
                expected.Add(Path.GetFullPath(path));
            }
            foreach (string actual in EnumerateFilesSafely(snapshotRoot))
            {
                if (!expected.Contains(Path.GetFullPath(actual)))
                {
                    throw new InvalidDataException(
                        "The previous installation snapshot contains an unexpected file.");
                }
            }
            return entries;
        }

        private static string ComputeFileSha256(string path)
        {
            using (var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            using (SHA256 hash = SHA256.Create())
            {
                return BytesToLowerHex(hash.ComputeHash(input));
            }
        }

        private static void RestoreFileSnapshot(
            string transactionDirectory,
            string installDirectory)
        {
            string snapshotRoot = Path.Combine(
                transactionDirectory,
                "snapshot",
                "files");
            RestoreFileSnapshotAtPaths(
                snapshotRoot,
                Path.Combine(
                    transactionDirectory,
                    UpdateFileManifestName),
                installDirectory,
                Path.GetFileName(transactionDirectory));
        }

        private static void RestoreFileSnapshotAtPaths(
            string snapshotRoot,
            string manifestPath,
            string installDirectory,
            string transactionId)
        {
            if (!IsLowerHex(transactionId, 32))
            {
                throw new InvalidDataException(
                    "The update restore transaction identifier is invalid.");
            }
            List<SnapshotFileEntry> entries =
                ValidateFileSnapshotAtPaths(snapshotRoot, manifestPath);
            string installRoot = Path.GetFullPath(installDirectory).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string installParent = Path.GetDirectoryName(installRoot);
            string restoreStage = installRoot + ".restore-" + transactionId;
            string failedStage = installRoot + ".failed-" + transactionId;
            if (!IsDirectChildPath(installParent, restoreStage) ||
                !IsDirectChildPath(installParent, failedStage))
            {
                throw new IOException(
                    "The update restore stage escaped the installation parent.");
            }
            TryDeleteValidatedTree(restoreStage, installParent);
            Directory.CreateDirectory(restoreStage);
            ValidateDirectoryNoReparse(restoreStage, true);
            try
            {
                foreach (SnapshotFileEntry entry in entries)
                {
                    string source = ResolveSnapshotFile(
                        snapshotRoot,
                        entry.RelativePath);
                    string destination = ResolveSnapshotFile(
                        restoreStage,
                        entry.RelativePath);
                    string directory = Path.GetDirectoryName(destination);
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    File.Copy(source, destination, false);
                    if (new FileInfo(destination).Length != entry.Length ||
                        !FixedTimeEquals(
                            ComputeFileSha256(destination),
                            entry.Sha256))
                    {
                        throw new InvalidDataException(
                            "The staged rollback copy failed verification.");
                    }
                }
                ValidateDirectoryTreeWithoutReparse(restoreStage);

                TryDeleteValidatedTree(failedStage, installParent);
                if (Directory.Exists(installRoot))
                {
                    ValidateDirectoryTreeWithoutReparse(installRoot);
                    Directory.Move(installRoot, failedStage);
                }
                else if (File.Exists(installRoot))
                {
                    throw new IOException(
                        "A file occupies the installation directory.");
                }

                try
                {
                    Directory.Move(restoreStage, installRoot);
                }
                catch
                {
                    if (!Directory.Exists(installRoot) &&
                        Directory.Exists(failedStage))
                    {
                        Directory.Move(failedStage, installRoot);
                    }
                    throw;
                }
                TryDeleteValidatedTree(failedStage, installParent);
            }
            finally
            {
                TryDeleteValidatedTree(restoreStage, installParent);
            }
        }

        private static void TryDeleteValidatedTree(
            string path,
            string expectedParent)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                if (!IsDirectChildPath(expectedParent, fullPath))
                {
                    return;
                }
                if (Directory.Exists(fullPath))
                {
                    ValidateDirectoryTreeWithoutReparse(fullPath);
                    Directory.Delete(fullPath, true);
                }
                else if (File.Exists(fullPath))
                {
                    ValidateFileNotReparse(fullPath);
                    File.Delete(fullPath);
                }
            }
            catch
            {
                // Cleanup never widens the rollback target.
            }
        }

        private static void WriteRegistrationSnapshot(
            PostInstallRegistrationSnapshot snapshot,
            string path)
        {
            string temporary = Path.Combine(
                Path.GetDirectoryName(path),
                ".registration-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    65536,
                    FileOptions.WriteThrough))
                using (var writer = new BinaryWriter(
                    output,
                    new UTF8Encoding(false)))
                {
                    writer.Write("BoostixRegistration1");
                    WriteShortcutSnapshot(writer, snapshot.StartMenuShortcut);
                    WriteShortcutSnapshot(writer, snapshot.DesktopShortcut);
                    writer.Write(snapshot.StartMenuDirectoryExisted);
                    WriteRegistrySnapshot(writer, snapshot.UninstallKey, 0);
                    WriteRegistrySnapshot(writer, snapshot.AppPathsKey, 0);
                    writer.Flush();
                    output.Flush(true);
                }
                ExecuteFileMutationWithRetry(
                    delegate { File.Move(temporary, path); },
                    "publish registration snapshot");
            }
            finally
            {
                TryDeleteIfExists(temporary);
            }
        }

        private static PostInstallRegistrationSnapshot ReadRegistrationSnapshot(
            string path)
        {
            ValidateFileNotReparse(path);
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > 2 * 1024 * 1024)
            {
                throw new InvalidDataException(
                    "The registration rollback snapshot has an invalid size.");
            }
            using (var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            using (var reader = new BinaryReader(
                input,
                new UTF8Encoding(false, true)))
            {
                if (!string.Equals(
                        reader.ReadString(),
                        "BoostixRegistration1",
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The registration rollback snapshot has an invalid header.");
                }
                var snapshot = new PostInstallRegistrationSnapshot
                {
                    StartMenuShortcut = ReadShortcutSnapshot(reader),
                    DesktopShortcut = ReadShortcutSnapshot(reader),
                    StartMenuDirectoryExisted = reader.ReadBoolean(),
                    UninstallKey = ReadRegistrySnapshot(reader, 0),
                    AppPathsKey = ReadRegistrySnapshot(reader, 0)
                };
                ValidateRegistrationSnapshot(snapshot);
                if (input.Position != input.Length)
                {
                    throw new InvalidDataException(
                        "The registration rollback snapshot has trailing data.");
                }
                return snapshot;
            }
        }

        private static void WriteShortcutSnapshot(
            BinaryWriter writer,
            ShortcutSnapshot snapshot)
        {
            writer.Write(snapshot != null);
            if (snapshot == null)
            {
                return;
            }
            writer.Write(snapshot.Path ?? string.Empty);
            writer.Write(snapshot.Existed);
            writer.Write((int)snapshot.Attributes);
            writer.Write(snapshot.LastWriteTimeUtc.Ticks);
            byte[] contents = snapshot.Contents ?? new byte[0];
            if (contents.Length > 1024 * 1024)
            {
                throw new InvalidDataException(
                    "A shortcut rollback snapshot is too large.");
            }
            writer.Write(contents.Length);
            writer.Write(contents);
        }

        private static ShortcutSnapshot ReadShortcutSnapshot(BinaryReader reader)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }
            string path = reader.ReadString();
            bool existed = reader.ReadBoolean();
            FileAttributes attributes = (FileAttributes)reader.ReadInt32();
            long lastWriteTicks = reader.ReadInt64();
            int length = reader.ReadInt32();
            if (length < 0 || length > 1024 * 1024)
            {
                throw new InvalidDataException(
                    "A shortcut rollback snapshot is too large.");
            }
            byte[] contents = ReadExactly(reader, length);
            if (!existed && contents.Length != 0)
            {
                throw new InvalidDataException(
                    "A missing shortcut has unexpected snapshot bytes.");
            }
            if ((attributes & (
                    FileAttributes.ReparsePoint |
                    FileAttributes.Directory |
                    FileAttributes.Device)) != 0 ||
                lastWriteTicks < DateTime.MinValue.Ticks ||
                lastWriteTicks > DateTime.MaxValue.Ticks)
            {
                throw new InvalidDataException(
                    "A shortcut rollback snapshot has invalid metadata.");
            }
            return new ShortcutSnapshot
            {
                Path = path,
                Existed = existed,
                Contents = contents,
                Attributes = attributes,
                LastWriteTimeUtc = new DateTime(
                    lastWriteTicks,
                    DateTimeKind.Utc)
            };
        }

        private static void WriteRegistrySnapshot(
            BinaryWriter writer,
            RegistryKeySnapshot snapshot,
            int depth)
        {
            if (depth > 16)
            {
                throw new InvalidDataException(
                    "The registry rollback snapshot is too deep.");
            }
            writer.Write(snapshot != null);
            if (snapshot == null)
            {
                return;
            }
            writer.Write(snapshot.Name ?? string.Empty);
            writer.Write(snapshot.Existed);
            writer.Write(snapshot.Values.Count);
            foreach (RegistryValueSnapshot value in snapshot.Values)
            {
                writer.Write(value.Name ?? string.Empty);
                writer.Write((int)value.Kind);
                WriteRegistryValue(writer, value.Kind, value.Value);
            }
            writer.Write(snapshot.Children.Count);
            foreach (RegistryKeySnapshot child in snapshot.Children)
            {
                WriteRegistrySnapshot(writer, child, depth + 1);
            }
        }

        private static RegistryKeySnapshot ReadRegistrySnapshot(
            BinaryReader reader,
            int depth)
        {
            if (depth > 16)
            {
                throw new InvalidDataException(
                    "The registry rollback snapshot is too deep.");
            }
            if (!reader.ReadBoolean())
            {
                return null;
            }
            var snapshot = new RegistryKeySnapshot
            {
                Name = reader.ReadString(),
                Existed = reader.ReadBoolean()
            };
            int valueCount = reader.ReadInt32();
            if (valueCount < 0 || valueCount > 256)
            {
                throw new InvalidDataException(
                    "The registry rollback snapshot has too many values.");
            }
            for (int index = 0; index < valueCount; index++)
            {
                string name = reader.ReadString();
                RegistryValueKind kind = (RegistryValueKind)reader.ReadInt32();
                snapshot.Values.Add(new RegistryValueSnapshot
                {
                    Name = name,
                    Kind = kind,
                    Value = ReadRegistryValue(reader, kind)
                });
            }
            int childCount = reader.ReadInt32();
            if (childCount < 0 || childCount > 128)
            {
                throw new InvalidDataException(
                    "The registry rollback snapshot has too many child keys.");
            }
            for (int index = 0; index < childCount; index++)
            {
                snapshot.Children.Add(
                    ReadRegistrySnapshot(reader, depth + 1));
            }
            return snapshot;
        }

        private static void WriteRegistryValue(
            BinaryWriter writer,
            RegistryValueKind kind,
            object value)
        {
            switch (kind)
            {
                case RegistryValueKind.String:
                case RegistryValueKind.ExpandString:
                    writer.Write(value as string ?? string.Empty);
                    break;
                case RegistryValueKind.DWord:
                    writer.Write(Convert.ToInt32(
                        value,
                        CultureInfo.InvariantCulture));
                    break;
                case RegistryValueKind.QWord:
                    writer.Write(Convert.ToInt64(
                        value,
                        CultureInfo.InvariantCulture));
                    break;
                case RegistryValueKind.MultiString:
                    string[] strings = value as string[];
                    if (strings == null || strings.Length > 256)
                    {
                        throw new InvalidDataException(
                            "A multi-string registry value is invalid.");
                    }
                    writer.Write(strings.Length);
                    foreach (string item in strings)
                    {
                        writer.Write(item ?? string.Empty);
                    }
                    break;
                case RegistryValueKind.Binary:
                case RegistryValueKind.None:
                case RegistryValueKind.Unknown:
                    byte[] bytes = value as byte[];
                    if (bytes == null || bytes.Length > 1024 * 1024)
                    {
                        throw new InvalidDataException(
                            "A binary registry value is invalid.");
                    }
                    writer.Write(bytes.Length);
                    writer.Write(bytes);
                    break;
                default:
                    throw new InvalidDataException(
                        "An unsupported registry value kind was captured.");
            }
        }

        private static object ReadRegistryValue(
            BinaryReader reader,
            RegistryValueKind kind)
        {
            switch (kind)
            {
                case RegistryValueKind.String:
                case RegistryValueKind.ExpandString:
                    return reader.ReadString();
                case RegistryValueKind.DWord:
                    return reader.ReadInt32();
                case RegistryValueKind.QWord:
                    return reader.ReadInt64();
                case RegistryValueKind.MultiString:
                    int count = reader.ReadInt32();
                    if (count < 0 || count > 256)
                    {
                        throw new InvalidDataException(
                            "A multi-string registry snapshot is invalid.");
                    }
                    string[] strings = new string[count];
                    for (int index = 0; index < count; index++)
                    {
                        strings[index] = reader.ReadString();
                    }
                    return strings;
                case RegistryValueKind.Binary:
                case RegistryValueKind.None:
                case RegistryValueKind.Unknown:
                    int length = reader.ReadInt32();
                    if (length < 0 || length > 1024 * 1024)
                    {
                        throw new InvalidDataException(
                            "A binary registry snapshot is invalid.");
                    }
                    return ReadExactly(reader, length);
                default:
                    throw new InvalidDataException(
                        "An unsupported registry value kind was restored.");
            }
        }

        private static byte[] ReadExactly(BinaryReader reader, int length)
        {
            byte[] value = reader.ReadBytes(length);
            if (value.Length != length)
            {
                throw new EndOfStreamException();
            }
            return value;
        }

        private static void ValidateRegistrationSnapshot(
            PostInstallRegistrationSnapshot snapshot)
        {
            if (snapshot == null ||
                snapshot.UninstallKey == null ||
                snapshot.AppPathsKey == null ||
                !string.Equals(
                    snapshot.UninstallKey.Name,
                    UninstallRegistryPath,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    snapshot.AppPathsKey.Name,
                    AppPathsRegistryPath,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The registration rollback snapshot targets an unexpected path.");
            }
            if (snapshot.StartMenuShortcut == null &&
                snapshot.StartMenuDirectoryExisted)
            {
                throw new InvalidDataException(
                    "A skipped Start Menu snapshot has inconsistent metadata.");
            }
            if (snapshot.StartMenuShortcut != null &&
                !ValidateOptionalShortcutSnapshot(
                    snapshot.StartMenuShortcut,
                    Environment.SpecialFolder.CommonPrograms,
                    "Start Menu",
                    ProductName,
                    ProductName + ".lnk"))
            {
                snapshot.StartMenuShortcut = null;
                snapshot.StartMenuDirectoryExisted = false;
            }
            if (snapshot.DesktopShortcut != null &&
                !ValidateOptionalShortcutSnapshot(
                    snapshot.DesktopShortcut,
                    Environment.SpecialFolder.CommonDesktopDirectory,
                    "desktop",
                    ProductName + ".lnk"))
            {
                snapshot.DesktopShortcut = null;
            }
            ValidateRegistrySnapshotTree(snapshot.UninstallKey, true, 0);
            ValidateRegistrySnapshotTree(snapshot.AppPathsKey, true, 0);
        }

        private static bool ValidateOptionalShortcutSnapshot(
            ShortcutSnapshot snapshot,
            Environment.SpecialFolder folder,
            string purpose,
            params string[] relativeParts)
        {
            string root;
            if (!TryResolveOptionalShortcutRoot(folder, purpose, out root))
            {
                return false;
            }
            string expected = root;
            foreach (string part in relativeParts)
            {
                expected = Path.Combine(expected, part);
            }
            string actual;
            try
            {
                if (snapshot == null ||
                    string.IsNullOrWhiteSpace(snapshot.Path) ||
                    !Path.IsPathRooted(snapshot.Path))
                {
                    throw new InvalidDataException(
                        "An optional shortcut snapshot has an invalid path.");
                }
                actual = Path.GetFullPath(snapshot.Path);
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new InvalidDataException(
                    "An optional shortcut snapshot has an invalid path.",
                    exception);
            }
            if (!string.Equals(
                    actual,
                    Path.GetFullPath(expected),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The registration rollback snapshot targets an unexpected shortcut.");
            }
            return true;
        }

        private static void ValidateRegistrySnapshotTree(
            RegistryKeySnapshot snapshot,
            bool root,
            int depth)
        {
            if (snapshot == null || depth > 16 ||
                string.IsNullOrEmpty(snapshot.Name))
            {
                throw new InvalidDataException(
                    "The registry rollback snapshot is invalid.");
            }
            if (!root &&
                (snapshot.Name.IndexOf('\\') >= 0 ||
                 snapshot.Name.IndexOf('/') >= 0 ||
                 string.Equals(snapshot.Name, ".", StringComparison.Ordinal) ||
                 string.Equals(snapshot.Name, "..", StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    "The registry rollback snapshot contains path traversal.");
            }
            var valueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RegistryValueSnapshot value in snapshot.Values)
            {
                if (value == null ||
                    !valueNames.Add(value.Name ?? string.Empty))
                {
                    throw new InvalidDataException(
                        "The registry rollback snapshot contains duplicate values.");
                }
            }
            var childNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RegistryKeySnapshot child in snapshot.Children)
            {
                if (child == null || !childNames.Add(child.Name))
                {
                    throw new InvalidDataException(
                        "The registry rollback snapshot contains duplicate keys.");
                }
                ValidateRegistrySnapshotTree(child, false, depth + 1);
            }
        }

        private static Process StartUpdateRecoveryWatchdog(
            UpdateRollbackTransaction transaction)
        {
            string recoveryPath = transaction.RecoveryExecutablePath;
            if (string.IsNullOrWhiteSpace(recoveryPath))
            {
                recoveryPath = Path.Combine(
                    Path.GetDirectoryName(transaction.RootDirectory),
                    "." + transaction.Id + "-" + UpdateRecoveryExecutableName);
            }
            ValidateFileNotReparse(recoveryPath);
            int parentProcessId;
            long parentStartTimeUtcTicks;
            using (Process parent = Process.GetCurrentProcess())
            {
                parentProcessId = parent.Id;
                parentStartTimeUtcTicks =
                    parent.StartTime.ToUniversalTime().Ticks;
            }
            var startInfo = new ProcessStartInfo
            {
                FileName = recoveryPath,
                Arguments = "/update-recovery " + transaction.Id + " " +
                    parentProcessId.ToString(CultureInfo.InvariantCulture) + " " +
                    parentStartTimeUtcTicks.ToString(CultureInfo.InvariantCulture),
                // Do not make the protected rollback root the process current
                // directory. Windows otherwise keeps that directory busy briefly
                // after a successful update and can obstruct immediate uninstall
                // or machine cleanup.
                WorkingDirectory = Environment.SystemDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                ErrorDialog = false
            };
            Process watchdog = Process.Start(startInfo);
            if (watchdog == null)
            {
                throw new InvalidOperationException(
                    "The update recovery watchdog could not be started.");
            }
            return watchdog;
        }

        private static void WriteHealthRequest(
            UpdateRollbackTransaction transaction,
            string token)
        {
            string tokenHash;
            using (SHA256 sha256 = SHA256.Create())
            {
                tokenHash = BytesToLowerHex(
                    sha256.ComputeHash(HexToBytes(token)));
            }
            string content =
                "Format=1\n" +
                "Transaction=" + transaction.Id + "\n" +
                "TokenSha256=" + tokenHash + "\n" +
                "ExpectedSid=" + transaction.State.ExpectedSid + "\n" +
                "ExpectedVersion=" + transaction.State.ExpectedVersion + "\n" +
                "ExpiresUtcTicks=" +
                    DateTime.UtcNow.AddMinutes(5).Ticks.ToString(
                        CultureInfo.InvariantCulture) + "\n";
            WriteTextAtomically(
                Path.Combine(
                    transaction.RootDirectory,
                    UpdateHealthRequestName),
                content);
        }

        private static bool LaunchAndWaitForUpdateHealth(
            UpdateRollbackTransaction transaction,
            string token)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = InstalledExe,
                Arguments =
                    UpdateHealthProbeArgument + " " +
                    UpdateTransactionArgument + "=" + transaction.Id + " " +
                    UpdateHealthTokenArgument + "=" + token + " " +
                    UpdateHealthOwnerArgument + "=" +
                        transaction.State.ExpectedSid,
                WorkingDirectory = InstallDirectory,
                UseShellExecute = false,
                CreateNoWindow = false,
                ErrorDialog = false
            };
            Process probe = Process.Start(startInfo);
            if (probe == null)
            {
                InstallerDiagnostics.Write(
                    "Update health probe process was not created.");
                return false;
            }
            try
            {
                Stopwatch timer = Stopwatch.StartNew();
                while (timer.ElapsedMilliseconds <
                    UpdateHealthTimeoutMilliseconds)
                {
                    bool invalidSignal;
                    if (TryValidateReadySignal(
                            transaction.RootDirectory,
                            transaction.Id,
                            token,
                            transaction.State.ExpectedSid,
                            transaction.State.ExpectedVersion,
                            out invalidSignal))
                    {
                        if (!probe.WaitForExit(3000))
                        {
                            try
                            {
                                probe.Kill();
                            }
                            catch
                            {
                            }
                        }
                        InstallerDiagnostics.Write(
                            "Update health probe succeeded after " +
                            timer.ElapsedMilliseconds.ToString(
                                CultureInfo.InvariantCulture) + " ms.");
                        return true;
                    }
                    if (invalidSignal || probe.HasExited)
                    {
                        InstallerDiagnostics.Write(
                            invalidSignal
                                ? "Update health probe produced an invalid ready signal."
                                : "Update health probe exited before signalling readiness. ExitCode=" +
                                  probe.ExitCode.ToString(CultureInfo.InvariantCulture) + ".");
                        return false;
                    }
                    System.Threading.Thread.Sleep(
                        UpdateHealthPollMilliseconds);
                }
                InstallerDiagnostics.Write(
                    "Update health probe timed out after " +
                    UpdateHealthTimeoutMilliseconds.ToString(
                        CultureInfo.InvariantCulture) + " ms.");
                return false;
            }
            finally
            {
                if (!probe.HasExited)
                {
                    try
                    {
                        probe.Kill();
                        probe.WaitForExit(3000);
                    }
                    catch
                    {
                    }
                }
                probe.Dispose();
            }
        }

        private static bool TryValidateReadySignal(
            string transactionDirectory,
            string transactionId,
            string token,
            string expectedSid,
            string expectedVersion,
            out bool invalidSignal)
        {
            invalidSignal = false;
            string path = Path.Combine(
                transactionDirectory,
                UpdateReadySignalName);
            if (!File.Exists(path))
            {
                return false;
            }
            try
            {
                Dictionary<string, string> values = ReadControlFile(
                    path,
                    UpdateControlFileMaximumBytes);
                string format;
                string signalTransaction;
                string readySid;
                string readyVersion;
                string proof;
                if (values.Count != 5 ||
                    !values.TryGetValue("Format", out format) ||
                    !values.TryGetValue(
                        "Transaction",
                        out signalTransaction) ||
                    !values.TryGetValue("ReadySid", out readySid) ||
                    !values.TryGetValue("ReadyVersion", out readyVersion) ||
                    !values.TryGetValue("Proof", out proof) ||
                    !string.Equals(format, "1", StringComparison.Ordinal) ||
                    !string.Equals(
                        signalTransaction,
                        transactionId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        readySid,
                        expectedSid,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        readyVersion,
                        expectedVersion,
                        StringComparison.Ordinal) ||
                    !IsLowerHex(proof, 64))
                {
                    invalidSignal = true;
                    return false;
                }
                string expectedProof = ComputeReadyProof(
                    transactionId,
                    token,
                    expectedSid,
                    expectedVersion);
                if (!FixedTimeEquals(proof, expectedProof))
                {
                    invalidSignal = true;
                    return false;
                }
                return true;
            }
            catch
            {
                invalidSignal = true;
                return false;
            }
        }

        private static string ComputeReadyProof(
            string transactionId,
            string token,
            string sid,
            string version)
        {
            byte[] payload = Encoding.UTF8.GetBytes(
                "Boostix.UpdateReady.v1\n" +
                transactionId + "\n" +
                sid + "\n" +
                version);
            using (var hmac = new HMACSHA256(HexToBytes(token)))
            {
                return BytesToLowerHex(hmac.ComputeHash(payload));
            }
        }

        private static string CreateCryptographicToken()
        {
            byte[] value = new byte[32];
            using (RandomNumberGenerator generator =
                RandomNumberGenerator.Create())
            {
                generator.GetBytes(value);
            }
            return BytesToLowerHex(value);
        }

        private static byte[] HexToBytes(string text)
        {
            if (!IsLowerHex(text, 64))
            {
                throw new InvalidDataException(
                    "The update health token is invalid.");
            }
            byte[] bytes = new byte[text.Length / 2];
            for (int index = 0; index < bytes.Length; index++)
            {
                bytes[index] = byte.Parse(
                    text.Substring(index * 2, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture);
            }
            return bytes;
        }

        private static string BytesToLowerHex(byte[] bytes)
        {
            var result = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes)
            {
                result.Append(value.ToString(
                    "x2",
                    CultureInfo.InvariantCulture));
            }
            return result.ToString();
        }

        private static bool IsLowerHex(string text, int length)
        {
            if (string.IsNullOrEmpty(text) || text.Length != length)
            {
                return false;
            }
            foreach (char value in text)
            {
                if (!((value >= '0' && value <= '9') ||
                    (value >= 'a' && value <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool FixedTimeEquals(string left, string right)
        {
            if (left == null || right == null ||
                left.Length != right.Length)
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

        private static void RecoverInterruptedUpdateTransactions(
            bool launchAfterRollback)
        {
            string rollbackRoot = EnsureSecureUpdateRollbackRoot();
            foreach (string transactionDirectory in Directory.GetDirectories(
                rollbackRoot,
                "*",
                SearchOption.TopDirectoryOnly))
            {
                string transactionId = Path.GetFileName(
                    transactionDirectory);
                if (!IsLowerHex(transactionId, 32) ||
                    !IsDirectChildPath(
                        rollbackRoot,
                        transactionDirectory))
                {
                    // This cannot be a transaction created by Boostix. Leave it
                    // untouched for diagnostics, but do not let an unrelated
                    // administrator-created folder permanently deny installs.
                    InstallerDiagnostics.Write(
                        "An unrelated directory in the protected rollback root " +
                        "was ignored.");
                    continue;
                }
                RecoverOneUpdateTransaction(
                    transactionDirectory,
                    launchAfterRollback);
            }
            PruneStaleRecoveryExecutables(rollbackRoot);
        }

        private static void PruneStaleRecoveryExecutables(
            string rollbackRoot)
        {
            foreach (string candidate in Directory.GetFiles(
                rollbackRoot,
                ".*-recovery.exe",
                SearchOption.TopDirectoryOnly))
            {
                try
                {
                    string name = Path.GetFileName(candidate);
                    Match match = Regex.Match(
                        name,
                        @"^\.([0-9a-f]{32})-recovery\.exe$",
                        RegexOptions.IgnoreCase |
                        RegexOptions.CultureInvariant);
                    if (!match.Success ||
                        !IsDirectChildPath(rollbackRoot, candidate) ||
                        Directory.Exists(Path.Combine(
                            rollbackRoot,
                            match.Groups[1].Value)))
                    {
                        continue;
                    }
                    ValidateFileNotReparse(candidate);
                    File.Delete(candidate);
                }
                catch
                {
                    // A running watchdog will remove itself after its parent exits.
                }
            }
        }

        private static bool RecoverOneUpdateTransaction(
            string transactionDirectory,
            bool launchAfterRollback)
        {
            if (!Directory.Exists(transactionDirectory))
            {
                return false;
            }
            ValidateSecureDirectory(transactionDirectory);
            string statePath = Path.Combine(
                transactionDirectory,
                UpdateStateFileName);
            if (!File.Exists(statePath))
            {
                ValidateDirectoryTreeWithoutReparse(transactionDirectory);
                if (Directory.GetDirectories(
                        transactionDirectory,
                        "*",
                        SearchOption.TopDirectoryOnly).Length == 0)
                {
                    bool onlyTemporaryControlFiles = true;
                    foreach (string file in Directory.GetFiles(
                        transactionDirectory,
                        "*",
                        SearchOption.TopDirectoryOnly))
                    {
                        if (!Regex.IsMatch(
                                Path.GetFileName(file),
                                @"^\.control-[0-9a-f]{32}\.(tmp|old)$",
                                RegexOptions.IgnoreCase |
                                RegexOptions.CultureInvariant))
                        {
                            onlyTemporaryControlFiles = false;
                            break;
                        }
                    }
                    if (onlyTemporaryControlFiles)
                    {
                        TryDeleteUpdateTransaction(transactionDirectory);
                        return false;
                    }
                }
                throw new InvalidDataException(
                    "An update rollback transaction is missing its durable state.");
            }
            UpdateRollbackState state = ReadUpdateState(
                transactionDirectory);
            if (state.Status == UpdateRollbackStatus.Preparing)
            {
                // No installed file is touched before Prepared is persisted.
                TryDeleteUpdateTransaction(transactionDirectory);
                if (launchAfterRollback)
                {
                    LaunchPreviousInstalledApplication(state);
                }
                return false;
            }
            if (state.Status == UpdateRollbackStatus.Committed ||
                state.Status == UpdateRollbackStatus.RolledBack)
            {
                TryDeleteUpdateTransaction(transactionDirectory);
                return false;
            }

            var transaction = new UpdateRollbackTransaction
            {
                Id = state.TransactionId,
                RootDirectory = transactionDirectory,
                State = state
            };
            SetUpdateRollbackStatus(
                transaction,
                UpdateRollbackStatus.RollingBack);
            RestoreUpdateRollbackTransaction(transaction);
            SetUpdateRollbackStatus(
                transaction,
                UpdateRollbackStatus.RolledBack);
            TryDeleteUpdateTransaction(transactionDirectory);
            if (launchAfterRollback)
            {
                LaunchPreviousInstalledApplication(state);
            }
            return true;
        }

        private static void RestoreUpdateRollbackTransaction(
            UpdateRollbackTransaction transaction)
        {
            // Validate every durable recovery asset before touching the installed
            // directory or registration.
            ValidateUpdateSnapshot(transaction.RootDirectory);
            ValidateSnapshotApplicationIdentity(transaction);
            PostInstallRegistrationSnapshot registration =
                ReadRegistrationSnapshot(Path.Combine(
                    transaction.RootDirectory,
                    UpdateRegistrationSnapshotName));
            string previousInstallDirectory =
                transaction.State.PreviousInstallation ==
                    PreviousInstallationKind.Legacy
                    ? LegacyInstallDirectory
                    : InstallDirectory;
            StopInstalledApplication();
            RestoreFileSnapshot(
                transaction.RootDirectory,
                previousInstallDirectory);
            RestorePostInstallRegistration(registration);
            if (transaction.State.PreviousInstallation ==
                PreviousInstallationKind.Legacy)
            {
                DeleteAllowlistedDirectoryTree(
                    GetMachineProgramFilesDirectory(),
                    InstallDirectory,
                    UninstallProductDirectoryNames,
                    null);
            }
        }

        private static void ValidateSnapshotApplicationIdentity(
            UpdateRollbackTransaction transaction)
        {
            bool legacy = transaction.State.PreviousInstallation ==
                PreviousInstallationKind.Legacy;
            string snapshotApplication = Path.Combine(
                transaction.RootDirectory,
                "snapshot",
                "files",
                legacy ? "MajesticBoost.exe" : "Boostix.exe");
            ValidateFileNotReparse(snapshotApplication);
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(
                snapshotApplication);
            Version actualVersion;
            Version expectedVersion;
            bool productMatches = legacy
                ? string.Equals(
                    version.ProductName,
                    ProductBrand.LegacyInstallDirectoryName,
                    StringComparison.Ordinal)
                : string.Equals(
                      version.ProductName,
                      ProductName,
                      StringComparison.Ordinal) ||
                  string.Equals(
                      version.ProductName,
                      ProductBrand.LegacyInstallDirectoryName,
                      StringComparison.Ordinal);
            if (!productMatches ||
                !Version.TryParse(
                    (version.FileVersion ?? string.Empty).Trim(),
                    out actualVersion) ||
                !Version.TryParse(
                    transaction.State.PreviousVersion,
                    out expectedVersion) ||
                actualVersion != expectedVersion)
            {
                throw new InvalidDataException(
                    "The rollback snapshot does not contain the expected previous application.");
            }
        }

        private static void TryDeleteUpdateTransaction(
            string transactionDirectory)
        {
            try
            {
                string rollbackRoot = Path.GetDirectoryName(
                    Path.GetFullPath(transactionDirectory));
                if (!IsLowerHex(
                        Path.GetFileName(transactionDirectory),
                        32) ||
                    !IsDirectChildPath(
                        rollbackRoot,
                        transactionDirectory))
                {
                    return;
                }
                ValidateSecureDirectory(transactionDirectory);
                ValidateDirectoryTreeWithoutReparse(transactionDirectory);
                Directory.Delete(transactionDirectory, true);
            }
            catch
            {
                // A terminal marker makes a leftover directory harmless. The next
                // installer or watchdog invocation will retry bounded cleanup.
            }
        }

        private static void TryLogRecoveryFailure(
            string transactionId,
            Exception exception)
        {
            try
            {
                string directory = ResolveUpdateTransactionDirectory(
                    transactionId);
                if (!Directory.Exists(directory))
                {
                    return;
                }
                ValidateSecureDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "recovery.log"),
                    DateTime.UtcNow.ToString(
                        "o",
                        CultureInfo.InvariantCulture) +
                        "  " + exception.GetType().Name + ": " +
                        (exception.Message ?? string.Empty)
                            .Replace('\r', ' ')
                            .Replace('\n', ' ') +
                        Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private static void ScheduleRecoveryExecutableSelfDelete()
        {
            try
            {
                string executablePath = Path.GetFullPath(
                    Application.ExecutablePath);
                string rollbackRoot = Path.GetFullPath(
                    Path.GetDirectoryName(executablePath));
                string fileName = Path.GetFileName(executablePath);
                if (!Regex.IsMatch(
                        fileName,
                        @"^\.[0-9a-f]{32}-recovery\.exe$",
                        RegexOptions.IgnoreCase |
                        RegexOptions.CultureInvariant) ||
                    !string.Equals(
                        Path.GetFileName(rollbackRoot),
                        UpdateRollbackDirectoryName,
                        StringComparison.Ordinal))
                {
                    return;
                }
                int processId = Process.GetCurrentProcess().Id;
                string encodedPath = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(executablePath));
                string command =
                    "$ErrorActionPreference='SilentlyContinue';" +
                    "Wait-Process -Id " +
                    processId.ToString(CultureInfo.InvariantCulture) +
                    " -ErrorAction SilentlyContinue;" +
                    "$p=[Text.Encoding]::UTF8.GetString(" +
                    "[Convert]::FromBase64String('" + encodedPath + "'));" +
                    "$n=[IO.Path]::GetFileName($p);" +
                    "if($n -match '^\\.[0-9a-f]{32}-recovery\\.exe$'){" +
                    "[IO.File]::Delete($p)}";
                var info = new ProcessStartInfo
                {
                    FileName = Path.Combine(
                        Environment.SystemDirectory,
                        @"WindowsPowerShell\v1.0\powershell.exe"),
                    Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden " +
                        "-EncodedCommand " +
                        Convert.ToBase64String(
                            Encoding.Unicode.GetBytes(command)),
                    WorkingDirectory = Environment.SystemDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process cleanup = Process.Start(info);
                if (cleanup != null)
                {
                    cleanup.Dispose();
                }
            }
            catch
            {
            }
        }

        private sealed class SystemTransactionGuardSet : IDisposable
        {
            private FileStream boostixGuard;
            private FileStream legacyGuard;

            public SystemTransactionGuardSet(
                FileStream boostixTransactionGuard,
                FileStream legacyTransactionGuard)
            {
                boostixGuard = boostixTransactionGuard;
                legacyGuard = legacyTransactionGuard;
            }

            public void Dispose()
            {
                FileStream legacy = legacyGuard;
                FileStream boostix = boostixGuard;
                legacyGuard = null;
                boostixGuard = null;
                if (legacy != null)
                {
                    legacy.Dispose();
                }
                if (boostix != null)
                {
                    boostix.Dispose();
                }
            }
        }

        private static IDisposable AcquireSystemTransactionGuard(string operation)
        {
            string programData = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(programData))
            {
                throw new DirectoryNotFoundException(
                    "Не удалось определить защищённую папку ProgramData.");
            }

            return AcquireSystemTransactionGuardsAtRoots(
                Path.GetFullPath(Path.Combine(
                    programData,
                    "BoostixOptimization")),
                Path.GetFullPath(Path.Combine(
                    programData,
                    "CodexGamingOptimization")),
                operation);
        }

        private static IDisposable AcquireSystemTransactionGuardsAtRoots(
            string boostixStateRoot,
            string legacyStateRoot,
            string operation)
        {
            FileStream boostixGuard = null;
            FileStream legacyGuard = null;
            try
            {
                // Every installer operation takes the locks in the same order.
                // Apply/Restore select only one of these roots, so this ordering
                // prevents races without introducing a cross-root deadlock.
                boostixGuard = AcquireSystemTransactionGuardAtRoot(
                    boostixStateRoot,
                    operation);
                legacyGuard = AcquireSystemTransactionGuardAtRoot(
                    legacyStateRoot,
                    operation);
                return new SystemTransactionGuardSet(
                    boostixGuard,
                    legacyGuard);
            }
            catch
            {
                if (legacyGuard != null)
                {
                    legacyGuard.Dispose();
                }
                if (boostixGuard != null)
                {
                    boostixGuard.Dispose();
                }
                throw;
            }
        }

        private static FileStream AcquireSystemTransactionGuardAtRoot(
            string stateRoot,
            string operation)
        {
            if (string.IsNullOrWhiteSpace(stateRoot))
            {
                throw new ArgumentException(
                    "Protected optimization state root is required.",
                    "stateRoot");
            }

            string fullStateRoot = Path.GetFullPath(stateRoot);
            string parent = Path.GetDirectoryName(fullStateRoot);
            if (string.IsNullOrWhiteSpace(parent) ||
                !Directory.Exists(parent) ||
                !IsPathFreeOfReparsePoints(parent))
            {
                throw new IOException(
                    "Не удалось безопасно проверить папку системной оптимизации.");
            }
            if (File.Exists(fullStateRoot))
            {
                throw new IOException(
                    "Путь системной оптимизации занят файлом.");
            }
            if (!Directory.Exists(fullStateRoot))
            {
                Directory.CreateDirectory(fullStateRoot);
            }
            if (!IsPathFreeOfReparsePoints(fullStateRoot))
            {
                throw new IOException(
                    "Папка системной оптимизации не прошла проверку безопасности.");
            }

            string lockPath = Path.GetFullPath(Path.Combine(
                fullStateRoot,
                "transaction.lock"));
            if (!IsDirectChildPath(fullStateRoot, lockPath) ||
                Directory.Exists(lockPath) ||
                (File.Exists(lockPath) && !IsPathFreeOfReparsePoints(lockPath)))
            {
                throw new IOException(
                    "Файл блокировки системной оптимизации имеет небезопасный путь.");
            }

            FileStream guard;
            try
            {
                guard = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.None);
            }
            catch (IOException exception)
            {
                throw new InvalidOperationException(
                    "Нельзя продолжить " + operation +
                    ": сейчас выполняется настройка или восстановление Windows. " +
                    "Дождитесь завершения операции в Boostix и повторите попытку.",
                    exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new InvalidOperationException(
                    "Нельзя безопасно проверить системную транзакцию перед тем, как продолжить " +
                    operation + ". Повторите попытку с правами администратора.",
                    exception);
            }
            catch (System.Security.SecurityException exception)
            {
                throw new InvalidOperationException(
                    "Windows запретила проверку системной транзакции перед тем, как продолжить " +
                    operation + ".",
                    exception);
            }

            if (!IsPathFreeOfReparsePoints(lockPath))
            {
                guard.Dispose();
                throw new IOException(
                    "Файл блокировки системной оптимизации изменился во время проверки.");
            }
            return guard;
        }

        private static void EnsureUninstallStateAllowsRemoval()
        {
            string programData = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(programData))
            {
                throw new DirectoryNotFoundException(
                    "Не удалось определить защищённую папку ProgramData.");
            }

            EnsureUninstallStateAllowsRemovalAtRoots(
                Path.GetFullPath(Path.Combine(
                    programData,
                    "BoostixOptimization")),
                Path.GetFullPath(Path.Combine(
                    programData,
                    "CodexGamingOptimization")));
        }

        private static void EnsureUninstallStateAllowsRemovalAtRoots(
            string boostixStateRoot,
            string legacyStateRoot)
        {
            EnsureUninstallStateAllowsRemovalAtRoot(boostixStateRoot);
            EnsureUninstallStateAllowsRemovalAtRoot(legacyStateRoot);
        }

        private static void EnsureUninstallStateAllowsRemovalAtRoot(string stateRoot)
        {
            string fullStateRoot = Path.GetFullPath(stateRoot);
            if (File.Exists(fullStateRoot))
            {
                throw new IOException(
                    "Путь системной оптимизации занят файлом.");
            }
            if (!Directory.Exists(fullStateRoot))
            {
                return;
            }
            if (!IsPathFreeOfReparsePoints(fullStateRoot))
            {
                throw new IOException(
                    "Папка системной оптимизации не прошла проверку безопасности.");
            }

            string expectedPointerPath = Path.GetFullPath(Path.Combine(
                fullStateRoot,
                "latest-state.txt"));
            string[] pointerEntries = Directory.GetFileSystemEntries(
                fullStateRoot,
                "latest-state.txt",
                SearchOption.TopDirectoryOnly);
            if (pointerEntries.Length == 0)
            {
                return;
            }
            if (pointerEntries.Length != 1 ||
                !PathsEqual(pointerEntries[0], expectedPointerPath) ||
                Directory.Exists(expectedPointerPath) ||
                !File.Exists(expectedPointerPath) ||
                !IsPathFreeOfReparsePoints(expectedPointerPath))
            {
                throw CreateUnsafeUninstallStateException(
                    "указатель latest-state.txt неоднозначен или небезопасен");
            }

            string candidatePath;
            try
            {
                candidatePath = ReadBoundedUtf8File(
                    expectedPointerPath,
                    OptimizationStatePointerMaximumBytes).Trim();
            }
            catch (Exception exception)
            {
                throw CreateUnsafeUninstallStateException(
                    "latest-state.txt нельзя безопасно прочитать",
                    exception);
            }
            if (string.IsNullOrWhiteSpace(candidatePath) ||
                candidatePath.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0 ||
                !Path.IsPathRooted(candidatePath))
            {
                throw CreateUnsafeUninstallStateException(
                    "указатель latest-state.txt повреждён");
            }

            string fullStatePath;
            try
            {
                fullStatePath = Path.GetFullPath(candidatePath);
            }
            catch (Exception exception)
            {
                throw CreateUnsafeUninstallStateException(
                    "путь к резервной копии повреждён",
                    exception);
            }

            string backupsRoot = Path.GetFullPath(Path.Combine(
                fullStateRoot,
                "Backups"));
            string stateDirectory = Path.GetDirectoryName(fullStatePath);
            string stateDirectoryParent = string.IsNullOrWhiteSpace(stateDirectory)
                ? null
                : Path.GetDirectoryName(stateDirectory);
            if (!string.Equals(
                    Path.GetFileName(fullStatePath),
                    "state.json",
                    StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(stateDirectory) ||
                string.IsNullOrWhiteSpace(stateDirectoryParent) ||
                !PathsEqual(stateDirectoryParent, backupsRoot) ||
                !File.Exists(fullStatePath) ||
                !IsPathFreeOfReparsePoints(fullStatePath))
            {
                throw CreateUnsafeUninstallStateException(
                    "state.json должен быть прямым потомком защищённой папки Backups");
            }

            string json;
            try
            {
                json = ReadBoundedUtf8File(
                    fullStatePath,
                    OptimizationStateMaximumBytes);
            }
            catch (Exception exception)
            {
                throw CreateUnsafeUninstallStateException(
                    "state.json нельзя безопасно прочитать",
                    exception);
            }
            string trimmedJson = json.Trim();
            if (trimmedJson.Length < 2 ||
                trimmedJson[0] != '{' ||
                trimmedJson[trimmedJson.Length - 1] != '}')
            {
                throw CreateUnsafeUninstallStateException(
                    "state.json повреждён или не завершён");
            }

            const string statusPattern =
                "\\\"Status\\\"\\s*:\\s*\\\"(?<value>(?:\\\\.|[^\\\"\\\\])*)\\\"";
            MatchCollection statusMatches = Regex.Matches(
                trimmedJson,
                statusPattern,
                RegexOptions.CultureInvariant);
            if (statusMatches.Count != 1)
            {
                throw CreateUnsafeUninstallStateException(
                    "статус системной транзакции отсутствует или неоднозначен");
            }

            string status = statusMatches[0].Groups["value"].Value;
            if (status.Length == 0 ||
                status.Length > 64 ||
                !Regex.IsMatch(
                    status,
                    "^[A-Za-z]+$",
                    RegexOptions.CultureInvariant))
            {
                throw CreateUnsafeUninstallStateException(
                    "статус системной транзакции повреждён");
            }
            if (IsCompletedRestoreStatus(status))
            {
                return;
            }

            throw new InvalidOperationException(
                "Удаление остановлено: системная оптимизация Windows ещё не восстановлена " +
                "(статус: " + status + "). Откройте Boostix, нажмите " +
                "«ВОССТАНОВИТЬ WINDOWS» и дождитесь завершения восстановления. " +
                "Резервная копия сохранена.");
        }

        private static InvalidOperationException CreateUnsafeUninstallStateException(
            string reason,
            Exception innerException = null)
        {
            return new InvalidOperationException(
                "Удаление остановлено: состояние системной оптимизации нельзя " +
                "однозначно и безопасно проверить (" + reason + "). Откройте Boostix, " +
                "нажмите «ВОССТАНОВИТЬ WINDOWS» и только после успешного восстановления " +
                "повторите удаление. Резервная копия не удалена.",
                innerException);
        }

        private static bool IsCompletedRestoreStatus(string status)
        {
            return
                string.Equals(status, "Restored", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "RestoredWithConflicts", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "AlreadyRestored", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "AbortedNoChanges", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "SupersededLegacy", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadBoundedUtf8File(string path, int maximumBytes)
        {
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan))
            {
                if (stream.Length <= 0 || stream.Length > maximumBytes)
                {
                    throw new InvalidDataException(
                        "Protected optimization state file has an invalid size.");
                }
                using (var reader = new StreamReader(
                    stream,
                    new UTF8Encoding(false, true),
                    true,
                    4096,
                    false))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private static bool IsDirectChildPath(string parentPath, string childPath)
        {
            string fullParent = Path.GetFullPath(parentPath).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string fullChild = Path.GetFullPath(childPath);
            return string.Equals(
                Path.GetDirectoryName(fullChild),
                fullParent,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool PathsEqual(string left, string right)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(left).TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(right).TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPathFreeOfReparsePoints(string path)
        {
            try
            {
                string normalized = Path.GetFullPath(path);
                string root = Path.GetPathRoot(normalized);
                if (string.IsNullOrWhiteSpace(root))
                {
                    return false;
                }

                string current = root;
                string remainder = normalized.Substring(root.Length);
                string[] segments = remainder.Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries);
                for (int index = 0; index < segments.Length; index++)
                {
                    current = Path.Combine(current, segments[index]);
                    if (!Directory.Exists(current) && !File.Exists(current))
                    {
                        return false;
                    }
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    {
                        return false;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool DeleteAllowlistedDirectoryTree(
            string boundaryRoot,
            string targetPath,
            string[] allowedLeafNames,
            string preservedPath)
        {
            string fullBoundary = Path.GetFullPath(boundaryRoot).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string fullTarget = Path.GetFullPath(targetPath).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(fullBoundary) ||
                string.IsNullOrWhiteSpace(fullTarget) ||
                !Directory.Exists(fullBoundary) ||
                !IsPathFreeOfReparsePoints(fullBoundary) ||
                !IsDirectChildPath(fullBoundary, fullTarget) ||
                !IsAllowedLeafName(Path.GetFileName(fullTarget), allowedLeafNames))
            {
                throw new IOException(
                    "The uninstall cleanup target is outside its allowlisted boundary.");
            }

            string existingTarget = FindExactFileSystemEntry(
                fullBoundary,
                fullTarget);
            if (existingTarget == null)
            {
                return true;
            }
            fullTarget = Path.GetFullPath(existingTarget).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);

            uint boundaryAttributes;
            string finalBoundary;
            using (SafeFileHandle boundaryHandle = OpenVerifiedPathHandle(
                fullBoundary,
                FileReadAttributesAccess,
                out boundaryAttributes,
                out finalBoundary))
            {
                if ((boundaryAttributes & (uint)FileAttributes.Directory) == 0)
                {
                    throw new IOException(
                        "The uninstall cleanup boundary is not a directory.");
                }
            }

            uint targetAttributes;
            string finalTarget;
            using (SafeFileHandle targetHandle = OpenVerifiedPathHandle(
                fullTarget,
                FileReadAttributesAccess,
                out targetAttributes,
                out finalTarget))
            {
                if ((targetAttributes & (uint)FileAttributes.Directory) == 0 ||
                    !IsDirectChildPath(finalBoundary, finalTarget))
                {
                    throw new IOException(
                        "The uninstall cleanup target escaped its canonical boundary.");
                }
            }

            string fullPreservedPath = null;
            string finalPreservedPath = null;
            if (!string.IsNullOrWhiteSpace(preservedPath))
            {
                fullPreservedPath = Path.GetFullPath(preservedPath);
                if (!PathsEqual(fullPreservedPath, fullTarget) &&
                    !IsStrictChildPath(fullTarget, fullPreservedPath))
                {
                    throw new IOException(
                        "The preserved uninstall file is outside the cleanup target.");
                }

                uint preservedAttributes;
                using (SafeFileHandle preservedHandle = OpenVerifiedPathHandle(
                    fullPreservedPath,
                    FileReadAttributesAccess,
                    out preservedAttributes,
                    out finalPreservedPath))
                {
                    if ((preservedAttributes & (uint)FileAttributes.Directory) != 0 ||
                        !IsStrictChildPath(finalTarget, finalPreservedPath))
                    {
                        throw new IOException(
                            "The preserved uninstall file escaped the cleanup target.");
                    }
                }
            }

            List<SafeDeletionEntry> entries = BuildSafeDeletionPlan(
                fullTarget,
                finalTarget);
            entries.Sort(delegate(SafeDeletionEntry left, SafeDeletionEntry right)
            {
                int depthOrder = right.Depth.CompareTo(left.Depth);
                if (depthOrder != 0)
                {
                    return depthOrder;
                }
                if (left.IsDirectory != right.IsDirectory)
                {
                    return left.IsDirectory ? 1 : -1;
                }
                return string.Compare(
                    left.Path,
                    right.Path,
                    StringComparison.OrdinalIgnoreCase);
            });

            foreach (SafeDeletionEntry entry in entries)
            {
                if (fullPreservedPath != null &&
                    (PathsEqual(entry.Path, fullPreservedPath) ||
                     (entry.IsDirectory &&
                      IsStrictChildPath(entry.Path, fullPreservedPath))))
                {
                    continue;
                }
                DeleteVerifiedPathByHandle(
                    entry.Path,
                    entry.IsDirectory,
                    finalTarget,
                    false);
            }

            if (fullPreservedPath != null)
            {
                uint currentAttributes;
                string currentFinalPath;
                using (SafeFileHandle preservedHandle = OpenVerifiedPathHandle(
                    fullPreservedPath,
                    FileReadAttributesAccess,
                    out currentAttributes,
                    out currentFinalPath))
                {
                    if ((currentAttributes & (uint)FileAttributes.Directory) != 0 ||
                        !PathsEqual(currentFinalPath, finalPreservedPath))
                    {
                        throw new IOException(
                            "The preserved uninstall file changed during cleanup.");
                    }
                }
                return false;
            }

            DeleteVerifiedPathByHandle(
                fullTarget,
                true,
                finalTarget,
                true);
            return true;
        }

        private static List<SafeDeletionEntry> BuildSafeDeletionPlan(
            string rootPath,
            string finalRootPath)
        {
            var entries = new List<SafeDeletionEntry>();
            var pending = new Stack<SafeDeletionEntry>();
            pending.Push(new SafeDeletionEntry
            {
                Path = rootPath,
                IsDirectory = true,
                Depth = 0
            });

            while (pending.Count > 0)
            {
                SafeDeletionEntry current = pending.Pop();
                if (current.Depth > SafeDeletionMaximumDepth)
                {
                    throw new IOException(
                        "The uninstall cleanup tree exceeds the safe depth limit.");
                }

                uint currentAttributes;
                string currentFinalPath;
                using (SafeFileHandle currentHandle = OpenVerifiedPathHandle(
                    current.Path,
                    FileReadAttributesAccess,
                    out currentAttributes,
                    out currentFinalPath))
                {
                    if ((currentAttributes & (uint)FileAttributes.Directory) == 0 ||
                        (!PathsEqual(currentFinalPath, finalRootPath) &&
                         !IsStrictChildPath(finalRootPath, currentFinalPath)))
                    {
                        throw new IOException(
                            "An uninstall cleanup directory escaped the canonical target.");
                    }
                }

                foreach (string child in Directory.GetFileSystemEntries(
                    current.Path,
                    "*",
                    SearchOption.TopDirectoryOnly))
                {
                    string fullChild = Path.GetFullPath(child);
                    if (!IsDirectChildPath(current.Path, fullChild) ||
                        !IsStrictChildPath(rootPath, fullChild))
                    {
                        throw new IOException(
                            "An uninstall cleanup entry escaped the target tree.");
                    }

                    uint childAttributes;
                    string finalChildPath;
                    using (SafeFileHandle childHandle = OpenVerifiedPathHandle(
                        fullChild,
                        FileReadAttributesAccess,
                        out childAttributes,
                        out finalChildPath))
                    {
                        if (!IsStrictChildPath(finalRootPath, finalChildPath))
                        {
                            throw new IOException(
                                "An uninstall cleanup entry escaped the canonical target.");
                        }
                    }

                    bool isDirectory =
                        (childAttributes & (uint)FileAttributes.Directory) != 0;
                    var entry = new SafeDeletionEntry
                    {
                        Path = fullChild,
                        IsDirectory = isDirectory,
                        Depth = current.Depth + 1
                    };
                    entries.Add(entry);
                    if (entries.Count > SafeDeletionMaximumEntries)
                    {
                        throw new IOException(
                            "The uninstall cleanup tree exceeds the safe entry limit.");
                    }
                    if (isDirectory)
                    {
                        pending.Push(entry);
                    }
                }
            }
            return entries;
        }

        private static void DeleteVerifiedPathByHandle(
            string path,
            bool expectedDirectory,
            string finalRootPath,
            bool allowRoot)
        {
            uint attributes;
            string finalPath;
            using (SafeFileHandle handle = OpenVerifiedPathHandle(
                path,
                DeleteAccess | FileReadAttributesAccess,
                out attributes,
                out finalPath))
            {
                bool isDirectory =
                    (attributes & (uint)FileAttributes.Directory) != 0;
                bool insideBoundary = allowRoot
                    ? PathsEqual(finalPath, finalRootPath)
                    : IsStrictChildPath(finalRootPath, finalPath);
                if (isDirectory != expectedDirectory || !insideBoundary)
                {
                    throw new IOException(
                        "An uninstall cleanup entry changed before deletion.");
                }

                var disposition = new FileDispositionInformation
                {
                    DeleteFile = true
                };
                if (!SetFileInformationByHandle(
                    handle,
                    FileDispositionInformationClass,
                    ref disposition,
                    (uint)Marshal.SizeOf(typeof(FileDispositionInformation))))
                {
                    throw new System.ComponentModel.Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Windows refused a verified uninstall cleanup operation.");
                }
            }
        }

        private static SafeFileHandle OpenVerifiedPathHandle(
            string path,
            uint desiredAccess,
            out uint attributes,
            out string finalPath)
        {
            SafeFileHandle handle = CreateFile(
                path,
                desiredAccess,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle == null || handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                if (handle != null)
                {
                    handle.Dispose();
                }
                throw new System.ComponentModel.Win32Exception(
                    error,
                    "Windows could not open a protected uninstall cleanup path.");
            }

            try
            {
                ByHandleFileInformation information;
                if (!GetFileInformationByHandle(handle, out information))
                {
                    throw new System.ComponentModel.Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Windows could not inspect a protected uninstall cleanup path.");
                }
                attributes = information.FileAttributes;
                if ((attributes & (uint)FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        "Uninstall cleanup refuses junctions, symbolic links, and other reparse points.");
                }
                finalPath = ReadFinalPathFromHandle(handle);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static string ReadFinalPathFromHandle(SafeFileHandle handle)
        {
            uint capacity = 512;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var buffer = new StringBuilder((int)capacity);
                uint length = GetFinalPathNameByHandle(
                    handle,
                    buffer,
                    capacity,
                    0);
                if (length == 0)
                {
                    throw new System.ComponentModel.Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Windows could not canonicalize an uninstall cleanup path.");
                }
                if (length < capacity)
                {
                    return NormalizeFinalPath(buffer.ToString());
                }
                capacity = checked(length + 1);
            }
            throw new PathTooLongException(
                "The canonical uninstall cleanup path is too long.");
        }

        private static string NormalizeFinalPath(string path)
        {
            const string uncPrefix = @"\\?\UNC\";
            const string devicePrefix = @"\\?\";
            if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(@"\\" + path.Substring(uncPrefix.Length));
            }
            if (path.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(path.Substring(devicePrefix.Length));
            }
            return Path.GetFullPath(path);
        }

        private static string FindExactFileSystemEntry(
            string parentPath,
            string expectedPath)
        {
            string leafName = Path.GetFileName(expectedPath);
            string[] matches = Directory.GetFileSystemEntries(
                parentPath,
                leafName,
                SearchOption.TopDirectoryOnly);
            string match = null;
            foreach (string candidate in matches)
            {
                if (!PathsEqual(candidate, expectedPath))
                {
                    continue;
                }
                if (match != null)
                {
                    throw new IOException(
                        "The uninstall cleanup target is ambiguous.");
                }
                match = candidate;
            }
            return match;
        }

        private static bool IsAllowedLeafName(
            string leafName,
            string[] allowedLeafNames)
        {
            if (string.IsNullOrWhiteSpace(leafName) ||
                allowedLeafNames == null)
            {
                return false;
            }
            foreach (string allowed in allowedLeafNames)
            {
                if (string.Equals(
                    leafName,
                    allowed,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static void ScheduleVerifiedInstallDirectoryRemoval(
            string preservedExecutable)
        {
            if (string.IsNullOrWhiteSpace(preservedExecutable))
            {
                return;
            }
            string fullExecutable = Path.GetFullPath(preservedExecutable);
            string fullInstallDirectory = Path.GetFullPath(
                InstallDirectory).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            if (!IsDirectChildPath(fullInstallDirectory, fullExecutable) ||
                !IsPathFreeOfReparsePoints(fullExecutable))
            {
                throw new IOException(
                    "The running uninstaller cannot be safely scheduled for cleanup.");
            }
            if (!MoveFileEx(
                fullExecutable,
                null,
                MoveFileDelayUntilReboot))
            {
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows could not schedule the uninstaller for safe removal.");
            }
            if (!MoveFileEx(
                fullInstallDirectory,
                null,
                MoveFileDelayUntilReboot))
            {
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows could not schedule the empty install directory for safe removal.");
            }
        }

        public static void Uninstall(bool quiet)
        {
            if (!quiet)
            {
                DialogResult result = MessageBox.Show(
                    "Удалить Boostix и все установленные файлы?",
                    "Удаление Boostix",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (result != DialogResult.Yes)
                {
                    return;
                }
            }

            try
            {
                using (IDisposable systemTransactionGuard =
                    AcquireSystemTransactionGuard("удаление"))
                {
                RecoverInterruptedUpdateTransactions(false);
                EnsureUninstallStateAllowsRemoval();
                StopInstalledApplication();

                string desktopDirectory;
                if (TryResolveOptionalShortcutRoot(
                        Environment.SpecialFolder.CommonDesktopDirectory,
                        "desktop",
                        out desktopDirectory))
                {
                    foreach (string directoryName in UninstallProductDirectoryNames)
                    {
                        TryDeleteShortcut(Path.Combine(
                            desktopDirectory,
                            directoryName + ".lnk"));
                    }
                }

                string commonPrograms;
                if (TryResolveOptionalShortcutRoot(
                        Environment.SpecialFolder.CommonPrograms,
                        "Start Menu",
                        out commonPrograms))
                {
                    foreach (string directoryName in UninstallProductDirectoryNames)
                    {
                        TryDeleteOptionalShortcutDirectory(
                            commonPrograms,
                            Path.Combine(commonPrograms, directoryName),
                            UninstallProductDirectoryNames);
                    }
                }

                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, MachineRegistryView))
                {
                    baseKey.DeleteSubKeyTree(UninstallRegistryPath, false);
                    baseKey.DeleteSubKeyTree(AppPathsRegistryPath, false);
                    baseKey.DeleteSubKeyTree(LegacyUninstallRegistryPath, false);
                    baseKey.DeleteSubKeyTree(LegacyAppPathsRegistryPath, false);
                }

                string localApplicationData = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                foreach (string directoryName in UninstallLocalDataDirectoryNames)
                {
                    DeleteAllowlistedDirectoryTree(
                        localApplicationData,
                        Path.Combine(localApplicationData, directoryName),
                        UninstallLocalDataDirectoryNames,
                        null);
                }
                TryPruneProtectedCaptureFiles(true);

                string runningExecutable = Path.GetFullPath(
                    Application.ExecutablePath);
                string preservedExecutable =
                    IsDirectChildPath(InstallDirectory, runningExecutable)
                    ? runningExecutable
                    : null;
                bool installDirectoryRemoved =
                    DeleteAllowlistedDirectoryTree(
                        GetMachineProgramFilesDirectory(),
                        InstallDirectory,
                        UninstallProductDirectoryNames,
                        preservedExecutable);
                if (!installDirectoryRemoved)
                {
                    ScheduleVerifiedInstallDirectoryRemoval(
                        preservedExecutable);
                }
                if (Directory.Exists(LegacyInstallDirectory))
                {
                    DeleteAllowlistedDirectoryTree(
                        GetMachineProgramFilesDirectory(),
                        LegacyInstallDirectory,
                        UninstallProductDirectoryNames,
                        null);
                }

                if (!quiet)
                {
                    MessageBox.Show(
                        "Boostix удалён.",
                        "Удаление завершено",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }

                }
            }
            catch (Exception exception)
            {
                if (!quiet)
                {
                    MessageBox.Show(
                        "Не удалось полностью удалить программу:\r\n" + exception.Message,
                        "Ошибка удаления",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                Environment.ExitCode = 1;
                if (quiet)
                {
                    throw;
                }
            }
        }

        private sealed class ShortcutSnapshot
        {
            public string Path;
            public bool Existed;
            public byte[] Contents;
            public FileAttributes Attributes;
            public DateTime LastWriteTimeUtc;
        }

        private sealed class RegistryKeySnapshot
        {
            public string Name;
            public bool Existed;
            public readonly List<RegistryValueSnapshot> Values =
                new List<RegistryValueSnapshot>();
            public readonly List<RegistryKeySnapshot> Children =
                new List<RegistryKeySnapshot>();
        }

        private sealed class RegistryValueSnapshot
        {
            public string Name;
            public object Value;
            public RegistryValueKind Kind;
        }

        private sealed class PostInstallRegistrationSnapshot
        {
            public ShortcutSnapshot StartMenuShortcut;
            public ShortcutSnapshot DesktopShortcut;
            public bool StartMenuDirectoryExisted;
            public RegistryKeySnapshot UninstallKey;
            public RegistryKeySnapshot AppPathsKey;
        }

        private static PostInstallRegistrationSnapshot CapturePostInstallRegistration()
        {
            var snapshot = new PostInstallRegistrationSnapshot();
            string commonPrograms;
            if (TryResolveOptionalShortcutRoot(
                    Environment.SpecialFolder.CommonPrograms,
                    "Start Menu",
                    out commonPrograms))
            {
                string startMenuDirectory = Path.Combine(
                    commonPrograms,
                    ProductName);
                snapshot.StartMenuShortcut = TryCaptureShortcut(Path.Combine(
                    startMenuDirectory,
                    ProductName + ".lnk"),
                    "Start Menu");
                if (snapshot.StartMenuShortcut != null)
                {
                    snapshot.StartMenuDirectoryExisted =
                        Directory.Exists(startMenuDirectory);
                }
            }
            string desktopDirectory;
            if (TryResolveOptionalShortcutRoot(
                    Environment.SpecialFolder.CommonDesktopDirectory,
                    "desktop",
                    out desktopDirectory))
            {
                snapshot.DesktopShortcut = TryCaptureShortcut(
                    Path.Combine(desktopDirectory, ProductName + ".lnk"),
                    "desktop");
            }

            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                MachineRegistryView))
            {
                snapshot.UninstallKey = CaptureRegistryKey(baseKey, UninstallRegistryPath);
                snapshot.AppPathsKey = CaptureRegistryKey(baseKey, AppPathsRegistryPath);
            }
            return snapshot;
        }

        private static ShortcutSnapshot TryCaptureShortcut(
            string path,
            string purpose)
        {
            try
            {
                return CaptureShortcut(path);
            }
            catch (Exception exception)
            {
                InstallerDiagnostics.Write(
                    "Optional " + purpose +
                    " shortcut state could not be captured; it will not be changed.",
                    exception);
                return null;
            }
        }

        private static ShortcutSnapshot CaptureShortcut(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new IOException(
                    "A shortcut directory is missing or redirected.");
            }
            if (Directory.Exists(directory))
            {
                if (!IsPathFreeOfReparsePoints(directory))
                {
                    throw new IOException(
                        "A shortcut directory is missing or redirected.");
                }
            }
            else
            {
                string parent = Path.GetDirectoryName(directory);
                if (File.Exists(directory) ||
                    string.IsNullOrWhiteSpace(parent) ||
                    !Directory.Exists(parent) ||
                    !IsDirectChildPath(parent, directory) ||
                    !IsPathFreeOfReparsePoints(parent))
                {
                    throw new IOException(
                        "A shortcut directory is missing or redirected.");
                }
            }
            if (Directory.Exists(fullPath))
            {
                throw new IOException(
                    "A directory occupies the shortcut path.");
            }
            var snapshot = new ShortcutSnapshot
            {
                Path = fullPath,
                Existed = File.Exists(fullPath)
            };
            if (snapshot.Existed)
            {
                ValidateFileNotReparse(snapshot.Path);
                if (new FileInfo(snapshot.Path).Length > 1024 * 1024)
                {
                    throw new InvalidDataException(
                        "An existing shortcut is too large to snapshot safely.");
                }
                snapshot.Contents = File.ReadAllBytes(fullPath);
                snapshot.Attributes = File.GetAttributes(fullPath);
                snapshot.LastWriteTimeUtc = File.GetLastWriteTimeUtc(fullPath);
            }
            return snapshot;
        }

        private static RegistryKeySnapshot CaptureRegistryKey(RegistryKey parent, string path)
        {
            RegistryKey key = parent.OpenSubKey(path, false);
            if (key == null)
            {
                return new RegistryKeySnapshot { Name = path, Existed = false };
            }
            using (key)
            {
                RegistryKeySnapshot snapshot = CaptureRegistryTree(key, path);
                snapshot.Existed = true;
                return snapshot;
            }
        }

        private static RegistryKeySnapshot CaptureRegistryTree(RegistryKey key, string name)
        {
            var snapshot = new RegistryKeySnapshot { Name = name, Existed = true };
            foreach (string valueName in key.GetValueNames())
            {
                snapshot.Values.Add(new RegistryValueSnapshot
                {
                    Name = valueName,
                    Value = key.GetValue(
                        valueName,
                        null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames),
                    Kind = key.GetValueKind(valueName)
                });
            }
            foreach (string childName in key.GetSubKeyNames())
            {
                using (RegistryKey child = key.OpenSubKey(childName, false))
                {
                    if (child == null)
                    {
                        throw new IOException(
                            "An installation registry key changed while it was being backed up.");
                    }
                    snapshot.Children.Add(CaptureRegistryTree(child, childName));
                }
            }
            return snapshot;
        }

        private static void RestorePostInstallRegistration(
            PostInstallRegistrationSnapshot snapshot)
        {
            ValidateRegistrationSnapshot(snapshot);
            var failures = new List<Exception>();
            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                MachineRegistryView))
            {
                TryCompensation(
                    delegate { RestoreRegistryKey(baseKey, snapshot.AppPathsKey); },
                    failures);
                TryCompensation(
                    delegate { RestoreRegistryKey(baseKey, snapshot.UninstallKey); },
                    failures);
            }
            if (snapshot.DesktopShortcut != null)
            {
                TryRestoreOptionalShortcut(snapshot.DesktopShortcut);
            }
            if (snapshot.StartMenuShortcut != null)
            {
                TryRestoreOptionalShortcut(snapshot.StartMenuShortcut);
            }

            if (snapshot.StartMenuShortcut != null &&
                !snapshot.StartMenuDirectoryExisted)
            {
                TryDeleteEmptyDirectory(Path.GetDirectoryName(
                    snapshot.StartMenuShortcut.Path));
            }
            if (failures.Count != 0)
            {
                throw new AggregateException(
                    "One or more installation registration items could not be restored.",
                    failures);
            }
        }

        private static void TryCompensation(Action action, List<Exception> failures)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        private static void RestoreShortcut(ShortcutSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(snapshot.Path) ||
                !Path.IsPathRooted(snapshot.Path))
            {
                throw new IOException(
                    "A shortcut restore path is unavailable.");
            }
            string directory = Path.GetDirectoryName(snapshot.Path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new IOException(
                    "A shortcut restore directory is unavailable.");
            }
            if (!Directory.Exists(directory))
            {
                string parent = Path.GetDirectoryName(directory);
                if (File.Exists(directory) ||
                    string.IsNullOrWhiteSpace(parent) ||
                    !Directory.Exists(parent) ||
                    !IsDirectChildPath(parent, directory) ||
                    !IsPathFreeOfReparsePoints(parent))
                {
                    throw new IOException(
                        "A shortcut restore directory is unavailable or redirected.");
                }
                if (!snapshot.Existed)
                {
                    return;
                }
                Directory.CreateDirectory(directory);
            }
            if (!IsPathFreeOfReparsePoints(directory))
            {
                throw new IOException(
                    "A shortcut directory is redirected.");
            }
            if (Directory.Exists(snapshot.Path))
            {
                throw new IOException(
                    "A directory occupies the shortcut restore path.");
            }
            if (!snapshot.Existed)
            {
                if (File.Exists(snapshot.Path))
                {
                    ValidateFileNotReparse(snapshot.Path);
                    File.Delete(snapshot.Path);
                }
                return;
            }
            if (File.Exists(snapshot.Path))
            {
                ValidateFileNotReparse(snapshot.Path);
                File.SetAttributes(snapshot.Path, FileAttributes.Normal);
            }
            File.WriteAllBytes(snapshot.Path, snapshot.Contents);
            File.SetLastWriteTimeUtc(snapshot.Path, snapshot.LastWriteTimeUtc);
            File.SetAttributes(snapshot.Path, snapshot.Attributes);
        }

        private static void TryRestoreOptionalShortcut(
            ShortcutSnapshot snapshot)
        {
            try
            {
                RestoreShortcut(snapshot);
            }
            catch (Exception exception)
            {
                InstallerDiagnostics.Write(
                    "Optional shortcut state could not be restored: " +
                    (snapshot == null ? string.Empty : snapshot.Path),
                    exception);
            }
        }

        private static void RestoreRegistryKey(
            RegistryKey baseKey,
            RegistryKeySnapshot snapshot)
        {
            baseKey.DeleteSubKeyTree(snapshot.Name, false);
            if (!snapshot.Existed)
            {
                return;
            }

            using (RegistryKey key = baseKey.CreateSubKey(snapshot.Name))
            {
                if (key == null)
                {
                    throw new IOException(
                        "The previous installation registry key could not be recreated.");
                }
                RestoreRegistryTree(key, snapshot);
            }
        }

        private static void RestoreRegistryTree(
            RegistryKey key,
            RegistryKeySnapshot snapshot)
        {
            foreach (RegistryValueSnapshot value in snapshot.Values)
            {
                key.SetValue(value.Name, value.Value, value.Kind);
            }
            foreach (RegistryKeySnapshot childSnapshot in snapshot.Children)
            {
                using (RegistryKey child = key.CreateSubKey(childSnapshot.Name))
                {
                    if (child == null)
                    {
                        throw new IOException(
                            "A previous installation registry subkey could not be recreated.");
                    }
                    RestoreRegistryTree(child, childSnapshot);
                }
            }
        }

        private sealed class PayloadTransactionItem
        {
            public string ResourceName;
            public string StagePath;
            public string DestinationPath;
            public string BackupPath;
            public string ProgressText;
            public bool CopyInstaller;
            public bool Executable;
            public bool PresentMon;
            public bool DestinationExisted;
            public bool Committed;
            public bool Restored = true;
        }

        private sealed class CaptureDirectoryTransaction
        {
            public string CommonDataDirectory;
            public string ProductDirectory;
            public string CaptureDirectory;
            public bool ProductExisted;
            public bool CaptureExisted;
            public string ProductSecuritySddl;
            public string CaptureSecuritySddl;
            public bool ProductTouched;
            public bool CaptureTouched;
            public bool Restored = true;
        }

        private static void InstallPayloadsAtomically(Action<int, string> progress, Action registerInstallation)
        {
            string token = Guid.NewGuid().ToString("N");
            var items = new List<PayloadTransactionItem>
            {
                CreatePayloadItem(token, "Boost-Session", "Boostix.BoostSession.ps1", InstalledBoostSessionScript, "сценария Boost-сессии", false, false),
                CreatePayloadItem(token, "MaxFPS-Apply", "Boostix.MaxFPSApply.ps1", InstalledMaxFpsApplyScript, "профиля производительности", false, false),
                CreatePayloadItem(token, "MaxFPS-Restore", "Boostix.MaxFPSRestore.ps1", InstalledMaxFpsRestoreScript, "компонентов восстановления", false, false),
                CreatePayloadItem(token, "PresentMon-License", "Boostix.PresentMon.License.txt", InstalledPresentMonLicense, "лицензии измерителя FPS", false, false),
                CreatePayloadItem(token, "PresentMon-ThirdParty", "Boostix.PresentMon.ThirdParty.txt", InstalledPresentMonThirdParty, "уведомлений сторонних компонентов", false, false),
                CreatePayloadItem(token, "PresentMon", "Boostix.PresentMon.exe", InstalledPresentMon, "измерителя FPS", false, true),
#if LEGACY_UPDATE_BRIDGE
                CreatePayloadItem(token, "Uninstall", CanonicalSetupResourceName, UninstallerExe, "компонентов удаления", true, false),
#else
                CreatePayloadItem(token, "Uninstall", null, UninstallerExe, "компонентов удаления", true, false),
#endif
                CreatePayloadItem(token, "Boostix", "Boostix.Payload.exe", InstalledExe, "файлов программы", true, false)
            };
#if !LEGACY_UPDATE_BRIDGE
            items[6].CopyInstaller = true;
#else
            items[6].CopyInstaller = false;
#endif
            bool installationSucceeded = false;
            CaptureDirectoryTransaction captureDirectories = null;

            try
            {
                for (int index = 0; index < items.Count; index++)
                {
                    PayloadTransactionItem item = items[index];
                    ReportProgress(
                        progress,
                        10 + index * 2,
                        "Распаковка " + item.ProgressText);
                    if (item.CopyInstaller)
                    {
                        File.Copy(Application.ExecutablePath, item.StagePath, false);
                    }
                    else
                    {
                        ExtractResource(item.ResourceName, item.StagePath);
                    }
                }

                // No installed file is touched until every embedded payload exists
                // and passes its own integrity validation.
                for (int index = 0; index < items.Count; index++)
                {
                    PayloadTransactionItem item = items[index];
                    ReportProgress(
                        progress,
                        28 + index * 2,
                        "Проверка " + item.ProgressText);
                    ValidateStagedPayload(item.StagePath, item.Executable);
                    if (item.PresentMon)
                    {
                        ValidatePresentMonPayload(item.StagePath);
                    }
                }

                StopInstalledApplication();
                ReportProgress(progress, 45, "Остановка запущенной версии");
                captureDirectories = PrepareCaptureDirectoryTransaction();
                ApplyCaptureDirectoryTransaction(captureDirectories);
                ReportProgress(progress, 47, "Защита папки измерений");

                // Dependencies are published first; the main application remains
                // the final commit marker for the transaction.
                for (int index = 0; index < items.Count; index++)
                {
                    PayloadTransactionItem item = items[index];
                    string directory = Path.GetDirectoryName(item.DestinationPath);
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    ReportProgress(
                        progress,
                        48 + index * 3,
                        "Установка " + item.ProgressText);
                    CommitStagedFile(
                        item.StagePath,
                        item.DestinationPath,
                        item.BackupPath,
                        item.DestinationExisted);
                    item.Committed = true;
                }
                if (registerInstallation != null)
                {
                    registerInstallation();
                }
                installationSucceeded = true;
                ReportProgress(progress, 72, "Файлы программы обновлены");
            }
            catch
            {
                for (int index = items.Count - 1; index >= 0; index--)
                {
                    PayloadTransactionItem item = items[index];
                    if (item.Committed)
                    {
                        item.Restored = RestoreCommittedFile(
                            item.DestinationPath,
                            item.BackupPath,
                            item.DestinationExisted);
                    }
                }
                if (captureDirectories != null)
                {
                    captureDirectories.Restored =
                        RollbackCaptureDirectoryTransaction(captureDirectories);
                }
                throw;
            }
            finally
            {
                foreach (PayloadTransactionItem item in items)
                {
                    TryDeleteIfExists(item.StagePath);
                    if (installationSucceeded || !item.Committed || item.Restored)
                    {
                        TryDeleteIfExists(item.BackupPath);
                    }
                }
                if (!installationSucceeded)
                {
                    TryDeleteEmptyDirectory(PresentMonDirectory);
                    TryDeleteEmptyDirectory(Path.GetDirectoryName(PresentMonDirectory));
                }
            }
        }

        private static CaptureDirectoryTransaction PrepareCaptureDirectoryTransaction()
        {
            string commonDataDirectory;
            string productDirectory;
            string captureDirectory;
            ResolveProtectedCapturePaths(
                out commonDataDirectory,
                out productDirectory,
                out captureDirectory);

            ValidateCaptureDirectory(commonDataDirectory, true, "ProgramData");
            ValidateCaptureDirectory(productDirectory, false, ProductBrand.DataDirectoryName);
            ValidateCaptureDirectory(captureDirectory, false, "Captures");

            bool productExisted = Directory.Exists(productDirectory);
            bool captureExisted = Directory.Exists(captureDirectory);
            if (!productExisted && captureExisted)
            {
                throw new IOException(
                    "The capture directory exists without its protected product parent.");
            }

            return new CaptureDirectoryTransaction
            {
                CommonDataDirectory = commonDataDirectory,
                ProductDirectory = productDirectory,
                CaptureDirectory = captureDirectory,
                ProductExisted = productExisted,
                CaptureExisted = captureExisted,
                ProductSecuritySddl = productExisted
                    ? CaptureDirectorySecuritySddl(productDirectory)
                    : null,
                CaptureSecuritySddl = captureExisted
                    ? CaptureDirectorySecuritySddl(captureDirectory)
                    : null
            };
        }

        private static void ApplyCaptureDirectoryTransaction(
            CaptureDirectoryTransaction transaction)
        {
            if (transaction == null)
            {
                throw new ArgumentNullException("transaction");
            }

            EnsureCaptureDirectoryState(
                transaction.ProductDirectory,
                transaction.ProductExisted,
                ProductBrand.DataDirectoryName);
            transaction.ProductTouched = true;
            ApplySecureCaptureDirectory(
                transaction.ProductDirectory,
                transaction.ProductExisted,
                false);

            // The protected parent is tightened before the child is touched, so
            // an unelevated user cannot swap the Captures directory underneath
            // the elevated installer.
            EnsureCaptureDirectoryState(
                transaction.CaptureDirectory,
                transaction.CaptureExisted,
                "Captures");
            transaction.CaptureTouched = true;
            ApplySecureCaptureDirectory(
                transaction.CaptureDirectory,
                transaction.CaptureExisted,
                true);
        }

        private static DirectorySecurity CreateCaptureDirectorySecurity(
            bool allowInheritedFileCleanup)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);

            var administrators = new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                null);
            var system = new SecurityIdentifier(
                WellKnownSidType.LocalSystemSid,
                null);
            var authenticatedUsers = new SecurityIdentifier(
                WellKnownSidType.AuthenticatedUserSid,
                null);
            security.SetOwner(administrators);
            security.SetGroup(administrators);

            const InheritanceFlags inheritance =
                InheritanceFlags.ContainerInherit |
                InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(
                system,
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                administrators,
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                authenticatedUsers,
                FileSystemRights.ReadAndExecute,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            if (allowInheritedFileCleanup)
            {
                // Standard users still cannot create or modify entries in the
                // Captures directory. This object-only inherited right lets the
                // originating user delete the admin-created CSV after copying it
                // down from an over-the-shoulder UAC capture.
                security.AddAccessRule(new FileSystemAccessRule(
                    authenticatedUsers,
                    FileSystemRights.Delete,
                    InheritanceFlags.ObjectInherit,
                    PropagationFlags.InheritOnly,
                    AccessControlType.Allow));
            }
            return security;
        }

        private static void ApplySecureCaptureDirectory(
            string path,
            bool existed,
            bool allowInheritedFileCleanup)
        {
            DirectorySecurity security = CreateCaptureDirectorySecurity(
                allowInheritedFileCleanup);
            if (!existed)
            {
                Directory.CreateDirectory(path, security);
            }
            ValidateCaptureDirectory(path, true, Path.GetFileName(path));
            Directory.SetAccessControl(path, security);
            ValidateCaptureDirectory(path, true, Path.GetFileName(path));
        }

        private static bool RollbackCaptureDirectoryTransaction(
            CaptureDirectoryTransaction transaction)
        {
            bool captureRestored = true;
            if (transaction.CaptureTouched && !transaction.CaptureExisted)
            {
                captureRestored =
                    TryDeleteCreatedCaptureDirectory(transaction.CaptureDirectory);
            }

            bool productRestored = true;
            if (transaction.ProductTouched)
            {
                productRestored = transaction.ProductExisted
                    ? TryRestoreCaptureDirectorySecurity(
                        transaction.ProductDirectory,
                        transaction.ProductSecuritySddl)
                    : TryDeleteCreatedCaptureDirectory(transaction.ProductDirectory);
            }

            // Restore the child only after the original parent ACL is back.
            // If restoring the parent failed, keeping the child protected is
            // safer than restoring a possibly user-writable previous child ACL.
            if (transaction.CaptureTouched && transaction.CaptureExisted)
            {
                captureRestored = productRestored &&
                    TryRestoreCaptureDirectorySecurity(
                        transaction.CaptureDirectory,
                        transaction.CaptureSecuritySddl);
            }
            return productRestored && captureRestored;
        }

        private static bool TryRestoreCaptureDirectorySecurity(
            string path,
            string securitySddl)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(securitySddl))
                {
                    return false;
                }
                ValidateCaptureDirectory(path, true, Path.GetFileName(path));
                var security = new DirectorySecurity();
                security.SetSecurityDescriptorSddlForm(
                    securitySddl,
                    CaptureSecuritySections);
                Directory.SetAccessControl(path, security);
                ValidateCaptureDirectory(path, true, Path.GetFileName(path));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryDeleteCreatedCaptureDirectory(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return !File.Exists(path);
                }
                ValidateCaptureDirectory(path, true, Path.GetFileName(path));
                if (Directory.GetFileSystemEntries(path).Length != 0)
                {
                    return false;
                }
                Directory.Delete(path, false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string CaptureDirectorySecuritySddl(string path)
        {
            DirectorySecurity security = Directory.GetAccessControl(
                path,
                CaptureSecuritySections);
            return security.GetSecurityDescriptorSddlForm(CaptureSecuritySections);
        }

        private static void EnsureCaptureDirectoryState(
            string path,
            bool expectedToExist,
            string name)
        {
            bool exists = Directory.Exists(path);
            if (exists != expectedToExist ||
                (!exists && File.Exists(path)))
            {
                throw new IOException(
                    "The protected " + name +
                    " directory changed during installation.");
            }
            ValidateCaptureDirectory(path, expectedToExist, name);
        }

        private static void ResolveProtectedCapturePaths(
            out string commonDataDirectory,
            out string productDirectory,
            out string captureDirectory)
        {
            string commonData = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(commonData))
            {
                throw new DirectoryNotFoundException(
                    "The system ProgramData directory is unavailable.");
            }

            commonDataDirectory = Path.GetFullPath(commonData);
            productDirectory = Path.GetFullPath(Path.Combine(
                commonDataDirectory,
                ProductBrand.DataDirectoryName));
            captureDirectory = Path.GetFullPath(Path.Combine(
                productDirectory,
                "Captures"));
            if (!IsStrictChildPath(commonDataDirectory, productDirectory) ||
                !IsStrictChildPath(productDirectory, captureDirectory))
            {
                throw new IOException(
                    "The protected capture directory resolved outside ProgramData.");
            }
        }

        private static bool IsStrictChildPath(string parentPath, string childPath)
        {
            string prefix = parentPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return childPath.Length > prefix.Length &&
                   childPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateCaptureDirectory(
            string path,
            bool required,
            string name)
        {
            if (!Directory.Exists(path))
            {
                if (File.Exists(path))
                {
                    throw new IOException(
                        "A file occupies the protected " + name + " directory path.");
                }
                if (required)
                {
                    throw new DirectoryNotFoundException(
                        "The protected " + name + " directory is unavailable.");
                }
                return;
            }

            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "The protected " + name + " directory cannot be a reparse point.");
            }
        }

        private static void TryPruneProtectedCaptureFiles(bool removeDirectories)
        {
            try
            {
                string commonDataDirectory;
                string productDirectory;
                string captureDirectory;
                ResolveProtectedCapturePaths(
                    out commonDataDirectory,
                    out productDirectory,
                    out captureDirectory);
                ValidateCaptureDirectory(commonDataDirectory, true, "ProgramData");
                ValidateCaptureDirectory(productDirectory, false, ProductBrand.DataDirectoryName);
                ValidateCaptureDirectory(captureDirectory, false, "Captures");
                if (!Directory.Exists(captureDirectory))
                {
                    return;
                }

                string capturePrefix = captureDirectory.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                foreach (string candidate in Directory.GetFiles(
                    captureDirectory,
                    "Boostix-PresentMon-*.csv",
                    SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        string fullPath = Path.GetFullPath(candidate);
                        string fileName = Path.GetFileName(fullPath);
                        if (!fullPath.StartsWith(
                                capturePrefix,
                                StringComparison.OrdinalIgnoreCase) ||
                            !Regex.IsMatch(
                                fileName,
                                @"^Boostix-PresentMon-[0-9a-f]{32}\.csv$",
                                RegexOptions.IgnoreCase |
                                RegexOptions.CultureInvariant) ||
                            (File.GetAttributes(fullPath) &
                             FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }
                        File.Delete(fullPath);
                    }
                    catch
                    {
                        // Continue pruning other exact capture artifacts.
                    }
                }

                if (removeDirectories)
                {
                    TryDeleteCreatedCaptureDirectory(captureDirectory);
                    TryDeleteCreatedCaptureDirectory(productDirectory);
                }
            }
            catch
            {
                // Capture staging is temporary; an unsafe or busy path is left
                // untouched rather than making uninstall destructive.
            }
        }

        private static PayloadTransactionItem CreatePayloadItem(
            string token,
            string stageName,
            string resourceName,
            string destination,
            string progressText,
            bool executable,
            bool presentMon)
        {
            return new PayloadTransactionItem
            {
                ResourceName = resourceName,
                StagePath = Path.Combine(
                    InstallDirectory,
                    "." + stageName + "-" + token + ".stage"),
                DestinationPath = destination,
                BackupPath = Path.Combine(
                    InstallDirectory,
                    "." + stageName + "-" + token + ".backup"),
                ProgressText = progressText,
                Executable = executable,
                PresentMon = presentMon,
                DestinationExisted = File.Exists(destination)
            };
        }

        private static void EnsureInstallIsNotDowngrade()
        {
            foreach (string candidate in new[]
            {
                InstalledExe,
                LegacyInstalledExe
            })
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                ValidateFileNotReparse(candidate);
                FileVersionInfo installedInfo = FileVersionInfo.GetVersionInfo(
                    candidate);
                bool recognizedProduct =
                    string.Equals(
                        installedInfo.ProductName,
                        ProductName,
                        StringComparison.Ordinal) ||
                    string.Equals(
                        installedInfo.ProductName,
                        ProductBrand.LegacyInstallDirectoryName,
                        StringComparison.Ordinal);
                if (!recognizedProduct)
                {
                    InstallerDiagnostics.Write(
                        "An installed executable has no recognized Boostix identity; " +
                        "checking the next compatible installation before repair.");
                    continue;
                }
                if (IsDowngrade(
                        installedInfo.FileVersion,
                        ProductVersion + ".0"))
                {
                    Version installedVersion = Version.Parse(
                        installedInfo.FileVersion.Trim());
                    throw new InvalidOperationException(
                        "На компьютере уже установлена более новая версия Boostix (" +
                        installedVersion.ToString(3) + "). Установка более старой версии отменена.");
                }
                // Boostix is ordered first. Once a recognized active installation
                // is found, a stale legacy directory must not override its version.
                return;
            }

            InstallerDiagnostics.Write(
                "No installed executable has a recognized Boostix identity; " +
                "downgrade comparison is skipped for transactional repair.");
        }

        private static bool IsDowngrade(string installedVersionText, string setupVersionText)
        {
            Version installedVersion;
            Version setupVersion;
            return Version.TryParse((installedVersionText ?? string.Empty).Trim(), out installedVersion) &&
                Version.TryParse((setupVersionText ?? string.Empty).Trim(), out setupVersion) &&
                installedVersion > setupVersion;
        }

        private static void ValidateStagedPayload(string path, bool executable)
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0)
            {
                throw new InvalidDataException("Встроенные файлы установщика повреждены.");
            }

            if (!executable)
            {
                return;
            }

            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length < 2 || input.ReadByte() != 'M' || input.ReadByte() != 'Z')
                {
                    throw new InvalidDataException("Встроенный файл программы повреждён.");
                }
            }

            FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(path);
            Version payloadVersion;
            Version expectedVersion;
            if (!string.Equals(versionInfo.ProductName, ProductName, StringComparison.Ordinal) ||
                !Version.TryParse((versionInfo.FileVersion ?? string.Empty).Trim(), out payloadVersion) ||
                !Version.TryParse(ProductVersion + ".0", out expectedVersion) ||
                payloadVersion != expectedVersion)
            {
                throw new InvalidDataException("Встроенный исполняемый файл имеет неверную версию или имя продукта.");
            }
        }

        private static void ValidatePresentMonPayload(string path)
        {
            const long expectedLength = 956768;
            const string expectedSha256 =
                "9bec3083069f58f911e6a512f4806db51a27bd096103087bc1d05ef54c80a191";
            var file = new FileInfo(path);
            if (!file.Exists || file.Length != expectedLength)
            {
                throw new InvalidDataException("Встроенный измеритель FPS имеет неверный размер.");
            }

            string actualHash;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.ReadByte() != 'M' || input.ReadByte() != 'Z')
                {
                    throw new InvalidDataException("Встроенный измеритель FPS повреждён.");
                }
                input.Position = 0;
                using (SHA256 sha256 = SHA256.Create())
                {
                    actualHash = BitConverter.ToString(sha256.ComputeHash(input))
                        .Replace("-", string.Empty)
                        .ToLowerInvariant();
                }
            }
            if (!string.Equals(actualHash, expectedSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Не совпадает контрольная сумма измерителя FPS.");
            }
        }

        private static void ExecuteFileMutationWithRetry(
            Action mutation,
            string operation)
        {
            if (mutation == null)
            {
                throw new ArgumentNullException("mutation");
            }
            Exception lastFailure = null;
            for (int attempt = 1; attempt <= 7; attempt++)
            {
                try
                {
                    mutation();
                    if (attempt > 1)
                    {
                        InstallerDiagnostics.Write(
                            "File operation succeeded after retry: " + operation + ".");
                    }
                    return;
                }
                catch (IOException exception)
                {
                    lastFailure = exception;
                }
                catch (UnauthorizedAccessException exception)
                {
                    lastFailure = exception;
                }

                if (attempt < 7)
                {
                    InstallerDiagnostics.Write(
                        "Transient file operation failure; retrying " + operation + ".",
                        lastFailure);
                    System.Threading.Thread.Sleep(
                        Math.Min(2000, 125 * (1 << (attempt - 1))));
                }
            }
            throw new IOException(
                "Windows не смогла завершить операцию с файлами после нескольких попыток: " +
                operation + ".",
                lastFailure);
        }

        private static void CommitStagedFile(string stage, string destination, string backup, bool destinationExists)
        {
            if (destinationExists)
            {
                ExecuteFileMutationWithRetry(
                    delegate { File.Replace(stage, destination, backup, true); },
                    "replace installed payload");
            }
            else
            {
                ExecuteFileMutationWithRetry(
                    delegate { File.Move(stage, destination); },
                    "publish installed payload");
            }
        }

        private static void ReplaceFileWithoutRetainedBackup(string source, string destination)
        {
            string discardBackup = destination + ".replace-backup-" + Guid.NewGuid().ToString("N");
            try
            {
                ExecuteFileMutationWithRetry(
                    delegate { File.Replace(source, destination, discardBackup, true); },
                    "restore installed payload");
            }
            finally
            {
                try
                {
                    DeleteIfExists(discardBackup);
                }
                catch
                {
                    // A disposable copy of the failed destination must not make
                    // restoring the known-good installation report failure.
                }
            }
        }

        private static bool RestoreCommittedFile(string destination, string backup, bool destinationExisted)
        {
            try
            {
                if (destinationExisted && File.Exists(backup))
                {
                    if (File.Exists(destination))
                    {
                        ReplaceFileWithoutRetainedBackup(backup, destination);
                    }
                    else
                    {
                        ExecuteFileMutationWithRetry(
                            delegate { File.Move(backup, destination); },
                            "restore missing installed payload");
                    }
                }
                else if (!destinationExisted)
                {
                    DeleteIfExists(destination);
                }
                else
                {
                    return false;
                }
                return true;
            }
            catch
            {
                // Keep the original installation error; backup remains for diagnostics.
                return false;
            }
        }

        private static void ExtractResource(string resourceName, string destination)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream input = assembly.GetManifestResourceStream(resourceName))
            {
                if (input == null)
                {
                    throw new InvalidOperationException("В установщике отсутствует ресурс: " + resourceName);
                }
                using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                }
            }
        }

        private static void StopInstalledApplication()
        {
            StopInstalledApplicationByName("Boostix", InstalledExe);
            StopInstalledApplicationByName("MajesticBoost", LegacyInstalledExe);
        }

        private static void StopInstalledApplicationByName(
            string processName,
            string expectedPath)
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                try
                {
                    string runningPath = process.MainModule.FileName;
                    if (string.Equals(runningPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!process.CloseMainWindow() || !process.WaitForExit(1200))
                        {
                            process.Kill();
                        }
                        if (!process.WaitForExit(3000))
                        {
                            throw new InvalidOperationException("Закройте запущенный Boostix и повторите установку.");
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                    throw;
                }
                catch
                {
                    // An unrelated inaccessible process with the same name is ignored.
                }
                finally { process.Dispose(); }
            }
        }

        public static void LaunchInstalledApplication()
        {
            LaunchApplication(InstalledExe);
        }

        private static void LaunchPreviousInstalledApplication(
            UpdateRollbackState state)
        {
            string executable = state != null &&
                state.PreviousInstallation == PreviousInstallationKind.Legacy
                ? LegacyInstalledExe
                : InstalledExe;
            LaunchApplication(executable);
        }

        private static void LaunchApplication(string executable)
        {
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var startInfo = new ProcessStartInfo();
            startInfo.FileName = explorer;
            startInfo.Arguments = Quote(executable);
            startInfo.UseShellExecute = true;
            Process.Start(startInfo);
        }

        private static bool TryCreateShortcut(
            string shortcutPath,
            string targetPath,
            string workingDirectory,
            string description)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(shortcutPath) ||
                    !Path.IsPathRooted(shortcutPath))
                {
                    throw new InvalidOperationException(
                        "The shortcut path is unavailable.");
                }
                string directory = Path.GetDirectoryName(shortcutPath);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    throw new InvalidOperationException(
                        "The shortcut directory is unavailable.");
                }
                if (!Directory.Exists(directory))
                {
                    string parent = Path.GetDirectoryName(directory);
                    if (File.Exists(directory) ||
                        string.IsNullOrWhiteSpace(parent) ||
                        !Directory.Exists(parent) ||
                        !IsDirectChildPath(parent, directory) ||
                        !IsPathFreeOfReparsePoints(parent))
                    {
                        throw new IOException(
                            "The shortcut directory is unavailable or redirected.");
                    }
                    Directory.CreateDirectory(directory);
                }
                if (!IsPathFreeOfReparsePoints(directory))
                {
                    throw new IOException(
                        "The shortcut directory is redirected.");
                }
                CreateShortcut(
                    shortcutPath,
                    targetPath,
                    workingDirectory,
                    description);
                return true;
            }
            catch (Exception exception)
            {
                InstallerDiagnostics.Write(
                    "Optional shortcut could not be created: " + shortcutPath,
                    exception);
                return false;
            }
        }

        private static void TryDeleteShortcut(string shortcutPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(shortcutPath) ||
                    !Path.IsPathRooted(shortcutPath))
                {
                    throw new InvalidOperationException(
                        "The shortcut path is unavailable.");
                }
                string directory = Path.GetDirectoryName(shortcutPath);
                if (string.IsNullOrWhiteSpace(directory) ||
                    !Directory.Exists(directory) ||
                    !IsPathFreeOfReparsePoints(directory))
                {
                    throw new IOException(
                        "The shortcut directory is unavailable or redirected.");
                }
                if (Directory.Exists(shortcutPath))
                {
                    throw new IOException(
                        "A directory occupies the shortcut path.");
                }
                if (File.Exists(shortcutPath))
                {
                    ValidateFileNotReparse(shortcutPath);
                    File.Delete(shortcutPath);
                }
            }
            catch (Exception exception)
            {
                InstallerDiagnostics.Write(
                    "Optional shortcut could not be removed: " + shortcutPath,
                    exception);
            }
        }

        private static void TryDeleteOptionalShortcutDirectory(
            string boundaryRoot,
            string targetPath,
            string[] allowedLeafNames)
        {
            try
            {
                DeleteAllowlistedDirectoryTree(
                    boundaryRoot,
                    targetPath,
                    allowedLeafNames,
                    null);
            }
            catch (Exception exception)
            {
                InstallerDiagnostics.Write(
                    "Optional Start Menu shortcut directory could not be removed: " +
                    (targetPath ?? string.Empty),
                    exception);
            }
        }

        private static void CreateShortcut(
            string shortcutPath,
            string targetPath,
            string workingDirectory,
            string description)
        {
            object shellLinkObject = new ShellLink();
            try
            {
                IShellLinkW shellLink = (IShellLinkW)shellLinkObject;
                shellLink.SetPath(targetPath);
                shellLink.SetWorkingDirectory(workingDirectory);
                shellLink.SetDescription(description);
                shellLink.SetIconLocation(targetPath, 0);
                var persistFile = (System.Runtime.InteropServices.ComTypes.IPersistFile)
                    shellLinkObject;
                persistFile.Save(shortcutPath, true);
            }
            finally
            {
                if (shellLinkObject != null && Marshal.IsComObject(shellLinkObject))
                {
                    Marshal.FinalReleaseComObject(shellLinkObject);
                }
            }
        }

        private static int CalculateEstimatedSizeKb()
        {
            long total = 0;
            foreach (string file in new[]
            {
                InstalledExe,
                InstalledBoostSessionScript,
                InstalledMaxFpsApplyScript,
                InstalledMaxFpsRestoreScript,
                InstalledPresentMon,
                InstalledPresentMonLicense,
                InstalledPresentMonThirdParty,
                UninstallerExe
            })
            {
                if (File.Exists(file))
                {
                    total += new FileInfo(file).Length;
                }
            }
            return (int)Math.Max(1, total / 1024);
        }

        private static void EnsureSufficientDiskSpace()
        {
            string root = Path.GetPathRoot(InstallDirectory);
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new IOException(
                    "Не удалось определить диск для установки Boostix.");
            }
            var drive = new DriveInfo(root);
            const long safetyReserve = 128L * 1024L * 1024L;
            long existingBytes = Math.Max(
                0L,
                (long)CalculateEstimatedSizeKb() * 1024L);
            long required = Math.Max(
                safetyReserve,
                existingBytes * 3L + (32L * 1024L * 1024L));
            if (!drive.IsReady || drive.AvailableFreeSpace < required)
            {
                throw new IOException(
                    "Недостаточно свободного места для безопасной установки и отката. " +
                    "Освободите не менее " +
                    ((required + 1048575L) / 1048576L).ToString(
                        CultureInfo.InvariantCulture) +
                    " МБ и повторите попытку.");
            }
            InstallerDiagnostics.Write(
                "Disk preflight passed. Free=" +
                drive.AvailableFreeSpace.ToString(CultureInfo.InvariantCulture) +
                "; Required=" + required.ToString(CultureInfo.InvariantCulture) + ".");
        }

        private static void ReportProgress(Action<int, string> progress, int percent, string stage)
        {
            InstallerDiagnostics.Write(
                "Stage " + Math.Max(0, Math.Min(100, percent)).ToString(
                    CultureInfo.InvariantCulture) + "%: " + (stage ?? string.Empty));
            if (progress == null)
            {
                return;
            }

            try
            {
                progress(Math.Max(0, Math.Min(100, percent)), stage ?? string.Empty);
            }
            catch
            {
                // A closed or unavailable progress surface must not corrupt installation.
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value + "\"";
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static void TryDeleteIfExists(string path)
        {
            try
            {
                DeleteIfExists(path);
            }
            catch
            {
                // Cleanup must not turn a successful commit or the original
                // installation error into a different failure.
            }
        }

        private static void TryDeleteEmptyDirectory(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) &&
                    Directory.Exists(path) &&
                    IsPathFreeOfReparsePoints(path) &&
                    Directory.GetFileSystemEntries(path).Length == 0)
                {
                    Directory.Delete(path, false);
                }
            }
            catch
            {
                // A harmless empty directory can be removed by a later install.
            }
        }
    }

    internal static class BoostixFontProvider
    {
        public static Font Create(float size, FontStyle style)
        {
            try
            {
                return new Font(
                    "Segoe UI Variable Text",
                    size,
                    style,
                    GraphicsUnit.Point);
            }
            catch
            {
                return new Font("Segoe UI", size, style, GraphicsUnit.Point);
            }
        }
    }

    internal static class BoostixDrawing
    {
        private const uint SpiGetClientAreaAnimation = 0x1042;

        [DllImport("user32.dll", SetLastError = false)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(
            uint action,
            uint parameter,
            [MarshalAs(UnmanagedType.Bool)] ref bool value,
            uint updateFlags);

        public static bool ClientAreaAnimation()
        {
            bool enabled = true;
            try
            {
                return SystemParametersInfo(
                    SpiGetClientAreaAnimation,
                    0,
                    ref enabled,
                    0)
                    ? enabled
                    : true;
            }
            catch (DllNotFoundException)
            {
                return true;
            }
            catch (EntryPointNotFoundException)
            {
                return true;
            }
        }

        public static float DpiScale(Graphics graphics)
        {
            if (graphics == null ||
                float.IsNaN(graphics.DpiX) ||
                float.IsInfinity(graphics.DpiX) ||
                graphics.DpiX < 48F ||
                graphics.DpiX > 768F)
            {
                return 1F;
            }
            return graphics.DpiX / 96F;
        }

        public static float ScaleForDpi(float value, int dpi)
        {
            int normalized = dpi >= 48 && dpi <= 768 ? dpi : 96;
            return value * normalized / 96F;
        }

        public static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
        {
            radius = Math.Max(
                0.5F,
                Math.Min(radius, Math.Min(rectangle.Width, rectangle.Height) * 0.5F));
            float diameter = radius * 2F;
            var path = new GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180F, 90F);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270F, 90F);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0F, 90F);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90F, 90F);
            path.CloseFigure();
            return path;
        }

        public static Color Interpolate(Color from, Color to, float amount)
        {
            amount = Math.Max(0F, Math.Min(1F, amount));
            return Color.FromArgb(
                (int)Math.Round(from.A + ((to.A - from.A) * amount)),
                (int)Math.Round(from.R + ((to.R - from.R) * amount)),
                (int)Math.Round(from.G + ((to.G - from.G) * amount)),
                (int)Math.Round(from.B + ((to.B - from.B) * amount)));
        }

        public static float CssEase(float progress)
        {
            progress = Math.Max(0F, Math.Min(1F, progress));
            float low = 0F;
            float high = 1F;
            float parameter = progress;
            for (int index = 0; index < 10; index++)
            {
                parameter = (low + high) * 0.5F;
                float x = CubicBezier(parameter, 0.25F, 0.25F);
                if (x < progress)
                {
                    low = parameter;
                }
                else
                {
                    high = parameter;
                }
            }
            return CubicBezier(parameter, 0.10F, 1F);
        }

        private static float CubicBezier(float parameter, float firstControl, float secondControl)
        {
            float inverse = 1F - parameter;
            return (3F * inverse * inverse * parameter * firstControl)
                + (3F * inverse * parameter * parameter * secondControl)
                + (parameter * parameter * parameter);
        }
    }

    internal abstract class AnimatedButtonBase : Button
    {
        private readonly Timer animationTimer;
        private Color currentFill;
        private Color currentGlyph;
        private Color startFill;
        private Color startGlyph;
        private Color targetFill;
        private Color targetGlyph;
        private long animationStart;
        private int animationDuration;
        private bool pressed;

        protected AnimatedButtonBase()
        {
            SetStyle(
                ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw,
                true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            TabStop = true;

            animationTimer = new Timer();
            animationTimer.Interval = 15;
            animationTimer.Tick += AnimationTick;
        }

        protected abstract Color IdleFill { get; }
        protected abstract Color HoverFill { get; }
        protected abstract Color PressedFill { get; }
        protected abstract Color IdleGlyph { get; }
        protected abstract Color HoverGlyph { get; }
        protected abstract Color PressedGlyph { get; }
        protected abstract float CornerRadius { get; }

        protected void InitializeVisualState()
        {
            currentFill = IdleFill;
            currentGlyph = IdleGlyph;
            startFill = currentFill;
            startGlyph = currentGlyph;
            targetFill = currentFill;
            targetGlyph = currentGlyph;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (Enabled)
            {
                BeginTransition(HoverFill, HoverGlyph, 200);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            pressed = false;
            BeginTransition(IdleFill, IdleGlyph, 200);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Enabled && e.Button == MouseButtons.Left)
            {
                pressed = true;
                BeginTransition(PressedFill, PressedGlyph, 90);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            pressed = false;
            if (Enabled && ClientRectangle.Contains(e.Location))
            {
                BeginTransition(HoverFill, HoverGlyph, 160);
            }
            else
            {
                BeginTransition(IdleFill, IdleGlyph, 160);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Enabled && !pressed && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter))
            {
                pressed = true;
                BeginTransition(PressedFill, PressedGlyph, 90);
            }
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (pressed && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter))
            {
                pressed = false;
                bool pointerInside = ClientRectangle.Contains(PointToClient(Cursor.Position));
                BeginTransition(
                    pointerInside ? HoverFill : IdleFill,
                    pointerInside ? HoverGlyph : IdleGlyph,
                    160);
            }
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            if (pressed)
            {
                pressed = false;
                BeginTransition(IdleFill, IdleGlyph, 160);
            }
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            pressed = false;
            bool pointerInside = Enabled && ClientRectangle.Contains(PointToClient(Cursor.Position));
            BeginTransition(
                pointerInside ? HoverFill : IdleFill,
                pointerInside ? HoverGlyph : IdleGlyph,
                160);
            Invalidate();
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color parentColor = Parent == null ? Color.FromArgb(22, 22, 22) : Parent.BackColor;
            using (var backgroundBrush = new SolidBrush(parentColor))
            {
                e.Graphics.FillRectangle(backgroundBrush, ClientRectangle);
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float dpiScale = BoostixDrawing.DpiScale(e.Graphics);
            float edgeInset = Math.Max(1F, dpiScale);
            RectangleF buttonBounds = new RectangleF(
                0F,
                0F,
                Math.Max(1F, Width - edgeInset),
                Math.Max(1F, Height - edgeInset));
            Color fill = currentFill;
            Color glyph = currentGlyph;
            if (!Enabled)
            {
                fill = BoostixDrawing.Interpolate(fill, parentColor, 0.45F);
                glyph = Color.FromArgb(100, 100, 100);
            }
            if (SystemInformation.HighContrast)
            {
                fill = Enabled && (pressed || ClientRectangle.Contains(PointToClient(Cursor.Position)))
                    ? SystemColors.Highlight
                    : SystemColors.ControlDark;
                glyph = Enabled ? SystemColors.HighlightText : SystemColors.GrayText;
            }

            using (GraphicsPath path = BoostixDrawing.RoundedRectangle(
                buttonBounds,
                CornerRadius * dpiScale))
            using (var brush = new SolidBrush(fill))
            {
                e.Graphics.FillPath(brush, path);
            }

            DrawContent(e.Graphics, Rectangle.Round(buttonBounds), glyph);

            if (Focused && ShowFocusCues && Enabled)
            {
                float focusInset = 2.5F * dpiScale;
                RectangleF focusBounds = RectangleF.Inflate(
                    buttonBounds,
                    -focusInset,
                    -focusInset);
                Color focusColor = SystemInformation.HighContrast
                    ? SystemColors.HighlightText
                    : Color.FromArgb(
                        ProductBrand.AccentTextRed,
                        ProductBrand.AccentTextGreen,
                        ProductBrand.AccentTextBlue);
                using (GraphicsPath focusPath = BoostixDrawing.RoundedRectangle(
                    focusBounds,
                    Math.Max(1F, (CornerRadius - 2F) * dpiScale)))
                using (var focusPen = new Pen(focusColor, Math.Max(1F, dpiScale)))
                {
                    focusPen.DashStyle = DashStyle.Dot;
                    e.Graphics.DrawPath(focusPen, focusPath);
                }
            }
        }

        protected abstract void DrawContent(Graphics graphics, Rectangle bounds, Color glyphColor);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                animationTimer.Stop();
                animationTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        private void BeginTransition(Color fill, Color glyph, int duration)
        {
            if (!BoostixDrawing.ClientAreaAnimation())
            {
                animationTimer.Stop();
                currentFill = fill;
                currentGlyph = glyph;
                targetFill = fill;
                targetGlyph = glyph;
                Invalidate();
                return;
            }
            startFill = currentFill;
            startGlyph = currentGlyph;
            targetFill = fill;
            targetGlyph = glyph;
            animationStart = Stopwatch.GetTimestamp();
            animationDuration = Math.Max(1, duration);
            animationTimer.Start();
            Invalidate();
        }

        private void AnimationTick(object sender, EventArgs e)
        {
            double elapsed = (Stopwatch.GetTimestamp() - animationStart) * 1000D / Stopwatch.Frequency;
            float progress = (float)Math.Min(1D, elapsed / animationDuration);
            float eased = BoostixDrawing.CssEase(progress);
            currentFill = BoostixDrawing.Interpolate(startFill, targetFill, eased);
            currentGlyph = BoostixDrawing.Interpolate(startGlyph, targetGlyph, eased);
            Invalidate();
            if (progress >= 1F)
            {
                animationTimer.Stop();
                currentFill = targetFill;
                currentGlyph = targetGlyph;
            }
        }
    }

    internal sealed class BoostixActionButton : AnimatedButtonBase
    {
        public BoostixActionButton()
        {
            InitializeVisualState();
        }

        protected override Color IdleFill { get { return Color.FromArgb(37, 37, 37); } }
        protected override Color HoverFill
        {
            get
            {
                return Color.FromArgb(
                    ProductBrand.AccentRed,
                    ProductBrand.AccentGreen,
                    ProductBrand.AccentBlue);
            }
        }
        protected override Color PressedFill { get { return Color.FromArgb(91, 33, 182); } }
        protected override Color IdleGlyph { get { return Color.White; } }
        protected override Color HoverGlyph { get { return Color.White; } }
        protected override Color PressedGlyph { get { return Color.White; } }
        protected override float CornerRadius { get { return 8F; } }

        protected override void DrawContent(Graphics graphics, Rectangle bounds, Color glyphColor)
        {
            TextRenderer.DrawText(
                graphics,
                Text,
                Font,
                bounds,
                glyphColor,
                TextFormatFlags.HorizontalCenter
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine
                | TextFormatFlags.NoPadding);
        }
    }

    internal sealed class BoostixCloseButton : AnimatedButtonBase
    {
        public BoostixCloseButton()
        {
            InitializeVisualState();
        }

        protected override Color IdleFill { get { return Color.FromArgb(0, 231, 24, 42); } }
        protected override Color HoverFill { get { return Color.FromArgb(231, 24, 42); } }
        protected override Color PressedFill { get { return Color.FromArgb(197, 20, 35); } }
        protected override Color IdleGlyph { get { return Color.FromArgb(128, 255, 255, 255); } }
        protected override Color HoverGlyph { get { return Color.White; } }
        protected override Color PressedGlyph { get { return Color.White; } }
        protected override float CornerRadius { get { return 6F; } }

        protected override void ScaleControl(
            SizeF factor,
            BoundsSpecified specified)
        {
            base.ScaleControl(factor, specified);
            if (Parent != null)
            {
                Location = new Point(
                    Math.Max(0, Parent.ClientSize.Width - Width),
                    0);
            }
        }

        protected override void DrawContent(Graphics graphics, Rectangle bounds, Color glyphColor)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float dpiScale = BoostixDrawing.DpiScale(graphics);
            float centerX = bounds.Left + bounds.Width * 0.5F;
            float centerY = bounds.Top + bounds.Height * 0.5F;
            float halfSpan = 6F * dpiScale;
            using (var pen = new Pen(glyphColor, 1.6F * dpiScale))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                graphics.DrawLine(
                    pen,
                    centerX - halfSpan,
                    centerY - halfSpan,
                    centerX + halfSpan,
                    centerY + halfSpan);
                graphics.DrawLine(
                    pen,
                    centerX + halfSpan,
                    centerY - halfSpan,
                    centerX - halfSpan,
                    centerY + halfSpan);
            }
        }
    }

    internal sealed class BoostixToggle : CheckBox
    {
        private static readonly Color OffColor = Color.FromArgb(37, 37, 37);
        private static readonly Color OffHoverColor = Color.FromArgb(52, 52, 52);
        private static readonly Color OnColor = Color.FromArgb(
            ProductBrand.AccentRed,
            ProductBrand.AccentGreen,
            ProductBrand.AccentBlue);
        private readonly Timer animationTimer;
        private float thumbPosition;
        private float startThumbPosition;
        private float targetThumbPosition;
        private Color currentTrackColor;
        private Color startTrackColor;
        private Color targetTrackColor;
        private long animationStart;
        private bool pointerInside;

        public BoostixToggle()
        {
            SetStyle(
                ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor,
                true);
            AutoSize = false;
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = true;
            currentTrackColor = OffColor;
            targetTrackColor = OffColor;

            animationTimer = new Timer();
            animationTimer.Interval = 15;
            animationTimer.Tick += AnimationTick;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            thumbPosition = Checked ? 1F : 0F;
            startThumbPosition = thumbPosition;
            targetThumbPosition = thumbPosition;
            currentTrackColor = TargetTrackColor();
            startTrackColor = currentTrackColor;
            targetTrackColor = currentTrackColor;
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            if (!IsHandleCreated)
            {
                thumbPosition = Checked ? 1F : 0F;
                currentTrackColor = Checked ? OnColor : OffColor;
                return;
            }
            BeginTransition();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            pointerInside = true;
            BeginTransition();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            pointerInside = false;
            BeginTransition();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (!Enabled)
            {
                pointerInside = false;
            }
            BeginTransition();
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color parentColor = Parent == null ? Color.FromArgb(22, 22, 22) : Parent.BackColor;
            using (var backgroundBrush = new SolidBrush(parentColor))
            {
                e.Graphics.FillRectangle(backgroundBrush, ClientRectangle);
            }

            float dpiScale = BoostixDrawing.DpiScale(e.Graphics);
            float rightInset = 2F * dpiScale;
            float trackWidth = 36F * dpiScale;
            float trackHeight = 20F * dpiScale;
            float knobInset = 2F * dpiScale;
            float knobSize = 16F * dpiScale;
            Color textColor = Enabled ? ForeColor : Color.FromArgb(95, 95, 95);
            Rectangle textBounds = new Rectangle(
                0,
                0,
                Math.Max(0, Width - (int)Math.Ceiling(52F * dpiScale)),
                Height);
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                textBounds,
                textColor,
                TextFormatFlags.Left
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine
                | TextFormatFlags.NoPadding
                | TextFormatFlags.EndEllipsis);

            // Keep a DPI-scaled inset so antialiasing never clips the rounded cap.
            float trackLeft = Width - rightInset - trackWidth;
            float trackTop = (Height - trackHeight) * 0.5F;
            RectangleF trackBounds = new RectangleF(
                trackLeft,
                trackTop,
                trackWidth,
                trackHeight);
            Color trackColor = currentTrackColor;
            Color knobColor = Color.White;
            if (!Enabled)
            {
                trackColor = BoostixDrawing.Interpolate(trackColor, parentColor, 0.5F);
                knobColor = Color.FromArgb(145, 145, 145);
            }
            if (SystemInformation.HighContrast)
            {
                if (!Enabled)
                {
                    trackColor = SystemColors.ControlDarkDark;
                    knobColor = SystemColors.GrayText;
                }
                else
                {
                    trackColor = Checked ? SystemColors.Highlight : SystemColors.ControlDark;
                    knobColor = Checked ? SystemColors.HighlightText : SystemColors.Window;
                }
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath trackPath = BoostixDrawing.RoundedRectangle(
                trackBounds,
                trackHeight * 0.5F))
            using (var trackBrush = new SolidBrush(trackColor))
            {
                e.Graphics.FillPath(trackBrush, trackPath);
            }

            float knobTravel = Math.Max(
                0F,
                trackWidth - (knobInset * 2F) - knobSize);
            float knobLeft = trackLeft + knobInset + (knobTravel * thumbPosition);
            RectangleF knobBounds = new RectangleF(
                knobLeft,
                trackTop + knobInset,
                knobSize,
                knobSize);
            using (var knobBrush = new SolidBrush(knobColor))
            {
                e.Graphics.FillEllipse(knobBrush, knobBounds);
            }

            if (Focused && ShowFocusCues && Enabled)
            {
                RectangleF focusBounds = RectangleF.Inflate(
                    trackBounds,
                    1.5F * dpiScale,
                    1.5F * dpiScale);
                Color focusColor = SystemInformation.HighContrast
                    ? SystemColors.Highlight
                    : Color.FromArgb(
                        ProductBrand.AccentTextRed,
                        ProductBrand.AccentTextGreen,
                        ProductBrand.AccentTextBlue);
                using (GraphicsPath focusPath = BoostixDrawing.RoundedRectangle(
                    focusBounds,
                    focusBounds.Height * 0.5F))
                using (var focusPen = new Pen(focusColor, Math.Max(1F, dpiScale)))
                {
                    focusPen.DashStyle = DashStyle.Dot;
                    e.Graphics.DrawPath(focusPen, focusPath);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                animationTimer.Stop();
                animationTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        private Color TargetTrackColor()
        {
            if (Checked)
            {
                return OnColor;
            }
            return pointerInside && Enabled ? OffHoverColor : OffColor;
        }

        private void BeginTransition()
        {
            if (!IsHandleCreated)
            {
                return;
            }
            startThumbPosition = thumbPosition;
            targetThumbPosition = Checked ? 1F : 0F;
            startTrackColor = currentTrackColor;
            targetTrackColor = TargetTrackColor();
            if (!BoostixDrawing.ClientAreaAnimation())
            {
                animationTimer.Stop();
                thumbPosition = targetThumbPosition;
                currentTrackColor = targetTrackColor;
                Invalidate();
                return;
            }
            animationStart = Stopwatch.GetTimestamp();
            animationTimer.Start();
            Invalidate();
        }

        private void AnimationTick(object sender, EventArgs e)
        {
            double elapsed = (Stopwatch.GetTimestamp() - animationStart) * 1000D / Stopwatch.Frequency;
            float progress = (float)Math.Min(1D, elapsed / 200D);
            float eased = BoostixDrawing.CssEase(progress);
            thumbPosition = startThumbPosition + ((targetThumbPosition - startThumbPosition) * eased);
            currentTrackColor = BoostixDrawing.Interpolate(startTrackColor, targetTrackColor, eased);
            int trackAreaWidth = Math.Max(
                1,
                (int)Math.Ceiling(BoostixDrawing.ScaleForDpi(40F, DeviceDpi)));
            Invalidate(new Rectangle(
                Math.Max(0, Width - trackAreaWidth),
                0,
                Math.Min(trackAreaWidth, Width),
                Height));
            if (progress >= 1F)
            {
                animationTimer.Stop();
                thumbPosition = targetThumbPosition;
                currentTrackColor = targetTrackColor;
            }
        }
    }

    internal sealed class UpdateProgressForm : Form
    {
        private const int ProgressTrackWidth = 480;
        private readonly Color background = Color.FromArgb(22, 22, 22);
        private readonly Color accent = Color.FromArgb(
            ProductBrand.AccentRed,
            ProductBrand.AccentGreen,
            ProductBrand.AccentBlue);
        private readonly Color accentText = Color.FromArgb(
            ProductBrand.AccentTextRed,
            ProductBrand.AccentTextGreen,
            ProductBrand.AccentTextBlue);
        private readonly Color muted = Color.FromArgb(142, 142, 142);
        private readonly bool demoMode;
        private readonly Timer progressAnimationTimer;
        private readonly Timer demoTimer;
        private BoostixCloseButton closeButton;
        private BoostixActionButton actionButton;
        private Label headlineLabel;
        private Label descriptionLabel;
        private Label percentLabel;
        private Label phaseLabel;
        private Label detailLabel;
        private Panel progressFill;
        private int displayedProgress;
        private int targetProgress;
        private int demoMilestoneIndex;
        private bool installing;
        private bool successPending;
        private bool successShown;

        private static readonly int[] DemoPercentages =
        {
            0, 5, 10, 17, 25, 35, 44, 55, 68, 76, 87, 94, 100
        };

        private static readonly string[] DemoStages =
        {
            "Подготовка обновления",
            "Подготовка папки установки",
            "Остановка запущенной версии",
            "Распаковка файлов программы",
            "Распаковка компонентов обновления",
            "Проверка файлов программы",
            "Проверка компонентов обновления",
            "Установка профиля производительности",
            "Установка новой версии программы",
            "Обновление компонентов удаления",
            "Сохранение параметров установки",
            "Регистрация новой версии",
            "Обновление установлено"
        };

        public UpdateProgressForm(bool demoMode)
        {
            this.demoMode = demoMode;
            Text = "Boostix Update";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(560, 345);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = background;
            ForeColor = Color.White;
            Font = CreateUiFont(9F, FontStyle.Regular);
            DoubleBuffered = true;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            progressAnimationTimer = new Timer();
            progressAnimationTimer.Interval = 15;
            progressAnimationTimer.Tick += ProgressAnimationTick;

            demoTimer = new Timer();
            demoTimer.Interval = 360;
            demoTimer.Tick += DemoTimerTick;

            BuildInterface();
            Resize += delegate { ApplyRoundedRegion(); };
            Shown += UpdateProgressFormShown;
            MouseDown += DragWindow;
        }

        private void LayoutWindowChrome()
        {
            if (closeButton == null)
            {
                return;
            }

            closeButton.Location = new Point(
                Math.Max(0, ClientSize.Width - closeButton.Width),
                0);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int CsDropShadow = 0x00020000;
                CreateParams parameters = base.CreateParams;
                parameters.ClassStyle |= CsDropShadow;
                return parameters;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float dpiScale = BoostixDrawing.DpiScale(e.Graphics);
            using (GraphicsPath path = BoostixDrawing.RoundedRectangle(
                new RectangleF(
                    0F,
                    0F,
                    Math.Max(1F, ClientSize.Width - dpiScale),
                    Math.Max(1F, ClientSize.Height - dpiScale)),
                11F * dpiScale))
            using (var pen = new Pen(
                Color.FromArgb(56, 56, 56),
                Math.Max(1F, dpiScale)))
            {
                e.Graphics.DrawPath(pen, path);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (installing)
            {
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                progressAnimationTimer.Stop();
                progressAnimationTimer.Dispose();
                demoTimer.Stop();
                demoTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        private void BuildInterface()
        {
            closeButton = new BoostixCloseButton();
            closeButton.Location = new Point(530, 0);
            closeButton.Size = new Size(30, 30);
            closeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            closeButton.AccessibleName = "Закрыть обновление";
            closeButton.AccessibleDescription = "Закрывает окно обновления Boostix";
            closeButton.TabIndex = 1;
            closeButton.Click += delegate
            {
                if (!installing)
                {
                    Close();
                }
            };
            Controls.Add(closeButton);

            var iconBox = new PictureBox();
            iconBox.Location = new Point(40, 32);
            iconBox.Size = new Size(50, 50);
            iconBox.SizeMode = PictureBoxSizeMode.Zoom;
            iconBox.Image = Icon == null ? null : Icon.ToBitmap();
            iconBox.MouseDown += DragWindow;
            Controls.Add(iconBox);

            var title = MakeLabel("BOOSTIX", 22F, FontStyle.Bold, Color.White);
            title.Location = new Point(105, 31);
            title.AutoSize = true;
            title.MouseDown += DragWindow;
            Controls.Add(title);

            var version = MakeLabel(
                "UPDATE  •  v" + InstallerEngine.ProductVersion,
                8.5F,
                FontStyle.Bold,
                accentText);
            version.Location = new Point(108, 66);
            version.AutoSize = true;
            version.MouseDown += DragWindow;
            Controls.Add(version);

            headlineLabel = MakeLabel("УСТАНОВКА ОБНОВЛЕНИЯ", 16F, FontStyle.Bold, Color.White);
            headlineLabel.Location = new Point(40, 112);
            headlineLabel.AutoSize = true;
            Controls.Add(headlineLabel);

            descriptionLabel = MakeLabel(
                "Boostix обновляется до версии " + InstallerEngine.ProductVersion,
                10F,
                FontStyle.Regular,
                muted);
            descriptionLabel.Location = new Point(42, 146);
            descriptionLabel.AutoSize = true;
            Controls.Add(descriptionLabel);

            percentLabel = MakeLabel("0%", 24F, FontStyle.Bold, accent);
            percentLabel.Location = new Point(39, 181);
            percentLabel.Size = new Size(120, 42);
            percentLabel.TextAlign = ContentAlignment.MiddleLeft;
            percentLabel.AccessibleName = "Прогресс обновления: 0 процентов";
            Controls.Add(percentLabel);

            phaseLabel = MakeLabel("Подготовка обновления", 10F, FontStyle.Bold, Color.FromArgb(220, 220, 220));
            phaseLabel.Location = new Point(162, 190);
            phaseLabel.Size = new Size(356, 28);
            phaseLabel.TextAlign = ContentAlignment.MiddleLeft;
            phaseLabel.AutoEllipsis = true;
            Controls.Add(phaseLabel);

            var progressTrack = new Panel();
            progressTrack.Location = new Point(40, 231);
            progressTrack.Size = new Size(ProgressTrackWidth, 6);
            progressTrack.Anchor = AnchorStyles.Top |
                AnchorStyles.Left |
                AnchorStyles.Right;
            progressTrack.BackColor = Color.FromArgb(48, 48, 48);
            Controls.Add(progressTrack);

            progressFill = new Panel();
            progressFill.Location = new Point(0, 0);
            progressFill.Size = new Size(0, 6);
            progressFill.BackColor = accent;
            progressTrack.Controls.Add(progressFill);

            detailLabel = MakeLabel(
                "Не закрывайте установщик до завершения обновления.",
                9F,
                FontStyle.Regular,
                muted);
            detailLabel.Location = new Point(42, 252);
            detailLabel.Size = new Size(478, 34);
            detailLabel.AutoEllipsis = true;
            Controls.Add(detailLabel);

            actionButton = new BoostixActionButton();
            actionButton.Text = "ПРОДОЛЖИТЬ";
            actionButton.Location = new Point(350, 288);
            actionButton.Size = new Size(170, 42);
            actionButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            actionButton.ForeColor = Color.White;
            actionButton.Font = CreateUiFont(10F, FontStyle.Bold);
            actionButton.AccessibleName = "Продолжить после обновления";
            actionButton.AccessibleDescription = "Запускает обновлённую версию Boostix";
            actionButton.TabIndex = 0;
            actionButton.Visible = false;
            actionButton.Click += ActionButtonClick;
            Controls.Add(actionButton);

            AcceptButton = actionButton;
            CancelButton = closeButton;
        }

        private void UpdateProgressFormShown(object sender, EventArgs e)
        {
            LayoutWindowChrome();
            ApplyRoundedRegion();
            BeginInvoke(new Action(StartInstallation));
        }

        private void StartInstallation()
        {
            if (installing)
            {
                return;
            }

            installing = true;
            successPending = false;
            successShown = false;
            displayedProgress = 0;
            targetProgress = 0;
            progressFill.Width = 0;
            percentLabel.Text = "0%";
            percentLabel.AccessibleName = "Прогресс обновления: 0 процентов";
            headlineLabel.Text = "УСТАНОВКА ОБНОВЛЕНИЯ";
            headlineLabel.ForeColor = Color.White;
            descriptionLabel.Text = "Boostix обновляется до версии " + InstallerEngine.ProductVersion;
            phaseLabel.Text = "Подготовка обновления";
            phaseLabel.ForeColor = Color.FromArgb(220, 220, 220);
            detailLabel.Text = "Не закрывайте установщик до завершения обновления.";
            detailLabel.ForeColor = muted;
            actionButton.Visible = false;
            actionButton.Enabled = false;
            closeButton.Enabled = false;
            progressAnimationTimer.Start();

            if (demoMode)
            {
                demoMilestoneIndex = 0;
                demoTimer.Start();
                return;
            }

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    InstallerEngine.Install(
                        InstallerEngine.GetDesktopShortcutPreference(),
                        ReportProgressFromWorker);
                    PostToUi(InstallationCompleted);
                }
                catch (Exception exception)
                {
                    PostToUi(delegate { InstallationFailed(exception); });
                }
            });
        }

        private void ReportProgressFromWorker(int percent, string stage)
        {
            PostToUi(delegate { SetProgressTarget(percent, stage); });
        }

        private void PostToUi(Action action)
        {
            if (action == null || IsDisposed || Disposing)
            {
                return;
            }

            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(action);
                }
                else
                {
                    action();
                }
            }
            catch (ObjectDisposedException)
            {
                // The window was disposed while a background operation was finishing.
            }
            catch (InvalidOperationException)
            {
                // The window was closed while a background operation was finishing.
            }
        }

        private void SetProgressTarget(int percent, string stage)
        {
            int normalized = Math.Max(0, Math.Min(100, percent));
            targetProgress = Math.Max(targetProgress, normalized);
            if (!string.IsNullOrWhiteSpace(stage))
            {
                phaseLabel.Text = stage;
            }
            if (!BoostixDrawing.ClientAreaAnimation())
            {
                displayedProgress = targetProgress;
                UpdateProgressPresentation();
            }
            progressAnimationTimer.Start();
        }

        private void InstallationCompleted()
        {
            SetProgressTarget(100, "Обновление установлено");
            successPending = true;
        }

        private void InstallationFailed(Exception exception)
        {
            InstallerDiagnostics.Write("Update installation failed.", exception);
            installing = false;
            successPending = false;
            demoTimer.Stop();
            headlineLabel.Text = "НЕ УДАЛОСЬ ОБНОВИТЬ";
            headlineLabel.ForeColor = Color.FromArgb(255, 102, 122);
            descriptionLabel.Text = "Повторите установку обновления.";
            phaseLabel.Text = "Ошибка установки";
            phaseLabel.ForeColor = Color.FromArgb(255, 102, 122);
            detailLabel.Text = FriendlyError(exception) +
                "\r\nЖурнал: " + InstallerDiagnostics.LogPath;
            detailLabel.ForeColor = Color.FromArgb(205, 205, 205);
            actionButton.Text = "ПОВТОРИТЬ";
            actionButton.AccessibleName = "Повторить обновление Boostix";
            actionButton.AccessibleDescription = "Повторно запускает установку обновления";
            actionButton.Visible = true;
            actionButton.Enabled = true;
            closeButton.Enabled = true;
            actionButton.Focus();
        }

        private void ProgressAnimationTick(object sender, EventArgs e)
        {
            if (displayedProgress < targetProgress)
            {
                if (!BoostixDrawing.ClientAreaAnimation())
                {
                    displayedProgress = targetProgress;
                }
                else
                {
                    int difference = targetProgress - displayedProgress;
                    displayedProgress += Math.Min(
                        3,
                        Math.Max(1, (difference + 11) / 12));
                }
                if (displayedProgress > targetProgress)
                {
                    displayedProgress = targetProgress;
                }
                UpdateProgressPresentation();
            }

            if (successPending && displayedProgress >= 100)
            {
                successPending = false;
                ShowSuccess();
            }
            else if (displayedProgress >= targetProgress)
            {
                progressAnimationTimer.Stop();
            }
        }

        private void UpdateProgressPresentation()
        {
            percentLabel.Text = displayedProgress.ToString(
                CultureInfo.InvariantCulture) + "%";
            percentLabel.AccessibleName = "Прогресс обновления: " +
                displayedProgress.ToString(CultureInfo.InvariantCulture) +
                " процентов";
            int trackWidth = progressFill.Parent == null
                ? ProgressTrackWidth
                : progressFill.Parent.ClientSize.Width;
            progressFill.Width = Math.Max(
                0,
                Math.Min(
                    trackWidth,
                    (int)Math.Round(
                        trackWidth * (displayedProgress / 100D),
                        MidpointRounding.AwayFromZero)));
        }

        private void DemoTimerTick(object sender, EventArgs e)
        {
            if (demoMilestoneIndex >= DemoPercentages.Length)
            {
                demoTimer.Stop();
                successPending = true;
                progressAnimationTimer.Start();
                return;
            }

            SetProgressTarget(
                DemoPercentages[demoMilestoneIndex],
                DemoStages[demoMilestoneIndex]);
            demoMilestoneIndex++;
        }

        private void ShowSuccess()
        {
            if (successShown)
            {
                return;
            }

            successShown = true;
            installing = false;
            headlineLabel.Text = "ПРОГРАММА УСПЕШНО ОБНОВЛЕНА";
            headlineLabel.ForeColor = Color.White;
            descriptionLabel.Text = "Версия " + InstallerEngine.ProductVersion + " готова к запуску.";
            phaseLabel.Text = "Обновление завершено";
            phaseLabel.ForeColor = accentText;
            detailLabel.Text = "Нажмите «Продолжить», чтобы открыть Boostix.";
            detailLabel.ForeColor = muted;
            actionButton.Text = "ПРОДОЛЖИТЬ";
            actionButton.AccessibleName = "Продолжить после обновления";
            actionButton.AccessibleDescription = demoMode
                ? "Закрывает демонстрацию обновления"
                : "Запускает обновлённую версию Boostix";
            actionButton.Visible = true;
            actionButton.Enabled = true;
            closeButton.Enabled = true;
            actionButton.Focus();
        }

        private void ActionButtonClick(object sender, EventArgs e)
        {
            if (!successShown)
            {
                StartInstallation();
                return;
            }

            if (demoMode)
            {
                Close();
                return;
            }

            try
            {
                InstallerEngine.LaunchInstalledApplication();
                Close();
            }
            catch (Exception exception)
            {
                detailLabel.Text = "Не удалось запустить программу: " + FriendlyError(exception);
                detailLabel.ForeColor = Color.FromArgb(255, 102, 122);
                actionButton.Enabled = true;
                actionButton.Focus();
            }
        }

        private static string FriendlyError(Exception exception)
        {
            if (exception == null || string.IsNullOrWhiteSpace(exception.Message))
            {
                return "Неизвестная ошибка. Нажмите «Повторить».";
            }

            string message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return message.Length <= 150 ? message : message.Substring(0, 147) + "...";
        }

        private Label MakeLabel(string text, float size, FontStyle style, Color color)
        {
            var label = new Label();
            label.Text = text;
            label.Font = CreateUiFont(size, style);
            label.ForeColor = color;
            label.BackColor = Color.Transparent;
            return label;
        }

        private Font CreateUiFont(float size, FontStyle style)
        {
            return demoMode
                ? new Font("Segoe UI", size, style, GraphicsUnit.Point)
                : BoostixFontProvider.Create(size, style);
        }

        private void ApplyRoundedRegion()
        {
            using (GraphicsPath path = BoostixDrawing.RoundedRectangle(
                new RectangleF(0F, 0F, Width, Height),
                BoostixDrawing.ScaleForDpi(11F, DeviceDpi)))
            {
                Region oldRegion = Region;
                Region = new Region(path);
                if (oldRegion != null)
                {
                    oldRegion.Dispose();
                }
            }
        }

        private void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || installing)
            {
                return;
            }
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(Handle, 0xA1, new IntPtr(0x2), IntPtr.Zero);
        }
    }

    internal sealed class InstallerForm : Form
    {
        private readonly Color background = Color.FromArgb(22, 22, 22);
        private readonly Color panel = Color.FromArgb(27, 27, 27);
        private readonly Color accent = Color.FromArgb(
            ProductBrand.AccentRed,
            ProductBrand.AccentGreen,
            ProductBrand.AccentBlue);
        private readonly Color accentText = Color.FromArgb(
            ProductBrand.AccentTextRed,
            ProductBrand.AccentTextGreen,
            ProductBrand.AccentTextBlue);
        private readonly Color muted = Color.FromArgb(142, 142, 142);
        private BoostixActionButton installButton;
        private BoostixCloseButton closeButton;
        private BoostixToggle desktopShortcut;
        private Label statusLabel;
        private Panel progressFill;
        private bool installed;
        private bool installOperationRunning;

        public InstallerForm()
        {
            Text = "Boostix Setup";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(560, 360);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = background;
            ForeColor = Color.White;
            Font = BoostixFontProvider.Create(9F, FontStyle.Regular);
            DoubleBuffered = true;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            BuildInterface();
            Resize += delegate { ApplyRoundedRegion(); };
            Shown += delegate
            {
                LayoutWindowChrome();
                ApplyRoundedRegion();
            };
            MouseDown += DragWindow;
        }

        private void LayoutWindowChrome()
        {
            if (closeButton == null)
            {
                return;
            }

            closeButton.Location = new Point(
                Math.Max(0, ClientSize.Width - closeButton.Width),
                0);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int CsDropShadow = 0x00020000;
                CreateParams parameters = base.CreateParams;
                parameters.ClassStyle |= CsDropShadow;
                return parameters;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float dpiScale = BoostixDrawing.DpiScale(e.Graphics);
            using (GraphicsPath path = BoostixDrawing.RoundedRectangle(
                new RectangleF(
                    0F,
                    0F,
                    Math.Max(1F, ClientSize.Width - dpiScale),
                    Math.Max(1F, ClientSize.Height - dpiScale)),
                11F * dpiScale))
            using (var pen = new Pen(
                Color.FromArgb(56, 56, 56),
                Math.Max(1F, dpiScale)))
            {
                e.Graphics.DrawPath(pen, path);
            }
        }

        private void BuildInterface()
        {
            closeButton = new BoostixCloseButton();
            closeButton.Location = new Point(530, 0);
            closeButton.Size = new Size(30, 30);
            closeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            closeButton.AccessibleName = "Закрыть установщик";
            closeButton.AccessibleDescription = "Закрывает окно установки Boostix";
            closeButton.TabIndex = 2;
            closeButton.Click += delegate { Close(); };
            Controls.Add(closeButton);

            var iconBox = new PictureBox();
            iconBox.Location = new Point(38, 35);
            iconBox.Size = new Size(52, 52);
            iconBox.SizeMode = PictureBoxSizeMode.Zoom;
            iconBox.Image = Icon.ToBitmap();
            iconBox.MouseDown += DragWindow;
            Controls.Add(iconBox);

            var title = MakeLabel("BOOSTIX", 22F, FontStyle.Bold, Color.White);
            title.Location = new Point(105, 35);
            title.AutoSize = true;
            title.MouseDown += DragWindow;
            Controls.Add(title);

            var version = MakeLabel(
                "SETUP  •  v" + InstallerEngine.ProductVersion,
                8.5F,
                FontStyle.Bold,
                accentText);
            version.Location = new Point(108, 69);
            version.AutoSize = true;
            version.MouseDown += DragWindow;
            Controls.Add(version);

            var subtitle = MakeLabel("Установщик приложения для повышения производительности", 10F, FontStyle.Regular, muted);
            subtitle.Location = new Point(40, 110);
            subtitle.AutoSize = true;
            Controls.Add(subtitle);

            var locationPanel = new Panel();
            locationPanel.Location = new Point(40, 145);
            locationPanel.Size = new Size(480, 70);
            locationPanel.Anchor = AnchorStyles.Top |
                AnchorStyles.Left |
                AnchorStyles.Right;
            locationPanel.BackColor = panel;
            Controls.Add(locationPanel);

            var locationTitle = MakeLabel("ПАПКА УСТАНОВКИ", 8.5F, FontStyle.Bold, muted);
            locationTitle.Location = new Point(16, 11);
            locationTitle.AutoSize = true;
            locationPanel.Controls.Add(locationTitle);

            var locationValue = MakeLabel(InstallerEngine.InstallDirectory, 9.5F, FontStyle.Regular, Color.FromArgb(235, 235, 235));
            locationValue.Location = new Point(16, 34);
            locationValue.AutoEllipsis = true;
            locationValue.Size = new Size(448, 24);
            locationValue.Anchor = AnchorStyles.Top |
                AnchorStyles.Left |
                AnchorStyles.Right;
            locationPanel.Controls.Add(locationValue);

            desktopShortcut = new BoostixToggle();
            desktopShortcut.Text = "Создать ярлык на рабочем столе";
            desktopShortcut.Checked = InstallerEngine.GetDesktopShortcutPreference();
            desktopShortcut.Location = new Point(42, 226);
            desktopShortcut.Size = new Size(478, 26);
            desktopShortcut.Anchor = AnchorStyles.Top |
                AnchorStyles.Left |
                AnchorStyles.Right;
            desktopShortcut.ForeColor = Color.FromArgb(195, 195, 195);
            desktopShortcut.Font = BoostixFontProvider.Create(9.5F, FontStyle.Regular);
            desktopShortcut.AccessibleName = "Создать ярлык на рабочем столе";
            desktopShortcut.AccessibleDescription = "Включает или отключает создание ярлыка Boostix на рабочем столе";
            desktopShortcut.TabIndex = 0;
            Controls.Add(desktopShortcut);

            var progressTrack = new Panel();
            progressTrack.Location = new Point(40, 276);
            progressTrack.Size = new Size(480, 4);
            progressTrack.Anchor = AnchorStyles.Top |
                AnchorStyles.Left |
                AnchorStyles.Right;
            progressTrack.BackColor = Color.FromArgb(48, 48, 48);
            Controls.Add(progressTrack);

            progressFill = new Panel();
            progressFill.Location = new Point(0, 0);
            progressFill.Size = new Size(0, 4);
            progressFill.BackColor = accent;
            progressTrack.Controls.Add(progressFill);

            statusLabel = MakeLabel("ГОТОВО К УСТАНОВКЕ", 8.5F, FontStyle.Bold, muted);
            statusLabel.Location = new Point(42, 292);
            statusLabel.AutoSize = true;
            Controls.Add(statusLabel);

            installButton = new BoostixActionButton();
            installButton.Text = "УСТАНОВИТЬ";
            installButton.Location = new Point(350, 299);
            installButton.Size = new Size(170, 42);
            installButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            installButton.ForeColor = Color.White;
            installButton.Font = BoostixFontProvider.Create(10F, FontStyle.Bold);
            installButton.AccessibleName = "Установить Boostix";
            installButton.AccessibleDescription = "Начинает установку приложения";
            installButton.TabIndex = 1;
            installButton.Click += InstallButtonClick;
            Controls.Add(installButton);

            AcceptButton = installButton;
            CancelButton = closeButton;
        }

        private void InstallButtonClick(object sender, EventArgs e)
        {
            if (installOperationRunning)
            {
                return;
            }
            if (installed)
            {
                InstallerEngine.LaunchInstalledApplication();
                Close();
                return;
            }

            installOperationRunning = true;
            bool createDesktopShortcut = desktopShortcut.Checked;
            installButton.Enabled = false;
            closeButton.Enabled = false;
            desktopShortcut.Enabled = false;
            statusLabel.Text = "УСТАНАВЛИВАЮ...";
            statusLabel.ForeColor = Color.FromArgb(
                ProductBrand.AccentTextRed,
                ProductBrand.AccentTextGreen,
                ProductBrand.AccentTextBlue);
            AnimateProgress((int)Math.Round(
                GetProgressTrackWidth() * 0.25D,
                MidpointRounding.AwayFromZero));

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    InstallerEngine.Install(
                        createDesktopShortcut,
                        ReportInstallProgressFromWorker);
                    PostToUi(InstallationCompleted);
                }
                catch (Exception exception)
                {
                    PostToUi(delegate { InstallationFailed(exception); });
                }
            });
        }

        private void ReportInstallProgressFromWorker(int percent, string stage)
        {
            PostToUi(delegate
            {
                int normalized = Math.Max(0, Math.Min(100, percent));
                int trackWidth = GetProgressTrackWidth();
                AnimateProgress(Math.Max(
                    progressFill.Width,
                    (int)Math.Round(
                        trackWidth * (normalized / 100D),
                        MidpointRounding.AwayFromZero)));
                if (!string.IsNullOrWhiteSpace(stage))
                {
                    statusLabel.Text = stage;
                }
            });
        }

        private void InstallationCompleted()
        {
            installOperationRunning = false;
            AnimateProgress(GetProgressTrackWidth());
            statusLabel.Text = "УСТАНОВЛЕНО";
            statusLabel.ForeColor = accentText;
            installButton.Text = "ЗАПУСТИТЬ";
            installButton.AccessibleName = "Запустить Boostix";
            installButton.AccessibleDescription = "Запускает установленное приложение Boostix";
            installButton.Enabled = true;
            closeButton.Enabled = true;
            installed = true;
        }

        private void InstallationFailed(Exception exception)
        {
            InstallerDiagnostics.Write("Interactive installation failed.", exception);
            installOperationRunning = false;
            statusLabel.Text = "ОШИБКА УСТАНОВКИ";
            statusLabel.ForeColor = Color.FromArgb(255, 102, 122);
            installButton.Text = "ПОВТОРИТЬ";
            installButton.AccessibleName = "Повторить установку Boostix";
            installButton.AccessibleDescription = "Повторно запускает установку приложения";
            installButton.Enabled = true;
            closeButton.Enabled = true;
            desktopShortcut.Enabled = true;
            MessageBox.Show(
                "Не удалось установить Boostix:\r\n" + exception.Message +
                "\r\n\r\nЖурнал: " + InstallerDiagnostics.LogPath,
                "Ошибка установки",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        private void PostToUi(Action action)
        {
            if (action == null || IsDisposed || Disposing || !IsHandleCreated)
            {
                return;
            }
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(action);
                }
                else
                {
                    action();
                }
            }
            catch (ObjectDisposedException)
            {
                // The installer was disposed while the worker was completing.
            }
            catch (InvalidOperationException)
            {
                // The handle was destroyed while the worker was completing.
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (installOperationRunning && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        private void AnimateProgress(int width)
        {
            int trackWidth = GetProgressTrackWidth();
            progressFill.Width = Math.Max(0, Math.Min(trackWidth, width));
            progressFill.Refresh();
        }

        private int GetProgressTrackWidth()
        {
            return progressFill.Parent == null
                ? 480
                : Math.Max(0, progressFill.Parent.ClientSize.Width);
        }

        private static Label MakeLabel(string text, float size, FontStyle style, Color color)
        {
            var label = new Label();
            label.Text = text;
            label.Font = BoostixFontProvider.Create(size, style);
            label.ForeColor = color;
            label.BackColor = Color.Transparent;
            return label;
        }

        private void ApplyRoundedRegion()
        {
            using (GraphicsPath path = BoostixDrawing.RoundedRectangle(
                new RectangleF(0F, 0F, Width, Height),
                BoostixDrawing.ScaleForDpi(11F, DeviceDpi)))
            {
                Region oldRegion = Region;
                Region = new Region(path);
                if (oldRegion != null)
                {
                    oldRegion.Dispose();
                }
            }
        }

        private void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(Handle, 0xA1, new IntPtr(0x2), IntPtr.Zero);
        }
    }

    internal static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);
    }
}
