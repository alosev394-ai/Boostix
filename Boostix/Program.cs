using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Boostix.Branding;

[assembly: AssemblyTitle(ProductBrand.ProductName)]
[assembly: AssemblyDescription("Safe Windows performance preparation and diagnostics")]
[assembly: AssemblyCompany(ProductBrand.CompanyName)]
[assembly: AssemblyCopyright("© Silas Suspect")]
[assembly: AssemblyProduct(ProductBrand.ProductName)]
[assembly: AssemblyVersion(ProductBrand.AssemblyVersion)]
[assembly: AssemblyFileVersion(ProductBrand.AssemblyVersion)]

namespace Boostix
{
    internal static class Program
    {
        private const string ApplicationMutexName =
            @"Local\SilasSuspect.Boostix.Application";
        private const uint LoadLibrarySearchSystem32 = 0x00000800;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDefaultDllDirectories(
            uint directoryFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDllDirectory(string path);

        /// <summary>
        /// Passed to the new instance when Boostix restarts itself to change
        /// the interface language: it waits for the old instance to exit.
        /// </summary>
        internal const string RestartArgument = "--restarted";
        private const int RestartMutexWaitMilliseconds = 15000;

        [STAThread]
        private static void Main(string[] args)
        {
            // Nothing is read from disk before the DLL search is hardened, so
            // the only possible early message follows --lang or Windows.
            L.SetLanguage(L.FromArguments(args) ?? L.SystemDefault());
            if (!HardenNativeDllSearch())
            {
                MessageBox.Show(
                    L.T("Boostix не смог безопасно подготовить запуск. ") +
                    L.T("Установите актуальные обновления Windows и повторите попытку."),
                    ProductBrand.ProductName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            CrashLog.Configure(args);
            // The language is fixed before any window or static text exists;
            // changing it later restarts the app.
            L.SetLanguage(ChooseStartupLanguage(args));
            Run(args);
        }

        // Kept apart and never inlined: the JIT initialises the static fields
        // of the types a method touches (theme names among them) when it
        // compiles that method, which must happen after the language is set.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Run(string[] args)
        {
            using (var applicationMutex = new Mutex(
                false,
                GetApplicationMutexName(args)))
            {
                bool ownsMutex = false;
                try
                {
                    try
                    {
                        ownsMutex = applicationMutex.WaitOne(
                            HasArgument(args, RestartArgument) ? RestartMutexWaitMilliseconds : 0,
                            false);
                    }
                    catch (AbandonedMutexException)
                    {
                        ownsMutex = true;
                    }

                    if (!ownsMutex)
                    {
                        MessageBox.Show(
                            L.T("Boostix уже запущен."),
                            ProductBrand.ProductName,
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        return;
                    }

                    var application = new Application();
                    // The saved theme must be in place before the first colour
                    // is read. The elevated installer probe never reads user files.
                    bool healthProbe = HasArgument(args, UpdateHealthHandshake.ProbeArgument);
                    if (!healthProbe && !CrashLog.IsCurrentProcessElevated())
                    {
                        BoostixDesignTokens.SetPalette(BoostWindow.ReadThemePreference());
                    }
                    BoostixDesignTokens.GlassBackdrop = WindowBackdrop.IsAvailable;
                    BoostixDesignTokens.ApplyThemeResources(
                        application.Resources);
                    application.Resources[SystemParameters.FocusVisualStyleKey] =
                        BuildKeyboardFocusVisualStyle();
                    application.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    application.DispatcherUnhandledException += delegate(
                        object sender,
                        DispatcherUnhandledExceptionEventArgs eventArgs)
                    {
                        CrashLog.Write(
                            "Unhandled WPF dispatcher exception.",
                            eventArgs.Exception);
                    };
                    AppDomain.CurrentDomain.UnhandledException += delegate(
                        object sender,
                        UnhandledExceptionEventArgs eventArgs)
                    {
                        CrashLog.Write(
                            "Unhandled AppDomain exception.",
                            eventArgs.ExceptionObject as Exception);
                    };
                    TaskScheduler.UnobservedTaskException += delegate(
                        object sender,
                        UnobservedTaskExceptionEventArgs eventArgs)
                    {
                        CrashLog.Write(
                            "Unobserved task exception.",
                            eventArgs.Exception);
                        eventArgs.SetObserved();
                    };
                    try
                    {
                        application.Run(new BoostWindow(args));
                    }
                    catch (Exception ex)
                    {
                        CrashLog.Write("Application.Run failed.", ex);
                        MessageBox.Show(
                            L.T("Boostix столкнулся с ошибкой и безопасно остановлен. ") +
                            L.T("Диагностика сохранена в LocalAppData\\Boostix\\crash.log."),
                            ProductBrand.ProductName,
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                }
                finally
                {
                    if (ownsMutex)
                    {
                        applicationMutex.ReleaseMutex();
                    }
                }
            }
        }

        /// <summary>
        /// --lang wins. Test launches stay Russian because their checks read
        /// Russian text. The elevated installer probe never reads user files.
        /// Otherwise the saved choice, then the Windows language.
        /// </summary>
        internal static string ChooseStartupLanguage(string[] arguments)
        {
            string requested = L.FromArguments(arguments);
            if (requested != null)
            {
                return requested;
            }
            if (HasArgument(arguments, "--demo"))
            {
                return L.Russian;
            }
            if (HasArgument(arguments, UpdateHealthHandshake.ProbeArgument) ||
                CrashLog.IsCurrentProcessElevated())
            {
                return L.SystemDefault();
            }
            return BoostWindow.ReadLanguagePreference() ?? L.SystemDefault();
        }

        private static bool HasArgument(string[] arguments, string name)
        {
            return arguments != null && arguments.Any(argument => string.Equals(
                argument,
                name,
                StringComparison.OrdinalIgnoreCase));
        }

        private static string GetApplicationMutexName(string[] arguments)
        {
            const string prefix = "--test-instance=";
            bool demo = false;
            foreach (string argument in arguments ?? new string[0])
            {
                if (string.Equals(argument, "--demo", StringComparison.OrdinalIgnoreCase))
                {
                    demo = true;
                    break;
                }
            }
            if (!demo)
            {
                return ApplicationMutexName;
            }
            foreach (string argument in arguments ?? new string[0])
            {
                if (argument == null ||
                    !argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string token = argument.Substring(prefix.Length);
                if (token.Length != 32)
                {
                    continue;
                }
                bool valid = true;
                foreach (char character in token)
                {
                    if (!((character >= '0' && character <= '9') ||
                          (character >= 'a' && character <= 'f')))
                    {
                        valid = false;
                        break;
                    }
                }
                if (valid)
                {
                    return ApplicationMutexName + ".Test." + token;
                }
            }
            return ApplicationMutexName;
        }

        private static bool HardenNativeDllSearch()
        {
            try
            {
                // Remove the process working directory and the executable
                // directory from implicit native DLL resolution. Boostix has no
                // private native DLL dependency; all P/Invoke targets are Windows
                // system libraries.
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

        private static Style BuildKeyboardFocusVisualStyle()
        {
            var style = new Style(typeof(Control));
            var template = new ControlTemplate(typeof(Control));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetResourceReference(
                Border.BorderBrushProperty,
                BoostixDesignTokens.FocusBrushKey);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            border.SetValue(Border.MarginProperty, new Thickness(2));
            border.SetValue(UIElement.IsHitTestVisibleProperty, false);
            border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            template.VisualTree = border;
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }
    }

    internal sealed class BoostWindow : Window
    {
        private const double BaseWindowWidth = BoostixDesignTokens.WindowWidth;
        private const double BaseWindowHeight = BoostixDesignTokens.WindowHeight;
        private const double TitleControlSize = 32;
        private const double WorkAreaSafetyInset = 8;
        private const string GuidedProofScenarioId =
            "BOOSTIX-GUIDED-SAME-SCENE-V1";
        // Report titles are read on use, never cached before the language is set.
        private static string SessionPowerPlanStartActionTitle
        {
            get { return L.T("ПЛАН ПИТАНИЯ СЕССИИ"); }
        }

        private static string SessionPowerPlanStopActionTitle
        {
            get { return L.T("ВОССТАНОВЛЕНИЕ ПЛАНА ПИТАНИЯ"); }
        }
        private const int SessionPowerPlanShutdownGraceMilliseconds = 500;
        private const int MonitorDefaultToNearest = 2;
        private const int SwpNoActivate = 0x0010;
        private const int SwpNoZOrder = 0x0004;
        private const int WmDisplayChange = 0x007E;
        private const int WmDpiChanged = 0x02E0;
        private const int WmExitSizeMove = 0x0232;
        private const int WmSettingChange = 0x001A;
        private const int DwmWindowCornerPreference = 33;
        private const int DwmBorderColor = 34;
        private const int DwmRoundCornerPreference = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRectangle
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MonitorInformation
        {
            public int Size;
            public NativeRectangle Monitor;
            public NativeRectangle Work;
            public int Flags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(
            IntPtr window,
            int flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(
            IntPtr monitor,
            ref MonitorInformation information);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(
            IntPtr window,
            out NativeRectangle rectangle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            int flags);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr window,
            out uint processId);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(
            IntPtr window,
            int attribute,
            ref int attributeValue,
            int attributeSize);

        private sealed class ChromeButtonVisuals
        {
            public SolidColorBrush BackgroundBrush;
            public SolidColorBrush GlyphBrush;
            public bool IsClose;
        }

        private sealed class SessionPowerPlanStopRequest
        {
            public Guid SessionId;
            public Task<SessionPowerPlanOperationResult> StartTask;
            public BoostSessionReport CompletedReport;
            public Task<SessionPowerPlanStopCompletion> WorkerTask;
        }

        private sealed class SessionPowerPlanStopCompletion
        {
            public Guid SessionId;
            public BoostSessionReport CompletedReport;
            public SessionPowerPlanOperationResult StartResult;
            public SessionPowerPlanOperationResult StopResult;
            public Exception StartFailure;
            public Exception ReportSaveFailure;
        }

        private sealed class TrackedTargetPriority
        {
            public int ProcessId;
            public DateTime StartTimeUtc;
            public string ProcessName;
            public ProcessPriorityClass OriginalPriority;
            public bool ChangedByBoost;
        }

        private Button boostButton;
        private Button centerButton;
        private Button minimizeButton;
        private Button closeButton;
        private BoostGauge boostGauge;
        private Border targetIconHost;
        private TextBlock targetActionText;
        private Ellipse homeVerdictDot;
        private StackPanel homeMeters;
        private TextBlock homeChecksText;
        private TextBlock homeChecksDetail;
        private TextBlock caption;
        private Button targetSelectorButton;
        private TextBlock targetNameText;
        private TextBlock targetDetailText;
        private TextBlock liveStateText;
        private TextBlock liveTimerText;
        private OptimizationFlowOverlay optimizationOverlay;
        private UpdateFlowOverlay updateOverlay;
        private WhatsNewOverlay whatsNewOverlay;
        private BoostixTrayIcon trayIcon;
        private BoostToast boostToast;
        private bool startInTray;
        private bool gaugeSelfTestPlayed;
        private bool languageRestartPending;
        private HwndSource hotkeySource;
        private bool hotkeyRegistered;
        private BoostCenterOverlay boostCenterOverlay;
        private readonly SystemLoadSampler loadSampler = new SystemLoadSampler();
        private DispatcherTimer readinessTimer;
        private DispatcherTimer activeBoostTimer;
        private DispatcherTimer autoBoostDiscoveryTimer;
        private Process boostProcess;
        private string readinessSignalPath;
        private DateTime readinessDeadline;
        private int activeMaintenanceGeneration;
        private int activeMaintenancePending;
        private int activeDiagnosticPending;
        private int benchmarkCaptureActive;
        private int preflightGeneration;
        private int targetExitNotificationPending;
        private string priorityFailureTargetKey;
        private readonly object activeMaintenanceSync = new object();
        private bool animationRunning;
        private bool departureFinished;
        private bool boostReady;
        private bool boostActive;
        private bool preflightAccepted;
        private bool preflightForBoost;
        private bool autoBoostStartPending;
        private string deactivationSessionStatus = "Completed";
        private string deactivationSessionReason = L.T("Boost остановлен пользователем.");
        private BoostCenterSettings centerSettings = new BoostCenterSettings
        {
            CheckBeforeBoost = true,
            KeepDiscord = true,
            KeepEpic = true,
            KeepSteam = true,
            KeepOneDrive = true,
            KeepTeams = true,
            KeepWallpaper = true,
            KeepNvidiaOverlay = true
        };
        private BoostPreflightReport latestPreflight;
        private DiagnosticSnapshot latestDiagnosticSnapshot;
        private BoostSessionReport currentSession;
        private BoostSessionReport lastSession;
        private List<BoostSessionReport> sessionHistory =
            new List<BoostSessionReport>();
        private CancellationTokenSource benchmarkCancellation;
        private CancellationTokenSource impactScanCancellation;
        private List<BackgroundImpactResult> lastImpactResults =
            new List<BackgroundImpactResult>();
        private PerformanceCaptureAttemptResult lastCaptureAttempt;
        private readonly Dictionary<int, TrackedTargetPriority> trackedTargetPriorities =
            new Dictionary<int, TrackedTargetPriority>();
        private readonly GameTargetService gameTargetService;
        private readonly GameProfileStore gameProfileStore;
        private readonly PerformanceProofCheckpointStore proofCheckpointStore;
        private PerformanceProofCoordinator proofCoordinator;
        private readonly SessionPowerPlanManager sessionPowerPlanManager;
        private readonly object sessionPowerPlanSync = new object();
        private Guid? activePowerPlanSessionId;
        private Guid? recordedPowerPlanStartSessionId;
        private Task<SessionPowerPlanOperationResult> sessionPowerPlanStartTask;
        private SessionPowerPlanStopRequest sessionPowerPlanStopRequest;
        private IList<GameProfile> cachedGameProfiles = new List<GameProfile>();
        private bool gameProfilesDirty = true;
        private GameTargetIdentity selectedTarget;
        private SessionGuardSampler sessionGuardSampler;
        private CancellationTokenSource sessionGuardCancellation;
        private Process exactTargetExitWatcher;
        private EventHandler exactTargetExitHandler;
        private SessionGuardSample latestSessionGuardSample;
        private SessionGuardPressureState sessionGuardPressureState =
            new SessionGuardPressureState();
        private readonly SessionGuardPressurePolicy sessionGuardPressurePolicy =
            new SessionGuardPressurePolicy(
                SessionGuardPressurePolicyOptions.CreateDefault());
        private PagefileAssessment latestPagefileAssessment;
        private PagefileRecommendationCode? lastPagefileRecommendation;
        private readonly bool demoMode;
        private readonly bool safeMode;
        private readonly bool updateHealthProbe;
        private readonly double demoUiScale;
        private readonly string[] launchArguments;
        private Viewbox monitorAdaptiveViewbox;
        private Grid monitorDesignSurface;
        private HwndSource windowSource;
        private HwndSource backdropSource;
        private Border windowFrame;
        private Grid windowRoot;
        private IntPtr windowHandle;
        private bool applyingMonitorBounds;
        private bool monitorBoundsQueued;
        private bool themeNotificationsSubscribed;
        private IInputElement boostCenterFocusReturn;

        public BoostWindow(string[] args)
        {
            launchArguments = args ?? new string[0];
            gameTargetService = new GameTargetService(
                Assembly.GetExecutingAssembly().Location);
            gameProfileStore = new GameProfileStore(System.IO.Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                ProductBrand.DataDirectoryName,
                "game-profiles.dat"));
            proofCheckpointStore = new PerformanceProofCheckpointStore(
                System.IO.Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    ProductBrand.DataDirectoryName,
                    "proof-mode.checkpoint"));
            try
            {
                sessionPowerPlanManager = new SessionPowerPlanManager(
                    new WindowsSessionPowerPlanPlatform(),
                    new WindowsPowerCfgRunner(),
                    SecureSessionPowerPlanStateStore.CreateDefault(),
                    TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                sessionPowerPlanManager = null;
                CrashLog.Write(
                    "Session power-plan manager could not be initialized.",
                    ex);
            }
            demoMode = HasLaunchArgument(launchArguments, "--demo");
            startInTray = HasLaunchArgument(launchArguments, AutostartRegistration.TrayArgument);
            safeMode = HasLaunchArgument(launchArguments, "--safe-mode");
            updateHealthProbe = HasLaunchArgument(
                launchArguments,
                UpdateHealthHandshake.ProbeArgument);
            demoUiScale = demoMode
                ? GetDemoUiScale(launchArguments)
                : 1.0;
            double targetWidth = BaseWindowWidth * demoUiScale;
            double targetHeight = BaseWindowHeight * demoUiScale;
            // Demo-only emulation of a work area smaller than the design size:
            // the fixed 880x560 surface is scaled down uniformly, exactly as
            // the production Viewbox does on a small or high-DPI monitor.
            double demoFit = 1.0;
            if (HasLaunchArgument(launchArguments, "--demo-ultra-compact"))
            {
                demoFit = 360.0 / BaseWindowHeight;
            }
            else if (HasLaunchArgument(launchArguments, "--demo-compact"))
            {
                demoFit = 492.0 / BaseWindowHeight;
            }
            targetWidth = Math.Round(BaseWindowWidth * demoFit * demoUiScale);
            targetHeight = Math.Round(BaseWindowHeight * demoFit * demoUiScale);
            Title = ProductBrand.ProductName;
            Width = targetWidth;
            Height = targetHeight;
            MinWidth = Width;
            MinHeight = Height;
            MaxWidth = Width;
            MaxHeight = Height;
            // DWM draws the Windows 11 glass only behind a framed window;
            // WindowChrome below hides that frame, so both look the same.
            WindowStyle = WindowBackdrop.BuildSupportsGlass
                ? WindowStyle.SingleBorderWindow
                : WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // An opaque native window keeps WPF on the ClearType/hardware-rendered
            // path. Layered transparent windows rasterize text less sharply,
            // especially at 1080p and fractional DPI.
            AllowsTransparency = false;
            var chrome = new WindowChrome
            {
                CaptionHeight = 0,
                CornerRadius = new CornerRadius(12),
                GlassFrameThickness = new Thickness(0),
                ResizeBorderThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            };
            if (BoostixDesignTokens.GlassBackdrop)
            {
                // Windows 11 draws its acrylic under the whole client area;
                // the petrol tint layers above it stay readable.
                chrome.GlassFrameThickness = new Thickness(-1);
                Background = Brushes.Transparent;
            }
            else
            {
                SetResourceReference(
                    BackgroundProperty,
                    BoostixDesignTokens.BackgroundBrushKey);
            }
            WindowChrome.SetWindowChrome(this, chrome);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
            if (startInTray)
            {
                // Started with Windows: open quietly; Loaded hides the
                // window to the tray once the tray icon exists.
                WindowState = WindowState.Minimized;
                ShowActivated = false;
            }
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            FontFamily = LoadAppFontFamily();
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            TextOptions.SetTextHintingMode(this, TextHintingMode.Fixed);
            RenderOptions.SetBitmapScalingMode(
                this,
                BitmapScalingMode.HighQuality);
            Icon = BuildWindowIcon();
            AutomationProperties.SetName(this, ProductBrand.ProductName);
            AutomationProperties.SetAutomationId(this, "Boostix.MainWindow");

            FrameworkElement shell = BuildShell();
            if (!demoMode || demoFit < 1.0)
            {
                monitorDesignSurface = new Grid
                {
                    Width = BaseWindowWidth * demoUiScale,
                    Height = BaseWindowHeight * demoUiScale,
                    UseLayoutRounding = true,
                    SnapsToDevicePixels = true
                };
                monitorDesignSurface.Children.Add(shell);
                monitorAdaptiveViewbox = new Viewbox
                {
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Child = monitorDesignSurface
                };
                Content = monitorAdaptiveViewbox;
            }
            else
            {
                Content = shell;
            }
            Loaded += BoostWindowLoaded;
            SourceInitialized += BoostWindowSourceInitialized;
            Activated += delegate { FadeWindowTint(true); };
            Deactivated += delegate { FadeWindowTint(false); };
            KeyDown += WindowKeyDown;
            PreviewMouseLeftButtonDown += WindowMouseLeftButtonDown;
            Closing += BoostWindowClosing;
            Closed += WindowClosed;
        }

        private Grid BuildShell()
        {
            var shell = new Grid();
            if (Math.Abs(demoUiScale - 1.0) > 0.001)
            {
                shell.LayoutTransform = new ScaleTransform(
                    demoUiScale,
                    demoUiScale);
            }

            var frame = new Border();
            frame.BorderThickness = new Thickness(1);
            frame.SetResourceReference(
                Border.BorderBrushProperty,
                BoostixDesignTokens.BorderBrushKey);
            frame.SetResourceReference(
                Border.BackgroundProperty,
                BoostixDesignTokens.WindowTintBrushKey);
            shell.Children.Add(frame);
            windowFrame = frame;

            var root = new Grid();
            root.Background = Brushes.Transparent;
            KeyboardNavigation.SetTabNavigation(
                root,
                KeyboardNavigationMode.Cycle);
            root.SizeChanged += delegate { UpdateWindowFrameShape(); };
            frame.Child = root;
            windowRoot = root;
            UpdateWindowFrameShape();

            FrameworkElement home = BuildHomePage();

            boostCenterOverlay = new BoostCenterOverlay(
                LoadAppFontFamily(),
                LoadAppSemiboldFontFamily());
            boostCenterOverlay.SetHomeContent(home);
            boostCenterOverlay.RefreshRequested += delegate { QueuePreflight(preflightForBoost, true); };
            boostCenterOverlay.PageOpening += delegate { PublishBoostCenterData(); };
            boostCenterOverlay.CloseRequested += delegate
            {
                HandleBoostCenterReturnedHome(true);
            };
            boostCenterOverlay.HomeNavigated += delegate
            {
                HandleBoostCenterReturnedHome(false);
            };
            boostCenterOverlay.ProceedBoostRequested += delegate
            {
                if (boostActive || animationRunning)
                {
                    return;
                }
                preflightAccepted = true;
                preflightForBoost = false;
                StartBoost();
            };
            boostCenterOverlay.SettingsChanged += delegate
            {
                centerSettings = boostCenterOverlay.Settings;
                bool themeChanged = !string.Equals(
                        centerSettings.Theme,
                        BoostixDesignTokens.PaletteId,
                        StringComparison.OrdinalIgnoreCase) &&
                    BoostixDesignTokens.SetPalette(centerSettings.Theme);
                SaveBoostPreferences();
                ApplyHotkeyPreference();
                ApplyAutostartPreference();
                if (themeChanged)
                {
                    ApplyRuntimeTheme();
                }
                if (boostActive)
                {
                    RefreshActiveBoostMaintenance();
                }
            };
            boostCenterOverlay.LanguageChangeRequested += delegate
            {
                // The choice is already saved. Restarting ends the window, so
                // it waits while Boost or a measurement is running, and test
                // launches never start a real instance.
                if (demoMode ||
                    boostActive ||
                    animationRunning ||
                    benchmarkCancellation != null ||
                    string.Equals(centerSettings.Language, L.Language, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                languageRestartPending = true;
                Close();
            };
            boostCenterOverlay.RestoreRequested += delegate
            {
                if (optimizationOverlay != null && optimizationOverlay.ShowManualRestore())
                {
                    boostCenterOverlay.HandleEscape();
                }
            };
            boostCenterOverlay.OpenPagefileSettingsRequested += delegate
            {
                try
                {
                    string systemDirectory = Environment.GetFolderPath(
                        Environment.SpecialFolder.System);
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = System.IO.Path.Combine(
                            systemDirectory,
                            "SystemPropertiesPerformance.exe"),
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    CrashLog.Write(
                        "Could not open Windows performance settings.",
                        ex);
                }
            };
            boostCenterOverlay.BenchmarkRequested += BoostCenterBenchmarkRequested;
            boostCenterOverlay.ProofCancelRequested += delegate
            {
                if (benchmarkCancellation != null || proofCoordinator == null)
                {
                    return;
                }
                PerformanceProofTransition transition = proofCoordinator.Cancel(
                    L.T("Замер FPS сброшен пользователем."));
                string saveError;
                if (!proofCheckpointStore.TrySave(proofCoordinator, out saveError))
                {
                    CrashLog.Write(
                        "Could not save cancelled Proof Mode checkpoint: " + saveError,
                        null);
                }
                lastCaptureAttempt = null;
                boostCenterOverlay.SetPerformanceProofSnapshot(
                    proofCoordinator.GetSnapshot());
                boostCenterOverlay.SetBenchmarkMessage(
                    L.T("Замер FPS сброшен"),
                    transition.Message,
                    false);
            };
            boostCenterOverlay.ImpactScanRequested +=
                BoostCenterImpactScanRequested;
            boostCenterOverlay.ImpactCloseRequested +=
                BoostCenterImpactCloseRequested;
            boostCenterOverlay.TargetSelectionRequested += delegate
            {
                boostCenterOverlay.OpenHome();
                RunAfterPageTransition(OpenTargetSelector);
            };
            boostCenterOverlay.ProfileAutoBoostChanged +=
                BoostCenterProfileAutoBoostChanged;
            boostCenterOverlay.ProfileRemoveRequested +=
                BoostCenterProfileRemoveRequested;
            boostCenterOverlay.InstalledGamesScanRequested +=
                BoostCenterInstalledGamesScanRequested;
            boostCenterOverlay.InstalledGameAddRequested +=
                BoostCenterInstalledGameAddRequested;
            boostCenterOverlay.ExportDiagnosticsRequested +=
                BoostCenterExportDiagnosticsRequested;
            Panel.SetZIndex(boostCenterOverlay, 10);
            root.Children.Add(boostCenterOverlay);
            centerButton = boostCenterOverlay.HomeTabButton;

            FrameworkElement controls = BuildWindowControls();
            Panel.SetZIndex(controls, 60);
            root.Children.Add(controls);

            // Below the setup and update flows: when either is open it covers
            // this card, which then shows once they close.
            whatsNewOverlay = new WhatsNewOverlay();
            Panel.SetZIndex(whatsNewOverlay, 90);
            root.Children.Add(whatsNewOverlay);

            optimizationOverlay = new OptimizationFlowOverlay(
                this,
                launchArguments,
                LoadAppFontFamily(),
                LoadAppSemiboldFontFamily());
            optimizationOverlay.RequestApplicationClose += delegate { Close(); };
            Panel.SetZIndex(optimizationOverlay, 100);
            root.Children.Add(optimizationOverlay);

            updateOverlay = new UpdateFlowOverlay(
                this,
                launchArguments,
                LoadAppFontFamily(),
                LoadAppSemiboldFontFamily());
            updateOverlay.RequestApplicationClose += delegate { Close(); };
            Panel.SetZIndex(updateOverlay, 200);
            root.Children.Add(updateOverlay);

            return shell;
        }

        /// <summary>
        /// Publishes the latest window state before a section page renders.
        /// </summary>
        private void PublishBoostCenterData()
        {
            RememberBoostCenterFocusReturn(boostButton);
            boostCenterOverlay.SetSettings(centerSettings);
            boostCenterOverlay.SetPreflight(latestPreflight);
            boostCenterOverlay.SetSessionReport(currentSession ?? lastSession);
            boostCenterOverlay.SetDiagnosticSnapshot(
                latestDiagnosticSnapshot);
            boostCenterOverlay.SetSessionHistory(sessionHistory);
            UpdateBoostCenterSessionData();
        }

        private void HandleBoostCenterReturnedHome(bool restoreFocus)
        {
            if (preflightForBoost && !boostActive && !animationRunning)
            {
                Interlocked.Increment(ref preflightGeneration);
            }
            preflightForBoost = false;
            if (restoreFocus)
            {
                RestoreBoostCenterFocus();
            }
            else
            {
                boostCenterFocusReturn = null;
            }
        }

        /// <summary>
        /// Runs an action once the section slide has settled, so popups anchor
        /// to the final position of the Home controls.
        /// </summary>
        private void RunAfterPageTransition(Action action)
        {
            if (action == null)
            {
                return;
            }
            if (!SystemParameters.ClientAreaAnimation)
            {
                Dispatcher.BeginInvoke(action, DispatcherPriority.Input);
                return;
            }
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(
                    BoostCenterOverlay.PageTransitionTotalMilliseconds + 40)
            };
            timer.Tick += delegate
            {
                timer.Stop();
                action();
            };
            timer.Start();
        }

        private Grid BuildHomePage()
        {
            var home = new Grid();
            home.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            home.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
            home.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1, GridUnitType.Star)
            });
            AutomationProperties.SetAutomationId(home, "Boostix.Home");

            Button target = BuildTargetSelector();
            Grid.SetRow(target, 0);
            home.Children.Add(target);

            var hero = new Grid();
            hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(268) });
            hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            hero.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            Border boostCard = BuildBoostCard();
            Grid.SetColumn(boostCard, 0);
            hero.Children.Add(boostCard);
            Border systemCard = BuildHomeSystemCard();
            Grid.SetColumn(systemCard, 2);
            hero.Children.Add(systemCard);
            Grid.SetRow(hero, 2);
            home.Children.Add(hero);

            if (updateHealthProbe)
            {
                // The installer starts this probe with an elevated token. Do not
                // traverse user-controlled LocalAppData paths while that token is
                // active; the probe only needs deterministic in-memory defaults.
                centerSettings.CheckBeforeBoost = true;
                centerSettings.KeepDiscord = true;
                centerSettings.KeepEpic = true;
                centerSettings.KeepSteam = true;
                centerSettings.KeepOneDrive = true;
                centerSettings.KeepTeams = true;
                centerSettings.KeepWallpaper = true;
                centerSettings.KeepNvidiaOverlay = true;
            }
            else
            {
                LoadBoostPreferences();
            }

            return home;
        }

        private Button BuildTargetSelector()
        {
            var targetBackground = new SolidColorBrush(
                BoostixDesignTokens.Surface);
            var targetBorder = new SolidColorBrush(
                BoostixDesignTokens.Border);
            targetSelectorButton = new Button
            {
                MinHeight = 74,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(14, 12, 18, 12),
                Background = targetBackground,
                BorderBrush = targetBorder,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Template = MakeCardButtonTemplate(BoostixDesignTokens.RadiusLarge - 2),
                FocusVisualStyle = BoostixUi.FocusVisual(BoostixDesignTokens.RadiusLarge - 2)
            };
            KeyboardNavigation.SetTabIndex(targetSelectorButton, 10);
            AutomationProperties.SetName(
                targetSelectorButton,
                L.T("Выбрать игру для Boostix"));
            AutomationProperties.SetAutomationId(
                targetSelectorButton,
                "Boostix.Target.Select");
            AutomationProperties.SetHelpText(
                targetSelectorButton,
                L.T("Покажет запущенные игры. Boostix запомнит выбранную игру по её файлу EXE."));

            var targetContent = new Grid();
            targetContent.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });
            targetContent.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            targetContent.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });

            targetIconHost = new Border
            {
                Width = 48,
                Height = 48,
                CornerRadius = new CornerRadius(13),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center
            };
            targetIconHost.SetResourceReference(
                Border.BackgroundProperty,
                BoostixDesignTokens.SurfaceRaisedBrushKey);
            targetIconHost.SetResourceReference(
                Border.BorderBrushProperty,
                BoostixDesignTokens.BorderBrushKey);
            SetTargetIcon(null);
            targetContent.Children.Add(targetIconHost);

            var texts = new StackPanel
            {
                Margin = new Thickness(14, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            texts.Children.Add(BoostixUi.FieldLabel(L.T("Шаг 1. Игра")));
            targetNameText = MakeText(
                L.T("Выберите игру"),
                17,
                "#FFEDEFF2",
                FontWeights.SemiBold);
            targetNameText.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.TextBrushKey);
            targetNameText.FontFamily = LoadAppSemiboldFontFamily();
            targetNameText.TextTrimming = TextTrimming.CharacterEllipsis;
            targetNameText.Margin = new Thickness(0, 2, 0, 0);
            texts.Children.Add(targetNameText);

            targetDetailText = MakeText(
                L.T("Запустите игру, затем выберите её здесь"),
                BoostixDesignTokens.MetadataTextSize,
                "#FF8A929C",
                FontWeights.Normal);
            targetDetailText.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.MutedTextBrushKey);
            targetDetailText.TextTrimming = TextTrimming.CharacterEllipsis;
            targetDetailText.Margin = new Thickness(0, 2, 0, 0);
            texts.Children.Add(targetDetailText);
            Grid.SetColumn(texts, 1);
            targetContent.Children.Add(texts);

            var action = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            targetActionText = MakeText(
                L.T("Выбрать"),
                13.5,
                ProductBrand.AccentTextHex,
                FontWeights.SemiBold);
            targetActionText.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.AccentTextBrushKey);
            targetActionText.FontFamily = LoadAppSemiboldFontFamily();
            targetActionText.VerticalAlignment = VerticalAlignment.Center;
            action.Children.Add(targetActionText);
            FrameworkElement chevron = BoostixIcons.CreateThemed(
                BoostixIcons.ChevronRight,
                16,
                BoostixDesignTokens.AccentTextBrushKey);
            chevron.Margin = new Thickness(4, 1, 0, 0);
            action.Children.Add(chevron);
            Grid.SetColumn(action, 2);
            targetContent.Children.Add(action);

            targetSelectorButton.Content = targetContent;
            targetSelectorButton.Click += delegate { OpenTargetSelector(); };
            targetSelectorButton.MouseEnter += delegate
            {
                AnimateBrush(
                    targetBorder,
                    BoostixDesignTokens.SecondaryText,
                    BoostixDesignTokens.MotionStandardMilliseconds);
                AnimateBrush(
                    targetBackground,
                    BoostixDesignTokens.SurfaceRaised,
                    BoostixDesignTokens.MotionStandardMilliseconds);
            };
            targetSelectorButton.MouseLeave += delegate
            {
                AnimateBrush(
                    targetBorder,
                    BoostixDesignTokens.Border,
                    BoostixDesignTokens.MotionSlowMilliseconds);
                AnimateBrush(
                    targetBackground,
                    BoostixDesignTokens.Surface,
                    BoostixDesignTokens.MotionSlowMilliseconds);
            };
            return targetSelectorButton;
        }

        private void SetTargetIcon(string executablePath)
        {
            if (targetIconHost == null)
            {
                return;
            }
            ImageSource icon = BoostixUi.ExecutableIcon(executablePath);
            if (icon != null)
            {
                var image = new Image
                {
                    Source = icon,
                    Width = 32,
                    Height = 32,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                targetIconHost.Child = image;
                return;
            }
            FrameworkElement glyph = BoostixIcons.CreateThemed(
                BoostixIcons.Gamepad,
                24,
                BoostixDesignTokens.AccentTextBrushKey);
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            targetIconHost.Child = glyph;
        }

        private Border BuildBoostCard()
        {
            var card = BoostixUi.Card(new Thickness(14, 12, 14, 14));
            card.CornerRadius = new CornerRadius(BoostixDesignTokens.RadiusLarge);
            AutomationProperties.SetAutomationId(card, "Boostix.BoostCard");
            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1, GridUnitType.Star)
            });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock step = BoostixUi.FieldLabel(L.T("Шаг 2. Boost"));
            step.HorizontalAlignment = HorizontalAlignment.Left;
            layout.Children.Add(step);

            FrameworkElement gauge = BuildBoostButton();
            gauge.HorizontalAlignment = HorizontalAlignment.Center;
            gauge.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(gauge, 1);
            layout.Children.Add(gauge);

            caption = MakeText(
                L.T("Boost выключен"),
                15,
                "#FFB4BBC3",
                FontWeights.SemiBold);
            caption.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.SecondaryTextBrushKey);
            caption.FontFamily = LoadAppSemiboldFontFamily();
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            caption.TextAlignment = TextAlignment.Center;
            AutomationProperties.SetName(caption, L.T("Состояние Boost"));
            AutomationProperties.SetAutomationId(caption, "Boostix.Status");
            AutomationProperties.SetLiveSetting(caption, AutomationLiveSetting.Polite);
            Grid.SetRow(caption, 2);
            layout.Children.Add(caption);

            var hint = MakeText(
                L.T("Нажмите на датчик или Enter"),
                BoostixDesignTokens.MetadataTextSize,
                "#FF8A929C",
                FontWeights.Normal);
            hint.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.MutedTextBrushKey);
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.Margin = new Thickness(0, 3, 0, 0);
            AutomationProperties.SetAutomationId(hint, "Boostix.Hint");
            Grid.SetRow(hint, 3);
            layout.Children.Add(hint);
            card.Child = layout;
            return card;
        }

        private Border BuildHomeSystemCard()
        {
            var card = BoostixUi.Card(new Thickness(20, 18, 20, 12));
            card.CornerRadius = new CornerRadius(BoostixDesignTokens.RadiusLarge);
            AutomationProperties.SetAutomationId(card, "Boostix.SystemCard");
            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1, GridUnitType.Star)
            });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // One plain verdict first: can I play right now?
            var verdict = new Grid();
            verdict.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            verdict.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            homeVerdictDot = new Ellipse
            {
                Width = 10,
                Height = 10,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 12, 0)
            };
            verdict.Children.Add(homeVerdictDot);
            var verdictTexts = new StackPanel();
            homeChecksText = MakeText(
                L.T("Проверяем компьютер…"),
                17,
                "#FFEDEFF2",
                FontWeights.SemiBold);
            homeChecksText.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.TextBrushKey);
            homeChecksText.FontFamily = LoadAppSemiboldFontFamily();
            homeChecksText.TextWrapping = TextWrapping.Wrap;
            homeChecksText.TextTrimming = TextTrimming.None;
            AutomationProperties.SetAutomationId(homeChecksText, "Boostix.SystemChecks");
            AutomationProperties.SetLiveSetting(homeChecksText, AutomationLiveSetting.Polite);
            verdictTexts.Children.Add(homeChecksText);
            homeChecksDetail = MakeText(
                L.T("Это займёт пару секунд"),
                BoostixDesignTokens.BodyTextSize,
                "#FF8A929C",
                FontWeights.Normal);
            homeChecksDetail.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.MutedTextBrushKey);
            homeChecksDetail.TextWrapping = TextWrapping.Wrap;
            homeChecksDetail.TextTrimming = TextTrimming.None;
            homeChecksDetail.Margin = new Thickness(0, 3, 0, 0);
            verdictTexts.Children.Add(homeChecksDetail);
            Grid.SetColumn(verdictTexts, 1);
            verdict.Children.Add(verdictTexts);
            layout.Children.Add(verdict);

            var rows = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
            homeMeters = new StackPanel();
            AutomationProperties.SetAutomationId(homeMeters, "Boostix.SystemMeters");
            rows.Children.Add(homeMeters);
            liveStateText = AddStatusRow(rows, L.T("Сеанс"), L.T("Игра не выбрана"));
            liveTimerText = AddStatusRow(rows, L.T("Время в игре"), "00:00");
            Grid.SetRow(rows, 1);
            layout.Children.Add(rows);

            Button details = BoostixUi.ActionButton(
                L.T("Подробнее о проверке"),
                BoostixButtonKind.Ghost,
                null);
            details.MinWidth = 0;
            details.HorizontalAlignment = HorizontalAlignment.Left;
            details.Padding = new Thickness(10, 0, 10, 0);
            details.Margin = new Thickness(-10, 0, 0, 0);
            AutomationProperties.SetName(details, L.T("Открыть раздел «Проверка ПК»"));
            AutomationProperties.SetAutomationId(details, "Boostix.OpenSystem");
            KeyboardNavigation.SetTabIndex(details, 12);
            details.Click += delegate
            {
                if (boostCenterOverlay != null)
                {
                    boostCenterOverlay.OpenReadiness(false);
                }
            };
            Grid.SetRow(details, 3);
            layout.Children.Add(details);

            card.Child = layout;
            UpdateHomeSystemCard();
            return card;
        }

        private void UpdateHomeSystemCard()
        {
            if (homeMeters == null || homeChecksText == null || homeChecksDetail == null)
            {
                return;
            }

            DiagnosticSnapshot snapshot = latestDiagnosticSnapshot;
            homeMeters.Children.Clear();
            if (snapshot != null && snapshot.MemoryAvailable && snapshot.PhysicalTotalBytes > 0)
            {
                AddHomeMeter(
                    L.T("Свободно памяти"),
                    snapshot.PhysicalAvailableBytes,
                    snapshot.PhysicalTotalBytes,
                    snapshot.Pressure);
            }

            BoostPreflightReport report = latestPreflight;
            string verdict;
            string detail;
            Color color;
            if (report == null)
            {
                verdict = L.T("Проверяем компьютер…");
                detail = L.T("Это займёт пару секунд");
                color = BoostixDesignTokens.MutedText;
            }
            else
            {
                BoostCheckResult blocked = report.Checks.FirstOrDefault(
                    check => check.Severity == BoostCheckSeverity.Blocked);
                BoostCheckResult warning = report.Checks.FirstOrDefault(
                    check => check.Severity == BoostCheckSeverity.Warning);
                if (blocked != null)
                {
                    verdict = L.T("Boost сейчас не запустится");
                    detail = L.T("Причина: ") + LowerFirst(BoostixUi.DisplayCase(blocked.Title));
                    color = BoostixDesignTokens.Error;
                }
                else if (warning != null)
                {
                    verdict = L.T("Можно играть, есть замечание");
                    detail = L.T("Замечание: ") + LowerFirst(BoostixUi.DisplayCase(warning.Title));
                    color = BoostixDesignTokens.Warning;
                }
                else
                {
                    verdict = L.T("Компьютер готов к игре");
                    detail = L.T("Все проверки пройдены");
                    color = BoostixDesignTokens.Success;
                }
            }
            homeChecksText.Text = verdict;
            homeChecksDetail.Text = detail;
            if (homeVerdictDot != null)
            {
                homeVerdictDot.Fill = BoostixDesignTokens.Brush(color);
            }
            AutomationProperties.SetName(homeChecksText, verdict + ". " + detail);
        }

        private static string LowerFirst(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 2 || char.IsUpper(text[1]))
            {
                return text ?? string.Empty;
            }
            return char.ToLower(text[0], CultureInfo.CurrentCulture) + text.Substring(1);
        }

        private void AddHomeMeter(
            string label,
            long freeBytes,
            long totalBytes,
            DiagnosticPressureLevel pressure)
        {
            double fraction = totalBytes > 0
                ? Math.Max(0, Math.Min(1, (double)freeBytes / totalBytes))
                : 0;
            var row = new Grid
            {
                Margin = new Thickness(0, 0, 0, 12)
            };
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var name = MakeText(
                label,
                BoostixDesignTokens.BodyTextSize,
                "#FFB4BBC3",
                FontWeights.Normal);
            name.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.SecondaryTextBrushKey);
            row.Children.Add(name);
            string valueText = FormatMemoryCompact(freeBytes) + L.T(" из ") +
                FormatMemoryCompact(totalBytes);
            var value = MakeText(
                valueText,
                BoostixDesignTokens.BodyTextSize,
                "#FFEDEFF2",
                FontWeights.SemiBold);
            value.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.TextBrushKey);
            value.FontFamily = LoadAppSemiboldFontFamily();
            Grid.SetColumn(value, 1);
            row.Children.Add(value);

            Color fill = pressure == DiagnosticPressureLevel.Critical
                ? BoostixDesignTokens.Error
                : (pressure == DiagnosticPressureLevel.Elevated
                    ? BoostixDesignTokens.Warning
                    : BoostixDesignTokens.SecondaryText);
            Grid bar = BoostixUi.ProgressBar(fraction, fill, 4);
            bar.Margin = new Thickness(0, 7, 0, 0);
            Grid.SetRow(bar, 1);
            Grid.SetColumnSpan(bar, 2);
            row.Children.Add(bar);
            AutomationProperties.SetName(row, label + ": " + valueText);
            homeMeters.Children.Add(row);
        }

        private static string DescribePressure(DiagnosticPressureLevel pressure)
        {
            switch (pressure)
            {
                case DiagnosticPressureLevel.Normal:
                    return L.T("Норма");
                case DiagnosticPressureLevel.Elevated:
                    return L.T("Повышенная");
                case DiagnosticPressureLevel.Critical:
                    return L.T("Критично");
                default:
                    return L.T("Нет данных");
            }
        }

        private static Color PressureColor(DiagnosticPressureLevel pressure)
        {
            switch (pressure)
            {
                case DiagnosticPressureLevel.Normal:
                    return BoostixDesignTokens.Success;
                case DiagnosticPressureLevel.Elevated:
                    return BoostixDesignTokens.Warning;
                case DiagnosticPressureLevel.Critical:
                    return BoostixDesignTokens.Error;
                default:
                    return BoostixDesignTokens.MutedText;
            }
        }

        private TextBlock AddStatusRow(Panel host, string label, string value)
        {
            var row = new Grid
            {
                Margin = new Thickness(0, 0, 0, 10)
            };
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = MakeText(
                label,
                BoostixDesignTokens.BodyTextSize,
                "#FFB4BBC3",
                FontWeights.Normal);
            name.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.SecondaryTextBrushKey);
            row.Children.Add(name);
            var valueText = MakeText(
                value,
                BoostixDesignTokens.BodyTextSize,
                "#FFEDEFF2",
                FontWeights.SemiBold);
            valueText.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.TextBrushKey);
            valueText.FontFamily = LoadAppSemiboldFontFamily();
            valueText.TextTrimming = TextTrimming.CharacterEllipsis;
            AutomationProperties.SetLiveSetting(
                valueText,
                AutomationLiveSetting.Polite);
            Grid.SetColumn(valueText, 1);
            row.Children.Add(valueText);
            host.Children.Add(row);
            return valueText;
        }

        private void OpenTargetSelector()
        {
            if (targetSelectorButton == null || boostActive || animationRunning)
            {
                return;
            }

            var menu = new ContextMenu
            {
                PlacementTarget = targetSelectorButton,
                Placement = PlacementMode.Bottom,
                HorizontalOffset = 0,
                VerticalOffset = 6,
                MinWidth = Math.Max(320, targetSelectorButton.ActualWidth),
                Style = BuildTargetMenuStyle(),
                FontFamily = LoadAppFontFamily(),
                FontSize = BoostixDesignTokens.BodyTextSize,
                HasDropShadow = false
            };
            AutomationProperties.SetName(menu, L.T("Запущенные игры и программы"));
            Style itemStyle = BuildTargetMenuItemStyle();

            IList<GameTargetCandidate> candidates =
                gameTargetService.EnumerateCandidates();
            if (candidates.Count == 0)
            {
                var empty = MakeText(
                    L.T("Запущенных игр не нашли. Запустите игру и откройте список снова."),
                    BoostixDesignTokens.BodyTextSize,
                    "#FF8A929C",
                    FontWeights.Normal);
                empty.Foreground = BoostixDesignTokens.Brush(
                    BoostixDesignTokens.MutedText);
                empty.TextWrapping = TextWrapping.Wrap;
                empty.MaxWidth = 300;
                menu.Items.Add(new MenuItem
                {
                    Header = empty,
                    IsEnabled = false,
                    Style = itemStyle
                });
            }
            else
            {
                foreach (GameTargetCandidate candidate in candidates)
                {
                    GameTargetCandidate selectedCandidate = candidate;
                    var header = new Grid
                    {
                        MinWidth = 280
                    };
                    header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    header.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = new GridLength(1, GridUnitType.Star)
                    });
                    header.Children.Add(BuildCandidateIcon(
                        selectedCandidate.Identity.ExecutablePath));
                    var texts = new StackPanel
                    {
                        Margin = new Thickness(12, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var name = MakeText(
                        selectedCandidate.DisplayName,
                        13.5,
                        "#FFEDEFF2",
                        FontWeights.SemiBold);
                    name.Foreground = BoostixDesignTokens.Brush(
                        BoostixDesignTokens.Text);
                    name.FontFamily = LoadAppSemiboldFontFamily();
                    texts.Children.Add(name);
                    var detail = MakeText(
                        System.IO.Path.GetFileName(
                            selectedCandidate.Identity.ExecutablePath),
                        BoostixDesignTokens.MetadataTextSize,
                        "#FF8A929C",
                        FontWeights.Normal);
                    detail.Foreground = BoostixDesignTokens.Brush(
                        BoostixDesignTokens.MutedText);
                    detail.Margin = new Thickness(0, 2, 0, 0);
                    texts.Children.Add(detail);
                    Grid.SetColumn(texts, 1);
                    header.Children.Add(texts);

                    var item = new MenuItem
                    {
                        Header = header,
                        Style = itemStyle,
                        ToolTip = selectedCandidate.Identity.ExecutablePath
                    };
                    AutomationProperties.SetName(
                        item,
                        L.T("Выбрать ") + selectedCandidate.DisplayName +
                        ", PID " + selectedCandidate.Identity.ProcessId);
                    item.Click += delegate
                    {
                        GameProcessSnapshot current;
                        string error;
                        if (!gameTargetService.TryResolve(
                                selectedCandidate.Identity,
                                out current,
                                out error))
                        {
                            ClearSelectedTarget(
                                L.T("Список устарел. Откройте выбор игры снова"));
                            return;
                        }
                        SelectGameTarget(
                            selectedCandidate.Identity,
                            selectedCandidate.DisplayName,
                            true);
                    };
                    menu.Items.Add(item);
                }
            }

            menu.Closed += delegate
            {
                targetSelectorButton.ContextMenu = null;
                targetSelectorButton.Focus();
            };
            targetSelectorButton.ContextMenu = menu;
            menu.IsOpen = true;
        }

        private static FrameworkElement BuildCandidateIcon(string executablePath)
        {
            var frame = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(9),
                Background = BoostixDesignTokens.Brush(BoostixDesignTokens.SurfaceRaised),
                VerticalAlignment = VerticalAlignment.Center
            };
            ImageSource icon = BoostixUi.ExecutableIcon(executablePath);
            if (icon != null)
            {
                var image = new Image
                {
                    Source = icon,
                    Width = 22,
                    Height = 22,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                frame.Child = image;
            }
            else
            {
                FrameworkElement glyph = BoostixIcons.Create(
                    BoostixIcons.Gamepad,
                    18,
                    BoostixDesignTokens.MutedText);
                glyph.HorizontalAlignment = HorizontalAlignment.Center;
                glyph.VerticalAlignment = VerticalAlignment.Center;
                frame.Child = glyph;
            }
            return frame;
        }

        private static Style BuildTargetMenuStyle()
        {
            string xaml =
                "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" " +
                "TargetType=\"{x:Type ContextMenu}\">" +
                "<Setter Property=\"OverridesDefaultStyle\" Value=\"True\"/>" +
                "<Setter Property=\"SnapsToDevicePixels\" Value=\"True\"/>" +
                "<Setter Property=\"Template\"><Setter.Value>" +
                "<ControlTemplate TargetType=\"{x:Type ContextMenu}\">" +
                "<Border Background=\"" + ColorToXaml(BoostixDesignTokens.Surface) + "\" " +
                "BorderBrush=\"" + ColorToXaml(BoostixDesignTokens.Border) + "\" " +
                "BorderThickness=\"1\" CornerRadius=\"10\" Padding=\"6\">" +
                "<ScrollViewer MaxHeight=\"360\" VerticalScrollBarVisibility=\"Auto\" " +
                "HorizontalScrollBarVisibility=\"Disabled\">" +
                "<StackPanel IsItemsHost=\"True\" " +
                "KeyboardNavigation.DirectionalNavigation=\"Cycle\"/>" +
                "</ScrollViewer></Border>" +
                "</ControlTemplate></Setter.Value></Setter></Style>";
            return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
        }

        private static Style BuildTargetMenuItemStyle()
        {
            string xaml =
                "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" " +
                "TargetType=\"{x:Type MenuItem}\">" +
                "<Setter Property=\"OverridesDefaultStyle\" Value=\"True\"/>" +
                "<Setter Property=\"Cursor\" Value=\"Hand\"/>" +
                "<Setter Property=\"Template\"><Setter.Value>" +
                "<ControlTemplate TargetType=\"{x:Type MenuItem}\">" +
                "<Border x:Name=\"Bd\" Background=\"Transparent\" CornerRadius=\"8\" " +
                "Padding=\"10,8,14,8\" SnapsToDevicePixels=\"True\">" +
                "<ContentPresenter ContentSource=\"Header\" RecognizesAccessKey=\"False\"/>" +
                "</Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property=\"IsHighlighted\" Value=\"True\">" +
                "<Setter TargetName=\"Bd\" Property=\"Background\" Value=\"" +
                    ColorToXaml(BoostixDesignTokens.Hover) + "\"/>" +
                "</Trigger>" +
                "<Trigger Property=\"IsEnabled\" Value=\"False\">" +
                "<Setter Property=\"Cursor\" Value=\"Arrow\"/>" +
                "</Trigger>" +
                "</ControlTemplate.Triggers>" +
                "</ControlTemplate></Setter.Value></Setter></Style>";
            return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
        }

        private static string ColorToXaml(Color color)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "#{0:X2}{1:X2}{2:X2}{3:X2}",
                color.A,
                color.R,
                color.G,
                color.B);
        }

        private void SelectGameTarget(
            GameTargetIdentity identity,
            string displayName,
            bool saveProfile)
        {
            if (identity == null)
            {
                return;
            }
            selectedTarget = identity;
            string safeName = string.IsNullOrWhiteSpace(displayName)
                ? identity.ProcessName
                : displayName.Trim();
            targetNameText.Text = safeName;
            targetDetailText.Text = L.T("Игра запущена");
            targetDetailText.ToolTip = identity.ExecutablePath;
            SetTargetIcon(identity.ExecutablePath);
            if (targetActionText != null)
            {
                targetActionText.Text = L.T("Сменить");
            }
            AutomationProperties.SetName(
                targetSelectorButton,
                L.T("Выбрана игра ") + safeName);
            AutomationProperties.SetItemStatus(
                targetSelectorButton,
                L.T("Выбрано"));

            if (saveProfile)
            {
                try
                {
                    bool preserveAutoBoost = gameProfileStore.Load().Profiles.Any(
                        profile =>
                            GameExecutablePath.AreEquivalent(
                                profile.ExecutablePath,
                                identity.ExecutablePath) &&
                            profile.AutoBoost);
                    gameProfileStore.Upsert(
                        identity,
                        safeName,
                        preserveAutoBoost);
                    gameProfilesDirty = true;
                }
                catch (Exception ex)
                {
                    CrashLog.Write("Could not save the selected game profile.", ex);
                }
            }
            UpdateLiveSessionSummary();
        }

        private void ClearSelectedTarget(string detail)
        {
            selectedTarget = null;
            autoBoostStartPending = false;
            if (targetNameText == null || targetDetailText == null)
            {
                return;
            }
            targetNameText.Text = L.T("Выберите игру");
            targetDetailText.Text = string.IsNullOrWhiteSpace(detail)
                ? L.T("Запустите игру, затем выберите её здесь")
                : detail;
            targetDetailText.ToolTip = null;
            SetTargetIcon(null);
            if (targetActionText != null)
            {
                targetActionText.Text = L.T("Выбрать");
            }
            AutomationProperties.SetName(
                targetSelectorButton,
                L.T("Выбрать игру для Boostix"));
            AutomationProperties.SetItemStatus(
                targetSelectorButton,
                string.Empty);
            UpdateLiveSessionSummary();
        }

        private void TrySelectSavedAutoBoostTarget()
        {
            if (demoMode || safeMode || boostActive || animationRunning ||
                benchmarkCancellation != null || selectedTarget != null)
            {
                return;
            }

            GameTargetIdentity match = null;
            GameProfile matchedProfile = null;
            foreach (GameTargetCandidate candidate in
                gameTargetService.EnumerateCandidates())
            {
                GameTargetIdentity identity;
                GameProfile profile;
                string ignored;
                if (!gameTargetService.TryMatchSavedAutoBoostProfile(
                        candidate.Identity.ProcessId,
                        gameProfileStore,
                        out identity,
                        out profile,
                        out ignored))
                {
                    continue;
                }
                if (match != null)
                {
                    // More than one opted-in game is running. Never guess.
                    return;
                }
                match = identity;
                matchedProfile = profile;
            }
            if (match == null || matchedProfile == null)
            {
                return;
            }

            SelectGameTarget(match, matchedProfile.DisplayName, false);
            autoBoostStartPending = true;
            Dispatcher.BeginInvoke(
                new Action(RequestBoostStart),
                DispatcherPriority.ApplicationIdle);
        }

        private void StartAutoBoostDiscovery()
        {
            if (demoMode || safeMode)
            {
                return;
            }
            if (autoBoostDiscoveryTimer == null)
            {
                autoBoostDiscoveryTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3)
                };
                autoBoostDiscoveryTimer.Tick += delegate
                {
                    if (!boostActive && !animationRunning &&
                        selectedTarget != null)
                    {
                        GameProcessSnapshot current;
                        string error;
                        if (!gameTargetService.TryResolve(
                                selectedTarget,
                                out current,
                                out error))
                        {
                            ClearSelectedTarget(
                                L.T("Сохранённая игра сейчас не запущена"));
                        }
                    }
                    TrySelectSavedAutoBoostTarget();
                };
            }
            autoBoostDiscoveryTimer.Start();
        }

        private StackPanel BuildWindowControls()
        {
            var controls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Height = TitleControlSize,
                Margin = new Thickness(0, 14, 14, 0)
            };
            AutomationProperties.SetAutomationId(
                controls,
                "Boostix.TitleBar");

            minimizeButton = MakeWindowButton(L.T("Свернуть"), false);
            var minimize = minimizeButton;
            KeyboardNavigation.SetTabIndex(minimize, 90);
            AutomationProperties.SetAutomationId(
                minimize,
                "Boostix.Minimize");
            minimize.Click += delegate { MinimizeWindow(); };
            controls.Children.Add(minimize);

            closeButton = MakeWindowButton(L.T("Закрыть"), true);
            var close = closeButton;
            close.Margin = new Thickness(4, 0, 0, 0);
            KeyboardNavigation.SetTabIndex(close, 91);
            AutomationProperties.SetAutomationId(
                close,
                "Boostix.Close");
            close.Click += delegate { Close(); };
            controls.Children.Add(close);
            return controls;
        }

        private FrameworkElement BuildBoostButton()
        {
            boostGauge = new BoostGauge();
            boostButton = new Button();
            boostButton.Width = BoostGauge.GaugeWidth;
            boostButton.Height = BoostGauge.GaugeHeight;
            boostButton.HorizontalAlignment = HorizontalAlignment.Center;
            boostButton.VerticalAlignment = VerticalAlignment.Center;
            boostButton.BorderThickness = new Thickness(0);
            boostButton.Background = Brushes.Transparent;
            boostButton.Cursor = Cursors.Hand;
            boostButton.Template = MakeTransparentButtonTemplate(BoostixDesignTokens.RadiusLarge);
            boostButton.FocusVisualStyle = BoostixUi.FocusVisual(BoostixDesignTokens.RadiusLarge);
            AutomationProperties.SetName(boostButton, L.T("Активировать Boost производительности"));
            AutomationProperties.SetHelpText(
                boostButton,
                L.T("Готовит Windows к игре. Все изменения отменяются при остановке."));
            AutomationProperties.SetAutomationId(
                boostButton,
                "Boostix.Activate");
            KeyboardNavigation.SetTabIndex(boostButton, 11);
            boostButton.Content = boostGauge;
            boostButton.Click += BoostButtonClick;
            boostButton.MouseEnter += BoostButtonMouseEnter;
            boostButton.MouseLeave += BoostButtonMouseLeave;
            SetGaugeState(BoostGaugeState.Idle);
            return boostButton;
        }

        private void SetGaugeState(BoostGaugeState state)
        {
            if (boostGauge == null)
            {
                return;
            }
            boostGauge.SetState(state);
            if (state == BoostGaugeState.Idle)
            {
                boostGauge.SetReadout(L.T("Пуск"), string.Empty);
            }
        }

        /// <summary>
        /// Shows "What's new" once after an update. Demo launches never read or
        /// write the per-user marker, and an elevated process does not write
        /// to LocalAppData (same rule as the crash log).
        /// </summary>
        private void ShowWhatsNewIfUpdated()
        {
            string version = ProductBrand.ProductVersion;
            if (demoMode)
            {
                if (HasLaunchArgument(launchArguments, "--demo-whats-new"))
                {
                    whatsNewOverlay.Show(version, WhatsNewContent.LoadHighlights(version));
                }
                return;
            }
            if (CrashLog.IsCurrentProcessElevated())
            {
                return;
            }
            try
            {
                string directory = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    ProductBrand.DataDirectoryName);
                string markerPath = System.IO.Path.Combine(directory, "last-run-version.txt");
                string lastSeen = File.Exists(markerPath)
                    ? File.ReadAllText(markerPath, Encoding.UTF8).Trim()
                    : null;
                bool show = WhatsNewContent.ShouldShow(
                    lastSeen,
                    version,
                    WhatsNewContent.HasEarlierData(directory));
                if (!string.Equals(lastSeen, version, StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(directory);
                    BoostSessionReportStore.WriteAllTextAtomic(markerPath, version);
                }
                if (show)
                {
                    IList<string> highlights = WhatsNewContent.LoadHighlights(version);
                    if (highlights.Count > 0)
                    {
                        whatsNewOverlay.Show(version, highlights);
                    }
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("Could not check the last run version.", ex);
            }
        }

        private void LoadBoostPreferences()
        {
            var values = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = GetPreferencesPath();
                if (File.Exists(path))
                {
                    foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        int separator = line.IndexOf('=');
                        bool parsed;
                        if (separator > 0 && bool.TryParse(line.Substring(separator + 1).Trim(), out parsed))
                        {
                            values[line.Substring(0, separator).Trim()] = parsed;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("Could not read Boost preferences.", ex);
            }

            // Closing third-party applications is always opt-in for a safe default.
            centerSettings.KeepDiscord = !values.ContainsKey("KeepDiscord") ||
                GetPreference(values, "KeepDiscord");
            centerSettings.KeepEpic = !values.ContainsKey("KeepEpic") ||
                GetPreference(values, "KeepEpic");
            centerSettings.KeepSteam = !values.ContainsKey("KeepSteam") ||
                GetPreference(values, "KeepSteam");
            centerSettings.CheckBeforeBoost = values.ContainsKey("CheckBeforeBoost")
                ? GetPreference(values, "CheckBeforeBoost")
                : true;
            centerSettings.KeepOneDrive = !values.ContainsKey("KeepOneDrive") ||
                GetPreference(values, "KeepOneDrive");
            centerSettings.KeepTeams = !values.ContainsKey("KeepTeams") ||
                GetPreference(values, "KeepTeams");
            centerSettings.KeepWallpaper = !values.ContainsKey("KeepWallpaper") ||
                GetPreference(values, "KeepWallpaper");
            centerSettings.KeepNvidiaOverlay = !values.ContainsKey("KeepNvidiaOverlay") ||
                GetPreference(values, "KeepNvidiaOverlay");
            centerSettings.GlobalHotkey = !values.ContainsKey("GlobalHotkey") ||
                GetPreference(values, "GlobalHotkey");
            centerSettings.MinimizeToTray = GetPreference(values, "MinimizeToTray");
            centerSettings.GameSignals = !values.ContainsKey("GameSignals") ||
                GetPreference(values, "GameSignals");
            // Windows' own Run key decides start-up; test launches never touch it.
            centerSettings.StartWithWindows = !demoMode && AutostartRegistration.IsEnabled();
            // The theme itself was applied at start-up, before any colour was read.
            centerSettings.Theme = BoostixDesignTokens.PaletteId;
            // A choice saved while Boost ran waits for the next start; test
            // launches show the language they run in.
            centerSettings.Language = demoMode
                ? L.Language
                : ReadLanguagePreference() ?? L.Language;
        }

        private void ApplyAutostartPreference()
        {
            if (demoMode || centerSettings.StartWithWindows == AutostartRegistration.IsEnabled())
            {
                return;
            }
            if (!AutostartRegistration.Set(
                    centerSettings.StartWithWindows,
                    Assembly.GetExecutingAssembly().Location))
            {
                centerSettings.StartWithWindows = AutostartRegistration.IsEnabled();
                if (boostCenterOverlay != null)
                {
                    boostCenterOverlay.SetSettings(centerSettings);
                }
            }
        }

        private void SaveBoostPreferences()
        {
            try
            {
                string path = GetPreferencesPath();
                string content = string.Join(
                    Environment.NewLine,
                    new[]
                    {
                        "CheckBeforeBoost=" + centerSettings.CheckBeforeBoost,
                        "KeepDiscord=" + centerSettings.KeepDiscord,
                        "KeepEpic=" + centerSettings.KeepEpic,
                        "KeepSteam=" + centerSettings.KeepSteam,
                        "KeepOneDrive=" + centerSettings.KeepOneDrive,
                        "KeepTeams=" + centerSettings.KeepTeams,
                        "KeepWallpaper=" + centerSettings.KeepWallpaper,
                        "KeepNvidiaOverlay=" + centerSettings.KeepNvidiaOverlay,
                        "GlobalHotkey=" + centerSettings.GlobalHotkey,
                        "MinimizeToTray=" + centerSettings.MinimizeToTray,
                        "GameSignals=" + centerSettings.GameSignals,
                        "Theme=" + BoostixDesignTokens.PaletteId,
                        "Language=" + (centerSettings.Language ?? L.Language)
                    }) + Environment.NewLine;
                BoostSessionReportStore.WriteAllTextAtomic(
                    path,
                    content);
            }
            catch (Exception ex)
            {
                CrashLog.Write("Could not save Boost preferences.", ex);
            }
        }

        private static bool GetPreference(Dictionary<string, bool> values, string name)
        {
            bool value;
            return values.TryGetValue(name, out value) && value;
        }

        /// <summary>
        /// The theme id saved in the preferences file, or null.
        /// </summary>
        internal static string ReadThemePreference()
        {
            return ReadTextPreference("Theme");
        }

        /// <summary>
        /// The saved interface language, "ru" or "en", or null.
        /// </summary>
        internal static string ReadLanguagePreference()
        {
            string value = ReadTextPreference("Language");
            if (string.Equals(value, L.English, StringComparison.OrdinalIgnoreCase))
            {
                return L.English;
            }
            if (string.Equals(value, L.Russian, StringComparison.OrdinalIgnoreCase))
            {
                return L.Russian;
            }
            return null;
        }

        private static string ReadTextPreference(string name)
        {
            string prefix = name + "=";
            try
            {
                string path = GetPreferencesPath();
                if (!File.Exists(path))
                {
                    return null;
                }
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return line.Substring(prefix.Length).Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("Could not read the saved " + name + " preference.", ex);
            }
            return null;
        }

        private static string GetPreferencesPath()
        {
            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductBrand.DataDirectoryName,
                "boost-preferences.ini");
        }

        private void BoostButtonMouseEnter(object sender, MouseEventArgs e)
        {
            if (boostGauge != null)
            {
                boostGauge.SetHover(true);
            }
        }

        private void BoostButtonMouseLeave(object sender, MouseEventArgs e)
        {
            if (boostGauge != null)
            {
                boostGauge.SetHover(false);
            }
        }

        private void BoostButtonClick(object sender, RoutedEventArgs e)
        {
            ToggleBoost();
        }

        private void ToggleBoost()
        {
            if (animationRunning ||
                Interlocked.CompareExchange(ref benchmarkCaptureActive, 0, 0) != 0)
            {
                if (!animationRunning && caption != null)
                {
                    caption.Text = L.T("Дождитесь окончания замера");
                    caption.SetResourceReference(
                        TextBlock.ForegroundProperty,
                        BoostixDesignTokens.AccentTextBrushKey);
                }
                return;
            }

            if (boostActive)
            {
                StartBoostDeactivation();
            }
            else
            {
                RequestBoostStart();
            }
        }

        private void RequestBoostStart()
        {
            if (demoMode)
            {
                StartBoost();
                return;
            }
            GameProcessSnapshot targetSnapshot;
            string targetError = L.T("Выбранная игра больше не запущена.");
            if (selectedTarget == null ||
                !gameTargetService.TryResolve(
                    selectedTarget,
                    out targetSnapshot,
                    out targetError))
            {
                if (selectedTarget != null)
                {
                    ClearSelectedTarget(L.T("Выбранная игра закрыта или была перезапущена"));
                }
                caption.Text = L.T("Сначала выберите игру");
                caption.SetResourceReference(
                    TextBlock.ForegroundProperty,
                    BoostixDesignTokens.AccentTextBrushKey);
                OpenTargetSelector();
                return;
            }
            if (centerSettings.CheckBeforeBoost && !preflightAccepted)
            {
                QueuePreflight(true, true);
                return;
            }
            StartBoost();
        }

        private async void QueuePreflight(bool forBoost, bool showCenter)
        {
            int generation = Interlocked.Increment(ref preflightGeneration);
            preflightForBoost = forBoost;
            if (boostCenterOverlay != null && showCenter)
            {
                RememberBoostCenterFocusReturn(boostButton);
                boostCenterOverlay.OpenReadiness(forBoost);
                boostCenterOverlay.SetPreflight(null);
            }

            string optimizationStatus = "Unknown";
            if (optimizationOverlay != null)
            {
                optimizationStatus = optimizationOverlay.GetOptimizationStatus();
            }

            BoostPreflightReport report = null;
            DiagnosticSnapshot diagnostic = null;
            await Task.Run(delegate
            {
                report = BoostPreflightService.Run(
                    AppDomain.CurrentDomain.BaseDirectory,
                    optimizationStatus);
                diagnostic = DiagnosticSnapshotProvider.Capture();
            });

            if (generation != Interlocked.CompareExchange(ref preflightGeneration, 0, 0))
            {
                return;
            }
            latestPreflight = report;
            latestDiagnosticSnapshot = diagnostic;
            UpdateHomeSystemCard();
            if (boostCenterOverlay != null)
            {
                boostCenterOverlay.SetPreflight(report);
                boostCenterOverlay.SetDiagnosticSnapshot(diagnostic);
            }

            if (!forBoost)
            {
                return;
            }
            if (report != null && !report.HasWarnings)
            {
                if (boostActive || animationRunning)
                {
                    return;
                }
                preflightAccepted = true;
                if (boostCenterOverlay != null && boostCenterOverlay.IsOpen)
                {
                    boostCenterOverlay.HandleEscape();
                }
                StartBoost();
            }
            else if (!IsVisible || WindowState == WindowState.Minimized)
            {
                Notify(L.T("Перед запуском Boost есть замечания. Откройте Boostix."));
                RestoreWindow();
            }
        }

        private void StartBoost()
        {
            if (animationRunning || boostActive)
            {
                return;
            }
            if (!demoMode)
            {
                GameProcessSnapshot targetSnapshot;
                string targetError;
                if (selectedTarget == null ||
                    !gameTargetService.TryResolve(
                        selectedTarget,
                        out targetSnapshot,
                        out targetError))
                {
                    ClearSelectedTarget(
                        L.T("Игра перезапустилась. Выберите её снова"));
                    caption.Text = L.T("Игра недоступна");
                    caption.SetResourceReference(
                        TextBlock.ForegroundProperty,
                        BoostixDesignTokens.ErrorBrushKey);
                    return;
                }
            }
            preflightForBoost = false;
            Interlocked.Exchange(ref targetExitNotificationPending, 0);
            StopActiveBoostMaintenance();
            BeginSession(autoBoostStartPending ? "Auto" : "Manual");
            autoBoostStartPending = false;
            if (currentSession != null && selectedTarget != null)
            {
                currentSession.TargetName = selectedTarget.ProcessName;
            }
            animationRunning = true;
            departureFinished = false;
            boostReady = false;
            boostButton.IsEnabled = false;
            caption.Text = L.T("Запускаю Boost…");
            SetGaugeState(BoostGaugeState.Working);
            boostGauge.SetReadout(string.Empty, L.T("подготовка"));
            caption.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.AccentTextBrushKey);

            if (!LaunchBoostScript())
            {
                HandleBoostFailure(L.T("Boost не запущен"));
                return;
            }

            PlayDeparture();
        }

        private bool LaunchBoostScript()
        {
            if (demoMode)
            {
                var demoTimer = new DispatcherTimer();
                demoTimer.Interval = TimeSpan.FromMilliseconds(950);
                demoTimer.Tick += delegate
                {
                    demoTimer.Stop();
                    MarkBoostReady();
                };
                demoTimer.Start();
                return true;
            }

            try
            {
                string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string scriptPath = System.IO.Path.Combine(baseDirectory, "Boost-Session.ps1");
                if (!File.Exists(scriptPath))
                {
                    throw new FileNotFoundException(L.T("Boost-Session.ps1 не найден рядом с приложением."), scriptPath);
                }

                readinessSignalPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "Boostix-ready-" + Process.GetCurrentProcess().Id + ".flag");
                if (File.Exists(readinessSignalPath))
                {
                    File.Delete(readinessSignalPath);
                }

                string powershell = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell\\v1.0\\powershell.exe");
                var scriptArguments = new StringBuilder();
                scriptArguments.Append("-NoProfile -ExecutionPolicy Bypass -File \"");
                scriptArguments.Append(scriptPath);
                scriptArguments.Append("\"");
                if (!centerSettings.KeepDiscord)
                {
                    scriptArguments.Append(" -CloseDiscord");
                }
                if (!centerSettings.KeepEpic)
                {
                    scriptArguments.Append(" -CloseEpic");
                }
                if (!centerSettings.KeepSteam)
                {
                    scriptArguments.Append(" -CloseSteam");
                }
                if (!centerSettings.KeepOneDrive)
                {
                    scriptArguments.Append(" -CloseOneDrive");
                }
                if (!centerSettings.KeepTeams)
                {
                    scriptArguments.Append(" -CloseTeams");
                }
                if (!centerSettings.KeepWallpaper)
                {
                    scriptArguments.Append(" -CloseWallpaper");
                }
                if (!centerSettings.KeepNvidiaOverlay)
                {
                    scriptArguments.Append(" -CloseNvidiaOverlay");
                }
                if (currentSession != null)
                {
                    string resultPath = System.IO.Path.Combine(
                        BoostSessionReportStore.StateDirectory,
                        "Boost-Session-" + currentSession.SessionId + ".result");
                    scriptArguments.Append(" -ResultPath \"");
                    scriptArguments.Append(resultPath);
                    scriptArguments.Append("\"");
                }
                scriptArguments.Append(" -ReadySignalPath \"");
                scriptArguments.Append(readinessSignalPath);
                scriptArguments.Append("\"");

                var startInfo = new ProcessStartInfo();
                startInfo.FileName = powershell;
                startInfo.Arguments = scriptArguments.ToString();
                startInfo.WorkingDirectory = baseDirectory;
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                StopBoostProcess();
                boostProcess = Process.Start(startInfo);

                readinessDeadline = DateTime.UtcNow.AddSeconds(20);
                readinessTimer = new DispatcherTimer();
                readinessTimer.Interval = TimeSpan.FromMilliseconds(120);
                readinessTimer.Tick += ReadinessTimerTick;
                readinessTimer.Start();
                return true;
            }
            catch (Exception ex)
            {
                CrashLog.Write("Could not launch the Boost preparation script.", ex);
                StopBoostProcess();
                return false;
            }
        }

        private void ReadinessTimerTick(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(readinessSignalPath) && File.Exists(readinessSignalPath))
            {
                readinessTimer.Stop();
                TryDeleteReadinessSignal();
                MarkBoostReady();
                return;
            }

            bool processFailed = false;
            try
            {
                processFailed = boostProcess != null && boostProcess.HasExited && boostProcess.ExitCode != 0;
            }
            catch (InvalidOperationException) { }
            if (processFailed || DateTime.UtcNow >= readinessDeadline)
            {
                readinessTimer.Stop();
                HandleBoostFailure(L.T("Boost не запущен"));
            }
        }

        /// <summary>
        /// Ignition sweep: the needle runs to the end stop while the session
        /// script prepares Windows, then settles once both have finished.
        /// </summary>
        private void PlayDeparture()
        {
            boostGauge.SweepToEndStop(delegate
            {
                departureFinished = true;
                if (boostReady)
                {
                    PlayReturn();
                }
            });
        }

        private void MarkBoostReady()
        {
            if (boostReady || boostActive)
            {
                return;
            }
            boostReady = true;
            if (currentSession != null)
            {
                currentSession.Status = "Active";
                currentSession.AddAction(
                    L.T("ОДНОРАЗОВАЯ ПОДГОТОВКА"),
                    L.T("Закрыли программы, отмеченные в настройках."),
                    BoostActionOutcome.Changed);
                ImportBoostScriptResult(currentSession);
                SaveCurrentSession();
            }
            caption.Text = L.T("Boost включён");
            caption.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.SuccessBrushKey);
            if (departureFinished)
            {
                PlayReturn();
            }
        }

        private void PlayReturn()
        {
            boostGauge.SettleInBoostZone(CompleteBoostActivation);
        }

        private void CompleteBoostActivation()
        {
            boostActive = true;
            animationRunning = false;
            boostButton.IsEnabled = true;
            SetGaugeState(BoostGaugeState.Active);
            if (targetSelectorButton != null)
            {
                targetSelectorButton.IsEnabled = false;
            }
            SetBoostAutomationState(true);
            StartActiveBoostMaintenance();
            UpdateLiveSessionSummary();
        }

        private void HandleBoostFailure(string message)
        {
            if (readinessTimer != null)
            {
                readinessTimer.Stop();
            }
            TryDeleteReadinessSignal();
            StopActiveBoostMaintenance();
            StopBoostProcess();
            boostReady = false;
            departureFinished = false;
            boostGauge.SnapNeedle(false);
            Notify(message);
            caption.Text = message;
            caption.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.ErrorBrushKey);
            SetGaugeState(BoostGaugeState.Idle);
            animationRunning = false;
            boostButton.IsEnabled = true;
            if (targetSelectorButton != null)
            {
                targetSelectorButton.IsEnabled = true;
            }
            ImportBoostScriptResult(currentSession);
            CompleteCurrentSession("Failed", message);
        }

        private void StartBoostDeactivation(
            string sessionStatus = "Completed",
            string sessionReason = "Boost остановлен пользователем.")
        {
            deactivationSessionStatus = string.IsNullOrWhiteSpace(sessionStatus)
                ? "Completed"
                : sessionStatus;
            // Reasons passed by callers are already translated; L.T leaves
            // text that is not a Russian source string unchanged.
            deactivationSessionReason = L.T(sessionReason ?? string.Empty);
            animationRunning = true;
            boostButton.IsEnabled = false;
            boostActive = false;
            StopActiveBoostMaintenance();
            StopBoostProcess();
            caption.Text = L.T("Останавливаю Boost…");
            SetGaugeState(BoostGaugeState.Working);
            boostGauge.SetReadout(string.Empty, L.T("остановка"));
            caption.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.AccentTextBrushKey);
            boostGauge.SweepToRest(CompleteBoostDeactivation);
        }

        private void CompleteBoostDeactivation()
        {
            boostActive = false;
            boostReady = false;
            departureFinished = false;
            animationRunning = false;
            boostButton.IsEnabled = true;
            if (targetSelectorButton != null)
            {
                targetSelectorButton.IsEnabled = true;
            }
            caption.Text = L.T("Boost выключен");
            caption.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.SecondaryTextBrushKey);
            SetGaugeState(BoostGaugeState.Idle);
            SetBoostAutomationState(false);
            UpdateLiveSessionSummary();
            bool showCrashReport = string.Equals(
                deactivationSessionStatus,
                "TargetCrashed",
                StringComparison.OrdinalIgnoreCase);
            CompleteCurrentSession(
                deactivationSessionStatus,
                deactivationSessionReason);
            deactivationSessionStatus = "Completed";
            deactivationSessionReason = L.T("Boost остановлен пользователем.");
            preflightAccepted = false;

            if (showCrashReport && boostCenterOverlay != null)
            {
                boostCenterOverlay.SetSessionReport(lastSession);
                boostCenterOverlay.SetDiagnosticSnapshot(
                    latestDiagnosticSnapshot);
                boostCenterOverlay.SetSessionHistory(sessionHistory);
                RememberBoostCenterFocusReturn(boostButton);
                boostCenterOverlay.OpenReport();
            }
        }

        private void StartActiveBoostMaintenance()
        {
            int generation = AdvanceActiveMaintenanceGeneration();
            StartSessionGuard(generation);
            StartExactTargetExitWatcher(generation);
            if (activeBoostTimer == null)
            {
                activeBoostTimer = new DispatcherTimer();
                activeBoostTimer.Interval = TimeSpan.FromSeconds(1);
                activeBoostTimer.Tick += delegate { QueueActiveBoostMaintenance(); };
            }
            activeBoostTimer.Start();
            QueueActiveBoostMaintenance();
        }

        private void RefreshActiveBoostMaintenance()
        {
            // Preferences affect only a future preparation pass. Do not invalidate
            // the exact-target Session Guard that owns the current generation.
            QueueActiveBoostMaintenance();
        }

        private void StopActiveBoostMaintenance()
        {
            if (activeBoostTimer != null)
            {
                activeBoostTimer.Stop();
            }
            lock (activeMaintenanceSync)
            {
                Interlocked.Increment(ref activeMaintenanceGeneration);
                TryDeleteActiveBoostDemoSignal();
            }
            StopSessionGuard();
            StopExactTargetExitWatcher();
            RestoreOwnedTargetPriorities();
        }

        private void StartExactTargetExitWatcher(int generation)
        {
            StopExactTargetExitWatcher();
            if (demoMode || selectedTarget == null)
            {
                return;
            }
            GameTargetIdentity watchedIdentity = selectedTarget;
            Process process = null;
            try
            {
                process = Process.GetProcessById(watchedIdentity.ProcessId);
                DateTime actualStart = process.StartTime.ToUniversalTime();
                string actualPath =
                    GameExecutablePath.GetProcessImagePath(process);
                if (actualStart != watchedIdentity.ProcessStartTimeUtc ||
                    !GameExecutablePath.AreEquivalent(
                        actualPath,
                        watchedIdentity.ExecutablePath))
                {
                    process.Dispose();
                    return;
                }

                EventHandler handler = null;
                handler = delegate(object sender, EventArgs eventArgs)
                {
                    var exitedProcess = sender as Process;
                    DateTime exitedUtc = DateTime.UtcNow;
                    int? exitCode = null;
                    try
                    {
                        if (exitedProcess != null)
                        {
                            exitedUtc = exitedProcess.ExitTime.ToUniversalTime();
                            exitCode = exitedProcess.ExitCode;
                        }
                    }
                    catch
                    {
                    }
                    NotifySelectedTargetEnded(
                        generation,
                        L.T("Выбранная игра завершилась."),
                        exitedUtc,
                        exitCode);
                };
                exactTargetExitWatcher = process;
                exactTargetExitHandler = handler;
                process.Exited += handler;
                process.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                if (process != null)
                {
                    process.Dispose();
                }
                exactTargetExitWatcher = null;
                exactTargetExitHandler = null;
                AppendActiveBoostLog(
                    "Exact target exit watcher was unavailable: " +
                    ex.GetType().Name + ".");
            }
        }

        private void StopExactTargetExitWatcher()
        {
            Process watcher = exactTargetExitWatcher;
            EventHandler handler = exactTargetExitHandler;
            exactTargetExitWatcher = null;
            exactTargetExitHandler = null;
            if (watcher == null)
            {
                return;
            }
            try
            {
                if (handler != null)
                {
                    watcher.Exited -= handler;
                }
                watcher.EnableRaisingEvents = false;
            }
            catch
            {
            }
            finally
            {
                watcher.Dispose();
            }
        }

        private void StartSessionGuard(int generation)
        {
            StopSessionGuard();
            if (demoMode || selectedTarget == null)
            {
                return;
            }

            sessionGuardPressureState = new SessionGuardPressureState();
            latestSessionGuardSample = null;
            latestPagefileAssessment = null;
            lastPagefileRecommendation = null;
            var source = new WindowsSessionGuardMetricsSource();
            var sampler = new SessionGuardSampler(
                source,
                new SystemSessionGuardClock(),
                SessionGuardSamplerOptions.CreateDefault(),
                true);
            var cancellation = new CancellationTokenSource();
            sampler.SampleCaptured += delegate(
                object sender,
                SessionGuardSampleEventArgs eventArgs)
            {
                HandleSessionGuardSample(generation, eventArgs.Sample);
            };
            sampler.HeavySampleCaptured += delegate(
                object sender,
                SessionGuardHeavySampleEventArgs eventArgs)
            {
                HandleSessionGuardHeavySample(generation, eventArgs.Sample);
            };
            sampler.SamplingFaulted += delegate(
                object sender,
                SessionGuardSamplingFaultEventArgs eventArgs)
            {
                if (IsActiveMaintenanceGeneration(generation))
                {
                    AppendActiveBoostLog(
                        "Session Guard sampling fault: " +
                        eventArgs.Error.Message);
                }
            };
            sessionGuardSampler = sampler;
            sessionGuardCancellation = cancellation;
            var guardTarget = new SessionGuardTargetIdentity(
                selectedTarget.ProcessId,
                selectedTarget.ProcessStartTimeUtc,
                selectedTarget.ExecutablePath);
            Task ignored = sampler.StartAsync(
                guardTarget,
                cancellation.Token);
        }

        private void StopSessionGuard()
        {
            SessionGuardSampler sampler = sessionGuardSampler;
            CancellationTokenSource cancellation = sessionGuardCancellation;
            sessionGuardSampler = null;
            sessionGuardCancellation = null;
            if (cancellation != null)
            {
                try { cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            if (sampler == null && cancellation == null)
            {
                return;
            }
            Task.Run(delegate
            {
                try
                {
                    if (sampler != null)
                    {
                        sampler.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    AppendActiveBoostLog(
                        "Session Guard shutdown fault: " + ex.Message);
                }
                finally
                {
                    if (cancellation != null)
                    {
                        cancellation.Dispose();
                    }
                }
            });
        }

        private void HandleSessionGuardSample(
            int generation,
            SessionGuardSample sample)
        {
            if (sample == null || !IsActiveMaintenanceGeneration(generation))
            {
                return;
            }

            SessionGuardPressureEvaluation evaluation;
            lock (activeMaintenanceSync)
            {
                if (!IsActiveMaintenanceGeneration(generation))
                {
                    return;
                }
                latestSessionGuardSample = sample;
                evaluation = sessionGuardPressurePolicy.Evaluate(
                    sample,
                    sessionGuardPressureState,
                    sample.MonotonicTimestamp);
                sessionGuardPressureState = evaluation.NextState;
                if (currentSession != null && sample.SystemMetricsAvailable)
                {
                    bool firstMemorySample = currentSession.MemorySamples == 0;
                    currentSession.MemorySamples++;
                    currentSession.MinimumAvailableMemoryBytes =
                        firstMemorySample
                            ? sample.AvailablePhysicalBytes
                            : Math.Min(
                                currentSession.MinimumAvailableMemoryBytes,
                                sample.AvailablePhysicalBytes);
                    currentSession.MinimumCommitHeadroomBytes =
                        firstMemorySample
                            ? sample.CommitHeadroomBytes
                            : Math.Min(
                                currentSession.MinimumCommitHeadroomBytes,
                                sample.CommitHeadroomBytes);
                    if (sample.TargetMetricsAvailable)
                    {
                        currentSession.PeakTargetWorkingSetBytes = Math.Max(
                            currentSession.PeakTargetWorkingSetBytes,
                            sample.TargetWorkingSetBytes);
                        currentSession.PeakTargetPrivateBytes = Math.Max(
                            currentSession.PeakTargetPrivateBytes,
                            sample.TargetPrivateBytes);
                    }
                }
            }

            if (evaluation.Decision ==
                SessionGuardPressureDecision.CriticalAlertRaised)
            {
                RecordSessionAction(
                    L.T("SESSION GUARD — ДАВЛЕНИЕ ПАМЯТИ"),
                    L.T("Памяти долго не хватает. ") +
                    L.T("Закройте лишние программы или увеличьте ") +
                    L.T("файл подкачки."),
                    BoostActionOutcome.Skipped);
            }
            UpdateLiveSessionSummaryOnDispatcher();
        }

        private void HandleSessionGuardHeavySample(
            int generation,
            SessionGuardHeavySample sample)
        {
            if (sample == null || !IsActiveMaintenanceGeneration(generation))
            {
                return;
            }
            try
            {
                PagefileAssessment assessment = sample.Pagefile == null
                    ? null
                    : PagefileAdvisor.Assess(sample.Pagefile);
                bool changedRecommendation;
                lock (activeMaintenanceSync)
                {
                    changedRecommendation = assessment != null &&
                        assessment.RequiresAttention &&
                        (!lastPagefileRecommendation.HasValue ||
                         lastPagefileRecommendation.Value !=
                            assessment.Recommendation);
                    latestPagefileAssessment = assessment;
                    if (changedRecommendation)
                    {
                        lastPagefileRecommendation = assessment.Recommendation;
                    }
                    else if (assessment != null &&
                        !assessment.RequiresAttention)
                    {
                        lastPagefileRecommendation = null;
                    }
                }
                if (changedRecommendation)
                {
                    AppendActiveBoostLog(
                        "Pagefile Guard: " +
                        assessment.Summary + " " +
                        assessment.RecommendedAction);
                    RecordSessionAction(
                        "PAGEFILE GUARD",
                        assessment.Summary + " " +
                        assessment.RecommendedAction,
                        BoostActionOutcome.Skipped);
                }
            }
            catch (Exception ex)
            {
                AppendActiveBoostLog("Pagefile Guard assessment failed: " + ex.Message);
            }
            QueueActiveDiagnosticSample(generation);
            UpdateLiveSessionSummaryOnDispatcher();
        }

        private void QueueActiveDiagnosticSample(int generation)
        {
            if (!IsActiveMaintenanceGeneration(generation) ||
                Interlocked.CompareExchange(
                    ref activeDiagnosticPending,
                    1,
                    0) != 0)
            {
                return;
            }

            Task.Run(delegate
            {
                try
                {
                    DiagnosticSnapshot snapshot =
                        DiagnosticSnapshotProvider.Capture();
                    if (!IsActiveMaintenanceGeneration(generation))
                    {
                        return;
                    }
                    lock (activeMaintenanceSync)
                    {
                        if (!IsActiveMaintenanceGeneration(generation))
                        {
                            return;
                        }
                        latestDiagnosticSnapshot = snapshot;
                        if (currentSession != null)
                        {
                            currentSession.ApplyDiagnosticSnapshot(snapshot);
                        }
                    }
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            if (boostCenterOverlay != null)
                            {
                                boostCenterOverlay.SetDiagnosticSnapshot(
                                    snapshot);
                            }
                            UpdateHomeSystemCard();
                            SaveCurrentSession();
                        }), DispatcherPriority.Background);
                    }
                    catch (InvalidOperationException) { }
                    catch (TaskCanceledException) { }
                }
                catch (Exception ex)
                {
                    AppendActiveBoostLog(
                        "Active diagnostic sampling failed: " + ex.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref activeDiagnosticPending, 0);
                }
            });
        }

        private void UpdateLiveSessionSummaryOnDispatcher()
        {
            try
            {
                Dispatcher.BeginInvoke(
                    new Action(UpdateLiveSessionSummary),
                    DispatcherPriority.Background);
            }
            catch (InvalidOperationException) { }
            catch (TaskCanceledException) { }
        }

        private void UpdateLiveSessionSummary()
        {
            if (liveStateText == null || liveTimerText == null)
            {
                return;
            }

            SessionGuardSample sample;
            bool pressure;
            lock (activeMaintenanceSync)
            {
                sample = latestSessionGuardSample;
                pressure = sessionGuardPressureState != null &&
                    sessionGuardPressureState.CriticalAlertActive;
            }
            string state = selectedTarget == null
                ? L.T("Игра не выбрана")
                : (boostActive
                    ? (pressure ? L.T("Мало памяти") : L.T("Boost включён"))
                    : L.T("Готов к запуску"));
            liveStateText.Text = state;
            liveStateText.Foreground = BoostixDesignTokens.Brush(
                pressure
                    ? BoostixDesignTokens.Warning
                    : (boostActive
                        ? BoostixDesignTokens.Success
                        : BoostixDesignTokens.SecondaryText));
            TimeSpan duration = currentSession == null
                ? TimeSpan.Zero
                : DateTime.UtcNow - currentSession.StartedUtc;
            liveTimerText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0:00}:{1:00}",
                Math.Min(99, (int)duration.TotalMinutes),
                duration.Seconds);
            if (boostGauge != null && boostActive && !animationRunning)
            {
                boostGauge.SetReadout(liveTimerText.Text, L.T("остановить"));
                if (IsVisible && WindowState != WindowState.Minimized)
                {
                    boostGauge.SetLiveLoad(loadSampler.Sample());
                }
            }
            AutomationProperties.SetName(
                liveStateText,
                L.T("Состояние сеанса: ") + state);
            AutomationProperties.SetName(
                liveTimerText,
                L.T("Время в игре: ") + liveTimerText.Text);
            UpdateBoostCenterSessionData();
        }

        private void UpdateBoostCenterSessionData()
        {
            if (boostCenterOverlay == null)
            {
                return;
            }
            SessionGuardSample sample;
            SessionGuardPressureState pressureState;
            PagefileAssessment pagefile;
            lock (activeMaintenanceSync)
            {
                sample = latestSessionGuardSample;
                pressureState = sessionGuardPressureState == null
                    ? null
                    : sessionGuardPressureState.Clone();
                pagefile = latestPagefileAssessment;
            }
            boostCenterOverlay.SetLiveSession(
                selectedTarget,
                sample,
                pressureState,
                pagefile,
                boostActive,
                currentSession == null
                    ? (DateTime?)null
                    : currentSession.StartedUtc);
            boostCenterOverlay.SetPerformanceProofSnapshot(
                proofCoordinator == null
                    ? null
                    : proofCoordinator.GetSnapshot());
            if (!gameProfilesDirty)
            {
                boostCenterOverlay.SetGameProfiles(cachedGameProfiles);
                return;
            }
            try
            {
                GameProfileLoadResult loaded = gameProfileStore.Load();
                cachedGameProfiles = loaded.Profiles == null
                    ? (IList<GameProfile>)new List<GameProfile>()
                    : loaded.Profiles.ToList();
                gameProfilesDirty = false;
                boostCenterOverlay.SetGameProfiles(cachedGameProfiles);
            }
            catch (Exception ex)
            {
                CrashLog.Write("Could not refresh game profiles for Boost Center.", ex);
                cachedGameProfiles = new List<GameProfile>();
                gameProfilesDirty = false;
                boostCenterOverlay.SetGameProfiles(cachedGameProfiles);
            }
        }

        private async void BoostCenterImpactScanRequested(
            object sender,
            EventArgs e)
        {
            if (impactScanCancellation != null)
            {
                return;
            }

            var cancellation = new CancellationTokenSource();
            impactScanCancellation = cancellation;
            boostCenterOverlay.SetImpactResults(
                null,
                L.T("Проверяем фоновые программы…"),
                true);
            try
            {
                int excludedProcessId = selectedTarget == null
                    ? Process.GetCurrentProcess().Id
                    : selectedTarget.ProcessId;
                IList<BackgroundImpactResult> results =
                    await BackgroundImpactAnalyzer.MeasureAsync(
                        15000,
                        excludedProcessId,
                        cancellation.Token);
                lastImpactResults = results.ToList();
                boostCenterOverlay.SetImpactResults(
                    lastImpactResults,
                    lastImpactResults.Count == 0
                        ? L.T("Заметной фоновой нагрузки нет.")
                        : L.T("Закрыть программу можно кнопкой в её строке."),
                    false);
            }
            catch (OperationCanceledException)
            {
                boostCenterOverlay.SetImpactResults(
                    null,
                    L.T("Проверка отменена."),
                    false);
            }
            catch (Exception ex)
            {
                CrashLog.Write("Background impact scan failed.", ex);
                boostCenterOverlay.SetImpactResults(
                    null,
                    L.T("Проверка не завершилась. Попробуйте ещё раз."),
                    false);
            }
            finally
            {
                if (ReferenceEquals(impactScanCancellation, cancellation))
                {
                    impactScanCancellation = null;
                }
                cancellation.Dispose();
            }
        }

        private async void BoostCenterImpactCloseRequested(
            object sender,
            BackgroundImpactEventArgs e)
        {
            if (e == null || e.Identity == null)
            {
                return;
            }

            BackgroundCloseResult closeResult = await Task.Run(delegate
            {
                return BackgroundImpactAnalyzer.RequestGracefulClose(
                    new[] { e.Identity },
                    2500).FirstOrDefault();
            });
            if (closeResult == null)
            {
                boostCenterOverlay.SetImpactResults(
                    lastImpactResults,
                    L.T("Программа не закрылась."),
                    false);
                return;
            }

            if (closeResult.Exited)
            {
                lastImpactResults = lastImpactResults
                    .Where(item => item != null && item.Identity != null &&
                        (item.Identity.ProcessId != e.Identity.ProcessId ||
                         item.Identity.StartTimeUtc != e.Identity.StartTimeUtc))
                    .ToList();
            }
            string processName = string.IsNullOrWhiteSpace(
                e.Identity.ProcessName)
                ? L.T("Приложение")
                : e.Identity.ProcessName;
            boostCenterOverlay.SetImpactResults(
                lastImpactResults,
                processName + ": " + closeResult.Message,
                false);
            RecordSessionAction(
                processName + L.T(" — ФОНОВОЕ ПРИЛОЖЕНИЕ"),
                closeResult.Message,
                closeResult.Exited
                    ? BoostActionOutcome.Changed
                    : closeResult.CloseRequested
                        ? BoostActionOutcome.Skipped
                        : BoostActionOutcome.Failed);
        }

        private void BoostCenterProfileAutoBoostChanged(
            object sender,
            BoostProfileEventArgs e)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.ExecutablePath))
            {
                return;
            }
            try
            {
                if (!gameProfileStore.SetAutoBoost(
                        e.ExecutablePath,
                        e.Enabled))
                {
                    throw new InvalidOperationException(
                        L.T("Профиль уже удалён или изменён."));
                }
                gameProfilesDirty = true;
            }
            catch (Exception ex)
            {
                CrashLog.Write("Could not update Auto Boost profile state.", ex);
            }
            UpdateBoostCenterSessionData();
        }

        private async void BoostCenterInstalledGamesScanRequested(
            object sender,
            EventArgs e)
        {
            List<InstalledGame> games;
            try
            {
                games = await Task.Run(delegate { return GameLibraryScanner.Scan(); });
            }
            catch (Exception ex)
            {
                CrashLog.Write("Installed games could not be listed.", ex);
                games = new List<InstalledGame>();
            }
            if (boostCenterOverlay != null)
            {
                boostCenterOverlay.SetInstalledGames(games, false);
            }
        }

        private void BoostCenterInstalledGameAddRequested(
            object sender,
            InstalledGameEventArgs e)
        {
            if (e == null || e.Game == null)
            {
                return;
            }
            try
            {
                gameProfileStore.AddExecutable(e.Game.ExecutablePath, e.Game.Name);
                gameProfilesDirty = true;
            }
            catch (Exception ex)
            {
                CrashLog.Write("Could not add a found game.", ex);
            }
            UpdateBoostCenterSessionData();
        }

        private void BoostCenterProfileRemoveRequested(
            object sender,
            BoostProfileEventArgs e)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.ExecutablePath))
            {
                return;
            }
            try
            {
                gameProfileStore.Remove(e.ExecutablePath);
                gameProfilesDirty = true;
            }
            catch (Exception ex)
            {
                CrashLog.Write("Could not remove a game profile.", ex);
            }
            UpdateBoostCenterSessionData();
        }

        private void QueueActiveBoostMaintenance()
        {
            if (!boostActive || animationRunning)
            {
                return;
            }
            if (Interlocked.CompareExchange(ref activeMaintenancePending, 1, 0) != 0)
            {
                return;
            }

            int generation = ReadActiveMaintenanceGeneration();
            Task.Run(delegate
            {
                try
                {
                    RunActiveBoostMaintenance(generation);
                }
                catch (Exception ex)
                {
                    AppendActiveBoostLog("Active maintenance error: " + ex.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref activeMaintenancePending, 0);
                    if (generation != ReadActiveMaintenanceGeneration())
                    {
                        try
                        {
                            Dispatcher.BeginInvoke(new Action(delegate
                            {
                                if (boostActive && !animationRunning)
                                {
                                    QueueActiveBoostMaintenance();
                                }
                            }));
                        }
                        catch (InvalidOperationException) { }
                        catch (TaskCanceledException) { }
                    }
                }
            });
        }

        private void RunActiveBoostMaintenance(int generation)
        {
            if (!IsActiveMaintenanceGeneration(generation))
            {
                return;
            }

            if (demoMode)
            {
                lock (activeMaintenanceSync)
                {
                    if (IsActiveMaintenanceGeneration(generation))
                    {
                        File.WriteAllText(
                            GetActiveBoostDemoSignalPath(),
                            DateTime.UtcNow.ToString("o"),
                            new UTF8Encoding(false));
                    }
                }
                return;
            }

            GameTargetIdentity exactTarget = selectedTarget;
            GameProcessSnapshot targetSnapshot;
            string targetError = L.T("Выбранная игра больше не запущена.");
            if (exactTarget == null ||
                !gameTargetService.TryResolve(
                    exactTarget,
                    out targetSnapshot,
                    out targetError))
            {
                RestoreOwnedTargetPriorities();
                NotifySelectedTargetEnded(generation, targetError);
                return;
            }

            if (!IsExactTargetForeground(exactTarget.ProcessId))
            {
                // A focus change must undo Boostix-owned priority promptly. The
                // session remains active, but no background/browser process is
                // ever promoted and the game is restored within the 1 s tick.
                RestoreOwnedTargetPriorities();
                UpdateLiveSessionSummaryOnDispatcher();
                return;
            }

            Process target = null;

            try
            {
                target = Process.GetProcessById(exactTarget.ProcessId);
                int processId = target.Id;
                DateTime startTimeUtc = target.StartTime.ToUniversalTime();
                string actualPath =
                    GameExecutablePath.GetProcessImagePath(target);
                if (startTimeUtc != exactTarget.ProcessStartTimeUtc ||
                    !GameExecutablePath.AreEquivalent(
                        actualPath,
                        exactTarget.ExecutablePath))
                {
                    RestoreOwnedTargetPriorities();
                    NotifySelectedTargetEnded(
                        generation,
                        L.T("Windows переиспользовала PID или исполняемый файл изменился."));
                    return;
                }
                string actualName = target.ProcessName;
                target.Refresh();
                UpdateTargetMemoryTelemetry(
                    actualName,
                    Math.Max(0, target.WorkingSet64),
                    Math.Max(0, target.PrivateMemorySize64));
                ProcessPriorityClass originalPriority = target.PriorityClass;
                bool alreadyTracked;
                lock (activeMaintenanceSync)
                {
                    if (!IsActiveMaintenanceGeneration(generation))
                    {
                        return;
                    }
                    TrackedTargetPriority existing;
                    alreadyTracked =
                        trackedTargetPriorities.TryGetValue(processId, out existing) &&
                        existing.StartTimeUtc == startTimeUtc;
                    if (!alreadyTracked)
                    {
                        trackedTargetPriorities[processId] = new TrackedTargetPriority
                        {
                            ProcessId = processId,
                            StartTimeUtc = startTimeUtc,
                            ProcessName = actualName,
                            OriginalPriority = originalPriority,
                            ChangedByBoost = false
                        };
                    }
                }
                if (alreadyTracked)
                {
                    return;
                }

                bool canRaise =
                    originalPriority == ProcessPriorityClass.Normal ||
                    originalPriority == ProcessPriorityClass.BelowNormal ||
                    originalPriority == ProcessPriorityClass.Idle;
                if (!canRaise)
                {
                    RecordSessionAction(
                        actualName + L.T(" — ПРИОРИТЕТ"),
                        L.T("Существующий приоритет ") + originalPriority + L.T(" сохранён."),
                        BoostActionOutcome.AlreadyOptimal);
                    return;
                }

                bool priorityChanged = false;
                lock (activeMaintenanceSync)
                {
                    if (!IsActiveMaintenanceGeneration(generation))
                    {
                        return;
                    }
                    TrackedTargetPriority tracked;
                    if (trackedTargetPriorities.TryGetValue(processId, out tracked) &&
                        tracked.StartTimeUtc == startTimeUtc)
                    {
                        ProcessPriorityClass currentPriority = target.PriorityClass;
                        if (currentPriority == originalPriority &&
                            (currentPriority == ProcessPriorityClass.Normal ||
                             currentPriority == ProcessPriorityClass.BelowNormal ||
                             currentPriority == ProcessPriorityClass.Idle))
                        {
                            target.PriorityClass = ProcessPriorityClass.AboveNormal;
                            tracked.ChangedByBoost = true;
                            priorityChanged = true;
                        }
                    }
                }
                if (!priorityChanged)
                {
                    RecordSessionAction(
                        actualName + L.T(" — ПРИОРИТЕТ"),
                        L.T("Внешнее изменение приоритета сохранено."),
                        BoostActionOutcome.ExternalOverridePreserved);
                    return;
                }
                AppendActiveBoostLog(
                    "Set foreground target " + actualName + " (PID " +
                    processId + ") priority to AboveNormal.");
                RecordSessionAction(
                    actualName + L.T(" — ПРИОРИТЕТ"),
                    L.T("Приоритет ") + originalPriority +
                    L.T(" → AboveNormal. Исходное значение сохранено."),
                    BoostActionOutcome.Changed);
                if (currentSession != null)
                {
                    currentSession.TargetName = actualName;
                }
            }
            catch (Exception ex)
            {
                // Windows keeps denying the same elevated or protected game on
                // every tick; report that once per exact process instance.
                string failureKey = exactTarget.ProcessId.ToString(
                        CultureInfo.InvariantCulture) + ":" +
                    exactTarget.ProcessStartTimeUtc.Ticks.ToString(
                        CultureInfo.InvariantCulture);
                bool firstFailure;
                lock (activeMaintenanceSync)
                {
                    firstFailure = !string.Equals(
                        priorityFailureTargetKey,
                        failureKey,
                        StringComparison.Ordinal);
                    priorityFailureTargetKey = failureKey;
                }
                if (firstFailure)
                {
                    RecordSessionAction(
                        L.T("ЦЕЛЕВОЕ ПРИЛОЖЕНИЕ — ПРИОРИТЕТ"),
                        L.T("Не удалось изменить приоритет: ") + ex.Message,
                        BoostActionOutcome.Skipped);
                }
            }
            finally
            {
                if (target != null)
                {
                    target.Dispose();
                }
            }
        }

        private static bool IsExactTargetForeground(int processId)
        {
            if (processId <= 0)
            {
                return false;
            }
            try
            {
                IntPtr foreground = GetForegroundWindow();
                uint foregroundProcessId;
                GetWindowThreadProcessId(foreground, out foregroundProcessId);
                return foreground != IntPtr.Zero &&
                    foregroundProcessId == (uint)processId;
            }
            catch
            {
                return false;
            }
        }

        private void NotifySelectedTargetEnded(
            int generation,
            string reason,
            DateTime? observedExitedUtc = null,
            int? observedExitCode = null)
        {
            if (!IsActiveMaintenanceGeneration(generation) ||
                Interlocked.CompareExchange(
                    ref targetExitNotificationPending,
                    1,
                    0) != 0)
            {
                return;
            }

            GameTargetIdentity endedTarget = selectedTarget;
            string sessionId = currentSession == null
                ? string.Empty
                : currentSession.SessionId;
            DateTime exitedUtc = observedExitedUtc.HasValue
                ? observedExitedUtc.Value.ToUniversalTime()
                : DateTime.UtcNow;
            try
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (boostActive && !animationRunning)
                    {
                        StartBoostDeactivation(
                            "TargetExited",
                            L.T("Выбранная игра завершилась. Boostix проверяет журнал Windows."));
                    }
                    ClearSelectedTarget(
                        L.T("Игра закрылась. Выберите её снова после запуска"));
                }));
            }
            catch (InvalidOperationException)
            {
                Interlocked.Exchange(ref targetExitNotificationPending, 0);
                return;
            }
            catch (TaskCanceledException)
            {
                Interlocked.Exchange(ref targetExitNotificationPending, 0);
                return;
            }

            Task.Run(async delegate
            {
                CrashCorrelationResult correlation = null;
                try
                {
                    var target = new CrashCorrelationTarget
                    {
                        ProcessId = endedTarget == null
                            ? 0
                            : endedTarget.ProcessId,
                        ProcessName = endedTarget == null
                            ? string.Empty
                            : endedTarget.ProcessName,
                        StartedUtc = endedTarget == null
                            ? DateTime.MinValue
                            : endedTarget.ProcessStartTimeUtc,
                        ExitedUtc = exitedUtc,
                        ExitCode = observedExitCode,
                        ExpectedExit = false
                    };
                    var service = new CrashCorrelationService(
                        new WindowsCrashEventProvider());
                    foreach (int delay in new[] { 2500, 5000, 8000, 15000 })
                    {
                        await Task.Delay(delay).ConfigureAwait(false);
                        correlation = service.Correlate(target);
                        if (correlation.Status == CrashCorrelationStatus.Correlated ||
                            correlation.Status ==
                                CrashCorrelationStatus.ProviderUnavailable ||
                            correlation.Status == CrashCorrelationStatus.NotApplicable)
                        {
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    CrashLog.Write("Crash correlation retry failed.", ex);
                    correlation = new CrashCorrelationResult
                    {
                        Status = CrashCorrelationStatus.ProviderUnavailable,
                        Summary =
                            L.T("Журнал Windows недоступен. Причина завершения не определена.")
                    };
                }

                try
                {
                    await Dispatcher.InvokeAsync(delegate
                    {
                        ApplyCrashCorrelation(
                            sessionId,
                            endedTarget,
                            exitedUtc,
                            reason,
                            correlation);
                    });
                }
                catch (InvalidOperationException) { }
                catch (TaskCanceledException) { }
                finally
                {
                    Interlocked.Exchange(ref targetExitNotificationPending, 0);
                }
            });
        }

        private void ApplyCrashCorrelation(
            string sessionId,
            GameTargetIdentity endedTarget,
            DateTime exitedUtc,
            string resolutionError,
            CrashCorrelationResult correlation)
        {
            BoostSessionReport report = null;
            if (currentSession != null && string.Equals(
                    currentSession.SessionId,
                    sessionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                report = currentSession;
            }
            else if (lastSession != null && string.Equals(
                    lastSession.SessionId,
                    sessionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                report = lastSession;
            }
            if (report == null)
            {
                return;
            }

            bool correlated = correlation != null &&
                correlation.Status == CrashCorrelationStatus.Correlated;
            if (correlated)
            {
                report.TargetCrashUtc = correlation.EventUtc;
                report.Status = "TargetCrashed";
                report.TargetCrashCode = correlation.ExceptionCode;
                report.TargetCrashModule = correlation.FaultingModule;
                report.TargetCrashOffset = correlation.FaultOffset;
                report.StopReason = correlation.Summary;
                report.AddAction(
                    L.T("СВИДЕТЕЛЬСТВО СБОЯ WINDOWS"),
                    BuildCrashEvidenceDetail(correlation),
                    BoostActionOutcome.Failed);
            }
            else
            {
                report.TargetCrashUtc = null;
                report.Status = "TargetExited";
                report.StopReason =
                    L.T("Игра закрылась. Сбоя Windows не зафиксировала.");
                report.AddAction(
                    L.T("ЗАВЕРШЕНИЕ ИГРЫ"),
                    (correlation == null
                        ? L.T("Причина завершения не определена.")
                        : correlation.Summary) +
                    (string.IsNullOrWhiteSpace(resolutionError)
                        ? string.Empty
                        : " " + resolutionError),
                    BoostActionOutcome.Skipped);
            }
            try { BoostSessionReportStore.Save(report); }
            catch (Exception ex)
            {
                CrashLog.Write("Could not save crash correlation evidence.", ex);
            }
            RefreshSessionHistory();
            if (boostCenterOverlay != null)
            {
                boostCenterOverlay.SetSessionReport(report);
                boostCenterOverlay.SetSessionHistory(sessionHistory);
                if (correlated)
                {
                    RememberBoostCenterFocusReturn(centerButton);
                    boostCenterOverlay.OpenReport();
                }
            }
        }

        private static string BuildCrashEvidenceDetail(
            CrashCorrelationResult correlation)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(correlation.ExceptionCode))
            {
                parts.Add(L.T("код ") + correlation.ExceptionCode);
            }
            if (!string.IsNullOrWhiteSpace(correlation.FaultingModule))
            {
                parts.Add(L.T("модуль ") + correlation.FaultingModule);
            }
            if (!string.IsNullOrWhiteSpace(correlation.FaultOffset))
            {
                parts.Add(L.T("смещение ") + correlation.FaultOffset);
            }
            string evidence = parts.Count == 0
                ? L.T("Windows зафиксировала совпадающее событие.")
                : L.T("Windows зафиксировала: ") + string.Join(", ", parts) + ".";
            return evidence +
                L.T(" Это свидетельство журнала, а не автоматический диагноз.");
        }

        private void UpdateTargetMemoryTelemetry(
            string processName,
            long workingSetBytes,
            long privateBytes)
        {
            lock (activeMaintenanceSync)
            {
                if (currentSession == null)
                {
                    return;
                }
                currentSession.TargetName = processName ?? currentSession.TargetName;
                currentSession.PeakTargetWorkingSetBytes = Math.Max(
                    currentSession.PeakTargetWorkingSetBytes,
                    workingSetBytes);
                currentSession.PeakTargetPrivateBytes = Math.Max(
                    currentSession.PeakTargetPrivateBytes,
                    privateBytes);
            }
        }

        private static long MinimumPositive(long current, long candidate)
        {
            if (candidate <= 0)
            {
                return current;
            }
            return current <= 0 ? candidate : Math.Min(current, candidate);
        }

        private static string FormatBytesInvariant(long bytes)
        {
            return Math.Max(0, bytes).ToString(
                System.Globalization.CultureInfo.InvariantCulture) + " bytes";
        }

        private static string FormatMemoryCompact(long bytes)
        {
            if (bytes <= 0)
            {
                return L.T("0 МБ");
            }
            return BoostCenterOverlay.FormatBytesCompact(bytes);
        }

        private void RestoreOwnedTargetPriorities()
        {
            List<TrackedTargetPriority> tracked;
            lock (activeMaintenanceSync)
            {
                tracked = trackedTargetPriorities.Values.ToList();
                trackedTargetPriorities.Clear();
            }

            foreach (TrackedTargetPriority item in tracked)
            {
                if (!item.ChangedByBoost)
                {
                    continue;
                }
                Process process = null;
                try
                {
                    process = Process.GetProcessById(item.ProcessId);
                    if (process.StartTime.ToUniversalTime() != item.StartTimeUtc)
                    {
                        continue;
                    }
                    ProcessPriorityClass current = process.PriorityClass;
                    if (current != ProcessPriorityClass.AboveNormal)
                    {
                        RecordSessionAction(
                            item.ProcessName + L.T(" — ПРИОРИТЕТ"),
                            L.T("Внешнее изменение ") + current + L.T(" сохранено; Boost его не перезаписал."),
                            BoostActionOutcome.ExternalOverridePreserved);
                        continue;
                    }
                    process.PriorityClass = item.OriginalPriority;
                    RecordSessionAction(
                        item.ProcessName + L.T(" — ПРИОРИТЕТ"),
                        L.T("Восстановлен исходный приоритет ") + item.OriginalPriority + ".",
                        BoostActionOutcome.Restored);
                }
                catch
                {
                    // The target may already be closed; there is nothing left to restore.
                }
                finally
                {
                    if (process != null)
                    {
                        process.Dispose();
                    }
                }
            }
        }

        private int ReadActiveMaintenanceGeneration()
        {
            return Interlocked.CompareExchange(ref activeMaintenanceGeneration, 0, 0);
        }

        private int AdvanceActiveMaintenanceGeneration()
        {
            lock (activeMaintenanceSync)
            {
                return Interlocked.Increment(ref activeMaintenanceGeneration);
            }
        }

        private bool IsActiveMaintenanceGeneration(int generation)
        {
            return generation == ReadActiveMaintenanceGeneration();
        }

        private static void AppendActiveBoostLog(string message)
        {
            try
            {
                string directory = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    ProductBrand.DataDirectoryName);
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    System.IO.Path.Combine(directory, "Boost-Session.last.log"),
                    "[" + DateTime.Now.ToString("o") + "] " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        private static string GetActiveBoostDemoSignalPath()
        {
            return System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Boostix-demo-monitor-" + Process.GetCurrentProcess().Id + ".flag");
        }

        private static void TryDeleteActiveBoostDemoSignal()
        {
            try
            {
                string path = GetActiveBoostDemoSignalPath();
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch { }
        }

        private void BeginSession(string trigger)
        {
            if (currentSession != null)
            {
                CompleteCurrentSession("Interrupted", L.T("Начата новая сессия Boost."));
            }
            currentSession = BoostSessionReport.Start(trigger);
            lock (activeMaintenanceSync)
            {
                priorityFailureTargetKey = null;
            }
            currentSession.AddAction(
                L.T("ПАМЯТЬ ДО ЗАПУСКА"),
                FormatMemory(currentSession.AvailableMemoryStartBytes) +
                L.T(" доступно; сюда уже входит освобождаемый кэш Windows."),
                BoostActionOutcome.AlreadyOptimal);
            if (centerSettings.KeepDiscord)
            {
                currentSession.AddAction(
                    "DISCORD",
                    L.T("Сохранён по вашему выбору."),
                    BoostActionOutcome.Preserved);
            }
            if (centerSettings.KeepEpic)
            {
                currentSession.AddAction(
                    "EPIC GAMES",
                    L.T("Сохранён по вашему выбору."),
                    BoostActionOutcome.Preserved);
            }
            if (centerSettings.KeepSteam)
            {
                currentSession.AddAction(
                    "STEAM",
                    L.T("Сохранён по вашему выбору."),
                    BoostActionOutcome.Preserved);
            }
            SaveCurrentSession();
            RefreshSessionHistory();
            StartSessionPowerPlan();
        }

        private void StartSessionPowerPlan()
        {
            if (demoMode || safeMode || sessionPowerPlanManager == null ||
                currentSession == null)
            {
                return;
            }
            Guid sessionId;
            if (!Guid.TryParse(currentSession.SessionId, out sessionId) ||
                sessionId == Guid.Empty)
            {
                currentSession.AddAction(
                    L.T("ПЛАН ПИТАНИЯ"),
                    L.T("Идентификатор сессии не прошёл проверку; план Windows не менялся."),
                    BoostActionOutcome.Failed);
                SaveCurrentSession();
                return;
            }

            Task<SessionPowerPlanOperationResult> startTask;
            lock (sessionPowerPlanSync)
            {
                // A previous exact session retains ownership until its stop
                // worker has finished. Its completion retries the then-current
                // session without ever reusing the old marker identity.
                if (sessionPowerPlanStopRequest != null ||
                    activePowerPlanSessionId.HasValue)
                {
                    return;
                }

                activePowerPlanSessionId = sessionId;
                recordedPowerPlanStartSessionId = null;
                startTask = Task.Run(delegate
                {
                    return sessionPowerPlanManager.Start(sessionId);
                });
                sessionPowerPlanStartTask = startTask;
            }
            startTask.ContinueWith(
                delegate(Task<SessionPowerPlanOperationResult> completed)
                {
                    if (completed.Status != TaskStatus.RanToCompletion)
                    {
                        CrashLog.Write(
                            "Session power-plan activation task did not complete.",
                            completed.Exception);
                        return;
                    }
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            RecordSessionPowerPlanStartResult(
                                sessionId,
                                completed.Result);
                        }));
                    }
                    catch
                    {
                    }
                },
                TaskScheduler.Default);
        }

        private void RecordSessionPowerPlanStartResult(
            Guid sessionId,
            SessionPowerPlanOperationResult result)
        {
            if (result == null ||
                recordedPowerPlanStartSessionId == sessionId ||
                currentSession == null)
            {
                return;
            }
            Guid currentId;
            if (!Guid.TryParse(currentSession.SessionId, out currentId) ||
                currentId != sessionId)
            {
                return;
            }
            recordedPowerPlanStartSessionId = sessionId;
            currentSession.AddAction(
                SessionPowerPlanStartActionTitle,
                DescribeSessionPowerPlanResult(result),
                PowerPlanOutcome(result));
            SaveCurrentSession();
        }

        private void StopSessionPowerPlan()
        {
            if (sessionPowerPlanManager == null)
            {
                return;
            }

            SessionPowerPlanStopRequest request;
            lock (sessionPowerPlanSync)
            {
                if (sessionPowerPlanStopRequest != null ||
                    !activePowerPlanSessionId.HasValue)
                {
                    return;
                }

                Guid sessionId = activePowerPlanSessionId.Value;
                request = new SessionPowerPlanStopRequest
                {
                    SessionId = sessionId,
                    StartTask = sessionPowerPlanStartTask,
                    CompletedReport = FindSessionReportForPowerPlan(sessionId)
                };

                // Ownership moves atomically to one exact stop request. A new
                // session cannot start until this request releases the old ID.
                sessionPowerPlanStopRequest = request;
                activePowerPlanSessionId = null;
                sessionPowerPlanStartTask = null;
                recordedPowerPlanStartSessionId = null;
            }

            request.WorkerTask = StopSessionPowerPlanWorkerAsync(
                sessionPowerPlanManager,
                request);
            request.WorkerTask.ContinueWith(
                delegate(Task<SessionPowerPlanStopCompletion> completed)
                {
                    if (completed.Status != TaskStatus.RanToCompletion)
                    {
                        CrashLog.Write(
                            "Session power-plan stop worker did not complete.",
                            completed.Exception);
                        return;
                    }
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            CompleteSessionPowerPlanStop(
                                request,
                                completed.Result);
                        }));
                    }
                    catch
                    {
                        // During shutdown the trusted recovery marker is left
                        // for RecoverOnStartup instead of blocking WPF.
                    }
                },
                TaskScheduler.Default);
        }

        private BoostSessionReport FindSessionReportForPowerPlan(Guid sessionId)
        {
            BoostSessionReport report = ReportMatchesSession(
                currentSession,
                sessionId)
                    ? currentSession
                    : (ReportMatchesSession(lastSession, sessionId)
                        ? lastSession
                        : null);
            return report == null ? null : report.Clone();
        }

        private static bool ReportMatchesSession(
            BoostSessionReport report,
            Guid sessionId)
        {
            Guid reportId;
            return report != null &&
                Guid.TryParse(report.SessionId, out reportId) &&
                reportId == sessionId;
        }

        private static async Task<SessionPowerPlanStopCompletion>
            StopSessionPowerPlanWorkerAsync(
                SessionPowerPlanManager manager,
                SessionPowerPlanStopRequest request)
        {
            var completion = new SessionPowerPlanStopCompletion
            {
                SessionId = request.SessionId,
                CompletedReport = request.CompletedReport
            };

            if (request.StartTask != null)
            {
                try
                {
                    completion.StartResult = await request.StartTask
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    completion.StartFailure = ex;
                }
            }

            // Task.Run is required even when Start has already completed: the
            // manager wraps synchronous powercfg and verification calls.
            completion.StopResult = await Task.Run(delegate
            {
                return manager.Stop(request.SessionId);
            }).ConfigureAwait(false);

            if (completion.CompletedReport != null)
            {
                EnsurePowerPlanStartAction(
                    completion.CompletedReport,
                    completion.StartResult,
                    completion.StartFailure);
                EnsurePowerPlanResultAction(
                    completion.CompletedReport,
                    SessionPowerPlanStopActionTitle,
                    completion.StopResult);
                try
                {
                    BoostSessionReportStore.Save(
                        completion.CompletedReport);
                }
                catch (Exception ex)
                {
                    completion.ReportSaveFailure = ex;
                }
            }

            return completion;
        }

        private void CompleteSessionPowerPlanStop(
            SessionPowerPlanStopRequest request,
            SessionPowerPlanStopCompletion completion)
        {
            lock (sessionPowerPlanSync)
            {
                if (!object.ReferenceEquals(
                        sessionPowerPlanStopRequest,
                        request))
                {
                    return;
                }
                sessionPowerPlanStopRequest = null;
            }

            if (completion == null ||
                completion.SessionId != request.SessionId)
            {
                CrashLog.Write(
                    "Session power-plan stop returned a mismatched session.",
                    null);
            }
            else
            {
                if (completion.StartFailure != null)
                {
                    CrashLog.Write(
                        "Session power-plan activation completion failed.",
                        completion.StartFailure);
                }
                if (completion.ReportSaveFailure != null)
                {
                    CrashLog.Write(
                        "Session power-plan result report could not be saved.",
                        completion.ReportSaveFailure);
                }
                bool completedExactReport =
                    completion.CompletedReport != null &&
                    ReportMatchesSession(
                        completion.CompletedReport,
                        request.SessionId);
                if (completedExactReport &&
                    ReportMatchesSession(lastSession, request.SessionId))
                {
                    lastSession = completion.CompletedReport;
                    if (boostCenterOverlay != null)
                    {
                        boostCenterOverlay.SetSessionReport(lastSession);
                    }
                }
                else if (completedExactReport && lastSession != null)
                {
                    // The exact old report was persisted by the worker. If a
                    // newer session completed while Stop was running, restore
                    // its last-session pointer without ever copying the old
                    // result into the newer report.
                    try
                    {
                        BoostSessionReportStore.Save(lastSession.Clone());
                    }
                    catch (Exception ex)
                    {
                        CrashLog.Write(
                            "Latest session report could not be restored after power-plan stop.",
                            ex);
                    }
                }
                if (completedExactReport)
                {
                    RefreshSessionHistory();
                }
            }

            // If a new report began while the exact old stop was in flight,
            // activation is retried only after old ownership has been released.
            if (currentSession != null &&
                !Dispatcher.HasShutdownStarted &&
                !Dispatcher.HasShutdownFinished)
            {
                StartSessionPowerPlan();
            }
        }

        private void GiveSessionPowerPlanStopShutdownGrace()
        {
            Task<SessionPowerPlanStopCompletion> workerTask = null;
            lock (sessionPowerPlanSync)
            {
                if (sessionPowerPlanStopRequest != null)
                {
                    workerTask = sessionPowerPlanStopRequest.WorkerTask;
                }
            }
            if (workerTask == null || workerTask.IsCompleted)
            {
                return;
            }

            try
            {
                // Stop was already scheduled on a worker. Closing gets one
                // strictly bounded grace period; no powercfg call is ever
                // started on the WPF dispatcher. On timeout the trusted marker
                // remains available to RecoverOnStartup.
                Task.WaitAny(
                    new Task[] { workerTask },
                    SessionPowerPlanShutdownGraceMilliseconds);
            }
            catch (Exception ex)
            {
                CrashLog.Write(
                    "Session power-plan shutdown grace wait failed.",
                    ex);
            }
        }

        private static void EnsurePowerPlanStartAction(
            BoostSessionReport report,
            SessionPowerPlanOperationResult result,
            Exception failure)
        {
            if (HasSessionAction(report, SessionPowerPlanStartActionTitle))
            {
                return;
            }
            if (result != null)
            {
                EnsurePowerPlanResultAction(
                    report,
                    SessionPowerPlanStartActionTitle,
                    result);
                return;
            }

            report.AddAction(
                SessionPowerPlanStartActionTitle,
                failure == null
                    ? L.T("Результат включения плана питания недоступен.")
                    : L.T("Включение плана питания завершилось ошибкой; восстановление всё равно было запрошено."),
                BoostActionOutcome.Failed);
        }

        private static void EnsurePowerPlanResultAction(
            BoostSessionReport report,
            string title,
            SessionPowerPlanOperationResult result)
        {
            if (report == null || HasSessionAction(report, title))
            {
                return;
            }
            report.AddAction(
                title,
                DescribeSessionPowerPlanResult(result),
                PowerPlanOutcome(result));
        }

        private static bool HasSessionAction(
            BoostSessionReport report,
            string title)
        {
            return report != null &&
                (report.Actions ?? new List<BoostActionRecord>()).Any(
                    action => action != null &&
                        string.Equals(
                            action.Title,
                            title,
                            StringComparison.Ordinal));
        }

        private static BoostActionOutcome PowerPlanOutcome(
            SessionPowerPlanOperationResult result)
        {
            if (result == null)
            {
                return BoostActionOutcome.Failed;
            }
            if (result.Changed)
            {
                return BoostActionOutcome.Changed;
            }
            switch (result.Status)
            {
                case SessionPowerPlanStatus.AlreadyActive:
                case SessionPowerPlanStatus.AlreadyStopped:
                case SessionPowerPlanStatus.NoRecoveryNeeded:
                case SessionPowerPlanStatus.ExternalOverridePreserved:
                case SessionPowerPlanStatus.SkippedOnBattery:
                    return BoostActionOutcome.AlreadyOptimal;
                case SessionPowerPlanStatus.TrustedStateMissing:
                case SessionPowerPlanStatus.PowerSourceUnavailable:
                    return BoostActionOutcome.Skipped;
                default:
                    return BoostActionOutcome.Failed;
            }
        }

        private static string DescribeSessionPowerPlanResult(
            SessionPowerPlanOperationResult result)
        {
            if (result == null)
            {
                return L.T("Результат операции недоступен; состояние Windows не подтверждено.");
            }
            switch (result.Status)
            {
                case SessionPowerPlanStatus.Activated:
                    return L.T("Профиль Boostix Performance включён только на время этой игровой сессии.");
                case SessionPowerPlanStatus.Restored:
                case SessionPowerPlanStatus.Recovered:
                    return L.T("Исходный план питания Windows восстановлен.");
                case SessionPowerPlanStatus.AlreadyActive:
                    return L.T("План Boostix уже был активен до сессии и не был присвоен приложением.");
                case SessionPowerPlanStatus.AlreadyStopped:
                case SessionPowerPlanStatus.NoRecoveryNeeded:
                    return L.T("Управляемых изменений плана питания нет.");
                case SessionPowerPlanStatus.ExternalOverridePreserved:
                    return L.T("Пользователь или Windows сменили план питания; внешний выбор сохранён.");
                case SessionPowerPlanStatus.SkippedOnBattery:
                    return L.T("Ноутбук работает от батареи; план питания не менялся.");
                case SessionPowerPlanStatus.PowerSourceUnavailable:
                    return L.T("Источник питания не удалось подтвердить; план не менялся.");
                case SessionPowerPlanStatus.TrustedStateMissing:
                    return L.T("Проверенный профиль ещё не подготовлен; план питания не менялся.");
                case SessionPowerPlanStatus.LiveSessionPreserved:
                    return L.T("Обнаружена другая живая сессия Boostix; её план не изменён.");
                default:
                    return L.T("Операция остановлена безопасно: ") + result.Status + ".";
            }
        }

        private void RecordSessionAction(
            string title,
            string detail,
            BoostActionOutcome outcome)
        {
            if (!Dispatcher.CheckAccess())
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        RecordSessionAction(title, detail, outcome);
                    }));
                }
                catch { }
                return;
            }
            if (currentSession == null)
            {
                return;
            }
            lock (activeMaintenanceSync)
            {
                if (currentSession == null)
                {
                    return;
                }
                currentSession.AddAction(title, detail, outcome);
            }
            SaveCurrentSession();
        }

        private void ImportBoostScriptResult(BoostSessionReport report)
        {
            if (report == null || demoMode)
            {
                return;
            }
            string path = System.IO.Path.Combine(
                BoostSessionReportStore.StateDirectory,
                "Boost-Session-" + report.SessionId + ".result");
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 256 * 1024)
                {
                    return;
                }
                int stopped = 0;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0)
                    {
                        continue;
                    }
                    string key = line.Substring(0, separator).Trim();
                    string value = line.Substring(separator + 1).Trim();
                    if (IsIndexedResultKey(key, "Process"))
                    {
                        string[] parts = value.Split('|');
                        string processName = parts.Length > 0 ? parts[0] : value;
                        report.AddAction(
                            processName.ToUpperInvariant(),
                            L.T("Закрыт во время одноразовой подготовки."),
                            BoostActionOutcome.Changed);
                        stopped++;
                    }
                    else if (IsIndexedResultKey(key, "Warning"))
                    {
                        report.AddAction(
                            L.T("ПРЕДУПРЕЖДЕНИЕ ПОДГОТОВКИ"),
                            value,
                            BoostActionOutcome.Failed);
                    }
                }
                if (stopped == 0)
                {
                    report.AddAction(
                        L.T("ФОНОВЫЕ ПРОЦЕССЫ"),
                        L.T("Выбранные процессы уже не были запущены."),
                        BoostActionOutcome.AlreadyOptimal);
                }
            }
            catch (Exception ex)
            {
                report.AddAction(
                    L.T("ОТЧЁТ ПОДГОТОВКИ"),
                    L.T("Не удалось прочитать подробности: ") + ex.Message,
                    BoostActionOutcome.Skipped);
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch { }
            }
        }

        private static bool IsIndexedResultKey(string key, string prefix)
        {
            if (string.IsNullOrEmpty(key) ||
                string.IsNullOrEmpty(prefix) ||
                key.Length <= prefix.Length ||
                !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            for (int index = prefix.Length; index < key.Length; index++)
            {
                if (key[index] < '0' || key[index] > '9')
                {
                    return false;
                }
            }

            return key[prefix.Length] != '0';
        }

        private void SaveCurrentSession()
        {
            BoostSessionReport snapshot;
            lock (activeMaintenanceSync)
            {
                snapshot = currentSession == null
                    ? null
                    : currentSession.Clone();
            }
            if (snapshot == null)
            {
                return;
            }
            try
            {
                BoostSessionReportStore.Save(snapshot);
                if (boostCenterOverlay != null)
                {
                    boostCenterOverlay.SetSessionReport(snapshot);
                }
            }
            catch { }
        }

        private void RefreshSessionHistory()
        {
            try
            {
                sessionHistory = DiagnosticSessionHistory.LoadRecent(
                    DiagnosticSessionHistory.MaximumSessionCount);
            }
            catch
            {
                sessionHistory = new List<BoostSessionReport>();
            }

            if (currentSession != null &&
                !sessionHistory.Any(item =>
                    string.Equals(
                        item.SessionId,
                        currentSession.SessionId,
                        StringComparison.OrdinalIgnoreCase)))
            {
                sessionHistory.Insert(0, currentSession);
                if (sessionHistory.Count >
                    DiagnosticSessionHistory.MaximumSessionCount)
                {
                    sessionHistory.RemoveAt(sessionHistory.Count - 1);
                }
            }
            if (boostCenterOverlay != null)
            {
                boostCenterOverlay.SetSessionHistory(sessionHistory);
            }
        }

        private void CompleteCurrentSession(string status, string reason)
        {
            if (currentSession == null)
            {
                StopSessionPowerPlan();
                return;
            }
            // 2.0 intentionally does not trim working sets or purge Windows caches.
            // Session Guard records real available/commit/private metrics instead.
            currentSession.ManagedMemoryMaintenanceCycles = 0;
            currentSession.MemoryReliefAttempts = 0;
            currentSession.MemoryReliefSuccesses = 0;
            currentSession.MemoryReliefBytes = 0;
            currentSession.Complete(status, reason);
            currentSession.AddAction(
                L.T("ПАМЯТЬ ПОСЛЕ СЕССИИ"),
                FormatMemory(currentSession.AvailableMemoryEndBytes) +
                L.T(" доступно. Минимум за сессию: ") +
                FormatMemory(currentSession.MinimumAvailableMemoryBytes) +
                L.T("; минимальный резерв памяти: ") +
                FormatMemory(currentSession.MinimumCommitHeadroomBytes) + ".",
                BoostActionOutcome.AlreadyOptimal);
            try { BoostSessionReportStore.Save(currentSession); }
            catch { }
            lastSession = currentSession;
            if (string.Equals(status, "TargetExited", StringComparison.OrdinalIgnoreCase) &&
                centerSettings.GameSignals &&
                trayIcon != null)
            {
                trayIcon.Notify(SessionSummary.Describe(
                    lastSession.TargetName,
                    (lastSession.EndedUtc ?? DateTime.UtcNow) - lastSession.StartedUtc,
                    lastSession.WorstResourcePressure));
            }
            // Captures the already-completed report and returns immediately;
            // activation completion, restore and result persistence run off UI.
            StopSessionPowerPlan();
            currentSession = null;
            RefreshSessionHistory();
            if (boostCenterOverlay != null)
            {
                boostCenterOverlay.SetSessionReport(lastSession);
            }
        }

        private static string FormatMemory(long bytes)
        {
            if (bytes <= 0)
            {
                return L.T("данные недоступны");
            }
            return (bytes / 1073741824d).ToString("0.0") + L.T(" ГБ");
        }

        private async void BoostCenterExportDiagnosticsRequested(
            object sender,
            EventArgs e)
        {
            if (boostCenterOverlay == null)
            {
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = L.T("Сохранить диагностику Boostix"),
                Filter = L.T("Архив диагностики (*.zip)|*.zip"),
                DefaultExt = ".zip",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = ProductBrand.ProductFileName + "-Diagnostic-" +
                    DateTime.Now.ToString(
                        "yyyyMMdd-HHmmss",
                        CultureInfo.InvariantCulture) + ".zip"
            };
            bool? accepted = dialog.ShowDialog(this);
            if (accepted != true)
            {
                return;
            }

            boostCenterOverlay.SetDiagnosticExportMessage(
                L.T("Собираем диагностику"),
                L.T("Получаем показатели Windows и журналы Boostix, удаляем личные данные."),
                false);
            try
            {
                DiagnosticSnapshot snapshot = await Task.Run(
                    delegate { return DiagnosticSnapshotProvider.Capture(); });
                List<BoostSessionReport> reports = await Task.Run(
                    delegate
                    {
                        return DiagnosticSessionHistory.LoadRecent(
                            DiagnosticSessionHistory.MaximumSessionCount);
                    });
                lock (activeMaintenanceSync)
                {
                    if (currentSession != null)
                    {
                        currentSession.ApplyDiagnosticSnapshot(snapshot);
                    }
                    reports = MergeExportSessionSnapshot(
                        reports,
                        sessionHistory,
                        currentSession);
                }
                string destination = dialog.FileName;
                string applicationVersion = GetApplicationVersion();
                await Task.Run(delegate
                {
                    DiagnosticBundleBuilder.WriteBundle(
                        destination,
                        DiagnosticExportBuilder.BuildSafeReport(
                            snapshot,
                            reports,
                            "ApplicationVersion=" + applicationVersion),
                        DiagnosticBundleBuilder.BuildSystemSummary(applicationVersion),
                        DiagnosticBundleBuilder.CollectDefaultSources());
                });

                latestDiagnosticSnapshot = snapshot;
                sessionHistory = reports;
                boostCenterOverlay.SetDiagnosticSnapshot(snapshot);
                boostCenterOverlay.SetSessionHistory(reports);
                boostCenterOverlay.SetDiagnosticExportMessage(
                    L.T("Диагностика сохранена"),
                    L.T("Архив ") + System.IO.Path.GetFileName(destination) +
                        L.T(" готов: отчёт и журналы Boostix без имени пользователя, ") +
                        L.T("личных папок и секретов. Его можно отправить в поддержку."),
                    false);
                ShowFileInExplorer(destination);
            }
            catch (Exception ex)
            {
                CrashLog.Write("Diagnostic export failed.", ex);
                boostCenterOverlay.SetDiagnosticExportMessage(
                    L.T("Не удалось сохранить диагностику"),
                    L.T("Windows вернул ошибку ") + ex.GetType().Name +
                        L.T(". Выберите другую доступную папку и повторите."),
                    true);
            }
        }

        private static void ShowFileInExplorer(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        "explorer.exe"),
                    Arguments = "/select,\"" + path + "\"",
                    UseShellExecute = false
                });
            }
            catch (Exception ex)
            {
                CrashLog.Write("Could not show the diagnostic archive in Explorer.", ex);
            }
        }

        private static List<BoostSessionReport> MergeExportSessionSnapshot(
            IEnumerable<BoostSessionReport> storedReports,
            IEnumerable<BoostSessionReport> inMemoryReports,
            BoostSessionReport activeReport)
        {
            var merged = new Dictionary<string, BoostSessionReport>(
                StringComparer.OrdinalIgnoreCase);
            Action<IEnumerable<BoostSessionReport>> addReports = delegate(
                IEnumerable<BoostSessionReport> source)
            {
                foreach (BoostSessionReport report in source ??
                    Enumerable.Empty<BoostSessionReport>())
                {
                    if (report == null ||
                        string.IsNullOrWhiteSpace(report.SessionId) ||
                        merged.ContainsKey(report.SessionId))
                    {
                        continue;
                    }
                    merged.Add(report.SessionId, report.Clone());
                }
            };

            if (activeReport != null &&
                !string.IsNullOrWhiteSpace(activeReport.SessionId))
            {
                merged[activeReport.SessionId] = activeReport.Clone();
            }
            addReports(inMemoryReports);
            addReports(storedReports);
            return merged.Values
                .OrderByDescending(delegate(BoostSessionReport report)
                {
                    return report.StartedUtc;
                })
                .Take(DiagnosticSessionHistory.MaximumSessionCount)
                .ToList();
        }

        private async void BoostCenterBenchmarkRequested(
            object sender,
            BoostBenchmarkRequestEventArgs e)
        {
            if (benchmarkCancellation != null)
            {
                return;
            }

            benchmarkCancellation = new CancellationTokenSource();
            PerformanceProofStep issuedStep = null;
            var progress = new Progress<PerformanceCaptureProgress>(delegate(PerformanceCaptureProgress item)
            {
                if (boostCenterOverlay != null)
                {
                    boostCenterOverlay.SetBenchmarkProgress(
                        issuedStep == null
                            ? "PROOF MODE"
                            : L.T("PROOF MODE · ШАГ ") +
                                issuedStep.StepNumber.ToString(
                                    CultureInfo.CurrentCulture) + "/" +
                                issuedStep.TotalSteps.ToString(
                                    CultureInfo.CurrentCulture),
                        item == null ? L.T("Готовим замер.") : item.Message,
                        item == null ? 0 : item.Percent);
                }
            });
            Interlocked.Exchange(ref benchmarkCaptureActive, 1);
            if (targetSelectorButton != null)
            {
                targetSelectorButton.IsEnabled = false;
            }

            try
            {
                PerformanceTargetProcess captureTarget = e != null && e.Elevate
                    ? (lastCaptureAttempt == null
                        ? null
                        : lastCaptureAttempt.Target)
                    : BuildSelectedPerformanceTarget();
                if (captureTarget == null)
                {
                    boostCenterOverlay.SetBenchmarkMessage(
                        L.T("СНАЧАЛА ВЫБЕРИТЕ ИГРУ"),
                        L.T("Для замера FPS выберите запущенную игру."),
                        true);
                    return;
                }

                PerformanceProofContext frozenContext = await Task.Run(
                    delegate
                    {
                        return BuildPerformanceProofContext(
                            captureTarget,
                            GuidedProofScenarioId);
                    },
                    benchmarkCancellation.Token);
                string frozenTargetIdentity = frozenContext == null
                    ? string.Empty
                    : frozenContext.BuildExecutableIdentity();
                string frozenContextKey = frozenContext == null
                    ? string.Empty
                    : frozenContext.BuildContextKey();
                if (string.IsNullOrWhiteSpace(frozenTargetIdentity) ||
                    string.IsNullOrWhiteSpace(frozenContextKey))
                {
                    boostCenterOverlay.SetBenchmarkMessage(
                        L.T("НЕ УДАЛОСЬ ЗАФИКСИРОВАТЬ СЦЕНАРИЙ"),
                        L.T("Проверьте доступ к исполняемому файлу игры и повторите попытку."),
                        true);
                    return;
                }

                PerformanceProofCoordinatorSnapshot snapshot = proofCoordinator == null
                    ? null
                    : proofCoordinator.GetSnapshot();
                if (snapshot == null ||
                    snapshot.State != PerformanceProofCoordinatorState.AwaitingRun)
                {
                    PerformanceProofCoordinator started;
                    string startError;
                    if (!PerformanceProofCoordinator.TryStart(
                            frozenTargetIdentity,
                            frozenContextKey,
                            out started,
                            out startError))
                    {
                        boostCenterOverlay.SetBenchmarkMessage(
                            L.T("PROOF MODE НЕ ЗАПУЩЕН"),
                            startError,
                            true);
                        return;
                    }
                    string initialSaveError;
                    if (!proofCheckpointStore.TrySave(
                            started,
                            out initialSaveError))
                    {
                        boostCenterOverlay.SetBenchmarkMessage(
                            L.T("НЕ УДАЛОСЬ СОХРАНИТЬ ТЕСТ"),
                            initialSaveError,
                            true);
                        return;
                    }
                    proofCoordinator = started;
                    lastCaptureAttempt = null;
                    snapshot = proofCoordinator.GetSnapshot();
                }

                if (!string.Equals(
                        snapshot.TargetIdentity,
                        frozenTargetIdentity,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        snapshot.ContextKey,
                        frozenContextKey,
                        StringComparison.Ordinal))
                {
                    PerformanceProofTransition cancelled = proofCoordinator.Cancel(
                        L.T("Игра, монитор или настройки изменились. Начните новый замер FPS."));
                    string ignoredSaveError;
                    proofCheckpointStore.TrySave(
                        proofCoordinator,
                        out ignoredSaveError);
                    boostCenterOverlay.SetPerformanceProofSnapshot(
                        proofCoordinator.GetSnapshot());
                    boostCenterOverlay.SetBenchmarkMessage(
                        L.T("СЦЕНАРИЙ ИЗМЕНИЛСЯ"),
                        cancelled.Message,
                        true);
                    return;
                }

                issuedStep = snapshot.NextStep;
                if (issuedStep == null)
                {
                    boostCenterOverlay.SetBenchmarkMessage(
                        L.T("PROOF MODE ЗАВЕРШЁН"),
                        snapshot.Message,
                        false);
                    return;
                }
                boostCenterOverlay.SetPerformanceProofSnapshot(snapshot);
                if (boostActive != issuedStep.RequiresBoost)
                {
                    boostCenterOverlay.SetBenchmarkMessage(
                        issuedStep.RequiresBoost
                            ? L.T("ВКЛЮЧИТЕ BOOSTIX")
                            : L.T("ОТКЛЮЧИТЕ BOOSTIX"),
                        issuedStep.Instruction +
                            L.T(" После смены состояния нажмите кнопку этапа ещё раз."),
                        false);
                    return;
                }

                PerformanceCaptureAttemptResult result;
                if (e != null && e.Elevate)
                {
                    result = await PerformanceCaptureService.RetryElevatedAsync(
                        lastCaptureAttempt,
                        progress,
                        benchmarkCancellation.Token);
                }
                else
                {
                    result = await PerformanceCaptureService.CaptureTargetAsync(
                        captureTarget,
                        progress,
                        benchmarkCancellation.Token);
                }

                lastCaptureAttempt = result;
                if (result != null &&
                    result.Status == PerformanceCaptureStatus.Completed &&
                    result.Performance != null)
                {
                    PerformanceProofContext actualContext = await Task.Run(
                        delegate
                        {
                            return BuildPerformanceProofContext(
                                result.Target,
                                GuidedProofScenarioId);
                        },
                        benchmarkCancellation.Token);
                    string actualTargetIdentity = actualContext == null
                        ? string.Empty
                        : actualContext.BuildExecutableIdentity();
                    string actualContextKey = actualContext == null
                        ? string.Empty
                        : actualContext.BuildContextKey();
                    result.Performance.ComparisonContextKey = actualContextKey;

                    string beforeCheckpoint = proofCoordinator.CreateCheckpoint();
                    var proofRun = new PerformanceProofRun
                    {
                        RunId = Guid.NewGuid().ToString("N"),
                        TargetIdentity = actualTargetIdentity,
                        ContextKey = actualContextKey,
                        Variant = issuedStep.ExpectedVariant,
                        CapturedUtc = result.Performance.CapturedUtc,
                        CaptureDurationSeconds = result.CaptureDurationSeconds,
                        FrameTimesMs = result.FrameTimesMs == null
                            ? new List<double>()
                            : new List<double>(result.FrameTimesMs)
                    };
                    PerformanceProofTransition transition = proofCoordinator.SubmitRun(
                        proofRun,
                        issuedStep.PairId,
                        issuedStep.StepNumber);
                    if (transition.Status == PerformanceProofSubmissionStatus.Accepted ||
                        transition.Status == PerformanceProofSubmissionStatus.Completed)
                    {
                        string saveError;
                        if (!proofCheckpointStore.TrySave(
                                proofCoordinator,
                                out saveError))
                        {
                            PerformanceProofCoordinator rolledBack;
                            string rollbackError;
                            if (PerformanceProofCoordinator.TryRestore(
                                    beforeCheckpoint,
                                    out rolledBack,
                                    out rollbackError))
                            {
                                proofCoordinator = rolledBack;
                            }
                            boostCenterOverlay.SetPerformanceProofSnapshot(
                                proofCoordinator.GetSnapshot());
                            boostCenterOverlay.SetBenchmarkMessage(
                                L.T("ЭТАП НЕ СОХРАНЁН"),
                                saveError + L.T(" Повторите текущий этап."),
                                true);
                            return;
                        }
                        StorePerformanceResult(result.Performance);
                    }
                    else
                    {
                        string transitionSaveError;
                        proofCheckpointStore.TrySave(
                            proofCoordinator,
                            out transitionSaveError);
                    }

                    lastCaptureAttempt = null;
                    PerformanceProofCoordinatorSnapshot nextSnapshot =
                        proofCoordinator.GetSnapshot();
                    boostCenterOverlay.SetPerformanceProofSnapshot(nextSnapshot);
                    if (transition.Status == PerformanceProofSubmissionStatus.Completed &&
                        transition.FinalResult != null)
                    {
                        boostCenterOverlay.SetBenchmarkMessage(
                            L.T("PROOF MODE ЗАВЕРШЁН"),
                            FormatPerformanceProofSummary(transition.FinalResult),
                            transition.FinalResult.Verdict ==
                                PerformanceProofVerdict.Negative);
                    }
                    else if (transition.Status == PerformanceProofSubmissionStatus.Accepted)
                    {
                        boostCenterOverlay.SetBenchmarkMessage(
                            L.T("ЭТАП ") + issuedStep.StepNumber.ToString(
                                CultureInfo.CurrentCulture) + "/" +
                                issuedStep.TotalSteps.ToString(
                                    CultureInfo.CurrentCulture) + L.T(" ПРИНЯТ"),
                            transition.NextStep == null
                                ? transition.Message
                                : transition.NextStep.Instruction,
                            false);
                    }
                    else
                    {
                        boostCenterOverlay.SetBenchmarkMessage(
                            transition.Status == PerformanceProofSubmissionStatus.Failed
                                ? L.T("PROOF MODE ОСТАНОВЛЕН")
                                : L.T("ЭТАП НЕ ПРИНЯТ"),
                            transition.Message,
                            transition.Status == PerformanceProofSubmissionStatus.Failed);
                    }
                }
                else if (result != null && result.CanRetryElevated)
                {
                    boostCenterOverlay.SetBenchmarkNeedsElevation(result.Message);
                }
                else
                {
                    boostCenterOverlay.SetBenchmarkMessage(
                        L.T("ЗАМЕР НЕ ВЫПОЛНЕН"),
                        result == null ? L.T("PresentMon не вернул результат.") : result.Message,
                        true);
                }
            }
            catch (OperationCanceledException)
            {
                boostCenterOverlay.SetBenchmarkMessage(
                    L.T("ЗАМЕР ОТМЕНЁН"),
                    L.T("Измерение производительности остановлено."),
                    true);
            }
            catch (Exception ex)
            {
                CrashLog.Write("Proof Mode capture failed.", ex);
                boostCenterOverlay.SetBenchmarkMessage(
                    L.T("ЗАМЕР НЕ ВЫПОЛНЕН"),
                    L.T("Замер не получился. Повторите этот проход."),
                    true);
            }
            finally
            {
                Interlocked.Exchange(ref benchmarkCaptureActive, 0);
                benchmarkCancellation.Dispose();
                benchmarkCancellation = null;
                if (targetSelectorButton != null)
                {
                    targetSelectorButton.IsEnabled =
                        !boostActive && !animationRunning;
                }
            }
        }

        private static string FormatPerformanceProofSummary(
            PerformanceProofResult result)
        {
            if (result == null)
            {
                return L.T("Сравнение не вернуло результат.");
            }
            return string.Format(
                CultureInfo.CurrentCulture,
                L.T("{0} Средний FPS: {1:+0.0;-0.0;0.0}%, 1% low: {2:+0.0;-0.0;0.0} FPS, P95: {3:+0.0;-0.0;0.0} мс, разброс ±{4:0.0}%."),
                result.Summary ?? string.Empty,
                result.AverageFpsDeltaPercent,
                result.OnePercentLowFpsDelta,
                result.P95FrameTimeReductionMs,
                result.VariabilityPercent);
        }

        private PerformanceTargetProcess BuildSelectedPerformanceTarget()
        {
            GameProcessSnapshot snapshot;
            string error;
            if (selectedTarget == null ||
                !gameTargetService.TryResolve(
                    selectedTarget,
                    out snapshot,
                    out error))
            {
                return null;
            }

            long workingSetBytes = 0;
            try
            {
                using (Process process = Process.GetProcessById(
                    snapshot.ProcessId))
                {
                    workingSetBytes = Math.Max(0L, process.WorkingSet64);
                }
            }
            catch
            {
                // CaptureAsync performs the authoritative PID/start/path check.
                // Working-set size is display-only and must never select a target.
            }

            return new PerformanceTargetProcess
            {
                ProcessId = snapshot.ProcessId,
                ProcessName = snapshot.ProcessName,
                StartTimeUtc = snapshot.StartTimeUtc,
                WorkingSetBytes = workingSetBytes,
                ExecutablePath = snapshot.ExecutablePath
            };
        }

        private string BuildPerformanceContextKey(
            PerformanceTargetProcess capturedTarget)
        {
            PerformanceProofContext context = BuildPerformanceProofContext(
                capturedTarget,
                "UNPAIRED-60S;" + Guid.NewGuid().ToString("N"));
            return context == null ? string.Empty : context.BuildContextKey();
        }

        private PerformanceProofContext BuildPerformanceProofContext(
            PerformanceTargetProcess capturedTarget,
            string scenarioId)
        {
            if (capturedTarget == null ||
                capturedTarget.ProcessId <= 0 ||
                string.IsNullOrWhiteSpace(capturedTarget.ExecutablePath))
            {
                return null;
            }

            long executableLength = 0;
            string executableVersion = string.Empty;
            try
            {
                var file = new FileInfo(capturedTarget.ExecutablePath);
                executableLength = file.Exists ? file.Length : 0L;
                executableVersion = FileVersionInfo
                    .GetVersionInfo(capturedTarget.ExecutablePath)
                    .FileVersion ?? string.Empty;
            }
            catch
            {
                // The exact normalized path and process start identity still make
                // the context safe; missing version metadata remains explicit.
            }

            int displayWidth = Math.Max(
                320,
                (int)Math.Round(SystemParameters.PrimaryScreenWidth));
            int displayHeight = Math.Max(
                200,
                (int)Math.Round(SystemParameters.PrimaryScreenHeight));
            try
            {
                using (Process process = Process.GetProcessById(
                    capturedTarget.ProcessId))
                {
                    IntPtr monitor = MonitorFromWindow(
                        process.MainWindowHandle,
                        MonitorDefaultToNearest);
                    var information = new MonitorInformation
                    {
                        Size = Marshal.SizeOf(typeof(MonitorInformation))
                    };
                    if (monitor != IntPtr.Zero &&
                        GetMonitorInfo(monitor, ref information))
                    {
                        displayWidth = Math.Max(
                            320,
                            information.Monitor.Right - information.Monitor.Left);
                        displayHeight = Math.Max(
                            200,
                            information.Monitor.Bottom - information.Monitor.Top);
                    }
                }
            }
            catch
            {
                // Fall back to the current primary-screen dimensions.
            }

            double refreshRate = latestPreflight != null &&
                latestPreflight.RefreshRate >= 10
                ? latestPreflight.RefreshRate
                : 60.0;
            string adapterId = latestDiagnosticSnapshot == null
                ? string.Empty
                : (latestDiagnosticSnapshot.GpuAdapterLuid ?? string.Empty);
            if (string.IsNullOrWhiteSpace(adapterId) &&
                latestDiagnosticSnapshot != null)
            {
                adapterId = latestDiagnosticSnapshot.GpuAdapterNames ?? string.Empty;
            }

            return new PerformanceProofContext
            {
                ExecutablePath = capturedTarget.ExecutablePath,
                ExecutableSha256 = ComputeFileSha256(
                    capturedTarget.ExecutablePath),
                ExecutableVersion = executableVersion,
                ExecutableLength = executableLength,
                ScenarioId = scenarioId,
                DisplayWidth = displayWidth,
                DisplayHeight = displayHeight,
                RefreshRateHz = refreshRate,
                DisplayMode = "CURRENT;VSYNC=UNKNOWN;FRAMECAP=UNKNOWN",
                GraphicsPreset = "USER-CONFIRMED-UNCHANGED",
                GraphicsAdapterId = string.IsNullOrWhiteSpace(adapterId)
                    ? "UNKNOWN"
                    : adapterId,
                VSyncEnabled = false,
                FrameLimit = 0
            };
        }

        private static string ComputeFileSha256(string path)
        {
            try
            {
                using (FileStream stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (SHA256 algorithm = SHA256.Create())
                {
                    byte[] hash = algorithm.ComputeHash(stream);
                    var text = new StringBuilder(hash.Length * 2);
                    foreach (byte value in hash)
                    {
                        text.Append(value.ToString(
                            "x2",
                            CultureInfo.InvariantCulture));
                    }
                    return text.ToString();
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private void StorePerformanceResult(BoostPerformanceResult performance)
        {
            if (performance == null)
            {
                return;
            }
            if (currentSession != null)
            {
                currentSession.Performance = performance;
                currentSession.AddAction(
                    L.T("ТЕСТ ПРОИЗВОДИТЕЛЬНОСТИ"),
                    performance.Frames + L.T(" кадров проанализировано."),
                    BoostActionOutcome.Changed);
                SaveCurrentSession();
                return;
            }

            BoostSessionReport report = lastSession ?? BoostSessionReport.Start("Benchmark");
            report.Performance = performance;
            report.AddAction(
                L.T("ТЕСТ ПРОИЗВОДИТЕЛЬНОСТИ"),
                performance.Frames + L.T(" кадров проанализировано."),
                BoostActionOutcome.Changed);
            if (!report.EndedUtc.HasValue)
            {
                report.Complete("Completed", L.T("Ручной тест производительности."));
            }
            BoostSessionReportStore.Save(report);
            lastSession = report;
            RefreshSessionHistory();
            if (boostCenterOverlay != null)
            {
                boostCenterOverlay.SetSessionReport(lastSession);
            }
        }

        /// <summary>
        /// Tray icon and Ctrl+Alt+B exist for real use; demo/test launches
        /// opt in with --demo-tray so test runs leave no tray icons behind.
        /// </summary>
        private bool TrayAndHotkeyEnabled
        {
            get
            {
                return !updateHealthProbe &&
                    (!demoMode || HasLaunchArgument(launchArguments, "--demo-tray"));
            }
        }

        /// <summary>
        /// The dashboard self-test plays once, after the first frame is on
        /// screen, so it is seen from its first degree.
        /// </summary>
        private void QueueGaugeSelfTest()
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(delegate
                {
                    if (boostGauge != null && !gaugeSelfTestPlayed &&
                        IsVisible && WindowState != WindowState.Minimized)
                    {
                        gaugeSelfTestPlayed = true;
                        boostGauge.PlaySelfTest();
                    }
                }));
        }

        private void StartTrayAndHotkey()
        {
            if (!TrayAndHotkeyEnabled)
            {
                return;
            }
            try
            {
                trayIcon = new BoostixTrayIcon(Assembly.GetExecutingAssembly().Location);
                trayIcon.OpenRequested += delegate { RestoreWindow(); };
                trayIcon.BoostToggleRequested += delegate { ToggleBoostFromOutside(); };
                trayIcon.ExitRequested += delegate { Close(); };
                trayIcon.SetBoostState(boostActive);
            }
            catch (Exception ex)
            {
                trayIcon = null;
                CrashLog.Write("Could not create the notification-area icon.", ex);
            }
            ApplyHotkeyPreference();
        }

        private void ApplyHotkeyPreference()
        {
            if (!TrayAndHotkeyEnabled || windowHandle == IntPtr.Zero)
            {
                return;
            }
            if (centerSettings.GlobalHotkey && !hotkeyRegistered)
            {
                hotkeyRegistered = BoostHotkey.Register(windowHandle);
                if (!hotkeyRegistered)
                {
                    CrashLog.Write(
                        "The Boost hotkey " + BoostHotkey.DisplayText + " is used by another program.",
                        null);
                }
            }
            else if (!centerSettings.GlobalHotkey && hotkeyRegistered)
            {
                BoostHotkey.Unregister(windowHandle);
                hotkeyRegistered = false;
            }
        }

        private IntPtr HotkeyMessageHook(
            IntPtr window,
            int message,
            IntPtr wordParameter,
            IntPtr longParameter,
            ref bool handled)
        {
            if (message == BoostHotkey.WmHotkey && wordParameter.ToInt32() == BoostHotkey.Id)
            {
                handled = true;
                Dispatcher.BeginInvoke(new Action(ToggleBoostFromOutside));
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// Boost toggled from the hotkey or the tray while the window may be
        /// hidden behind a game: say what happened, or bring the window back
        /// when Boostix needs the user.
        /// </summary>
        private void ToggleBoostFromOutside()
        {
            if (!boostActive && !animationRunning && selectedTarget == null && !demoMode)
            {
                Notify(L.T("Сначала выберите игру в Boostix"));
                RestoreWindow();
                OpenTargetSelector();
                return;
            }
            if (!boostActive && !animationRunning && centerSettings.GameSignals &&
                TrayAndHotkeyEnabled && IsAwayFromWindow)
            {
                // Preparing takes a few seconds: show at once that the
                // shortcut was heard.
                if (boostToast == null)
                {
                    boostToast = new BoostToast();
                }
                boostToast.ShowMessage(L.T("Запускаю Boost…"), false);
            }
            ToggleBoost();
        }

        private void Notify(string text)
        {
            if (trayIcon != null && IsAwayFromWindow)
            {
                trayIcon.Notify(text);
            }
        }

        private bool IsAwayFromWindow
        {
            get { return !IsVisible || WindowState == WindowState.Minimized || !IsActive; }
        }

        /// <summary>
        /// While the user is in a game, Boost changes are confirmed with the
        /// badge and a short chime; with signals off, a notification as before.
        /// </summary>
        private void SignalBoostState(string text, bool lit, bool chime)
        {
            if (!IsAwayFromWindow)
            {
                return;
            }
            if (!centerSettings.GameSignals || !TrayAndHotkeyEnabled)
            {
                Notify(text);
                return;
            }
            if (!chime)
            {
                return;
            }
            if (boostToast == null)
            {
                boostToast = new BoostToast();
            }
            boostToast.ShowMessage(text, lit);
            if (!demoMode)
            {
                ConfirmationSound.Play(lit);
            }
        }

        private void MinimizeWindow()
        {
            if (centerSettings.MinimizeToTray && trayIcon != null)
            {
                Hide();
                trayIcon.Notify(L.T("Boostix работает в трее. ") + BoostHotkey.DisplayText + L.T(" включает Boost."));
                return;
            }
            WindowState = WindowState.Minimized;
        }

        private void RestoreWindow()
        {
            if (!IsVisible)
            {
                Show();
            }
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Activate();
            QueueGaugeSelfTest();
        }

        private void SetBoostAutomationState(bool active)
        {
            if (trayIcon != null)
            {
                trayIcon.SetBoostState(active);
            }
            // A game closing switches Boost off by itself; the summary after
            // the game says that, so only user switches get the badge.
            bool userSwitch = active || string.Equals(
                deactivationSessionStatus,
                "Completed",
                StringComparison.OrdinalIgnoreCase);
            SignalBoostState(active ? L.T("Boost включён") : L.T("Boost выключен"), active, userSwitch);
            AutomationProperties.SetName(
                boostButton,
                active ? L.T("Отключить Boost производительности") : L.T("Активировать Boost производительности"));
            AutomationProperties.SetHelpText(
                boostButton,
                active
                    ? L.T("Останавливает Boost и возвращает план электропитания.")
                    : L.T("Подготавливает Windows к высокой нагрузке и поддерживает приоритет активного полноэкранного приложения."));
        }

        private void RememberBoostCenterFocusReturn(IInputElement preferred)
        {
            if (boostCenterOverlay != null && boostCenterOverlay.IsOpen)
            {
                return;
            }

            IInputElement focused = Keyboard.FocusedElement;
            boostCenterFocusReturn =
                !object.ReferenceEquals(focused, this) &&
                CanReceiveKeyboardFocus(focused)
                ? focused
                : preferred;
            if (!CanReceiveKeyboardFocus(boostCenterFocusReturn))
            {
                boostCenterFocusReturn = centerButton;
            }
        }

        private void RestoreBoostCenterFocus()
        {
            IInputElement requested = boostCenterFocusReturn;
            boostCenterFocusReturn = null;
            Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(delegate
                {
                    IInputElement target = CanReceiveKeyboardFocus(requested)
                        ? requested
                        : centerButton;
                    if (CanReceiveKeyboardFocus(target))
                    {
                        var uiElement = target as UIElement;
                        if (uiElement != null)
                        {
                            uiElement.Focus();
                        }
                        Keyboard.Focus(target);
                    }
                }));
        }

        private static bool CanReceiveKeyboardFocus(IInputElement element)
        {
            var uiElement = element as UIElement;
            if (uiElement != null)
            {
                return uiElement.IsVisible &&
                    uiElement.IsEnabled &&
                    uiElement.Focusable;
            }

            var contentElement = element as ContentElement;
            return contentElement != null &&
                contentElement.IsEnabled &&
                contentElement.Focusable;
        }

        private void SubscribeToSystemThemeNotifications()
        {
            if (themeNotificationsSubscribed)
            {
                return;
            }

            SystemParameters.StaticPropertyChanged +=
                SystemParametersStaticPropertyChanged;
            themeNotificationsSubscribed = true;
        }

        private void SystemParametersStaticPropertyChanged(
            object sender,
            PropertyChangedEventArgs e)
        {
            if (e != null &&
                !string.IsNullOrEmpty(e.PropertyName) &&
                !string.Equals(
                    e.PropertyName,
                    "HighContrast",
                    StringComparison.Ordinal))
            {
                return;
            }
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(ApplyRuntimeTheme));
        }

        /// <summary>
        /// Replaces every semantic resource and resets controls that own mutable
        /// animation brushes. This is safe to call while High Contrast changes;
        /// no restart and no mutation of a frozen application brush is required.
        /// </summary>
        private void ApplyRuntimeTheme()
        {
            Application application = Application.Current;
            if (application != null)
            {
                BoostixDesignTokens.ApplyThemeResources(application.Resources);
                BoostixDesignTokens.ApplyWindowTint(application.Resources, IsActive);
            }

            RefreshChromeButtonTheme(centerButton);
            RefreshChromeButtonTheme(minimizeButton);
            RefreshChromeButtonTheme(closeButton);

            if (targetSelectorButton != null)
            {
                var background = targetSelectorButton.Background as SolidColorBrush;
                var border = targetSelectorButton.BorderBrush as SolidColorBrush;
                if (background != null)
                {
                    background.BeginAnimation(
                        SolidColorBrush.ColorProperty,
                        null);
                    background.Color = BoostixDesignTokens.Surface;
                }
                if (border != null)
                {
                    border.BeginAnimation(
                        SolidColorBrush.ColorProperty,
                        null);
                    border.Color = targetSelectorButton.IsMouseOver
                        ? BoostixDesignTokens.SecondaryText
                        : BoostixDesignTokens.Border;
                }
                if (targetSelectorButton.ContextMenu != null)
                {
                    targetSelectorButton.ContextMenu.IsOpen = false;
                }
            }

            UpdateHomeSystemCard();
            if (boostGauge != null)
            {
                boostGauge.RefreshTheme();
            }
            UpdateLiveSessionSummary();

            if (boostCenterOverlay != null)
            {
                boostCenterOverlay.RefreshTheme();
            }
            ApplyNativeWindowAppearance();
        }

        private static Color TransparentThemeBackground()
        {
            Color background = BoostixDesignTokens.Background;
            return Color.FromArgb(0, background.R, background.G, background.B);
        }

        private static void RefreshChromeButtonTheme(Button button)
        {
            var visuals = button == null
                ? null
                : button.Tag as ChromeButtonVisuals;
            if (visuals == null)
            {
                return;
            }

            visuals.BackgroundBrush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                null);
            visuals.GlyphBrush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                null);
            var translation = button.RenderTransform as TranslateTransform;
            if (translation != null)
            {
                translation.BeginAnimation(TranslateTransform.YProperty, null);
                translation.Y = 0;
            }

            bool hovered = button.IsMouseOver;
            visuals.BackgroundBrush.Color = hovered
                ? (visuals.IsClose
                    ? BoostixDesignTokens.Destructive
                    : BoostixDesignTokens.Hover)
                : TransparentThemeBackground();
            visuals.GlyphBrush.Color = hovered
                ? BoostixDesignTokens.AccentForeground
                : BoostixDesignTokens.ChromeGlyph;
        }

        private static DoubleAnimation MakeEaseAnimation(double from, double to, TimeSpan duration, EasingMode mode)
        {
            var animation = new DoubleAnimation(from, to, duration);
            animation.EasingFunction = new CubicEase { EasingMode = mode };
            return animation;
        }

        private void WindowKeyDown(object sender, KeyEventArgs e)
        {
            if (updateOverlay != null && updateOverlay.ConsumesApplicationInput)
            {
                if (e.Key == Key.Escape)
                {
                    updateOverlay.HandleEscape();
                }
                e.Handled = true;
                return;
            }

            if (optimizationOverlay != null && optimizationOverlay.IsFlowVisible)
            {
                if (e.Key == Key.Escape)
                {
                    optimizationOverlay.HandleEscape();
                }
                e.Handled = true;
                return;
            }

            if (whatsNewOverlay != null && whatsNewOverlay.IsShown)
            {
                if (e.Key == Key.Escape)
                {
                    whatsNewOverlay.Hide();
                }
                e.Handled = true;
                return;
            }

            if (boostCenterOverlay != null &&
                (boostCenterOverlay.ConsumesApplicationInput ||
                 (e.Key == Key.Tab &&
                  (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)))
            {
                boostCenterOverlay.HandleKey(e);
                return;
            }

            if (e.Key == Key.OemComma &&
                (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                preflightForBoost = false;
                boostCenterOverlay.OpenSettings();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter || e.Key == Key.Space)
            {
                if (Keyboard.FocusedElement is ButtonBase)
                {
                    return;
                }
                ToggleBoost();
                e.Handled = true;
            }
        }

        /// <summary>
        /// On a framed window WindowChrome sets the window region inside its
        /// own SourceInitialized handler; the resize that follows raises
        /// Loaded before BoostWindowSourceInitialized runs. The handle is
        /// therefore stored first, so Loaded can register Ctrl+Alt+B.
        /// </summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            windowHandle = new WindowInteropHelper(this).Handle;
            base.OnSourceInitialized(e);
        }

        private void BoostWindowSourceInitialized(object sender, EventArgs e)
        {
            windowHandle = new WindowInteropHelper(this).Handle;
            if (windowHandle == IntPtr.Zero)
            {
                return;
            }

            if (WindowStyle == WindowStyle.SingleBorderWindow)
            {
                WindowBackdrop.HideSystemCaptionButtons(windowHandle);
            }
            ApplyNativeWindowAppearance();
            backdropSource = HwndSource.FromHwnd(windowHandle);
            if (backdropSource != null)
            {
                backdropSource.AddHook(BackdropMessageHook);
            }
            if (TrayAndHotkeyEnabled)
            {
                hotkeySource = HwndSource.FromHwnd(windowHandle);
                if (hotkeySource != null)
                {
                    hotkeySource.AddHook(HotkeyMessageHook);
                }
            }
            if (demoMode)
            {
                return;
            }

            windowSource = HwndSource.FromHwnd(windowHandle);
            if (windowSource != null)
            {
                windowSource.AddHook(WindowMessageHook);
            }
            LocationChanged += BoostWindowLocationChanged;
            ApplyMonitorWorkAreaBounds(true);
        }

        private void ApplyNativeWindowAppearance()
        {
            if (windowHandle == IntPtr.Zero)
            {
                return;
            }

            try
            {
                int cornerPreference = DwmRoundCornerPreference;
                DwmSetWindowAttribute(
                    windowHandle,
                    DwmWindowCornerPreference,
                    ref cornerPreference,
                    Marshal.SizeOf(typeof(int)));

                // COLORREF uses 0x00BBGGRR. In High Contrast the native edge
                // follows the user's ActiveBorder colour as well.
                Color themeBorder = BoostixDesignTokens.Border;
                int borderColor = themeBorder.R |
                    (themeBorder.G << 8) |
                    (themeBorder.B << 16);
                DwmSetWindowAttribute(
                    windowHandle,
                    DwmBorderColor,
                    ref borderColor,
                    Marshal.SizeOf(typeof(int)));
            }
            catch (DllNotFoundException)
            {
                // Older Windows versions keep the rectangular native fallback.
            }
            catch (EntryPointNotFoundException)
            {
                // DWM corner attributes are optional on older Windows versions.
            }

            UpdateGlassBackdrop();
        }

        /// <summary>
        /// Puts the window on Windows 11 glass, or back on opaque petrol,
        /// whenever High Contrast or the transparency effects switch changes.
        /// </summary>
        private void UpdateGlassBackdrop()
        {
            if (windowHandle == IntPtr.Zero)
            {
                return;
            }
            bool glass = WindowBackdrop.IsAvailable &&
                WindowBackdrop.TryApply(windowHandle);
            if (!glass && BoostixDesignTokens.GlassBackdrop)
            {
                WindowBackdrop.Remove(windowHandle);
            }
            if (glass == BoostixDesignTokens.GlassBackdrop)
            {
                return;
            }

            BoostixDesignTokens.GlassBackdrop = glass;
            WindowChrome chrome = WindowChrome.GetWindowChrome(this);
            if (chrome != null)
            {
                chrome.GlassFrameThickness = glass
                    ? new Thickness(-1)
                    : new Thickness(0);
            }
            if (glass)
            {
                Background = Brushes.Transparent;
            }
            else
            {
                SetResourceReference(
                    BackgroundProperty,
                    BoostixDesignTokens.BackgroundBrushKey);
            }
            UpdateWindowFrameShape();
            ApplyRuntimeTheme();
        }

        /// <summary>
        /// Fades the window and sidebar tint the way DWM fades its backdrop
        /// when the window gains or loses focus. Theme refreshes still set the
        /// tint at once through BoostixDesignTokens.ApplyWindowTint.
        /// </summary>
        private static void FadeWindowTint(bool active)
        {
            Application application = Application.Current;
            if (application == null)
            {
                return;
            }
            FadeTintResource(
                application.Resources,
                BoostixDesignTokens.WindowTintBrushKey,
                active ? BoostixDesignTokens.WindowTint : BoostixDesignTokens.Background);
            FadeTintResource(
                application.Resources,
                BoostixDesignTokens.SidebarTintBrushKey,
                active ? BoostixDesignTokens.SidebarTint : BoostixDesignTokens.Sidebar);
        }

        private static void FadeTintResource(
            ResourceDictionary resources,
            string key,
            Color target)
        {
            var brush = resources[key] as SolidColorBrush;
            if (brush == null || brush.IsFrozen)
            {
                brush = new SolidColorBrush(brush == null ? target : brush.Color);
                resources[key] = brush;
            }
            BoostixUi.AnimateColor(
                brush,
                target,
                BoostixDesignTokens.MotionStandardMilliseconds,
                true);
        }

        /// <summary>
        /// On glass the frame follows the 8 px corners DWM gives the window,
        /// so no untinted acrylic shows in the corners.
        /// </summary>
        private void UpdateWindowFrameShape()
        {
            double radius = BoostixDesignTokens.GlassBackdrop ? 8 : 12;
            if (windowFrame != null)
            {
                windowFrame.CornerRadius = new CornerRadius(radius);
            }
            if (windowRoot != null)
            {
                windowRoot.Clip = new RectangleGeometry(
                    new Rect(
                        0,
                        0,
                        Math.Max(0, windowRoot.ActualWidth),
                        Math.Max(0, windowRoot.ActualHeight)),
                    radius - 1,
                    radius - 1);
            }
        }

        private IntPtr BackdropMessageHook(
            IntPtr window,
            int message,
            IntPtr wordParameter,
            IntPtr longParameter,
            ref bool handled)
        {
            // Windows broadcasts WM_SETTINGCHANGE when transparency effects
            // are switched in Settings.
            if (message == WmSettingChange)
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(UpdateGlassBackdrop));
            }
            return IntPtr.Zero;
        }

        private IntPtr WindowMessageHook(
            IntPtr window,
            int message,
            IntPtr wordParameter,
            IntPtr longParameter,
            ref bool handled)
        {
            if (message == WmDpiChanged ||
                message == WmDisplayChange ||
                message == WmSettingChange ||
                message == WmExitSizeMove)
            {
                QueueMonitorWorkAreaBounds();
            }
            return IntPtr.Zero;
        }

        private void BoostWindowLocationChanged(object sender, EventArgs e)
        {
            if (applyingMonitorBounds ||
                Mouse.LeftButton == MouseButtonState.Pressed)
            {
                return;
            }
            QueueMonitorWorkAreaBounds();
        }

        private void QueueMonitorWorkAreaBounds()
        {
            if (demoMode ||
                windowHandle == IntPtr.Zero ||
                monitorBoundsQueued)
            {
                return;
            }

            monitorBoundsQueued = true;
            Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(delegate
                {
                    monitorBoundsQueued = false;
                    if (Mouse.LeftButton == MouseButtonState.Pressed)
                    {
                        return;
                    }
                    ApplyMonitorWorkAreaBounds(false);
                }));
        }

        private void ApplyMonitorWorkAreaBounds(bool centerOnMonitor)
        {
            if (demoMode ||
                applyingMonitorBounds ||
                windowHandle == IntPtr.Zero)
            {
                return;
            }

            IntPtr monitor = MonitorFromWindow(
                windowHandle,
                MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return;
            }

            var information = new MonitorInformation
            {
                Size = Marshal.SizeOf(typeof(MonitorInformation))
            };
            NativeRectangle current;
            if (!GetMonitorInfo(monitor, ref information) ||
                !GetWindowRect(windowHandle, out current))
            {
                return;
            }

            uint dpiX;
            uint dpiY;
            GetEffectiveWindowDpi(out dpiX, out dpiY);
            int[] placement = CalculateMonitorPlacementForSize(
                information.Work.Left,
                information.Work.Top,
                information.Work.Right,
                information.Work.Bottom,
                dpiX,
                dpiY,
                current.Left,
                current.Top,
                centerOnMonitor,
                BaseWindowWidth,
                BaseWindowHeight);

            applyingMonitorBounds = true;
            try
            {
                double widthDip = placement[2] * 96.0 / dpiX;
                double heightDip = placement[3] * 96.0 / dpiY;
                MinWidth = 0;
                MinHeight = 0;
                MaxWidth = double.PositiveInfinity;
                MaxHeight = double.PositiveInfinity;
                Width = widthDip;
                Height = heightDip;
                MinWidth = widthDip;
                MaxWidth = widthDip;
                MinHeight = heightDip;
                MaxHeight = heightDip;
                SetWindowPos(
                    windowHandle,
                    IntPtr.Zero,
                    placement[0],
                    placement[1],
                    placement[2],
                    placement[3],
                    SwpNoActivate | SwpNoZOrder);
            }
            finally
            {
                applyingMonitorBounds = false;
            }
        }

        private void GetEffectiveWindowDpi(out uint dpiX, out uint dpiY)
        {
            uint dpi = 0;
            try
            {
                dpi = GetDpiForWindow(windowHandle);
            }
            catch (EntryPointNotFoundException)
            {
                dpi = 0;
            }

            if (dpi >= 48 && dpi <= 768)
            {
                dpiX = dpi;
                dpiY = dpi;
                return;
            }

            PresentationSource source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
            {
                Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
                double x = Math.Abs(fromDevice.M11);
                double y = Math.Abs(fromDevice.M22);
                if (x > 0.001 && y > 0.001)
                {
                    dpiX = (uint)Math.Round(96.0 / x);
                    dpiY = (uint)Math.Round(96.0 / y);
                    return;
                }
            }

            dpiX = 96;
            dpiY = 96;
        }

        internal static int[] CalculateMonitorPlacement(
            int workLeft,
            int workTop,
            int workRight,
            int workBottom,
            uint dpiX,
            uint dpiY,
            int currentLeft,
            int currentTop,
            bool centerOnMonitor)
        {
            return CalculateMonitorPlacementForSize(
                workLeft,
                workTop,
                workRight,
                workBottom,
                dpiX,
                dpiY,
                currentLeft,
                currentTop,
                centerOnMonitor,
                BaseWindowWidth,
                BaseWindowHeight);
        }

        private static int[] CalculateMonitorPlacementForSize(
            int workLeft,
            int workTop,
            int workRight,
            int workBottom,
            uint dpiX,
            uint dpiY,
            int currentLeft,
            int currentTop,
            bool centerOnMonitor,
            double layoutWidthDip,
            double layoutHeightDip)
        {
            if (dpiX < 48 || dpiX > 768)
            {
                dpiX = 96;
            }
            if (dpiY < 48 || dpiY > 768)
            {
                dpiY = 96;
            }

            long workWidth = Math.Max(1L, (long)workRight - workLeft);
            long workHeight = Math.Max(1L, (long)workBottom - workTop);
            int insetX = Math.Max(
                0,
                (int)Math.Round(WorkAreaSafetyInset * dpiX / 96.0));
            int insetY = Math.Max(
                0,
                (int)Math.Round(WorkAreaSafetyInset * dpiY / 96.0));
            long availableWidth = Math.Max(1L, workWidth - insetX * 2L);
            long availableHeight = Math.Max(1L, workHeight - insetY * 2L);
            double baseWidthPixels = layoutWidthDip * dpiX / 96.0;
            double baseHeightPixels = layoutHeightDip * dpiY / 96.0;
            // The design surface is fixed; a work area that cannot hold it at
            // 1:1 receives a uniformly down-scaled window (flag 1).
            bool useScaledLayout =
                baseWidthPixels > availableWidth + 0.5 ||
                baseHeightPixels > availableHeight + 0.5;
            double scale = Math.Min(
                1.0,
                Math.Min(
                    availableWidth / baseWidthPixels,
                    availableHeight / baseHeightPixels));
            int width = Math.Max(
                1,
                (int)Math.Floor(baseWidthPixels * scale));
            int height = Math.Max(
                1,
                (int)Math.Floor(baseHeightPixels * scale));

            long minimumLeft = (long)workLeft + insetX;
            long minimumTop = (long)workTop + insetY;
            long maximumLeft = (long)workRight - insetX - width;
            long maximumTop = (long)workBottom - insetY - height;
            if (maximumLeft < minimumLeft)
            {
                minimumLeft = workLeft;
                maximumLeft = (long)workRight - width;
            }
            if (maximumTop < minimumTop)
            {
                minimumTop = workTop;
                maximumTop = (long)workBottom - height;
            }

            long left = centerOnMonitor
                ? (long)workLeft + (workWidth - width) / 2L
                : currentLeft;
            long top = centerOnMonitor
                ? (long)workTop + (workHeight - height) / 2L
                : currentTop;
            left = Math.Max(minimumLeft, Math.Min(maximumLeft, left));
            top = Math.Max(minimumTop, Math.Min(maximumTop, top));
            return new[]
            {
                ClampToInt32(left),
                ClampToInt32(top),
                width,
                height,
                useScaledLayout ? 1 : 0
            };
        }

        private static int ClampToInt32(long value)
        {
            if (value < int.MinValue)
            {
                return int.MinValue;
            }
            if (value > int.MaxValue)
            {
                return int.MaxValue;
            }
            return (int)value;
        }

        private async void BoostWindowLoaded(object sender, RoutedEventArgs e)
        {
            SubscribeToSystemThemeNotifications();
            ApplyRuntimeTheme();

            if (updateHealthProbe)
            {
                try
                {
                    await VerifyLocalStartupForUpdateAsync();
                    UpdateHealthHandshake.CompleteReadyHandshakeIfRequested(
                        launchArguments);
                }
                catch (Exception ex)
                {
                    CrashLog.Write(
                        "Update health probe did not reach local application readiness.",
                        ex);
                }
                Application.Current.Shutdown(0);
                return;
            }

            ShowWhatsNewIfUpdated();
            StartTrayAndHotkey();
            if (startInTray && trayIcon != null)
            {
                Hide();
            }
            QueueGaugeSelfTest();

            if (!demoMode && sessionPowerPlanManager != null)
            {
                SessionPowerPlanOperationResult recovery = await Task.Run(
                    delegate
                    {
                        return sessionPowerPlanManager.RecoverOnStartup();
                    });
                if (recovery != null &&
                    recovery.Status != SessionPowerPlanStatus.NoRecoveryNeeded &&
                    recovery.Status != SessionPowerPlanStatus.TrustedStateMissing)
                {
                    CrashLog.Write(
                        "Session power-plan startup recovery: " +
                            recovery.Status + ". " + recovery.Detail,
                        null);
                }
            }

            bool canContinue = true;
            bool updateDemo = HasLaunchArgument(
                launchArguments,
                "--demo-update");
            if (!safeMode &&
                (!demoMode || updateDemo) &&
                updateOverlay != null)
            {
                canContinue = await updateOverlay.CheckForUpdatesAsync();
            }
            if (!canContinue)
            {
                return;
            }

            boostButton.IsEnabled = !safeMode;
            if (!safeMode && optimizationOverlay != null)
            {
                optimizationOverlay.ShowIfRequired();
            }
            if (safeMode)
            {
                caption.Text = L.T("Безопасный режим");
                caption.SetResourceReference(
                    TextBlock.ForegroundProperty,
                    BoostixDesignTokens.AccentTextBrushKey);
                AutomationProperties.SetName(
                    boostButton,
                    L.T("Boostix недоступен в безопасном режиме"));
                AutomationProperties.SetHelpText(
                    boostButton,
                    L.T("В безопасном режиме Boost не запускается и Windows не меняется."));
            }
            lastSession = BoostSessionReportStore.LoadLast();
            if (lastSession != null && !lastSession.EndedUtc.HasValue)
            {
                lastSession.AddAction(
                    L.T("ПРЕДЫДУЩАЯ СЕССИЯ"),
                    L.T("Сеанс завершён при следующем запуске Boostix."),
                    BoostActionOutcome.Skipped);
                lastSession.Complete(
                    "Interrupted",
                    L.T("Предыдущая сессия завершилась вместе с приложением или Windows."));
                try
                {
                    BoostSessionReportStore.Save(lastSession);
                }
                catch (Exception ex)
                {
                    CrashLog.Write(
                        "Could not finalize the previous session report.",
                        ex);
                }
            }
            RefreshSessionHistory();
            PerformanceProofCoordinator restoredProof;
            string proofLoadError;
            if (proofCheckpointStore.TryLoad(
                    out restoredProof,
                    out proofLoadError))
            {
                proofCoordinator = restoredProof;
            }
            else if (!string.IsNullOrWhiteSpace(proofLoadError) &&
                     proofLoadError.IndexOf(
                         L.T("не найден"),
                         StringComparison.OrdinalIgnoreCase) < 0)
            {
                CrashLog.Write(
                    "Proof Mode checkpoint was not restored: " + proofLoadError,
                    null);
            }
            if (boostCenterOverlay != null)
            {
                boostCenterOverlay.SetSettings(centerSettings);
                boostCenterOverlay.SetSessionReport(lastSession);
                boostCenterOverlay.SetSessionHistory(sessionHistory);
                UpdateBoostCenterSessionData();
            }
            QueuePreflight(false, false);
            if (demoMode && HasLaunchArgument(
                    launchArguments,
                    "--demo-target"))
            {
                SelectCurrentProcessAsDemoTarget();
            }
            else
            {
                TrySelectSavedAutoBoostTarget();
            }
            StartAutoBoostDiscovery();

            if (HasLaunchArgument(launchArguments, "--demo-center") &&
                boostCenterOverlay != null)
            {
                RememberBoostCenterFocusReturn(centerButton);
                boostCenterOverlay.OpenReadiness(false);
            }
            else if (HasLaunchArgument(launchArguments, "--demo-report") &&
                     boostCenterOverlay != null)
            {
                RememberBoostCenterFocusReturn(centerButton);
                boostCenterOverlay.OpenReport();
            }
            else if (HasLaunchArgument(launchArguments, "--demo-history") &&
                     boostCenterOverlay != null)
            {
                RememberBoostCenterFocusReturn(centerButton);
                boostCenterOverlay.OpenHistory();
            }
        }

        private void SelectCurrentProcessAsDemoTarget()
        {
            Process process = null;
            try
            {
                process = Process.GetCurrentProcess();
                string path = GameExecutablePath.GetProcessImagePath(process);
                string displayPath;
                string comparisonPath;
                string error;
                if (!GameExecutablePath.TryNormalize(
                        path,
                        out displayPath,
                        out comparisonPath,
                        out error))
                {
                    throw new InvalidOperationException(error);
                }
                SelectGameTarget(
                    new GameTargetIdentity(
                        process.Id,
                        process.StartTime.ToUniversalTime(),
                        displayPath,
                        comparisonPath,
                        process.ProcessName),
                    L.T("Демо-игра"),
                    false);
            }
            catch (Exception ex)
            {
                CrashLog.Write("Could not create the demo target.", ex);
            }
            finally
            {
                if (process != null)
                {
                    process.Dispose();
                }
            }
        }

        private async Task VerifyLocalStartupForUpdateAsync()
        {
            if (boostButton == null ||
                boostCenterOverlay == null ||
                optimizationOverlay == null ||
                updateOverlay == null)
            {
                throw new InvalidOperationException(
                    "The main window did not initialize its local UI services.");
            }

            // The installer probe deliberately avoids WMI, performance counters,
            // session-history parsing and visibility checks. Those dependencies can
            // be slow or unavailable in RDP/session-0 and used to roll back healthy
            // updates. Normal startup performs the full diagnostics afterwards.
            if (!optimizationOverlay.IsInitializedForUpdateHealth())
            {
                throw new InvalidOperationException(
                    "The optimization service did not initialize.");
            }
            boostCenterOverlay.SetSettings(centerSettings);
            boostButton.IsEnabled = true;
            UpdateLayout();

            await Dispatcher.InvokeAsync(
                delegate { },
                DispatcherPriority.ApplicationIdle);
            if (Dispatcher.HasShutdownStarted ||
                Dispatcher.HasShutdownFinished ||
                !boostButton.IsEnabled)
            {
                throw new InvalidOperationException(
                    "The application did not reach an objectively usable local state.");
            }
        }

        private void BoostWindowClosing(object sender, CancelEventArgs e)
        {
            if (updateOverlay != null && updateOverlay.ShouldCancelWindowClose())
            {
                e.Cancel = true;
                return;
            }
            if (optimizationOverlay != null && optimizationOverlay.ShouldCancelWindowClose())
            {
                e.Cancel = true;
            }
            if (e.Cancel)
            {
                languageRestartPending = false;
            }
        }

        /// <summary>
        /// Starts the next instance for a language change once this window has
        /// really closed. It waits on the application mutex for this one to exit.
        /// </summary>
        private static void StartRestartedInstance()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Assembly.GetExecutingAssembly().Location,
                    Arguments = Program.RestartArgument,
                    UseShellExecute = false
                });
            }
            catch (Exception ex)
            {
                CrashLog.Write("Could not restart Boostix to change the language.", ex);
            }
        }

        private void WindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }
            var element = e.OriginalSource as DependencyObject;
            while (element != null)
            {
                if (element is ButtonBase)
                {
                    return;
                }
                element = VisualTreeHelper.GetParent(element);
            }
            try { DragMove(); }
            catch (InvalidOperationException) { }
            finally { QueueMonitorWorkAreaBounds(); }
        }

        private void WindowClosed(object sender, EventArgs e)
        {
            if (themeNotificationsSubscribed)
            {
                SystemParameters.StaticPropertyChanged -=
                    SystemParametersStaticPropertyChanged;
                themeNotificationsSubscribed = false;
            }
            LocationChanged -= BoostWindowLocationChanged;
            if (windowSource != null)
            {
                windowSource.RemoveHook(WindowMessageHook);
                windowSource = null;
            }
            if (backdropSource != null)
            {
                backdropSource.RemoveHook(BackdropMessageHook);
                backdropSource = null;
            }
            if (boostToast != null)
            {
                boostToast.Close();
                boostToast = null;
            }
            if (hotkeyRegistered)
            {
                BoostHotkey.Unregister(windowHandle);
                hotkeyRegistered = false;
            }
            if (hotkeySource != null)
            {
                hotkeySource.RemoveHook(HotkeyMessageHook);
                hotkeySource = null;
            }
            if (trayIcon != null)
            {
                trayIcon.Dispose();
                trayIcon = null;
            }
            windowHandle = IntPtr.Zero;
            bool sessionWasRunning =
                boostActive ||
                animationRunning ||
                boostProcess != null;
            boostActive = false;
            Interlocked.Increment(ref preflightGeneration);
            if (benchmarkCancellation != null)
            {
                benchmarkCancellation.Cancel();
            }
            if (impactScanCancellation != null)
            {
                impactScanCancellation.Cancel();
            }
            if (autoBoostDiscoveryTimer != null)
            {
                autoBoostDiscoveryTimer.Stop();
            }
            if (readinessTimer != null)
            {
                readinessTimer.Stop();
            }
            StopActiveBoostMaintenance();
            ImportBoostScriptResult(currentSession);
            CompleteCurrentSession(
                sessionWasRunning ? "Interrupted" : "Completed",
                sessionWasRunning
                    ? L.T("Приложение закрыто до штатного завершения Boost.")
                    : L.T("Приложение закрыто."));
            GiveSessionPowerPlanStopShutdownGrace();
            TryDeleteReadinessSignal();
            StopBoostProcess();
            if (languageRestartPending)
            {
                languageRestartPending = false;
                StartRestartedInstance();
            }
        }

        private void TryDeleteReadinessSignal()
        {
            try
            {
                if (!string.IsNullOrEmpty(readinessSignalPath) && File.Exists(readinessSignalPath))
                {
                    File.Delete(readinessSignalPath);
                }
            }
            catch { }
        }

        private void StopBoostProcess()
        {
            Process process = boostProcess;
            boostProcess = null;
            if (process == null)
            {
                return;
            }
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(1000);
                }
            }
            catch { }
            finally
            {
                process.Dispose();
            }
        }

        private static bool HasLaunchArgument(string[] arguments, string expected)
        {
            foreach (string argument in arguments ?? new string[0])
            {
                if (string.Equals(argument, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static double GetDemoUiScale(string[] arguments)
        {
            const string prefix = "--demo-ui-scale=";
            foreach (string argument in arguments ?? new string[0])
            {
                if (argument == null ||
                    !argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                double scale;
                if (double.TryParse(
                        argument.Substring(prefix.Length),
                        NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture,
                        out scale) &&
                    scale >= 1.0 &&
                    scale <= 2.0)
                {
                    return scale;
                }
            }
            return 1.0;
        }

        private static Button MakeWindowButton(string accessibleName, bool isClose)
        {
            var button = new Button();
            button.Width = 38;
            button.Height = TitleControlSize;
            var backgroundBrush = new SolidColorBrush(
                TransparentThemeBackground());
            var glyphBrush = new SolidColorBrush(
                BoostixDesignTokens.ChromeGlyph);
            button.Foreground = glyphBrush;
            button.Background = backgroundBrush;
            button.BorderThickness = new Thickness(0);
            button.Cursor = Cursors.Hand;
            button.ToolTip = accessibleName;
            button.Template = MakeChromeButtonTemplate();
            button.FocusVisualStyle = BoostixUi.FocusVisual(8);
            button.Tag = new ChromeButtonVisuals
            {
                BackgroundBrush = backgroundBrush,
                GlyphBrush = glyphBrush,
                IsClose = isClose
            };
            AutomationProperties.SetName(button, accessibleName);
            button.Content = BoostixIcons.Create(
                isClose ? BoostixIcons.Close : BoostixIcons.Minimize,
                16,
                glyphBrush,
                1.9);

            button.MouseEnter += delegate
            {
                BoostixUi.AnimateColor(
                    backgroundBrush,
                    isClose
                        ? BoostixDesignTokens.Destructive
                        : BoostixDesignTokens.Hover,
                    BoostixDesignTokens.MotionFastMilliseconds,
                    true);
                BoostixUi.AnimateColor(
                    glyphBrush,
                    isClose
                        ? BoostixDesignTokens.AccentForeground
                        : BoostixDesignTokens.Text,
                    BoostixDesignTokens.MotionFastMilliseconds,
                    true);
            };
            button.MouseLeave += delegate
            {
                BoostixUi.AnimateColor(
                    backgroundBrush,
                    TransparentThemeBackground(),
                    BoostixDesignTokens.MotionSlowMilliseconds,
                    true);
                BoostixUi.AnimateColor(
                    glyphBrush,
                    BoostixDesignTokens.ChromeGlyph,
                    BoostixDesignTokens.MotionSlowMilliseconds,
                    true);
            };
            return button;
        }

        private static ControlTemplate MakeTransparentButtonTemplate(double radius)
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(0));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            border.AppendChild(presenter);
            template.VisualTree = border;
            return template;
        }

        private static ControlTemplate MakeCardButtonTemplate(double radius)
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(
                Border.CornerRadiusProperty,
                new CornerRadius(radius));
            border.SetValue(
                Border.BackgroundProperty,
                new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(
                Border.BorderBrushProperty,
                new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(
                Border.BorderThicknessProperty,
                new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(
                Border.PaddingProperty,
                new TemplateBindingExtension(Control.PaddingProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(
                ContentPresenter.HorizontalAlignmentProperty,
                new TemplateBindingExtension(
                    Control.HorizontalContentAlignmentProperty));
            presenter.SetValue(
                ContentPresenter.VerticalAlignmentProperty,
                new TemplateBindingExtension(
                    Control.VerticalContentAlignmentProperty));
            border.AppendChild(presenter);
            template.VisualTree = border;
            return template;
        }

        private static void AnimateBrush(
            SolidColorBrush brush,
            Color target,
            int milliseconds)
        {
            if (brush == null)
            {
                return;
            }
            Color start = brush.Color;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = target;
            if (SystemParameters.HighContrast ||
                !BoostixDesignTokens.MotionEnabled ||
                start == target)
            {
                return;
            }
            brush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(
                    start,
                    target,
                    TimeSpan.FromMilliseconds(milliseconds))
                {
                    EasingFunction = new CubicEase
                    {
                        EasingMode = EasingMode.EaseInOut
                    },
                    FillBehavior = FillBehavior.Stop
                },
                HandoffBehavior.SnapshotAndReplace);
        }

        private static ControlTemplate MakeChromeButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(0));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            presenter.SetValue(TextElement.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            border.AppendChild(presenter);
            template.VisualTree = border;
            return template;
        }

        private static TextBlock MakeText(string text, double size, string color, FontWeight weight)
        {
            var block = new TextBlock();
            block.Text = text;
            block.FontSize = Math.Max(
                BoostixDesignTokens.MetadataTextSize,
                size);
            block.FontWeight = weight;
            block.Foreground = BrushFrom(color);
            TextOptions.SetTextFormattingMode(block, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(block, TextRenderingMode.ClearType);
            TextOptions.SetTextHintingMode(block, TextHintingMode.Fixed);
            return block;
        }

        private static FontFamily LoadAppFontFamily()
        {
            return BoostixDesignTokens.TextFontFamily;
        }

        private static FontFamily LoadAppSemiboldFontFamily()
        {
            return BoostixDesignTokens.DisplayFontFamily;
        }

        private static string GetApplicationVersion()
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            return string.Format(
                "v. {0}.{1}.{2}",
                version.Major,
                version.Minor,
                Math.Max(0, version.Build));
        }

        private static Brush MakeLinearBrush(string from, string to, double angle)
        {
            double radians = angle * Math.PI / 180.0;
            double x = Math.Cos(radians) * 0.5;
            double y = Math.Sin(radians) * 0.5;
            var brush = new LinearGradientBrush();
            brush.StartPoint = new Point(0.5 - x, 0.5 - y);
            brush.EndPoint = new Point(0.5 + x, 0.5 + y);
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(from), 0));
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(to), 1));
            brush.Freeze();
            return brush;
        }

        private static Brush BrushFrom(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// Window/taskbar icon: the Boostix gauge mark on a petrol tile.
        /// </summary>
        private static ImageSource BuildWindowIcon()
        {
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(
                BrushFrom("#FF0F4C5C"),
                new Pen(BrushFrom("#FF21505D"), 1),
                new RectangleGeometry(new Rect(1, 1, 30, 30), 8, 8)));
            var center = new Point(16, 18.5);
            group.Children.Add(new GeometryDrawing(
                null,
                new Pen(BrushFrom("#FF9FC0C6"), 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                IconArc(center, 9.5, -120, 55)));
            group.Children.Add(new GeometryDrawing(
                null,
                new Pen(BrushFrom(ProductBrand.AccentHex), 2.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                IconArc(center, 9.5, 62, 120)));
            double needle = 78 * Math.PI / 180;
            group.Children.Add(new GeometryDrawing(
                null,
                new Pen(BrushFrom("#FFEDF3F4"), 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                new LineGeometry(
                    center,
                    new Point(center.X + 8.7 * Math.Sin(needle), center.Y - 8.7 * Math.Cos(needle)))));
            group.Children.Add(new GeometryDrawing(
                BrushFrom(ProductBrand.AccentHex),
                null,
                new EllipseGeometry(center, 2.4, 2.4)));
            var image = new DrawingImage(group);
            image.Freeze();
            return image;
        }

        private static Geometry IconArc(Point center, double radius, double fromAngle, double toAngle)
        {
            Func<double, Point> at = delegate(double angle)
            {
                double radians = angle * Math.PI / 180;
                return new Point(
                    center.X + radius * Math.Sin(radians),
                    center.Y - radius * Math.Cos(radians));
            };
            var figure = new PathFigure { StartPoint = at(fromAngle), IsClosed = false };
            figure.Segments.Add(new ArcSegment(
                at(toAngle),
                new Size(radius, radius),
                0,
                toAngle - fromAngle > 180,
                SweepDirection.Clockwise,
                true));
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();
            return geometry;
        }

    }

    internal static class CrashLog
    {
        private const long MaximumLogBytes = 512 * 1024;
        private static readonly object Sync = new object();
        private static volatile bool suppressFileLogging;

        public static void Configure(string[] arguments)
        {
            bool healthProbe = false;
            foreach (string argument in arguments ?? new string[0])
            {
                if (string.Equals(
                    argument,
                    UpdateHealthHandshake.ProbeArgument,
                    StringComparison.OrdinalIgnoreCase))
                {
                    healthProbe = true;
                    break;
                }
            }

            // LocalAppData belongs to the interactive user and can contain
            // junctions or reparse points. Never write there with an elevated
            // token, and keep the installer health probe file-system neutral.
            suppressFileLogging = healthProbe || IsCurrentProcessElevated();
        }

        public static void Write(string message, Exception exception)
        {
            if (suppressFileLogging)
            {
                Trace.WriteLine(BuildEntry(message, exception));
                return;
            }

            try
            {
                lock (Sync)
                {
                    string directory = System.IO.Path.Combine(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.LocalApplicationData),
                        ProductBrand.DataDirectoryName);
                    Directory.CreateDirectory(directory);
                    string path = System.IO.Path.Combine(directory, "crash.log");
                    string previousPath = System.IO.Path.Combine(
                        directory,
                        "crash.previous.log");
                    if (File.Exists(path) &&
                        new FileInfo(path).Length >= MaximumLogBytes)
                    {
                        if (File.Exists(previousPath))
                        {
                            File.Delete(previousPath);
                        }
                        File.Move(path, previousPath);
                    }

                    File.AppendAllText(
                        path,
                        BuildEntry(message, exception),
                        new UTF8Encoding(false));
                }
            }
            catch
            {
                // Crash logging must never trigger another application failure.
            }
        }

        private static string BuildEntry(string message, Exception exception)
        {
            var entry = new StringBuilder();
            entry.Append('[');
            entry.Append(DateTime.UtcNow.ToString("o"));
            entry.Append("] ");
            entry.AppendLine(message ?? "Unknown application error.");
            if (exception != null)
            {
                entry.AppendLine(exception.ToString());
            }
            return entry.ToString();
        }

        internal static bool IsCurrentProcessElevated()
        {
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    var principal = new System.Security.Principal.WindowsPrincipal(identity);
                    return principal.IsInRole(
                        System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                // If token inspection is unavailable, prefer disabling a
                // diagnostic file over risking a privileged profile write.
                return true;
            }
        }
    }
}
