using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
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

        [STAThread]
        private static void Main(string[] args)
        {
            if (!HardenNativeDllSearch())
            {
                MessageBox.Show(
                    "Boostix не смог безопасно подготовить запуск. " +
                    "Установите актуальные обновления Windows и повторите попытку.",
                    ProductBrand.ProductName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            CrashLog.Configure(args);

            using (var applicationMutex = new Mutex(
                false,
                GetApplicationMutexName(args)))
            {
                bool ownsMutex = false;
                try
                {
                    try
                    {
                        ownsMutex = applicationMutex.WaitOne(0, false);
                    }
                    catch (AbandonedMutexException)
                    {
                        ownsMutex = true;
                    }

                    if (!ownsMutex)
                    {
                        MessageBox.Show(
                            "Boostix уже запущен.",
                            ProductBrand.ProductName,
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        return;
                    }

                    var application = new Application();
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
                            "Boostix столкнулся с ошибкой и безопасно остановлен. " +
                            "Диагностика сохранена в LocalAppData\\Boostix\\crash.log.",
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
        private const double BaseWindowWidth = 460;
        private const double BaseWindowHeight = 552;
        private const double CenterWindowWidth = 620;
        private const double ShellShadowMargin = 0;
        private const double MainContentInset = 24;
        private const double TitleControlSize = 32;
        private const double PreferenceRowHeight = 38;
        private const double ToggleSafeGutter = 12;
        private const double CompactWindowHeight = 492;
        private const double WorkAreaSafetyInset = 8;
        private const string GuidedProofScenarioId =
            "BOOSTIX-GUIDED-SAME-SCENE-V1";
        private const string SessionPowerPlanStartActionTitle =
            "ПЛАН ПИТАНИЯ СЕССИИ";
        private const string SessionPowerPlanStopActionTitle =
            "ВОССТАНОВЛЕНИЕ ПЛАНА ПИТАНИЯ";
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

        private sealed class PreferenceToggleVisuals
        {
            public SolidColorBrush TrackBrush;
            public SolidColorBrush KnobBrush;
            public TranslateTransform KnobTranslation;
        }

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
        private Border boostSurface;
        private FrameworkElement titleSection;
        private FrameworkElement boostButtonSection;
        private FrameworkElement preferenceSection;
        private Grid rocket;
        private Canvas grayRocketLayer;
        private Canvas colorRocketLayer;
        private Canvas starField;
        private Grid flameLayer;
        private TextBlock caption;
        private Button targetSelectorButton;
        private TextBlock targetNameText;
        private TextBlock targetDetailText;
        private TextBlock liveStateText;
        private TextBlock liveMemoryText;
        private TextBlock liveTimerText;
        private CheckBox keepDiscordToggle;
        private CheckBox keepEpicToggle;
        private CheckBox keepSteamToggle;
        private OptimizationFlowOverlay optimizationOverlay;
        private UpdateFlowOverlay updateOverlay;
        private BoostCenterOverlay boostCenterOverlay;
        private ScaleTransform rocketScale;
        private TranslateTransform flightTranslation;
        private TranslateTransform floatTranslation;
        private readonly List<FrameworkElement> stars = new List<FrameworkElement>();
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
        private readonly object activeMaintenanceSync = new object();
        private bool animationRunning;
        private bool departureFinished;
        private bool boostReady;
        private bool boostActive;
        private bool preferencesLoaded;
        private bool preflightAccepted;
        private bool preflightForBoost;
        private bool autoBoostStartPending;
        private string deactivationSessionStatus = "Completed";
        private string deactivationSessionReason = "Boost остановлен пользователем.";
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
        private bool compactMainLayout;
        private bool centerWindowMode;
        private readonly bool scaleMainLayoutToWorkArea;
        private readonly string[] launchArguments;
        private Viewbox monitorAdaptiveViewbox;
        private Grid monitorDesignSurface;
        private RowDefinition mainTitleRow;
        private RowDefinition mainBoostRow;
        private RowDefinition mainCaptionRow;
        private RowDefinition mainFooterRow;
        private HwndSource windowSource;
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
            safeMode = HasLaunchArgument(launchArguments, "--safe-mode");
            updateHealthProbe = HasLaunchArgument(
                launchArguments,
                UpdateHealthHandshake.ProbeArgument);
            demoUiScale = demoMode
                ? GetDemoUiScale(launchArguments)
                : 1.0;
            double targetWidth = BaseWindowWidth * demoUiScale;
            double targetHeight = BaseWindowHeight * demoUiScale;
            bool compactDemo = HasLaunchArgument(
                launchArguments,
                "--demo-compact");
            bool ultraCompactDemo = HasLaunchArgument(
                launchArguments,
                "--demo-ultra-compact");
            if (ultraCompactDemo)
            {
                targetHeight = 360;
            }
            else if (compactDemo)
            {
                targetHeight = CompactWindowHeight;
            }
            compactMainLayout =
                targetHeight < BaseWindowHeight - 0.5;
            scaleMainLayoutToWorkArea =
                targetHeight < CompactWindowHeight - 0.5 ||
                targetWidth < BaseWindowWidth - 0.5;
            Title = ProductBrand.ProductName;
            Width = targetWidth;
            Height = targetHeight;
            MinWidth = Width;
            MinHeight = Height;
            MaxWidth = Width;
            MaxHeight = Height;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // An opaque native window keeps WPF on the ClearType/hardware-rendered
            // path. Layered transparent windows rasterize text less sharply,
            // especially at 1080p and fractional DPI.
            AllowsTransparency = false;
            SetResourceReference(
                BackgroundProperty,
                BoostixDesignTokens.BackgroundBrushKey);
            WindowChrome.SetWindowChrome(
                this,
                new WindowChrome
                {
                    CaptionHeight = 0,
                    CornerRadius = new CornerRadius(11),
                    GlassFrameThickness = new Thickness(0),
                    ResizeBorderThickness = new Thickness(0),
                    UseAeroCaptionButtons = false
                });
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
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
            if (!demoMode)
            {
                Grid designSurface = BuildAdaptiveDesignSurface(shell);
                monitorAdaptiveViewbox = new Viewbox
                {
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Child = designSurface
                };
                Content = monitorAdaptiveViewbox;
            }
            else if (scaleMainLayoutToWorkArea)
            {
                shell.Width = BaseWindowWidth;
                shell.Height = CompactWindowHeight;
                Content = new Viewbox
                {
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = shell
                };
            }
            else
            {
                Content = shell;
            }
            Loaded += BoostWindowLoaded;
            SourceInitialized += BoostWindowSourceInitialized;
            KeyDown += WindowKeyDown;
            PreviewMouseLeftButtonDown += WindowMouseLeftButtonDown;
            Closing += BoostWindowClosing;
            Closed += WindowClosed;
        }

        private Grid BuildAdaptiveDesignSurface(
            FrameworkElement shell)
        {
            if (shell == null)
            {
                throw new ArgumentNullException("shell");
            }

            monitorDesignSurface = new Grid
            {
                Width = BaseWindowWidth,
                Height = compactMainLayout
                    ? CompactWindowHeight
                    : BaseWindowHeight,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true
            };
            monitorDesignSurface.Children.Add(shell);
            return monitorDesignSurface;
        }

        private Grid BuildShell()
        {
            var shell = new Grid
            {
                Margin = new Thickness(ShellShadowMargin * demoUiScale)
            };
            if (Math.Abs(demoUiScale - 1.0) > 0.001)
            {
                shell.LayoutTransform = new ScaleTransform(
                    demoUiScale,
                    demoUiScale);
            }

            var frame = new Border();
            frame.CornerRadius = new CornerRadius(11);
            frame.BorderThickness = new Thickness(1);
            frame.SetResourceReference(
                Border.BorderBrushProperty,
                BoostixDesignTokens.BorderBrushKey);
            frame.SetResourceReference(
                Border.BackgroundProperty,
                BoostixDesignTokens.BackgroundBrushKey);
            shell.Children.Add(frame);

            var root = new Grid();
            root.Background = Brushes.Transparent;
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            mainTitleRow = new RowDefinition
            {
                Height = new GridLength(compactMainLayout ? 62 : 70)
            };
            root.RowDefinitions.Add(mainTitleRow);
            mainBoostRow = new RowDefinition
            {
                Height = new GridLength(compactMainLayout ? 184 : 190)
            };
            root.RowDefinitions.Add(mainBoostRow);
            mainCaptionRow = new RowDefinition
            {
                Height = new GridLength(compactMainLayout ? 32 : 36)
            };
            root.RowDefinitions.Add(mainCaptionRow);
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(114) });
            mainFooterRow = new RowDefinition
            {
                Height = new GridLength(1, GridUnitType.Star),
                MinHeight = compactMainLayout ? 12 : MainContentInset
            };
            root.RowDefinitions.Add(mainFooterRow);
            KeyboardNavigation.SetTabNavigation(
                root,
                KeyboardNavigationMode.Cycle);
            root.SizeChanged += delegate
            {
                root.Clip = new RectangleGeometry(
                    new Rect(0, 0, Math.Max(0, root.ActualWidth), Math.Max(0, root.ActualHeight)),
                    10,
                    10);
            };
            frame.Child = root;

            var controls = BuildWindowControls();
            Grid.SetRow(controls, 0);
            root.Children.Add(controls);

            titleSection = BuildTitle();
            titleSection.Margin = new Thickness(MainContentInset, 0, MainContentInset, 0);
            Grid.SetRow(titleSection, 1);
            root.Children.Add(titleSection);

            boostButtonSection = BuildBoostButton();
            boostButtonSection.Margin = new Thickness(MainContentInset, 0, MainContentInset, 0);
            Grid.SetRow(boostButtonSection, 2);
            root.Children.Add(boostButtonSection);
            boostButton.IsEnabled = false;

            caption = MakeText(
                "НАЖМИ, ЧТОБЫ АКТИВИРОВАТЬ",
                BoostixDesignTokens.MetadataTextSize,
                "#FF8E8E8E",
                FontWeights.Bold);
            caption.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.MutedTextBrushKey);
            caption.FontFamily = LoadAppSemiboldFontFamily();
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            caption.VerticalAlignment = VerticalAlignment.Center;
            caption.Margin = new Thickness(MainContentInset, 0, MainContentInset, 0);
            AutomationProperties.SetName(caption, "Состояние Boost");
            AutomationProperties.SetAutomationId(caption, "Boostix.Status");
            AutomationProperties.SetLiveSetting(caption, AutomationLiveSetting.Polite);
            Grid.SetRow(caption, 3);
            root.Children.Add(caption);

            preferenceSection = BuildSessionSummaryPanel();
            Grid.SetRow(preferenceSection, 4);
            root.Children.Add(preferenceSection);

            boostCenterOverlay = new BoostCenterOverlay(
                LoadAppFontFamily(),
                LoadAppSemiboldFontFamily());
            boostCenterOverlay.RefreshRequested += delegate { QueuePreflight(preflightForBoost, true); };
            boostCenterOverlay.CloseRequested += delegate
            {
                if (preflightForBoost && !boostActive && !animationRunning)
                {
                    Interlocked.Increment(ref preflightGeneration);
                }
                preflightForBoost = false;
                RestoreBoostCenterFocus();
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
                SaveBoostPreferences();
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
                    "Proof Mode сброшен пользователем.");
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
                    "PROOF MODE СБРОШЕН",
                    transition.Message,
                    false);
            };
            boostCenterOverlay.ImpactScanRequested +=
                BoostCenterImpactScanRequested;
            boostCenterOverlay.ImpactCloseRequested +=
                BoostCenterImpactCloseRequested;
            boostCenterOverlay.TargetSelectionRequested += delegate
            {
                boostCenterOverlay.HandleEscape();
                Dispatcher.BeginInvoke(
                    new Action(OpenTargetSelector),
                    DispatcherPriority.Input);
            };
            boostCenterOverlay.ProfileAutoBoostChanged +=
                BoostCenterProfileAutoBoostChanged;
            boostCenterOverlay.ProfileRemoveRequested +=
                BoostCenterProfileRemoveRequested;
            boostCenterOverlay.ExportDiagnosticsRequested +=
                BoostCenterExportDiagnosticsRequested;
            boostCenterOverlay.IsVisibleChanged += delegate
            {
                bool centerVisible =
                    boostCenterOverlay.Visibility == Visibility.Visible;
                SetMainContentVisible(!centerVisible);
                SetCenterWindowMode(centerVisible);
            };
            Grid.SetRow(boostCenterOverlay, 1);
            Grid.SetRowSpan(boostCenterOverlay, 5);
            Panel.SetZIndex(boostCenterOverlay, 50);
            root.Children.Add(boostCenterOverlay);

            optimizationOverlay = new OptimizationFlowOverlay(
                this,
                launchArguments,
                LoadAppFontFamily(),
                LoadAppSemiboldFontFamily());
            optimizationOverlay.RequestApplicationClose += delegate { Close(); };
            Grid.SetRow(optimizationOverlay, 0);
            Grid.SetRowSpan(optimizationOverlay, 6);
            Panel.SetZIndex(optimizationOverlay, 100);
            root.Children.Add(optimizationOverlay);

            updateOverlay = new UpdateFlowOverlay(
                this,
                launchArguments,
                LoadAppFontFamily(),
                LoadAppSemiboldFontFamily());
            updateOverlay.RequestApplicationClose += delegate { Close(); };
            Grid.SetRow(updateOverlay, 0);
            Grid.SetRowSpan(updateOverlay, 6);
            Panel.SetZIndex(updateOverlay, 200);
            root.Children.Add(updateOverlay);

            var watermark = MakeText(
                "by Silas Suspect",
                BoostixDesignTokens.MetadataTextSize,
                "#FF8A8A8A",
                FontWeights.SemiBold);
            watermark.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.MutedTextBrushKey);
            watermark.FontFamily = LoadAppSemiboldFontFamily();
            watermark.HorizontalAlignment = HorizontalAlignment.Right;
            watermark.VerticalAlignment = VerticalAlignment.Bottom;
            watermark.Margin = new Thickness(
                MainContentInset,
                0,
                MainContentInset,
                MainContentInset);
            watermark.Opacity = 0.72;
            watermark.IsHitTestVisible = false;
            AutomationProperties.SetName(watermark, "by Silas Suspect");
            AutomationProperties.SetAutomationId(
                watermark,
                "Boostix.Watermark");
            Grid.SetRow(watermark, 0);
            Grid.SetRowSpan(watermark, 6);
            Panel.SetZIndex(watermark, 400);
            root.Children.Add(watermark);

            return shell;
        }

        private Grid BuildSessionSummaryPanel()
        {
            var panel = new Grid
            {
                MaxWidth = 348,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(
                    MainContentInset,
                    0,
                    MainContentInset,
                    0)
            };
            panel.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(52)
            });
            panel.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(62)
            });

            var targetBackground = new SolidColorBrush(
                BoostixDesignTokens.Surface);
            var targetBorder = new SolidColorBrush(
                BoostixDesignTokens.Border);
            targetSelectorButton = new Button
            {
                Height = 44,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(14, 5, 12, 5),
                Background = targetBackground,
                BorderBrush = targetBorder,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Template = MakeCardButtonTemplate(8)
            };
            KeyboardNavigation.SetTabIndex(targetSelectorButton, 11);
            AutomationProperties.SetName(
                targetSelectorButton,
                "Выбрать игру для Boostix");
            AutomationProperties.SetAutomationId(
                targetSelectorButton,
                "Boostix.Target.Select");
            AutomationProperties.SetHelpText(
                targetSelectorButton,
                "Выбор привязывается к точному процессу, времени запуска и пути EXE.");

            var targetContent = new Grid();
            targetContent.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            targetContent.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });
            targetContent.RowDefinitions.Add(new RowDefinition
            {
                Height = GridLength.Auto
            });
            targetContent.RowDefinitions.Add(new RowDefinition
            {
                Height = GridLength.Auto
            });

            targetNameText = MakeText(
                "ВЫБРАТЬ ИГРУ",
                BoostixDesignTokens.BodyTextSize,
                "#FFF4F4F4",
                FontWeights.Bold);
            targetNameText.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.TextBrushKey);
            targetNameText.FontFamily = LoadAppSemiboldFontFamily();
            Grid.SetRow(targetNameText, 0);
            targetContent.Children.Add(targetNameText);

            targetDetailText = MakeText(
                "Точный процесс ещё не выбран",
                BoostixDesignTokens.MetadataTextSize,
                "#FF8E8E8E",
                FontWeights.Normal);
            targetDetailText.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.MutedTextBrushKey);
            targetDetailText.TextTrimming = TextTrimming.CharacterEllipsis;
            targetDetailText.Margin = new Thickness(0, 1, 8, 0);
            Grid.SetRow(targetDetailText, 1);
            targetContent.Children.Add(targetDetailText);

            var chevron = MakeText(
                "›",
                20,
                ProductBrand.AccentTextHex,
                FontWeights.Bold);
            chevron.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.AccentTextBrushKey);
            chevron.VerticalAlignment = VerticalAlignment.Center;
            chevron.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(chevron, 1);
            Grid.SetRowSpan(chevron, 2);
            targetContent.Children.Add(chevron);
            targetSelectorButton.Content = targetContent;
            targetSelectorButton.Click += delegate { OpenTargetSelector(); };
            targetSelectorButton.MouseEnter += delegate
            {
                AnimateBrush(
                    targetBorder,
                    BoostixDesignTokens.Accent,
                    BoostixDesignTokens.MotionStandardMilliseconds);
            };
            targetSelectorButton.MouseLeave += delegate
            {
                AnimateBrush(
                    targetBorder,
                    BoostixDesignTokens.Border,
                    BoostixDesignTokens.MotionSlowMilliseconds);
            };
            Grid.SetRow(targetSelectorButton, 0);
            panel.Children.Add(targetSelectorButton);

            var metrics = new Grid
            {
                Margin = new Thickness(0, 5, 0, 0)
            };
            metrics.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            metrics.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            metrics.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            liveStateText = AddLiveMetric(metrics, "СЕАНС", "ГОТОВ", 0);
            liveMemoryText = AddLiveMetric(metrics, "COMMIT", "—", 1);
            liveTimerText = AddLiveMetric(metrics, "ВРЕМЯ", "00:00", 2);
            Grid.SetRow(metrics, 1);
            panel.Children.Add(metrics);

            if (updateHealthProbe)
            {
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

            return panel;
        }

        private TextBlock AddLiveMetric(
            Grid host,
            string label,
            string value,
            int column)
        {
            var metric = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var labelText = MakeText(
                label,
                BoostixDesignTokens.MetadataTextSize,
                "#FF8E8E8E",
                FontWeights.Bold);
            labelText.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.MutedTextBrushKey);
            labelText.FontFamily = LoadAppSemiboldFontFamily();
            labelText.HorizontalAlignment = HorizontalAlignment.Center;
            metric.Children.Add(labelText);
            var valueText = MakeText(
                value,
                13,
                "#FFBDBDBD",
                FontWeights.Bold);
            valueText.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.SecondaryTextBrushKey);
            valueText.FontFamily = LoadAppSemiboldFontFamily();
            valueText.HorizontalAlignment = HorizontalAlignment.Center;
            valueText.Margin = new Thickness(0, 2, 0, 0);
            AutomationProperties.SetLiveSetting(
                valueText,
                AutomationLiveSetting.Polite);
            metric.Children.Add(valueText);
            Grid.SetColumn(metric, column);
            host.Children.Add(metric);
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
                VerticalOffset = 4,
                Background = BoostixDesignTokens.Brush(
                    BoostixDesignTokens.Surface),
                BorderBrush = BoostixDesignTokens.Brush(
                    BoostixDesignTokens.Border),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4),
                FontFamily = LoadAppFontFamily(),
                FontSize = BoostixDesignTokens.BodyTextSize,
                HasDropShadow = false
            };
            AutomationProperties.SetName(menu, "Запущенные игры и приложения");

            IList<GameTargetCandidate> candidates =
                gameTargetService.EnumerateCandidates();
            if (candidates.Count == 0)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = "Нет подходящих запущенных окон",
                    IsEnabled = false,
                    Foreground = BoostixDesignTokens.Brush(
                        BoostixDesignTokens.MutedText),
                    Padding = new Thickness(10, 8, 10, 8)
                });
            }
            else
            {
                foreach (GameTargetCandidate candidate in candidates)
                {
                    GameTargetCandidate selectedCandidate = candidate;
                    var header = new StackPanel
                    {
                        MinWidth = 300,
                        Margin = new Thickness(3, 2, 3, 2)
                    };
                    var name = MakeText(
                        selectedCandidate.DisplayName.ToUpperInvariant(),
                        BoostixDesignTokens.BodyTextSize,
                        "#FFF4F4F4",
                        FontWeights.Bold);
                    name.SetResourceReference(
                        TextBlock.ForegroundProperty,
                        BoostixDesignTokens.TextBrushKey);
                    name.FontFamily = LoadAppSemiboldFontFamily();
                    header.Children.Add(name);
                    var detail = MakeText(
                        "PID " + selectedCandidate.Identity.ProcessId +
                        "  •  " + System.IO.Path.GetFileName(
                            selectedCandidate.Identity.ExecutablePath),
                        BoostixDesignTokens.MetadataTextSize,
                        "#FF8E8E8E",
                        FontWeights.Normal);
                    detail.SetResourceReference(
                        TextBlock.ForegroundProperty,
                        BoostixDesignTokens.MutedTextBrushKey);
                    detail.Margin = new Thickness(0, 2, 0, 0);
                    header.Children.Add(detail);

                    var item = new MenuItem
                    {
                        Header = header,
                        Background = Brushes.Transparent,
                        Foreground = BoostixDesignTokens.Brush(
                            BoostixDesignTokens.Text),
                        Padding = new Thickness(6),
                        ToolTip = selectedCandidate.Identity.ExecutablePath
                    };
                    AutomationProperties.SetName(
                        item,
                        "Выбрать " + selectedCandidate.DisplayName +
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
                                "Список устарел — откройте выбор игры снова");
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
            targetNameText.Text = safeName.ToUpperInvariant();
            targetDetailText.Text = "PID " + identity.ProcessId +
                "  •  " + System.IO.Path.GetFileName(identity.ExecutablePath);
            targetDetailText.ToolTip = identity.ExecutablePath;
            AutomationProperties.SetName(
                targetSelectorButton,
                "Выбрана игра " + safeName + ", PID " + identity.ProcessId);
            AutomationProperties.SetItemStatus(
                targetSelectorButton,
                "Выбрано");

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
            targetNameText.Text = "ВЫБРАТЬ ИГРУ";
            targetDetailText.Text = string.IsNullOrWhiteSpace(detail)
                ? "Точный процесс ещё не выбран"
                : detail;
            targetDetailText.ToolTip = null;
            AutomationProperties.SetName(
                targetSelectorButton,
                "Выбрать игру для Boostix");
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
                                "Сохранённая игра сейчас не запущена");
                        }
                    }
                    TrySelectSavedAutoBoostTarget();
                };
            }
            autoBoostDiscoveryTimer.Start();
        }

        private Grid BuildWindowControls()
        {
            var header = new Grid
            {
                Margin = new Thickness(0),
                Height = 48
            };
            AutomationProperties.SetAutomationId(
                header,
                "Boostix.TitleBar");

            centerButton = MakeCenterButton();
            var center = centerButton;
            center.HorizontalAlignment = HorizontalAlignment.Left;
            center.VerticalAlignment = VerticalAlignment.Top;
            center.Margin = new Thickness(10, 10, 0, 0);
            KeyboardNavigation.SetTabIndex(center, 0);
            AutomationProperties.SetAutomationId(
                center,
                "Boostix.OpenCenter");
            center.Click += delegate
            {
                if (boostCenterOverlay != null)
                {
                    if (boostCenterOverlay.IsOpen)
                    {
                        boostCenterOverlay.HandleEscape();
                        return;
                    }
                    RememberBoostCenterFocusReturn(center);
                    preflightForBoost = false;
                    boostCenterOverlay.SetSettings(centerSettings);
                    boostCenterOverlay.SetPreflight(latestPreflight);
                    boostCenterOverlay.SetSessionReport(currentSession ?? lastSession);
                    boostCenterOverlay.SetDiagnosticSnapshot(
                        latestDiagnosticSnapshot);
                    boostCenterOverlay.SetSessionHistory(sessionHistory);
                    UpdateBoostCenterSessionData();
                    boostCenterOverlay.OpenReadiness(false);
                }
            };
            header.Children.Add(center);

            var controls = new StackPanel();
            controls.Orientation = Orientation.Horizontal;
            controls.HorizontalAlignment = HorizontalAlignment.Right;
            controls.VerticalAlignment = VerticalAlignment.Top;
            controls.Height = TitleControlSize;
            controls.Margin = new Thickness(0, 10, 10, 0);

            var version = MakeText(
                GetApplicationVersion() + "  " + ProductBrand.ReleaseLabel,
                11.5,
                "#FF8B8B8B",
                FontWeights.Bold);
            version.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.MutedTextBrushKey);
            version.FontFamily = LoadAppSemiboldFontFamily();
            version.RenderTransform = new TranslateTransform(0, 2);
            version.VerticalAlignment = VerticalAlignment.Center;
            version.Margin = new Thickness(0, 0, 10, 0);
            AutomationProperties.SetName(
                version,
                "Версия приложения " + GetApplicationVersion() + " " +
                ProductBrand.ReleaseLabel);
            AutomationProperties.SetAutomationId(
                version,
                "Boostix.Version");
            controls.Children.Add(version);

            minimizeButton = MakeWindowButton("Свернуть", false);
            var minimize = minimizeButton;
            KeyboardNavigation.SetTabIndex(minimize, 90);
            AutomationProperties.SetAutomationId(
                minimize,
                "Boostix.Minimize");
            minimize.Click += delegate { WindowState = WindowState.Minimized; };
            controls.Children.Add(minimize);

            closeButton = MakeWindowButton("Закрыть", true);
            var close = closeButton;
            KeyboardNavigation.SetTabIndex(close, 91);
            AutomationProperties.SetAutomationId(
                close,
                "Boostix.Close");
            close.Click += delegate { Close(); };
            controls.Children.Add(close);

            header.Children.Add(controls);
            return header;
        }

        private StackPanel BuildTitle()
        {
            var title = new StackPanel();
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.VerticalAlignment = VerticalAlignment.Center;

            var firstLine = MakeText("BOOSTIX", 30, "#FFF4F4F4", FontWeights.Bold);
            firstLine.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.TextBrushKey);
            firstLine.HorizontalAlignment = HorizontalAlignment.Center;
            firstLine.Margin = new Thickness(0);
            AutomationProperties.SetAutomationId(
                firstLine,
                "Boostix.Title");
            title.Children.Add(firstLine);
            return title;
        }

        private Grid BuildBoostButton()
        {
            var stage = new Grid();
            stage.ClipToBounds = false;

            boostButton = new Button();
            boostButton.Width = 184;
            boostButton.Height = 184;
            boostButton.HorizontalAlignment = HorizontalAlignment.Center;
            boostButton.VerticalAlignment = VerticalAlignment.Center;
            boostButton.BorderThickness = new Thickness(0);
            boostButton.Background = Brushes.Transparent;
            boostButton.Cursor = Cursors.Hand;
            boostButton.Template = MakeTransparentButtonTemplate();
            AutomationProperties.SetName(boostButton, "Активировать Boostix");
            AutomationProperties.SetHelpText(
                boostButton,
                "Применяет выбранную подготовку производительности без запуска сторонних программ.");
            AutomationProperties.SetAutomationId(
                boostButton,
                "Boostix.Activate");
            KeyboardNavigation.SetTabIndex(boostButton, 10);

            boostSurface = new Border();
            boostSurface.Width = 178;
            boostSurface.Height = 178;
            boostSurface.CornerRadius = new CornerRadius(54);
            boostSurface.SetResourceReference(
                Border.BackgroundProperty,
                BoostixDesignTokens.SurfaceBrushKey);
            boostSurface.SetResourceReference(
                Border.BorderBrushProperty,
                BoostixDesignTokens.AccentBrushKey);
            boostSurface.BorderThickness = new Thickness(1.5);

            var viewport = new Grid();
            viewport.Width = 176;
            viewport.Height = 176;
            viewport.Background = Brushes.Transparent;
            viewport.Clip = new RectangleGeometry(new Rect(0, 0, 176, 176), 52, 52);
            boostSurface.Child = viewport;

            starField = BuildStarField();
            viewport.Children.Add(starField);

            rocket = BuildRocket();
            viewport.Children.Add(rocket);

            boostButton.Content = boostSurface;
            boostButton.Click += BoostButtonClick;
            boostButton.MouseEnter += BoostButtonMouseEnter;
            boostButton.MouseLeave += BoostButtonMouseLeave;
            stage.Children.Add(boostButton);
            return stage;
        }

        private void SetCompactMainLayout(bool compact)
        {
            if (compactMainLayout == compact)
            {
                return;
            }

            compactMainLayout = compact;

            if (mainTitleRow != null)
            {
                mainTitleRow.Height = new GridLength(compact ? 62 : 70);
            }
            if (mainBoostRow != null)
            {
                mainBoostRow.Height = new GridLength(compact ? 184 : 190);
            }
            if (mainCaptionRow != null)
            {
                mainCaptionRow.Height = new GridLength(compact ? 32 : 36);
            }
            if (mainFooterRow != null)
            {
                mainFooterRow.MinHeight = compact ? 12 : MainContentInset;
            }
            if (monitorDesignSurface != null)
            {
                monitorDesignSurface.Width = centerWindowMode
                    ? CenterWindowWidth
                    : BaseWindowWidth;
                monitorDesignSurface.Height = compact
                    ? CompactWindowHeight
                    : BaseWindowHeight;
            }
        }

        private void SetCenterWindowMode(bool enabled)
        {
            if (centerWindowMode == enabled)
            {
                return;
            }
            centerWindowMode = enabled;

            double targetWidth = enabled
                ? CenterWindowWidth
                : BaseWindowWidth;
            double targetHeight = compactMainLayout
                ? CompactWindowHeight
                : BaseWindowHeight;
            if (monitorDesignSurface != null)
            {
                monitorDesignSurface.Width = targetWidth;
                monitorDesignSurface.Height = targetHeight;
            }

            if (demoMode || windowHandle == IntPtr.Zero)
            {
                MinWidth = 0;
                MinHeight = 0;
                MaxWidth = double.PositiveInfinity;
                MaxHeight = double.PositiveInfinity;
                Width = targetWidth * demoUiScale;
                Height = targetHeight * demoUiScale;
                MinWidth = Width;
                MaxWidth = Width;
                MinHeight = Height;
                MaxHeight = Height;
                return;
            }

            Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(delegate { ApplyMonitorWorkAreaBounds(true); }));
        }

        private Grid BuildPreferencePanel()
        {
            preferencesLoaded = false;
            var panel = new Grid
            {
                MaxWidth = 300,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(
                    MainContentInset,
                    0,
                    MainContentInset,
                    0)
            };
            panel.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(PreferenceRowHeight)
            });
            panel.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(PreferenceRowHeight)
            });
            panel.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(PreferenceRowHeight)
            });

            keepDiscordToggle = BuildPreferenceToggle("НЕ ЗАКРЫВАТЬ DISCORD");
            keepEpicToggle = BuildPreferenceToggle("НЕ ЗАКРЫВАТЬ EPIC GAMES");
            keepSteamToggle = BuildPreferenceToggle("НЕ ЗАКРЫВАТЬ STEAM");
            KeyboardNavigation.SetTabIndex(keepDiscordToggle, 20);
            KeyboardNavigation.SetTabIndex(keepEpicToggle, 21);
            KeyboardNavigation.SetTabIndex(keepSteamToggle, 22);
            Grid.SetRow(keepDiscordToggle, 0);
            Grid.SetRow(keepEpicToggle, 1);
            Grid.SetRow(keepSteamToggle, 2);
            panel.Children.Add(keepDiscordToggle);
            panel.Children.Add(keepEpicToggle);
            panel.Children.Add(keepSteamToggle);

            if (updateHealthProbe)
            {
                // The installer starts this probe with an elevated token. Do not
                // traverse user-controlled LocalAppData paths while that token is
                // active; the probe only needs deterministic in-memory defaults.
                centerSettings.CheckBeforeBoost = true;
                centerSettings.KeepOneDrive = true;
                centerSettings.KeepTeams = true;
                centerSettings.KeepWallpaper = true;
                centerSettings.KeepNvidiaOverlay = true;
            }
            else
            {
                LoadBoostPreferences();
            }
            UpdatePreferenceToggle(keepDiscordToggle, false);
            UpdatePreferenceToggle(keepEpicToggle, false);
            UpdatePreferenceToggle(keepSteamToggle, false);
            preferencesLoaded = true;
            return panel;
        }

        private CheckBox BuildPreferenceToggle(string text)
        {
            var toggle = new CheckBox();
            toggle.MinWidth = 252;
            toggle.MaxWidth = 300;
            toggle.Height = PreferenceRowHeight;
            toggle.HorizontalAlignment = HorizontalAlignment.Stretch;
            toggle.Background = Brushes.Transparent;
            toggle.BorderThickness = new Thickness(0);
            toggle.Cursor = Cursors.Hand;
            toggle.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            toggle.VerticalContentAlignment = VerticalAlignment.Center;
            toggle.Template = MakeTransparentCheckBoxTemplate();
            AutomationProperties.SetName(toggle, text.ToLowerInvariant());
            AutomationProperties.SetAutomationId(
                toggle,
                "Boostix.Keep." +
                text.Replace("НЕ ЗАКРЫВАТЬ ", string.Empty)
                    .Replace(" ", string.Empty));
            AutomationProperties.SetHelpText(
                toggle,
                "Если включено, Boostix не будет закрывать эту программу. " +
                "Закрытые по вашему выбору программы автоматически не запускаются заново.");

            var content = new Grid();
            content.Height = PreferenceRowHeight;
            content.HorizontalAlignment = HorizontalAlignment.Stretch;
            content.UseLayoutRounding = true;
            content.SnapsToDevicePixels = true;
            content.ClipToBounds = false;
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(36 + ToggleSafeGutter)
            });

            var label = MakeText(
                text,
                BoostixDesignTokens.MetadataTextSize,
                "#FFBDBDBD",
                FontWeights.SemiBold);
            label.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.SecondaryTextBrushKey);
            label.FontFamily = LoadAppSemiboldFontFamily();
            label.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(label, 0);
            content.Children.Add(label);

            var trackBrush = new SolidColorBrush(BoostixDesignTokens.SurfaceRaised);
            var track = new Border();
            track.Width = 36;
            track.Height = 20;
            track.CornerRadius = new CornerRadius(10);
            track.Background = trackBrush;
            track.HorizontalAlignment = HorizontalAlignment.Right;
            track.VerticalAlignment = VerticalAlignment.Center;
            track.Margin = new Thickness(0, 0, ToggleSafeGutter, 0);
            track.UseLayoutRounding = true;
            track.SnapsToDevicePixels = true;
            track.ClipToBounds = false;
            Grid.SetColumn(track, 1);

            var knobBrush = new SolidColorBrush(
                BoostixDesignTokens.ToggleKnobOff);
            var knob = new Ellipse();
            knob.Width = 16;
            knob.Height = 16;
            knob.Margin = new Thickness(3, 0, 0, 0);
            knob.HorizontalAlignment = HorizontalAlignment.Left;
            knob.VerticalAlignment = VerticalAlignment.Center;
            knob.Fill = knobBrush;
            knob.UseLayoutRounding = true;
            knob.SnapsToDevicePixels = true;
            var knobTranslation = new TranslateTransform();
            knob.RenderTransform = knobTranslation;
            track.Child = knob;
            content.Children.Add(track);

            toggle.Tag = new PreferenceToggleVisuals
            {
                TrackBrush = trackBrush,
                KnobBrush = knobBrush,
                KnobTranslation = knobTranslation
            };
            toggle.Content = content;
            toggle.Checked += PreferenceToggleChanged;
            toggle.Unchecked += PreferenceToggleChanged;
            toggle.MouseEnter += delegate { UpdatePreferenceToggle(toggle, true); };
            toggle.MouseLeave += delegate { UpdatePreferenceToggle(toggle, true); };
            return toggle;
        }

        private void PreferenceToggleChanged(object sender, RoutedEventArgs e)
        {
            var toggle = sender as CheckBox;
            if (toggle != null)
            {
                UpdatePreferenceToggle(toggle, true);
            }
            if (preferencesLoaded)
            {
                SaveBoostPreferences();
                if (boostActive)
                {
                    RefreshActiveBoostMaintenance();
                }
            }
        }

        private static void UpdatePreferenceToggle(CheckBox toggle, bool animate)
        {
            var visuals = toggle == null
                ? null
                : toggle.Tag as PreferenceToggleVisuals;
            if (visuals == null)
            {
                return;
            }

            bool isChecked = toggle.IsChecked == true;
            Color targetColor = isChecked
                ? BoostixDesignTokens.Accent
                : (toggle.IsMouseOver
                    ? BoostixDesignTokens.Hover
                    : BoostixDesignTokens.SurfaceRaised);
            Color knobColor = isChecked || toggle.IsMouseOver
                ? BoostixDesignTokens.ToggleKnobOn
                : BoostixDesignTokens.ToggleKnobOff;
            double targetX = isChecked ? 14 : 0;
            if (!animate ||
                SystemParameters.HighContrast ||
                !SystemParameters.ClientAreaAnimation)
            {
                visuals.TrackBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                visuals.KnobBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                visuals.KnobTranslation.BeginAnimation(TranslateTransform.XProperty, null);
                visuals.TrackBrush.Color = targetColor;
                visuals.KnobBrush.Color = knobColor;
                visuals.KnobTranslation.X = targetX;
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            visuals.TrackBrush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(targetColor, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            visuals.KnobBrush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(knobColor, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            visuals.KnobTranslation.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
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
                        "KeepNvidiaOverlay=" + centerSettings.KeepNvidiaOverlay
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

        private static string GetPreferencesPath()
        {
            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductBrand.DataDirectoryName,
                "boost-preferences.ini");
        }

        private Canvas BuildStarField()
        {
            var canvas = new Canvas();
            canvas.Width = 176;
            canvas.Height = 176;
            canvas.Opacity = 0;
            canvas.IsHitTestVisible = false;

            var random = new Random(2407);
            for (int index = 0; index < 18; index++)
            {
                var star = new Rectangle();
                star.Width = 3 + random.NextDouble() * 8;
                star.Height = index % 4 == 0 ? 1.7 : 1.15;
                star.RadiusX = star.Height / 2;
                star.RadiusY = star.Height / 2;
                star.Fill = index % 3 == 0
                    ? BrushFrom(ProductBrand.AccentVisualHex)
                    : BrushFrom(ProductBrand.AccentTextHex);
                star.Opacity = 0.28 + random.NextDouble() * 0.58;
                star.RenderTransformOrigin = new Point(0.5, 0.5);
                var transforms = new TransformGroup();
                transforms.Children.Add(new RotateTransform(-31));
                transforms.Children.Add(new TranslateTransform());
                star.RenderTransform = transforms;
                Canvas.SetLeft(star, 15 + random.NextDouble() * 155);
                Canvas.SetTop(star, 4 + random.NextDouble() * 154);
                canvas.Children.Add(star);
                stars.Add(star);
            }
            return canvas;
        }

        private Grid BuildRocket()
        {
            var host = new Grid();
            host.Width = 106;
            host.Height = 112;
            host.HorizontalAlignment = HorizontalAlignment.Center;
            host.VerticalAlignment = VerticalAlignment.Center;
            host.RenderTransformOrigin = new Point(0.5, 0.5);

            rocketScale = new ScaleTransform(1, 1);
            flightTranslation = new TranslateTransform(0, 0);
            floatTranslation = new TranslateTransform(0, 0);
            var transforms = new TransformGroup();
            transforms.Children.Add(rocketScale);
            transforms.Children.Add(new RotateTransform(43));
            transforms.Children.Add(flightTranslation);
            transforms.Children.Add(floatTranslation);
            host.RenderTransform = transforms;

            flameLayer = BuildStaticFlame();
            flameLayer.Opacity = 0;
            host.Children.Add(flameLayer);

            grayRocketLayer = BuildRocketLayer(false);
            grayRocketLayer.Opacity = 1;
            host.Children.Add(grayRocketLayer);

            colorRocketLayer = BuildRocketLayer(true);
            colorRocketLayer.Opacity = 0;
            host.Children.Add(colorRocketLayer);
            return host;
        }

        private Grid BuildStaticFlame()
        {
            var grid = new Grid();
            grid.Width = 72;
            grid.Height = 103;
            grid.HorizontalAlignment = HorizontalAlignment.Center;
            grid.VerticalAlignment = VerticalAlignment.Center;
            grid.IsHitTestVisible = false;

            var outer = new System.Windows.Shapes.Path();
            outer.Data = Geometry.Parse("M 24,68 C 18,82 24,96 36,102 C 48,96 54,82 48,68 Z");
            outer.Fill = MakeLinearBrush("#FFFFD166", "#FFFF4D5A", 90);
            outer.Effect = new DropShadowEffect
            {
                BlurRadius = 13,
                ShadowDepth = 0,
                Opacity = 0.88,
                Color = Color.FromRgb(255, 83, 70)
            };
            grid.Children.Add(outer);

            var inner = new System.Windows.Shapes.Path();
            inner.Data = Geometry.Parse("M 30,71 C 27,82 31,91 36,96 C 41,91 45,82 42,71 Z");
            inner.Fill = MakeLinearBrush("#FFFFFFFF", "#FFFFBE45", 90);
            grid.Children.Add(inner);
            return grid;
        }

        private Canvas BuildRocketLayer(bool useColor)
        {
            var canvas = new Canvas();
            canvas.Width = 72;
            canvas.Height = 103;
            canvas.HorizontalAlignment = HorizontalAlignment.Center;
            canvas.VerticalAlignment = VerticalAlignment.Center;
            canvas.IsHitTestVisible = false;

            var leftFin = new System.Windows.Shapes.Path();
            leftFin.Data = Geometry.Parse("M 19,51 L 7,70 L 22,65 Z");
            leftFin.Fill = useColor
                ? MakeLinearBrush("#FFB794F4", "#FF6D28D9", 90)
                : MakeLinearBrush("#FF9AA1AB", "#FF606873", 90);
            canvas.Children.Add(leftFin);

            var rightFin = new System.Windows.Shapes.Path();
            rightFin.Data = Geometry.Parse("M 53,51 L 65,70 L 50,65 Z");
            rightFin.Fill = useColor
                ? MakeLinearBrush(ProductBrand.AccentVisualHex, "#FF5B21B6", 90)
                : MakeLinearBrush("#FF9AA1AB", "#FF59616D", 90);
            canvas.Children.Add(rightFin);

            var body = new System.Windows.Shapes.Path();
            body.Data = Geometry.Parse("M 36,3 C 22,15 18,35 18,57 C 18,67 25,74 36,79 C 47,74 54,67 54,57 C 54,35 50,15 36,3 Z");
            body.Fill = useColor
                ? MakeLinearBrush("#FFFFFFFF", "#FFD9C6CD", 32)
                : MakeLinearBrush("#FFD1D5DB", "#FF737B87", 32);
            body.Stroke = useColor ? BrushFrom("#D9FFFFFF") : BrushFrom("#FFBBC0C8");
            body.StrokeThickness = 1;
            canvas.Children.Add(body);

            var bodyShade = new System.Windows.Shapes.Path();
            bodyShade.Data = Geometry.Parse("M 36,3 C 48,17 51,36 50,58 C 48,66 43,72 36,79 C 47,74 54,67 54,57 C 54,35 50,15 36,3 Z");
            bodyShade.Fill = useColor ? BrushFrom("#503C1722") : BrushFrom("#35545A64");
            canvas.Children.Add(bodyShade);

            var window = new Ellipse();
            window.Width = 18;
            window.Height = 18;
            Canvas.SetLeft(window, 27);
            Canvas.SetTop(window, 27);
            window.Fill = useColor
                ? MakeLinearBrush("#FFD8B4FE", ProductBrand.AccentVisualHex, 45)
                : MakeLinearBrush("#FFB5BBC3", "#FF68717C", 45);
            window.Stroke = useColor ? BrushFrom("#FFFFFFFF") : BrushFrom("#FFD4D7DC");
            window.StrokeThickness = 2;
            if (useColor)
            {
                window.Effect = new DropShadowEffect
                {
                    BlurRadius = 9,
                    ShadowDepth = 0,
                    Opacity = 0.7,
                    Color = Color.FromRgb(
                        ProductBrand.AccentRed,
                        ProductBrand.AccentGreen,
                        ProductBrand.AccentBlue)
                };
            }
            canvas.Children.Add(window);

            var seam = new Line();
            seam.X1 = 23;
            seam.Y1 = 57;
            seam.X2 = 49;
            seam.Y2 = 57;
            seam.Stroke = useColor ? BrushFrom("#668C4A61") : BrushFrom("#66656D78");
            seam.StrokeThickness = 1;
            canvas.Children.Add(seam);
            return canvas;
        }

        private void BoostButtonMouseEnter(object sender, MouseEventArgs e)
        {
            if (animationRunning)
            {
                return;
            }
            AnimateRocketScale(1.08, 155);
            if (!boostActive)
            {
                AnimateRocketColor(true, 180);
            }
        }

        private void BoostButtonMouseLeave(object sender, MouseEventArgs e)
        {
            if (animationRunning)
            {
                return;
            }
            AnimateRocketScale(1, 180);
            if (!boostActive)
            {
                AnimateRocketColor(false, 210);
                flameLayer.Opacity = 0;
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
                    caption.Text = "ДОЖДИТЕСЬ ОКОНЧАНИЯ ЗАМЕРА";
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
            string targetError = "Выбранная игра больше не запущена.";
            if (selectedTarget == null ||
                !gameTargetService.TryResolve(
                    selectedTarget,
                    out targetSnapshot,
                    out targetError))
            {
                if (selectedTarget != null)
                {
                    ClearSelectedTarget("Выбранная игра закрыта или была перезапущена");
                }
                caption.Text = "СНАЧАЛА ВЫБЕРИТЕ ИГРУ";
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
                boostCenterOverlay.SetPreflight(null);
                boostCenterOverlay.OpenReadiness(forBoost);
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
                        "Выберите снова: процесс больше не совпадает с профилем");
                    caption.Text = "ЦЕЛЬ НЕДОСТУПНА";
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
            AnimateRocketColor(true, 110);
            AnimateRocketScale(1.08, 100);
            flameLayer.Opacity = 1;
            caption.Text = "АКТИВИРУЮ BOOST...";
            caption.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.AccentTextBrushKey);

            if (!LaunchBoostScript())
            {
                HandleBoostFailure("BOOST НЕ ЗАПУЩЕН");
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
                    throw new FileNotFoundException("Boost-Session.ps1 не найден рядом с приложением.", scriptPath);
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

                readinessDeadline = DateTime.Now.AddSeconds(20);
                readinessTimer = new DispatcherTimer();
                readinessTimer.Interval = TimeSpan.FromMilliseconds(120);
                readinessTimer.Tick += ReadinessTimerTick;
                readinessTimer.Start();
                return true;
            }
            catch
            {
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
            if (processFailed || DateTime.Now >= readinessDeadline)
            {
                readinessTimer.Stop();
                HandleBoostFailure("BOOST НЕ ЗАПУЩЕН");
            }
        }

        private void PlayDeparture()
        {
            if (!SystemParameters.ClientAreaAnimation)
            {
                departureFinished = true;
                if (boostReady)
                {
                    PlayReturn();
                }
                return;
            }

            var duration = TimeSpan.FromMilliseconds(620);
            var x = MakeEaseAnimation(0, 152, duration, EasingMode.EaseIn);
            var y = MakeEaseAnimation(0, -108, duration, EasingMode.EaseIn);
            var opacity = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(560));
            opacity.BeginTime = TimeSpan.FromMilliseconds(60);
            opacity.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn };
            x.Completed += delegate
            {
                departureFinished = true;
                if (boostReady)
                {
                    PlayReturn();
                }
            };
            flightTranslation.BeginAnimation(TranslateTransform.XProperty, x);
            flightTranslation.BeginAnimation(TranslateTransform.YProperty, y);
            rocket.BeginAnimation(UIElement.OpacityProperty, opacity);
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
                    "ОДНОРАЗОВАЯ ПОДГОТОВКА",
                    "Фоновые приложения обработаны один раз по выбранным переключателям.",
                    BoostActionOutcome.Changed);
                ImportBoostScriptResult(currentSession);
                SaveCurrentSession();
            }
            StartStarfield();
            caption.Text = "BOOST АКТИВЕН";
            caption.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.AccentTextBrushKey);
            if (departureFinished)
            {
                PlayReturn();
            }
        }

        private void PlayReturn()
        {
            flightTranslation.BeginAnimation(TranslateTransform.XProperty, null);
            flightTranslation.BeginAnimation(TranslateTransform.YProperty, null);
            rocket.BeginAnimation(UIElement.OpacityProperty, null);
            if (!SystemParameters.ClientAreaAnimation)
            {
                CompleteBoostActivation();
                return;
            }

            flightTranslation.X = -152;
            flightTranslation.Y = 104;
            rocket.Opacity = 0;

            var duration = TimeSpan.FromMilliseconds(760);
            var x = MakeEaseAnimation(-152, 0, duration, EasingMode.EaseOut);
            var y = MakeEaseAnimation(104, 0, duration, EasingMode.EaseOut);
            var opacity = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(470));
            opacity.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            x.Completed += delegate { CompleteBoostActivation(); };
            flightTranslation.BeginAnimation(TranslateTransform.XProperty, x);
            flightTranslation.BeginAnimation(TranslateTransform.YProperty, y);
            rocket.BeginAnimation(UIElement.OpacityProperty, opacity);
        }

        private void CompleteBoostActivation()
        {
            flightTranslation.BeginAnimation(TranslateTransform.XProperty, null);
            flightTranslation.BeginAnimation(TranslateTransform.YProperty, null);
            rocket.BeginAnimation(UIElement.OpacityProperty, null);
            flightTranslation.X = 0;
            flightTranslation.Y = 0;
            rocket.Opacity = 1;
            SetRocketScaleImmediately(boostButton.IsMouseOver ? 1.08 : 1);
            boostActive = true;
            animationRunning = false;
            boostButton.IsEnabled = true;
            if (targetSelectorButton != null)
            {
                targetSelectorButton.IsEnabled = false;
            }
            SetBoostAutomationState(true);
            StartActiveBoostMaintenance();
            StartRocketFloat();
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
            flightTranslation.BeginAnimation(TranslateTransform.XProperty, null);
            flightTranslation.BeginAnimation(TranslateTransform.YProperty, null);
            rocket.BeginAnimation(UIElement.OpacityProperty, null);
            flightTranslation.X = 0;
            flightTranslation.Y = 0;
            rocket.Opacity = 1;
            flameLayer.Opacity = 0;
            AnimateRocketColor(false, 180);
            AnimateRocketScale(1, 160);
            caption.Text = message;
            caption.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.ErrorBrushKey);
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
            deactivationSessionReason = sessionReason ?? string.Empty;
            animationRunning = true;
            boostButton.IsEnabled = false;
            boostActive = false;
            StopActiveBoostMaintenance();
            StopBoostProcess();
            StopRocketFloat();
            caption.Text = "ОТКЛЮЧАЮ BOOST...";
            caption.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.AccentTextBrushKey);
            flameLayer.Opacity = 1;
            AnimateRocketColor(true, 100);
            AnimateRocketScale(1.08, 100);

            if (!SystemParameters.ClientAreaAnimation)
            {
                PlayDeactivationReturn();
                return;
            }

            var duration = TimeSpan.FromMilliseconds(620);
            var x = MakeEaseAnimation(0, 152, duration, EasingMode.EaseIn);
            var y = MakeEaseAnimation(0, -108, duration, EasingMode.EaseIn);
            var opacity = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(560));
            opacity.BeginTime = TimeSpan.FromMilliseconds(60);
            opacity.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn };
            x.Completed += delegate { PlayDeactivationReturn(); };
            flightTranslation.BeginAnimation(TranslateTransform.XProperty, x);
            flightTranslation.BeginAnimation(TranslateTransform.YProperty, y);
            rocket.BeginAnimation(UIElement.OpacityProperty, opacity);
        }

        private void PlayDeactivationReturn()
        {
            flightTranslation.BeginAnimation(TranslateTransform.XProperty, null);
            flightTranslation.BeginAnimation(TranslateTransform.YProperty, null);
            rocket.BeginAnimation(UIElement.OpacityProperty, null);
            flightTranslation.X = -152;
            flightTranslation.Y = 104;
            rocket.Opacity = 0;
            flameLayer.Opacity = 0;
            SetRocketColorImmediately(false);
            StopStarfield();

            if (!SystemParameters.ClientAreaAnimation)
            {
                CompleteBoostDeactivation();
                return;
            }

            var duration = TimeSpan.FromMilliseconds(760);
            var x = MakeEaseAnimation(-152, 0, duration, EasingMode.EaseOut);
            var y = MakeEaseAnimation(104, 0, duration, EasingMode.EaseOut);
            var opacity = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(470));
            opacity.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            x.Completed += delegate { CompleteBoostDeactivation(); };
            flightTranslation.BeginAnimation(TranslateTransform.XProperty, x);
            flightTranslation.BeginAnimation(TranslateTransform.YProperty, y);
            rocket.BeginAnimation(UIElement.OpacityProperty, opacity);
        }

        private void CompleteBoostDeactivation()
        {
            flightTranslation.BeginAnimation(TranslateTransform.XProperty, null);
            flightTranslation.BeginAnimation(TranslateTransform.YProperty, null);
            rocket.BeginAnimation(UIElement.OpacityProperty, null);
            flightTranslation.X = 0;
            flightTranslation.Y = 0;
            rocket.Opacity = 1;
            boostActive = false;
            boostReady = false;
            departureFinished = false;
            animationRunning = false;
            boostButton.IsEnabled = true;
            if (targetSelectorButton != null)
            {
                targetSelectorButton.IsEnabled = true;
            }
            caption.Text = "НАЖМИ, ЧТОБЫ АКТИВИРОВАТЬ";
            caption.SetResourceReference(
                TextBlock.ForegroundProperty,
                BoostixDesignTokens.MutedTextBrushKey);
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
            deactivationSessionReason = "Boost остановлен пользователем.";
            preflightAccepted = false;

            bool isHovered = boostButton.IsMouseOver;
            SetRocketScaleImmediately(isHovered ? 1.08 : 1);
            AnimateRocketColor(isHovered, 180);
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

        private void StartStarfield()
        {
            if (!SystemParameters.ClientAreaAnimation)
            {
                starField.BeginAnimation(UIElement.OpacityProperty, null);
                starField.Opacity = 0;
                return;
            }
            if (starField.Opacity > 0.5)
            {
                return;
            }
            var appear = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320));
            starField.BeginAnimation(UIElement.OpacityProperty, appear);

            for (int index = 0; index < stars.Count; index++)
            {
                var transforms = (TransformGroup)stars[index].RenderTransform;
                var translation = (TranslateTransform)transforms.Children[1];
                double durationMs = 1050 + (index % 6) * 180;
                var x = new DoubleAnimation(66, -112, TimeSpan.FromMilliseconds(durationMs));
                var y = new DoubleAnimation(-44, 74, TimeSpan.FromMilliseconds(durationMs));
                x.BeginTime = TimeSpan.FromMilliseconds((index % 9) * 115);
                y.BeginTime = x.BeginTime;
                x.RepeatBehavior = RepeatBehavior.Forever;
                y.RepeatBehavior = RepeatBehavior.Forever;
                translation.BeginAnimation(TranslateTransform.XProperty, x);
                translation.BeginAnimation(TranslateTransform.YProperty, y);
            }
        }

        private void StopStarfield()
        {
            if (!SystemParameters.ClientAreaAnimation)
            {
                ResetStarfield();
                return;
            }
            var disappear = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
            disappear.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            disappear.Completed += delegate
            {
                ResetStarfield();
            };
            starField.BeginAnimation(UIElement.OpacityProperty, disappear);
        }

        private void ResetStarfield()
        {
            starField.BeginAnimation(UIElement.OpacityProperty, null);
            starField.Opacity = 0;
            foreach (FrameworkElement star in stars)
            {
                var transforms = (TransformGroup)star.RenderTransform;
                var translation = (TranslateTransform)transforms.Children[1];
                translation.BeginAnimation(TranslateTransform.XProperty, null);
                translation.BeginAnimation(TranslateTransform.YProperty, null);
                translation.X = 0;
                translation.Y = 0;
            }
        }

        private void StartRocketFloat()
        {
            if (!SystemParameters.ClientAreaAnimation)
            {
                StopRocketFloat();
                return;
            }
            var y = new DoubleAnimation(-3.5, 3.5, TimeSpan.FromMilliseconds(1250));
            y.AutoReverse = true;
            y.RepeatBehavior = RepeatBehavior.Forever;
            y.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };
            var x = new DoubleAnimation(-1.6, 1.6, TimeSpan.FromMilliseconds(1650));
            x.AutoReverse = true;
            x.RepeatBehavior = RepeatBehavior.Forever;
            x.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };
            floatTranslation.BeginAnimation(TranslateTransform.YProperty, y);
            floatTranslation.BeginAnimation(TranslateTransform.XProperty, x);
        }

        private void StopRocketFloat()
        {
            floatTranslation.BeginAnimation(TranslateTransform.XProperty, null);
            floatTranslation.BeginAnimation(TranslateTransform.YProperty, null);
            floatTranslation.X = 0;
            floatTranslation.Y = 0;
        }

        private void SetRocketColorImmediately(bool colorized)
        {
            colorRocketLayer.BeginAnimation(UIElement.OpacityProperty, null);
            grayRocketLayer.BeginAnimation(UIElement.OpacityProperty, null);
            colorRocketLayer.Opacity = colorized ? 1 : 0;
            grayRocketLayer.Opacity = colorized ? 0 : 1;
        }

        private void SetRocketScaleImmediately(double scale)
        {
            rocketScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            rocketScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            rocketScale.ScaleX = scale;
            rocketScale.ScaleY = scale;
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
                string actualPath = process.MainModule == null
                    ? string.Empty
                    : process.MainModule.FileName;
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
                        "Выбранная игра завершилась.",
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
                    "SESSION GUARD — ДАВЛЕНИЕ ПАМЯТИ",
                    "Устойчиво мал доступный запас RAM или commit. " +
                    "Boostix ничего не очищает принудительно; проверьте файл " +
                    "подкачки и закройте выбранные фоновые приложения.",
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
            if (liveStateText == null ||
                liveMemoryText == null ||
                liveTimerText == null)
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
                ? "НЕТ ЦЕЛИ"
                : (boostActive
                    ? (pressure ? "ДАВЛЕНИЕ" : "АКТИВЕН")
                    : "ГОТОВ");
            liveStateText.Text = state;
            liveStateText.Foreground = BoostixDesignTokens.Brush(
                pressure
                    ? BoostixDesignTokens.Warning
                    : (boostActive
                        ? BoostixDesignTokens.Success
                        : BoostixDesignTokens.SecondaryText));
            liveMemoryText.Text = sample != null && sample.SystemMetricsAvailable
                ? FormatMemoryCompact(sample.CommitHeadroomBytes)
                : "—";
            TimeSpan duration = currentSession == null
                ? TimeSpan.Zero
                : DateTime.UtcNow - currentSession.StartedUtc;
            liveTimerText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0:00}:{1:00}",
                Math.Min(99, (int)duration.TotalMinutes),
                duration.Seconds);
            AutomationProperties.SetName(
                liveStateText,
                "Состояние сеанса: " + state);
            AutomationProperties.SetName(
                liveMemoryText,
                "Доступный запас commit: " + liveMemoryText.Text);
            AutomationProperties.SetName(
                liveTimerText,
                "Длительность сеанса: " + liveTimerText.Text);
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
                "Измеряем влияние фоновых приложений…",
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
                        ? "За время замера заметная фоновая нагрузка не обнаружена."
                        : "Показаны прямые измерения. Закрытие — только по вашему выбору и без принуждения.",
                    false);
            }
            catch (OperationCanceledException)
            {
                boostCenterOverlay.SetImpactResults(
                    null,
                    "Измерение отменено.",
                    false);
            }
            catch (Exception ex)
            {
                CrashLog.Write("Background impact scan failed.", ex);
                boostCenterOverlay.SetImpactResults(
                    null,
                    "Не удалось завершить измерение. Откройте диагностику и повторите.",
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
                    "Не удалось отправить запрос закрытия.",
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
                ? "Приложение"
                : e.Identity.ProcessName;
            boostCenterOverlay.SetImpactResults(
                lastImpactResults,
                processName + ": " + closeResult.Message,
                false);
            RecordSessionAction(
                processName + " — ФОНОВОЕ ПРИЛОЖЕНИЕ",
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
                        "Профиль уже удалён или изменён.");
                }
                gameProfilesDirty = true;
            }
            catch (Exception ex)
            {
                CrashLog.Write("Could not update Auto Boost profile state.", ex);
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
            string targetError = "Выбранная игра больше не запущена.";
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
                string actualPath = target.MainModule == null
                    ? string.Empty
                    : target.MainModule.FileName;
                if (startTimeUtc != exactTarget.ProcessStartTimeUtc ||
                    !GameExecutablePath.AreEquivalent(
                        actualPath,
                        exactTarget.ExecutablePath))
                {
                    RestoreOwnedTargetPriorities();
                    NotifySelectedTargetEnded(
                        generation,
                        "Windows переиспользовала PID или исполняемый файл изменился.");
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
                        actualName + " — ПРИОРИТЕТ",
                        "Существующий приоритет " + originalPriority + " сохранён.",
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
                        actualName + " — ПРИОРИТЕТ",
                        "Внешнее изменение приоритета сохранено.",
                        BoostActionOutcome.ExternalOverridePreserved);
                    return;
                }
                AppendActiveBoostLog(
                    "Set foreground target " + actualName + " (PID " +
                    processId + ") priority to AboveNormal.");
                RecordSessionAction(
                    actualName + " — ПРИОРИТЕТ",
                    "Приоритет " + originalPriority +
                    " → AboveNormal. Исходное значение сохранено.",
                    BoostActionOutcome.Changed);
                if (currentSession != null)
                {
                    currentSession.TargetName = actualName;
                }
            }
            catch (Exception ex)
            {
                RecordSessionAction(
                    "ЦЕЛЕВОЕ ПРИЛОЖЕНИЕ — ПРИОРИТЕТ",
                    "Не удалось изменить приоритет: " + ex.Message,
                    BoostActionOutcome.Skipped);
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
                            "Выбранная игра завершилась. Boostix проверяет журнал Windows.");
                    }
                    ClearSelectedTarget(
                        "Игра завершилась — выберите её после следующего запуска");
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
                            "Журнал Windows недоступен. Причина завершения не определена."
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
                    "СВИДЕТЕЛЬСТВО СБОЯ WINDOWS",
                    BuildCrashEvidenceDetail(correlation),
                    BoostActionOutcome.Failed);
            }
            else
            {
                report.TargetCrashUtc = null;
                report.Status = "TargetExited";
                report.StopReason =
                    "Игра завершилась; подтверждённое событие сбоя не найдено.";
                report.AddAction(
                    "ЗАВЕРШЕНИЕ ИГРЫ",
                    (correlation == null
                        ? "Причина завершения не определена."
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
                parts.Add("код " + correlation.ExceptionCode);
            }
            if (!string.IsNullOrWhiteSpace(correlation.FaultingModule))
            {
                parts.Add("модуль " + correlation.FaultingModule);
            }
            if (!string.IsNullOrWhiteSpace(correlation.FaultOffset))
            {
                parts.Add("смещение " + correlation.FaultOffset);
            }
            string evidence = parts.Count == 0
                ? "Windows зафиксировала совпадающее событие."
                : "Windows зафиксировала: " + string.Join(", ", parts) + ".";
            return evidence +
                " Это свидетельство журнала, а не автоматический диагноз.";
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
                return "0 МБ";
            }
            return (bytes / 1048576d).ToString(
                "0.0",
                System.Globalization.CultureInfo.CurrentCulture) + " МБ";
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
                            item.ProcessName + " — ПРИОРИТЕТ",
                            "Внешнее изменение " + current + " сохранено; Boost его не перезаписал.",
                            BoostActionOutcome.ExternalOverridePreserved);
                        continue;
                    }
                    process.PriorityClass = item.OriginalPriority;
                    RecordSessionAction(
                        item.ProcessName + " — ПРИОРИТЕТ",
                        "Восстановлен исходный приоритет " + item.OriginalPriority + ".",
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
                CompleteCurrentSession("Interrupted", "Начата новая сессия Boost.");
            }
            currentSession = BoostSessionReport.Start(trigger);
            currentSession.AddAction(
                "ПАМЯТЬ ДО ЗАПУСКА",
                FormatMemory(currentSession.AvailableMemoryStartBytes) +
                " доступно; сюда уже входит освобождаемый кэш Windows.",
                BoostActionOutcome.AlreadyOptimal);
            if (centerSettings.KeepDiscord)
            {
                currentSession.AddAction(
                    "DISCORD",
                    "Сохранён по вашему выбору.",
                    BoostActionOutcome.Preserved);
            }
            if (centerSettings.KeepEpic)
            {
                currentSession.AddAction(
                    "EPIC GAMES",
                    "Сохранён по вашему выбору.",
                    BoostActionOutcome.Preserved);
            }
            if (centerSettings.KeepSteam)
            {
                currentSession.AddAction(
                    "STEAM",
                    "Сохранён по вашему выбору.",
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
                    "ПЛАН ПИТАНИЯ",
                    "Идентификатор сессии не прошёл проверку; план Windows не менялся.",
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
                    ? "Результат включения плана питания недоступен."
                    : "Включение плана питания завершилось ошибкой; восстановление всё равно было запрошено.",
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
                return "Результат операции недоступен; состояние Windows не подтверждено.";
            }
            switch (result.Status)
            {
                case SessionPowerPlanStatus.Activated:
                    return "Профиль Boostix Performance включён только на время этой игровой сессии.";
                case SessionPowerPlanStatus.Restored:
                case SessionPowerPlanStatus.Recovered:
                    return "Исходный план питания Windows восстановлен.";
                case SessionPowerPlanStatus.AlreadyActive:
                    return "План Boostix уже был активен до сессии и не был присвоен приложением.";
                case SessionPowerPlanStatus.AlreadyStopped:
                case SessionPowerPlanStatus.NoRecoveryNeeded:
                    return "Управляемых изменений плана питания нет.";
                case SessionPowerPlanStatus.ExternalOverridePreserved:
                    return "Пользователь или Windows сменили план питания; внешний выбор сохранён.";
                case SessionPowerPlanStatus.SkippedOnBattery:
                    return "Ноутбук работает от батареи; план питания не менялся.";
                case SessionPowerPlanStatus.PowerSourceUnavailable:
                    return "Источник питания не удалось подтвердить; план не менялся.";
                case SessionPowerPlanStatus.TrustedStateMissing:
                    return "Проверенный профиль ещё не подготовлен; план питания не менялся.";
                case SessionPowerPlanStatus.LiveSessionPreserved:
                    return "Обнаружена другая живая сессия Boostix; её план не изменён.";
                default:
                    return "Операция остановлена безопасно: " + result.Status + ".";
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
                            "Закрыт во время одноразовой подготовки.",
                            BoostActionOutcome.Changed);
                        stopped++;
                    }
                    else if (IsIndexedResultKey(key, "Warning"))
                    {
                        report.AddAction(
                            "ПРЕДУПРЕЖДЕНИЕ ПОДГОТОВКИ",
                            value,
                            BoostActionOutcome.Failed);
                    }
                }
                if (stopped == 0)
                {
                    report.AddAction(
                        "ФОНОВЫЕ ПРОЦЕССЫ",
                        "Выбранные процессы уже не были запущены.",
                        BoostActionOutcome.AlreadyOptimal);
                }
            }
            catch (Exception ex)
            {
                report.AddAction(
                    "ОТЧЁТ ПОДГОТОВКИ",
                    "Не удалось прочитать подробности: " + ex.Message,
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
                "ПАМЯТЬ ПОСЛЕ СЕССИИ",
                FormatMemory(currentSession.AvailableMemoryEndBytes) +
                " доступно. Минимум за сессию: " +
                FormatMemory(currentSession.MinimumAvailableMemoryBytes) +
                "; минимальный запас commit: " +
                FormatMemory(currentSession.MinimumCommitHeadroomBytes) + ".",
                BoostActionOutcome.AlreadyOptimal);
            try { BoostSessionReportStore.Save(currentSession); }
            catch { }
            lastSession = currentSession;
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
                return "данные недоступны";
            }
            return (bytes / 1073741824d).ToString("0.0") + " ГБ";
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
                Title = "Сохранить диагностику Boostix",
                Filter = "Текстовый отчёт (*.txt)|*.txt",
                DefaultExt = ".txt",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = ProductBrand.ProductFileName + "-Diagnostic-" +
                    DateTime.Now.ToString(
                        "yyyyMMdd-HHmmss",
                        CultureInfo.InvariantCulture) + ".txt"
            };
            bool? accepted = dialog.ShowDialog(this);
            if (accepted != true)
            {
                return;
            }

            boostCenterOverlay.SetDiagnosticExportMessage(
                "СОБИРАЕМ ДИАГНОСТИКУ",
                "Получаем показатели Windows и удаляем персональные пути и секреты.",
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
                await Task.Run(delegate
                {
                    DiagnosticExportBuilder.WriteSafeReport(
                        destination,
                        snapshot,
                        reports,
                        "ApplicationVersion=" + GetApplicationVersion());
                });

                latestDiagnosticSnapshot = snapshot;
                sessionHistory = reports;
                boostCenterOverlay.SetDiagnosticSnapshot(snapshot);
                boostCenterOverlay.SetSessionHistory(reports);
                boostCenterOverlay.SetDiagnosticExportMessage(
                    "ОТЧЁТ СОХРАНЁН",
                    "Файл " + System.IO.Path.GetFileName(destination) +
                        " готов — персональные пути и секреты удалены.",
                    false);
            }
            catch (Exception ex)
            {
                CrashLog.Write("Diagnostic export failed.", ex);
                boostCenterOverlay.SetDiagnosticExportMessage(
                    "НЕ УДАЛОСЬ СОХРАНИТЬ ОТЧЁТ",
                    "Windows вернул ошибку " + ex.GetType().Name +
                        ". Выберите другую доступную папку и повторите.",
                    true);
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
                            : "PROOF MODE · ШАГ " +
                                issuedStep.StepNumber.ToString(
                                    CultureInfo.CurrentCulture) + "/" +
                                issuedStep.TotalSteps.ToString(
                                    CultureInfo.CurrentCulture),
                        item == null ? "Подготовка измерения." : item.Message,
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
                        "СНАЧАЛА ВЫБЕРИТЕ ИГРУ",
                        "Proof Mode работает только с точным запущенным процессом игры.",
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
                        "НЕ УДАЛОСЬ ЗАФИКСИРОВАТЬ СЦЕНАРИЙ",
                        "Проверьте доступ к исполняемому файлу игры и повторите попытку.",
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
                            "PROOF MODE НЕ ЗАПУЩЕН",
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
                            "НЕ УДАЛОСЬ СОХРАНИТЬ ТЕСТ",
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
                        "Игра, монитор или параметры сценария изменились. Начните новый Proof Mode.");
                    string ignoredSaveError;
                    proofCheckpointStore.TrySave(
                        proofCoordinator,
                        out ignoredSaveError);
                    boostCenterOverlay.SetPerformanceProofSnapshot(
                        proofCoordinator.GetSnapshot());
                    boostCenterOverlay.SetBenchmarkMessage(
                        "СЦЕНАРИЙ ИЗМЕНИЛСЯ",
                        cancelled.Message,
                        true);
                    return;
                }

                issuedStep = snapshot.NextStep;
                if (issuedStep == null)
                {
                    boostCenterOverlay.SetBenchmarkMessage(
                        "PROOF MODE ЗАВЕРШЁН",
                        snapshot.Message,
                        false);
                    return;
                }
                boostCenterOverlay.SetPerformanceProofSnapshot(snapshot);
                if (boostActive != issuedStep.RequiresBoost)
                {
                    boostCenterOverlay.SetBenchmarkMessage(
                        issuedStep.RequiresBoost
                            ? "ВКЛЮЧИТЕ BOOSTIX"
                            : "ОТКЛЮЧИТЕ BOOSTIX",
                        issuedStep.Instruction +
                            " После смены состояния нажмите кнопку этапа ещё раз.",
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
                                "ЭТАП НЕ СОХРАНЁН",
                                saveError + " Повторите текущий этап.",
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
                            "PROOF MODE ЗАВЕРШЁН",
                            FormatPerformanceProofSummary(transition.FinalResult),
                            transition.FinalResult.Verdict ==
                                PerformanceProofVerdict.Negative);
                    }
                    else if (transition.Status == PerformanceProofSubmissionStatus.Accepted)
                    {
                        boostCenterOverlay.SetBenchmarkMessage(
                            "ЭТАП " + issuedStep.StepNumber.ToString(
                                CultureInfo.CurrentCulture) + "/" +
                                issuedStep.TotalSteps.ToString(
                                    CultureInfo.CurrentCulture) + " ПРИНЯТ",
                            transition.NextStep == null
                                ? transition.Message
                                : transition.NextStep.Instruction,
                            false);
                    }
                    else
                    {
                        boostCenterOverlay.SetBenchmarkMessage(
                            transition.Status == PerformanceProofSubmissionStatus.Failed
                                ? "PROOF MODE ОСТАНОВЛЕН"
                                : "ЭТАП НЕ ПРИНЯТ",
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
                        "ЗАМЕР НЕ ВЫПОЛНЕН",
                        result == null ? "PresentMon не вернул результат." : result.Message,
                        true);
                }
            }
            catch (OperationCanceledException)
            {
                boostCenterOverlay.SetBenchmarkMessage(
                    "ЗАМЕР ОТМЕНЁН",
                    "Измерение производительности остановлено.",
                    true);
            }
            catch (Exception ex)
            {
                CrashLog.Write("Proof Mode capture failed.", ex);
                boostCenterOverlay.SetBenchmarkMessage(
                    "ЗАМЕР НЕ ВЫПОЛНЕН",
                    "Непредвиденная ошибка измерения записана в журнал. Повторите текущий этап.",
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
                return "Сравнение не вернуло результат.";
            }
            return string.Format(
                CultureInfo.CurrentCulture,
                "{0} Средний FPS: {1:+0.0;-0.0;0.0}%, 1% low: {2:+0.0;-0.0;0.0} FPS, P95: {3:+0.0;-0.0;0.0} мс, разброс ±{4:0.0}%.",
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
                    "ТЕСТ ПРОИЗВОДИТЕЛЬНОСТИ",
                    performance.Frames + " кадров проанализировано.",
                    BoostActionOutcome.Changed);
                SaveCurrentSession();
                return;
            }

            BoostSessionReport report = lastSession ?? BoostSessionReport.Start("Benchmark");
            report.Performance = performance;
            report.AddAction(
                "ТЕСТ ПРОИЗВОДИТЕЛЬНОСТИ",
                performance.Frames + " кадров проанализировано.",
                BoostActionOutcome.Changed);
            if (!report.EndedUtc.HasValue)
            {
                report.Complete("Completed", "Ручной тест производительности.");
            }
            BoostSessionReportStore.Save(report);
            lastSession = report;
            RefreshSessionHistory();
            if (boostCenterOverlay != null)
            {
                boostCenterOverlay.SetSessionReport(lastSession);
            }
        }

        private void SetBoostAutomationState(bool active)
        {
            AutomationProperties.SetName(
                boostButton,
                active ? "Отключить Boost производительности" : "Активировать Boost производительности");
            AutomationProperties.SetHelpText(
                boostButton,
                active
                    ? "Останавливает активный контроль фоновых процессов. Применённые системные изменения не откатываются."
                    : "Подготавливает Windows к высокой нагрузке и поддерживает приоритет активного полноэкранного приложения.");
        }

        private void SetMainContentVisible(bool visible)
        {
            Visibility state = visible ? Visibility.Visible : Visibility.Hidden;
            if (titleSection != null)
            {
                titleSection.Visibility = state;
            }
            if (boostButtonSection != null)
            {
                boostButtonSection.Visibility = state;
            }
            if (caption != null)
            {
                caption.Visibility = state;
            }
            if (preferenceSection != null)
            {
                preferenceSection.Visibility = state;
            }
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
                        ? BoostixDesignTokens.Accent
                        : BoostixDesignTokens.Border;
                }
                if (targetSelectorButton.ContextMenu != null)
                {
                    targetSelectorButton.ContextMenu.IsOpen = false;
                }
            }

            UpdatePreferenceToggle(keepDiscordToggle, false);
            UpdatePreferenceToggle(keepEpicToggle, false);
            UpdatePreferenceToggle(keepSteamToggle, false);
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

        private void AnimateRocketColor(bool colorized, int milliseconds)
        {
            double colorTarget = colorized ? 1 : 0;
            double grayTarget = colorized ? 0 : 1;
            if (!SystemParameters.ClientAreaAnimation)
            {
                colorRocketLayer.BeginAnimation(UIElement.OpacityProperty, null);
                grayRocketLayer.BeginAnimation(UIElement.OpacityProperty, null);
                colorRocketLayer.Opacity = colorTarget;
                grayRocketLayer.Opacity = grayTarget;
                return;
            }
            var colorAnimation = new DoubleAnimation(colorTarget, TimeSpan.FromMilliseconds(milliseconds));
            var grayAnimation = new DoubleAnimation(grayTarget, TimeSpan.FromMilliseconds(milliseconds));
            colorAnimation.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            grayAnimation.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            colorRocketLayer.BeginAnimation(UIElement.OpacityProperty, colorAnimation);
            grayRocketLayer.BeginAnimation(UIElement.OpacityProperty, grayAnimation);
        }

        private void AnimateRocketScale(double target, int milliseconds)
        {
            if (!SystemParameters.ClientAreaAnimation)
            {
                SetRocketScaleImmediately(target);
                return;
            }
            var x = new DoubleAnimation(target, TimeSpan.FromMilliseconds(milliseconds));
            var y = new DoubleAnimation(target, TimeSpan.FromMilliseconds(milliseconds));
            x.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            y.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            rocketScale.BeginAnimation(ScaleTransform.ScaleXProperty, x);
            rocketScale.BeginAnimation(ScaleTransform.ScaleYProperty, y);
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

            if (boostCenterOverlay != null && boostCenterOverlay.ConsumesApplicationInput)
            {
                boostCenterOverlay.HandleKey(e);
                return;
            }

            if (e.Key == Key.OemComma &&
                (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                preflightForBoost = false;
                boostCenterOverlay.SetSettings(centerSettings);
                boostCenterOverlay.SetPreflight(latestPreflight);
                boostCenterOverlay.SetSessionReport(currentSession ?? lastSession);
                boostCenterOverlay.SetDiagnosticSnapshot(
                    latestDiagnosticSnapshot);
                boostCenterOverlay.SetSessionHistory(sessionHistory);
                UpdateBoostCenterSessionData();
                RememberBoostCenterFocusReturn(centerButton);
                boostCenterOverlay.OpenSettings();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter || e.Key == Key.Space)
            {
                if (Keyboard.FocusedElement is ButtonBase)
                {
                    return;
                }
                ToggleBoost();
                e.Handled = true;
            }
        }

        private void BoostWindowSourceInitialized(object sender, EventArgs e)
        {
            windowHandle = new WindowInteropHelper(this).Handle;
            if (windowHandle == IntPtr.Zero)
            {
                return;
            }

            ApplyNativeWindowAppearance();
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
                centerWindowMode ? CenterWindowWidth : BaseWindowWidth,
                BaseWindowHeight);

            applyingMonitorBounds = true;
            try
            {
                bool useCompactLayout =
                    placement.Length >= 5 && placement[4] != 0;
                SetCompactMainLayout(useCompactLayout);
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
            double normalWidthPixels = layoutWidthDip * dpiX / 96.0;
            double normalHeightPixels = layoutHeightDip * dpiY / 96.0;
            bool useCompactLayout =
                normalWidthPixels > availableWidth + 0.5 ||
                normalHeightPixels > availableHeight + 0.5;
            double effectiveHeightDip = useCompactLayout
                ? CompactWindowHeight
                : layoutHeightDip;
            double baseWidthPixels = layoutWidthDip * dpiX / 96.0;
            double baseHeightPixels = effectiveHeightDip * dpiY / 96.0;
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
                useCompactLayout ? 1 : 0
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
                caption.Text = "БЕЗОПАСНЫЙ РЕЖИМ";
                caption.SetResourceReference(
                    TextBlock.ForegroundProperty,
                    BoostixDesignTokens.AccentTextBrushKey);
                AutomationProperties.SetName(
                    boostButton,
                    "Boostix недоступен в безопасном режиме");
                AutomationProperties.SetHelpText(
                    boostButton,
                    "В безопасном режиме системные изменения и Boost не запускаются.");
            }
            lastSession = BoostSessionReportStore.LoadLast();
            if (lastSession != null && !lastSession.EndedUtc.HasValue)
            {
                lastSession.AddAction(
                    "ПРЕДЫДУЩАЯ СЕССИЯ",
                    "Отчёт был безопасно завершён при следующем запуске приложения.",
                    BoostActionOutcome.Skipped);
                lastSession.Complete(
                    "Interrupted",
                    "Предыдущая сессия завершилась вместе с приложением или Windows.");
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
                         "не найден",
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
                string path = process.MainModule == null
                    ? Assembly.GetExecutingAssembly().Location
                    : process.MainModule.FileName;
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
                    "ДЕМО-ИГРА",
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
                    ? "Приложение закрыто до штатного завершения Boost."
                    : "Приложение закрыто.");
            GiveSessionPowerPlanStopShutdownGrace();
            TryDeleteReadinessSignal();
            StopBoostProcess();
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

        private static Button MakeCenterButton()
        {
            var backgroundBrush = new SolidColorBrush(
                TransparentThemeBackground());
            var glyphBrush = new SolidColorBrush(
                BoostixDesignTokens.ChromeGlyph);
            var button = new Button
            {
                Width = TitleControlSize,
                Height = TitleControlSize,
                Background = backgroundBrush,
                Foreground = glyphBrush,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "Центр Boostix",
                Template = MakeChromeButtonTemplate()
            };
            button.Tag = new ChromeButtonVisuals
            {
                BackgroundBrush = backgroundBrush,
                GlyphBrush = glyphBrush,
                IsClose = false
            };
            AutomationProperties.SetName(button, "Открыть центр Boostix");

            button.Content = new TextBlock
            {
                Text = "\uE713",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 19,
                FontWeight = FontWeights.Normal,
                Foreground = glyphBrush,
                Width = TitleControlSize,
                Height = TitleControlSize,
                LineHeight = TitleControlSize,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };

            var lift = new TranslateTransform();
            button.RenderTransform = lift;
            button.RenderTransformOrigin = new Point(0.5, 0.5);
            button.MouseEnter += delegate
            {
                if (SystemParameters.HighContrast ||
                    !SystemParameters.ClientAreaAnimation)
                {
                    backgroundBrush.BeginAnimation(
                        SolidColorBrush.ColorProperty,
                        null);
                    glyphBrush.BeginAnimation(
                        SolidColorBrush.ColorProperty,
                        null);
                    lift.BeginAnimation(TranslateTransform.YProperty, null);
                    backgroundBrush.Color = BoostixDesignTokens.Hover;
                    glyphBrush.Color = BoostixDesignTokens.AccentForeground;
                    lift.Y = 0;
                    return;
                }
                var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
                backgroundBrush.BeginAnimation(
                    SolidColorBrush.ColorProperty,
                    new ColorAnimation(
                        BoostixDesignTokens.Hover,
                        TimeSpan.FromMilliseconds(210)) { EasingFunction = ease });
                glyphBrush.BeginAnimation(
                    SolidColorBrush.ColorProperty,
                    new ColorAnimation(
                        BoostixDesignTokens.AccentForeground,
                        TimeSpan.FromMilliseconds(210)) { EasingFunction = ease });
                lift.BeginAnimation(
                    TranslateTransform.YProperty,
                    new DoubleAnimation(
                        -1,
                        TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
            };
            button.MouseLeave += delegate
            {
                if (SystemParameters.HighContrast ||
                    !SystemParameters.ClientAreaAnimation)
                {
                    backgroundBrush.BeginAnimation(
                        SolidColorBrush.ColorProperty,
                        null);
                    glyphBrush.BeginAnimation(
                        SolidColorBrush.ColorProperty,
                        null);
                    lift.BeginAnimation(TranslateTransform.YProperty, null);
                    backgroundBrush.Color = TransparentThemeBackground();
                    glyphBrush.Color = BoostixDesignTokens.ChromeGlyph;
                    lift.Y = 0;
                    return;
                }
                var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
                backgroundBrush.BeginAnimation(
                    SolidColorBrush.ColorProperty,
                    new ColorAnimation(
                        TransparentThemeBackground(),
                        TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
                glyphBrush.BeginAnimation(
                    SolidColorBrush.ColorProperty,
                    new ColorAnimation(
                        BoostixDesignTokens.ChromeGlyph,
                        TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
                lift.BeginAnimation(
                    TranslateTransform.YProperty,
                    new DoubleAnimation(
                        0,
                        TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            };
            return button;
        }

        private static Button MakeWindowButton(string accessibleName, bool isClose)
        {
            var button = new Button();
            button.Width = TitleControlSize;
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
            button.Tag = new ChromeButtonVisuals
            {
                BackgroundBrush = backgroundBrush,
                GlyphBrush = glyphBrush,
                IsClose = isClose
            };
            AutomationProperties.SetName(button, accessibleName);

            var glyphCanvas = new Canvas();
            glyphCanvas.Width = TitleControlSize;
            glyphCanvas.Height = TitleControlSize;
            glyphCanvas.Background = Brushes.Transparent;
            glyphCanvas.IsHitTestVisible = false;
            if (isClose)
            {
                var closeGlyph = new System.Windows.Shapes.Path();
                closeGlyph.Data = Geometry.Parse("M 11,11 L 21,21 M 21,11 L 11,21");
                closeGlyph.Stroke = glyphBrush;
                closeGlyph.StrokeThickness = 2;
                closeGlyph.StrokeStartLineCap = PenLineCap.Round;
                closeGlyph.StrokeEndLineCap = PenLineCap.Round;
                glyphCanvas.Children.Add(closeGlyph);
            }
            else
            {
                var minimizeGlyph = new Rectangle();
                minimizeGlyph.Width = 16;
                minimizeGlyph.Height = 2;
                minimizeGlyph.RadiusX = 1;
                minimizeGlyph.RadiusY = 1;
                minimizeGlyph.Fill = glyphBrush;
                Canvas.SetLeft(minimizeGlyph, 8);
                Canvas.SetTop(minimizeGlyph, 19);
                glyphCanvas.Children.Add(minimizeGlyph);
            }

            button.Content = glyphCanvas;

            var lift = new TranslateTransform();
            button.RenderTransform = lift;
            button.RenderTransformOrigin = new Point(0.5, 0.5);

            button.MouseEnter += delegate
            {
                Panel.SetZIndex(button, 2);
                if (SystemParameters.HighContrast ||
                    !SystemParameters.ClientAreaAnimation)
                {
                    backgroundBrush.BeginAnimation(
                        SolidColorBrush.ColorProperty,
                        null);
                    glyphBrush.BeginAnimation(
                        SolidColorBrush.ColorProperty,
                        null);
                    lift.BeginAnimation(TranslateTransform.YProperty, null);
                    backgroundBrush.Color = isClose
                        ? BoostixDesignTokens.Destructive
                        : BoostixDesignTokens.Hover;
                    glyphBrush.Color = BoostixDesignTokens.AccentForeground;
                    lift.Y = 0;
                    return;
                }
                var colorEase = new CubicEase { EasingMode = EasingMode.EaseInOut };
                var liftEase = new SineEase { EasingMode = EasingMode.EaseInOut };
                backgroundBrush.BeginAnimation(
                    SolidColorBrush.ColorProperty,
                    new ColorAnimation(
                        isClose
                            ? BoostixDesignTokens.Destructive
                            : BoostixDesignTokens.Hover,
                        TimeSpan.FromMilliseconds(220)) { EasingFunction = colorEase });
                glyphBrush.BeginAnimation(
                    SolidColorBrush.ColorProperty,
                    new ColorAnimation(
                        BoostixDesignTokens.AccentForeground,
                        TimeSpan.FromMilliseconds(220)) { EasingFunction = colorEase });
                lift.BeginAnimation(
                    TranslateTransform.YProperty,
                    new DoubleAnimation(-1, TimeSpan.FromMilliseconds(320)) { EasingFunction = liftEase });
            };
            button.MouseLeave += delegate
            {
                Panel.SetZIndex(button, 0);
                if (SystemParameters.HighContrast ||
                    !SystemParameters.ClientAreaAnimation)
                {
                    backgroundBrush.BeginAnimation(
                        SolidColorBrush.ColorProperty,
                        null);
                    glyphBrush.BeginAnimation(
                        SolidColorBrush.ColorProperty,
                        null);
                    lift.BeginAnimation(TranslateTransform.YProperty, null);
                    backgroundBrush.Color = TransparentThemeBackground();
                    glyphBrush.Color = BoostixDesignTokens.ChromeGlyph;
                    lift.Y = 0;
                    return;
                }
                var colorEase = new CubicEase { EasingMode = EasingMode.EaseInOut };
                var liftEase = new SineEase { EasingMode = EasingMode.EaseInOut };
                backgroundBrush.BeginAnimation(
                    SolidColorBrush.ColorProperty,
                    new ColorAnimation(
                        TransparentThemeBackground(),
                        TimeSpan.FromMilliseconds(260)) { EasingFunction = colorEase });
                glyphBrush.BeginAnimation(
                    SolidColorBrush.ColorProperty,
                    new ColorAnimation(
                        BoostixDesignTokens.ChromeGlyph,
                        TimeSpan.FromMilliseconds(240)) { EasingFunction = colorEase });
                lift.BeginAnimation(
                    TranslateTransform.YProperty,
                    new DoubleAnimation(0, TimeSpan.FromMilliseconds(360)) { EasingFunction = liftEase });
            };
            return button;
        }

        private static ControlTemplate MakeTransparentButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
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

        private static ControlTemplate MakeTransparentCheckBoxTemplate()
        {
            var template = new ControlTemplate(typeof(CheckBox));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(0));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            border.AppendChild(presenter);
            template.VisualTree = border;
            return template;
        }

        private static ControlTemplate MakeChromeButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
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
            return new FontFamily("Segoe UI Variable Text, Segoe UI");
        }

        private static FontFamily LoadAppSemiboldFontFamily()
        {
            return new FontFamily("Segoe UI Variable Display, Segoe UI Semibold");
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

        private static ImageSource BuildWindowIcon()
        {
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(
                BrushFrom("#FF1B1B1B"),
                new Pen(BrushFrom(ProductBrand.AccentVisualHex), 1.5),
                new RectangleGeometry(new Rect(1, 1, 30, 30), 8, 8)));
            group.Children.Add(new GeometryDrawing(
                MakeLinearBrush("#FFFFFFFF", "#FFD8B4FE", 90),
                null,
                Geometry.Parse("M 16,5 C 10,10 9,18 10,23 L 16,27 L 22,23 C 23,18 22,10 16,5 Z")));
            group.Children.Add(new GeometryDrawing(
                BrushFrom("#FFFF6B57"),
                null,
                Geometry.Parse("M 13,24 C 13,28 15,30 16,31 C 17,30 19,28 19,24 Z")));
            var image = new DrawingImage(group);
            image.Freeze();
            return image;
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

        private static bool IsCurrentProcessElevated()
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
