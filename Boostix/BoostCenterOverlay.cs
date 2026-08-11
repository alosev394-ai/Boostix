using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Boostix.Branding;

namespace Boostix
{
    internal sealed class BoostCenterTabStrip : Grid
    {
        private readonly List<BoostCenterTabButton> tabs =
            new List<BoostCenterTabButton>();

        internal BoostCenterTabButton SelectedTab
        {
            get
            {
                foreach (BoostCenterTabButton tab in tabs)
                {
                    if (tab.IsTabSelected)
                    {
                        return tab;
                    }
                }
                return null;
            }
        }

        internal void Register(BoostCenterTabButton tab)
        {
            if (tab != null && !tabs.Contains(tab))
            {
                tabs.Add(tab);
            }
        }

        internal void SelectRelative(BoostCenterTabButton current, int offset)
        {
            int index = tabs.IndexOf(current);
            if (index < 0 || tabs.Count == 0)
            {
                return;
            }

            int nextIndex = (index + offset) % tabs.Count;
            if (nextIndex < 0)
            {
                nextIndex += tabs.Count;
            }
            BoostCenterTabButton next = tabs[nextIndex];
            next.SelectTabPreservingFocus();
            next.Focus();
            Keyboard.Focus(next);
        }

        internal void NotifySelectionChanged(
            BoostCenterTabButton previous,
            BoostCenterTabButton selected)
        {
            if (object.ReferenceEquals(previous, selected))
            {
                return;
            }

            if (previous != null)
            {
                previous.RaiseSelectionChanged(true, false);
            }
            if (selected != null)
            {
                selected.RaiseSelectionChanged(false, true);
            }

            var peer = (UIElementAutomationPeer.FromElement(this) ??
                UIElementAutomationPeer.CreatePeerForElement(this)) as
                BoostCenterTabStripAutomationPeer;
            if (peer != null)
            {
                peer.RaiseSelectionInvalidated();
            }
        }

        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new BoostCenterTabStripAutomationPeer(this);
        }
    }

    internal sealed class BoostCenterTabStripAutomationPeer :
        FrameworkElementAutomationPeer,
        ISelectionProvider
    {
        private readonly BoostCenterTabStrip owner;

        public BoostCenterTabStripAutomationPeer(BoostCenterTabStrip element)
            : base(element)
        {
            owner = element;
        }

        public bool CanSelectMultiple
        {
            get { return false; }
        }

        public bool IsSelectionRequired
        {
            get { return true; }
        }

        public IRawElementProviderSimple[] GetSelection()
        {
            BoostCenterTabButton selected = owner.SelectedTab;
            if (selected == null)
            {
                return new IRawElementProviderSimple[0];
            }

            AutomationPeer selectedPeer =
                UIElementAutomationPeer.FromElement(selected) ??
                UIElementAutomationPeer.CreatePeerForElement(selected);
            if (selectedPeer == null)
            {
                return new IRawElementProviderSimple[0];
            }
            return new[] { ProviderFromPeer(selectedPeer) };
        }

        protected override AutomationControlType GetAutomationControlTypeCore()
        {
            return AutomationControlType.Tab;
        }

        protected override string GetClassNameCore()
        {
            return "Tab";
        }

        public override object GetPattern(PatternInterface patternInterface)
        {
            if (patternInterface == PatternInterface.Selection)
            {
                return this;
            }
            return base.GetPattern(patternInterface);
        }

        internal void RaiseSelectionInvalidated()
        {
            RaiseAutomationEvent(
                AutomationEvents.SelectionPatternOnInvalidated);
        }
    }

    internal sealed class BoostCenterTabButton : Button
    {
        private readonly BoostCenterTabStrip selectionContainer;
        private readonly Func<bool> isSelected;
        private readonly Action<bool> select;

        public BoostCenterTabButton(
            BoostCenterTabStrip container,
            Func<bool> selected,
            Action<bool> selectAction)
        {
            selectionContainer = container;
            isSelected = selected;
            select = selectAction;
        }

        internal BoostCenterTabStrip SelectionContainer
        {
            get { return selectionContainer; }
        }

        internal bool IsTabSelected
        {
            get { return isSelected != null && isSelected(); }
        }

        internal void SelectTab()
        {
            if (select != null)
            {
                select(false);
            }
        }

        internal void SelectTabPreservingFocus()
        {
            if (select != null)
            {
                select(true);
            }
        }


        internal void RaiseSelectionChanged(
            bool oldValue,
            bool newValue)
        {
            var peer = (UIElementAutomationPeer.FromElement(this) ??
                UIElementAutomationPeer.CreatePeerForElement(this)) as
                BoostCenterTabAutomationPeer;
            if (peer != null)
            {
                peer.RaiseSelectionChanged(oldValue, newValue);
            }
        }

        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new BoostCenterTabAutomationPeer(this);
        }

        protected override void OnClick()
        {
            base.OnClick();
            SelectTabPreservingFocus();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e != null &&
                Keyboard.Modifiers == ModifierKeys.None &&
                (e.Key == Key.Left || e.Key == Key.Up))
            {
                selectionContainer.SelectRelative(this, -1);
                e.Handled = true;
                return;
            }
            if (e != null &&
                Keyboard.Modifiers == ModifierKeys.None &&
                (e.Key == Key.Right || e.Key == Key.Down))
            {
                selectionContainer.SelectRelative(this, 1);
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }
    }

    internal sealed class BoostCenterTabAutomationPeer : ButtonAutomationPeer,
        ISelectionItemProvider
    {
        private readonly BoostCenterTabButton owner;

        public BoostCenterTabAutomationPeer(BoostCenterTabButton element)
            : base(element)
        {
            owner = element;
        }

        public bool IsSelected
        {
            get { return owner.IsTabSelected; }
        }

        public IRawElementProviderSimple SelectionContainer
        {
            get
            {
                AutomationPeer containerPeer =
                    UIElementAutomationPeer.FromElement(
                        owner.SelectionContainer) ??
                    UIElementAutomationPeer.CreatePeerForElement(
                        owner.SelectionContainer);
                return containerPeer == null
                    ? null
                    : ProviderFromPeer(containerPeer);
            }
        }

        public void AddToSelection()
        {
            Select();
        }

        public void RemoveFromSelection()
        {
            if (IsSelected)
            {
                throw new InvalidOperationException(
                    "A selected Boost Center tab cannot be removed from selection.");
            }
        }

        public void Select()
        {
            owner.Dispatcher.BeginInvoke(new Action(owner.SelectTab));
        }

        protected override AutomationControlType GetAutomationControlTypeCore()
        {
            return AutomationControlType.TabItem;
        }

        protected override string GetClassNameCore()
        {
            return "TabItem";
        }

        public override object GetPattern(PatternInterface patternInterface)
        {
            if (patternInterface == PatternInterface.SelectionItem)
            {
                return this;
            }
            return base.GetPattern(patternInterface);
        }


        internal void RaiseSelectionChanged(
            bool oldValue,
            bool newValue)
        {
            if (oldValue == newValue)
            {
                return;
            }

            RaisePropertyChangedEvent(
                SelectionItemPatternIdentifiers.IsSelectedProperty,
                oldValue,
                newValue);
            RaiseAutomationEvent(
                newValue
                    ? AutomationEvents.SelectionItemPatternOnElementSelected
                    : AutomationEvents.SelectionItemPatternOnElementRemovedFromSelection);
        }
    }

    internal sealed class BoostBenchmarkRequestEventArgs : EventArgs
    {
        public BoostBenchmarkRequestEventArgs(bool elevate)
        {
            Elevate = elevate;
        }

        public bool Elevate { get; private set; }
    }

    internal sealed class BoostProfileEventArgs : EventArgs
    {
        public BoostProfileEventArgs(string executablePath, bool enabled)
        {
            ExecutablePath = executablePath ?? string.Empty;
            Enabled = enabled;
        }

        public string ExecutablePath { get; private set; }
        public bool Enabled { get; private set; }
    }

    internal sealed class BackgroundImpactEventArgs : EventArgs
    {
        public BackgroundImpactEventArgs(BackgroundProcessIdentity identity)
        {
            Identity = identity;
        }

        public BackgroundProcessIdentity Identity { get; private set; }
    }

    internal sealed class BoostCenterOverlay : Grid
    {
        internal const int PageTransitionExitMilliseconds = 90;
        internal const int PageTransitionEnterMilliseconds = 130;
        internal const int PageTransitionTotalMilliseconds =
            PageTransitionExitMilliseconds + PageTransitionEnterMilliseconds;
        private const double OuterContentInset = 24;
        private const double ScrollSafeInset = 12;
        private const double ToggleSafeGutter = 12;

        private enum CenterPage
        {
            Readiness,
            Impact,
            Report,
            Profiles,
            Settings
        }

        private sealed class ToggleVisuals
        {
            public SolidColorBrush TrackBrush;
            public SolidColorBrush KnobBrush;
            public TranslateTransform KnobTranslation;
        }

        private sealed class ScrollAnimationProxy : FrameworkElement
        {
            public static readonly DependencyProperty OffsetProperty =
                DependencyProperty.Register(
                    "Offset",
                    typeof(double),
                    typeof(ScrollAnimationProxy),
                    new PropertyMetadata(0.0, OffsetChanged));

            private readonly Action<double> applyOffset;

            public ScrollAnimationProxy(Action<double> apply)
            {
                applyOffset = apply;
            }

            public double Offset
            {
                get { return (double)GetValue(OffsetProperty); }
                set { SetValue(OffsetProperty, value); }
            }

            private static void OffsetChanged(
                DependencyObject sender,
                DependencyPropertyChangedEventArgs args)
            {
                var proxy = sender as ScrollAnimationProxy;
                if (proxy != null && proxy.applyOffset != null)
                {
                    proxy.applyOffset((double)args.NewValue);
                }
            }
        }

        // These are properties, not captured fields: SystemParameters can switch
        // High Contrast while the application is already running.
        private static Color BackgroundColor { get { return BoostixDesignTokens.Background; } }
        private static Color SurfaceColor { get { return BoostixDesignTokens.Surface; } }
        private static Color HoverColor { get { return BoostixDesignTokens.Hover; } }
        private static Color ButtonColor { get { return BoostixDesignTokens.SurfaceRaised; } }
        private static Color BorderColor { get { return BoostixDesignTokens.Border; } }
        private static Color DividerColor { get { return BoostixDesignTokens.Divider; } }
        private static Color TextColor { get { return BoostixDesignTokens.Text; } }
        private static Color SecondaryColor { get { return BoostixDesignTokens.SecondaryText; } }
        private static Color MutedColor { get { return BoostixDesignTokens.MutedText; } }
        private static Color AccentColor { get { return BoostixDesignTokens.Accent; } }
        private static Color AccentTextColor { get { return BoostixDesignTokens.AccentText; } }
        private static Color AccentForegroundColor { get { return BoostixDesignTokens.AccentForeground; } }
        private static Color AccentPressedColor { get { return BoostixDesignTokens.AccentPressed; } }
        private static Color FocusColor { get { return BoostixDesignTokens.Focus; } }
        private static Color ErrorColor { get { return BoostixDesignTokens.Error; } }
        private static Color DestructiveColor { get { return BoostixDesignTokens.Destructive; } }
        private static Color SuccessColor { get { return BoostixDesignTokens.Success; } }
        private static Color WarningColor { get { return BoostixDesignTokens.Warning; } }

        private readonly FontFamily regularFont;
        private readonly FontFamily semiboldFont;
        private readonly Grid contentRoot;
        private readonly StackPanel pageContent;
        private readonly ScrollViewer pageScroller;
        private readonly ScrollAnimationProxy scrollAnimationProxy;
        private readonly StackPanel footerButtons;
        private TextBlock headerTitle;
        private BoostCenterTabStrip tabStrip;
        private TextBlock subtitle;
        private StackPanel reportStack;
        private readonly Dictionary<CenterPage, BoostCenterTabButton> tabButtons =
            new Dictionary<CenterPage, BoostCenterTabButton>();
        private readonly Dictionary<CenterPage, Border> tabIndicators =
            new Dictionary<CenterPage, Border>();
        private readonly Dictionary<CenterPage, ScaleTransform> tabIndicatorScales =
            new Dictionary<CenterPage, ScaleTransform>();
        private readonly TranslateTransform entranceTranslation;
        private readonly TranslateTransform subtitleTranslation =
            new TranslateTransform();
        private readonly TranslateTransform pageTranslation =
            new TranslateTransform();
        private readonly TranslateTransform footerTranslation =
            new TranslateTransform();

        private CenterPage currentPage;
        private CenterPage renderedPage;
        private BoostPreflightReport preflight;
        private BoostSessionReport sessionReport;
        private DiagnosticSnapshot diagnosticSnapshot;
        private List<BoostSessionReport> sessionHistory =
            new List<BoostSessionReport>();
        private List<BackgroundImpactResult> impactResults =
            new List<BackgroundImpactResult>();
        private List<GameProfile> gameProfiles = new List<GameProfile>();
        private GameTargetIdentity selectedTarget;
        private SessionGuardSample sessionGuardSample;
        private SessionGuardPressureState sessionGuardPressureState;
        private PagefileAssessment pagefileAssessment;
        private PerformanceProofCoordinatorSnapshot proofSnapshot;
        private bool boostSessionActive;
        private DateTime? boostSessionStartedUtc;
        private bool impactScanBusy;
        private string impactMessage = string.Empty;
        private BoostCenterSettings settings = new BoostCenterSettings();
        private bool settingsLoading;
        private bool requireBoostDecision;
        private bool benchmarkBusy;
        private bool benchmarkNeedsElevation;
        private int benchmarkPercent;
        private string benchmarkTitle;
        private string benchmarkDetail;
        private string exportMessageTitle;
        private string exportMessageDetail;
        private bool exportMessageError;
        private Button preferredFocusButton;
        private TextBlock benchmarkNoticeTitleBlock;
        private TextBlock benchmarkNoticeDetailBlock;
        private Border benchmarkProgressFill;
        private Button benchmarkButton;
        private double smoothScrollTarget;
        private int smoothScrollGeneration;
        private bool smoothScrollAnimating;
        private int pageTransitionGeneration;
        private bool pageTransitionAnimating;

        public BoostCenterOverlay(
            FontFamily normalFont,
            FontFamily boldFont)
        {
            regularFont = normalFont ?? new FontFamily("Segoe UI");
            semiboldFont = boldFont ?? regularFont;

            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            Background = new SolidColorBrush(BackgroundColor);
            Visibility = Visibility.Collapsed;
            Focusable = true;
            KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.Cycle);
            AutomationProperties.SetName(this, "Центр Boostix");
            AutomationProperties.SetAutomationId(
                this,
                "Boostix.Center");

            entranceTranslation = new TranslateTransform();
            RenderTransform = entranceTranslation;

            contentRoot = new Grid
            {
                Margin = new Thickness(
                    OuterContentInset,
                    4,
                    OuterContentInset,
                    OuterContentInset)
            };
            contentRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(58) });
            contentRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
            contentRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            contentRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(72) });
            Children.Add(contentRoot);

            var header = BuildHeader();
            Grid.SetRow(header, 0);
            contentRoot.Children.Add(header);

            var tabs = BuildTabs();
            Grid.SetRow(tabs, 1);
            contentRoot.Children.Add(tabs);

            pageContent = new StackPanel
            {
                Margin = new Thickness(0, 8, ScrollSafeInset, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true,
                ClipToBounds = false
            };
            pageScroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CanContentScroll = false,
                Content = pageContent,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0),
                RenderTransform = pageTranslation
            };
            AutomationProperties.SetName(
                pageScroller,
                "Содержимое раздела Центра Boostix");
            AutomationProperties.SetAutomationId(
                pageScroller,
                "Boostix.Center.PageScroller");
            pageScroller.Resources[typeof(ScrollBar)] = MakeBoostixVerticalScrollBarStyle();
            scrollAnimationProxy = new ScrollAnimationProxy(
                delegate(double offset) { pageScroller.ScrollToVerticalOffset(offset); });
            pageScroller.PreviewMouseWheel += PageScrollerPreviewMouseWheel;
            pageScroller.PreviewKeyDown += delegate { CancelSmoothMouseWheelScroll(); };
            pageScroller.AddHandler(
                Thumb.DragStartedEvent,
                new DragStartedEventHandler(
                    delegate { CancelSmoothMouseWheelScroll(); }));
            Grid.SetRow(pageScroller, 2);
            contentRoot.Children.Add(pageScroller);

            footerButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                RenderTransform = footerTranslation
            };
            AutomationProperties.SetName(
                footerButtons,
                "Действия Центра Boostix");
            Grid.SetRow(footerButtons, 3);
            contentRoot.Children.Add(footerButtons);

            PreviewKeyDown += OverlayPreviewKeyDown;
        }

        public event EventHandler CloseRequested;
        public event EventHandler RefreshRequested;
        public event EventHandler ProceedBoostRequested;
        public event EventHandler RestoreRequested;
        public event EventHandler OpenPagefileSettingsRequested;
        public event EventHandler ExportDiagnosticsRequested;
        public event EventHandler SettingsChanged;
        public event EventHandler<BoostBenchmarkRequestEventArgs> BenchmarkRequested;
        public event EventHandler ProofCancelRequested;
        public event EventHandler ImpactScanRequested;
        public event EventHandler<BackgroundImpactEventArgs> ImpactCloseRequested;
        public event EventHandler TargetSelectionRequested;
        public event EventHandler<BoostProfileEventArgs> ProfileAutoBoostChanged;
        public event EventHandler<BoostProfileEventArgs> ProfileRemoveRequested;

        public bool IsOpen
        {
            get { return Visibility == Visibility.Visible; }
        }

        public bool ConsumesApplicationInput
        {
            get { return IsOpen; }
        }

        public BoostCenterSettings Settings
        {
            get { return settings.Clone(); }
        }

        /// <summary>
        /// Rebuilds all theme-sensitive page visuals after Windows changes
        /// High Contrast. The selected page is retained and no UIA selection
        /// event is emitted because the logical selection did not change.
        /// </summary>
        internal void RefreshTheme()
        {
            Background = new SolidColorBrush(BackgroundColor);
            if (headerTitle != null)
            {
                headerTitle.Foreground = new SolidColorBrush(TextColor);
            }
            if (subtitle != null)
            {
                subtitle.Foreground = new SolidColorBrush(MutedColor);
            }

            foreach (KeyValuePair<CenterPage, BoostCenterTabButton> pair in
                tabButtons)
            {
                var foreground = pair.Value.Foreground as SolidColorBrush;
                if (foreground == null)
                {
                    foreground = new SolidColorBrush(MutedColor);
                    pair.Value.Foreground = foreground;
                }
                foreground.BeginAnimation(
                    SolidColorBrush.ColorProperty,
                    null);
                pair.Value.FocusVisualStyle =
                    MakeKeyboardFocusVisualStyle(2);

                Border indicator;
                if (tabIndicators.TryGetValue(pair.Key, out indicator))
                {
                    indicator.Background = new SolidColorBrush(AccentColor);
                }
            }

            pageScroller.Resources[typeof(ScrollBar)] =
                MakeBoostixVerticalScrollBarStyle();
            FinishPageTransitionImmediately();
            if (IsOpen)
            {
                RenderCurrentPage();
            }
            UpdateTabs(false);
        }

        public void SetSettings(BoostCenterSettings value)
        {
            settings = value == null ? new BoostCenterSettings() : value.Clone();
            if (IsOpen && currentPage == CenterPage.Settings)
            {
                RenderCurrentPage();
            }
        }

        public void SetPreflight(BoostPreflightReport report)
        {
            preflight = report;
            if (IsOpen && currentPage == CenterPage.Readiness)
            {
                RenderCurrentPage();
            }
        }

        public void SetSessionReport(BoostSessionReport report)
        {
            sessionReport = report;
            if (IsOpen && currentPage == CenterPage.Report)
            {
                RenderCurrentPage();
            }
        }

        public void SetDiagnosticSnapshot(DiagnosticSnapshot snapshot)
        {
            diagnosticSnapshot = snapshot;
            if (IsOpen && currentPage == CenterPage.Readiness)
            {
                RenderCurrentPage();
            }
        }

        public void SetSessionHistory(IEnumerable<BoostSessionReport> reports)
        {
            sessionHistory = (reports ?? Enumerable.Empty<BoostSessionReport>())
                .Where(item => item != null)
                .OrderByDescending(item => item.StartedUtc)
                .Take(DiagnosticSessionHistory.MaximumSessionCount)
                .ToList();
            if (IsOpen &&
                currentPage == CenterPage.Report)
            {
                RenderCurrentPage();
            }
        }

        public void SetLiveSession(
            GameTargetIdentity target,
            SessionGuardSample sample,
            SessionGuardPressureState pressureState,
            PagefileAssessment pagefile,
            bool active,
            DateTime? startedUtc)
        {
            selectedTarget = target;
            sessionGuardSample = sample;
            sessionGuardPressureState = pressureState == null
                ? null
                : pressureState.Clone();
            pagefileAssessment = pagefile;
            boostSessionActive = active;
            boostSessionStartedUtc = startedUtc;
            if (IsOpen && currentPage == CenterPage.Readiness)
            {
                RenderCurrentPage();
            }
        }

        public void SetImpactResults(
            IEnumerable<BackgroundImpactResult> results,
            string message,
            bool busy)
        {
            impactResults = (results ?? Enumerable.Empty<BackgroundImpactResult>())
                .Where(item => item != null && item.Identity != null)
                .Take(24)
                .ToList();
            impactMessage = message ?? string.Empty;
            impactScanBusy = busy;
            if (IsOpen && currentPage == CenterPage.Impact)
            {
                RenderCurrentPage();
            }
        }

        public void SetGameProfiles(IEnumerable<GameProfile> profiles)
        {
            gameProfiles = (profiles ?? Enumerable.Empty<GameProfile>())
                .Where(item => item != null)
                .OrderBy(item => item.DisplayName)
                .Take(256)
                .ToList();
            if (IsOpen && currentPage == CenterPage.Profiles)
            {
                RenderCurrentPage();
            }
        }

        public void SetDiagnosticExportMessage(
            string title,
            string detail,
            bool isError)
        {
            exportMessageTitle = title ?? string.Empty;
            exportMessageDetail = detail ?? string.Empty;
            exportMessageError = isError;
            if (IsOpen &&
                currentPage == CenterPage.Report)
            {
                RenderCurrentPage();
            }
        }

        public void OpenReadiness(bool boostDecision)
        {
            requireBoostDecision = boostDecision;
            Open(CenterPage.Readiness);
        }

        public void OpenReport()
        {
            requireBoostDecision = false;
            Open(CenterPage.Report);
        }

        public void OpenHistory()
        {
            requireBoostDecision = false;
            Open(CenterPage.Report);
        }

        public void OpenImpact()
        {
            requireBoostDecision = false;
            Open(CenterPage.Impact);
        }

        public void OpenProfiles()
        {
            requireBoostDecision = false;
            Open(CenterPage.Profiles);
        }

        public void OpenSettings()
        {
            requireBoostDecision = false;
            Open(CenterPage.Settings);
        }

        public void SetBenchmarkProgress(
            string title,
            string detail,
            int percent)
        {
            bool wasBusy = benchmarkBusy;
            benchmarkBusy = true;
            benchmarkNeedsElevation = false;
            benchmarkTitle = title ?? "ТЕСТ ПРОИЗВОДИТЕЛЬНОСТИ";
            benchmarkDetail = detail ?? string.Empty;
            benchmarkPercent = Math.Max(0, Math.Min(100, percent));
            if (IsOpen && currentPage == CenterPage.Report)
            {
                if (!wasBusy ||
                    benchmarkNoticeTitleBlock == null ||
                    benchmarkButton == null)
                {
                    RenderCurrentPage();
                }
                else
                {
                    UpdateBenchmarkProgressVisuals();
                }
            }
        }

        public void SetBenchmarkNeedsElevation(string detail)
        {
            benchmarkBusy = false;
            benchmarkNeedsElevation = true;
            benchmarkTitle = "НУЖНЫ ПРАВА ДЛЯ ЗАМЕРА";
            benchmarkDetail = detail ??
                "Windows разрешает покадровую телеметрию только администратору или участнику Performance Log Users.";
            benchmarkPercent = 0;
            Open(CenterPage.Report);
        }

        public void SetBenchmarkMessage(
            string title,
            string detail,
            bool isError)
        {
            benchmarkBusy = false;
            benchmarkNeedsElevation = false;
            benchmarkTitle = title ?? (isError ? "ЗАМЕР НЕ ВЫПОЛНЕН" : "ЗАМЕР ЗАВЕРШЁН");
            benchmarkDetail = detail ?? string.Empty;
            benchmarkPercent = isError ? -1 : 100;
            Open(CenterPage.Report);
        }

        public void SetPerformanceProofSnapshot(
            PerformanceProofCoordinatorSnapshot value)
        {
            bool changed = proofSnapshot == null
                ? value != null
                : value == null ||
                    !string.Equals(
                        proofSnapshot.ProofId,
                        value.ProofId,
                        StringComparison.Ordinal) ||
                    proofSnapshot.State != value.State ||
                    proofSnapshot.CompletedSteps != value.CompletedSteps ||
                    proofSnapshot.Failure != value.Failure;
            proofSnapshot = value;
            if (changed && IsOpen && currentPage == CenterPage.Report && !benchmarkBusy)
            {
                RenderCurrentPage();
            }
        }

        public void HandleEscape()
        {
            if (benchmarkBusy)
            {
                return;
            }
            Close();
        }

        public void HandleKey(KeyEventArgs e)
        {
            if (!IsOpen || e == null)
            {
                return;
            }

            if (e.Key == Key.Escape)
            {
                HandleEscape();
                e.Handled = true;
            }
            else if (e.Key == Key.F5 && currentPage == CenterPage.Readiness)
            {
                Raise(RefreshRequested);
                e.Handled = true;
            }
            else if (e.Key == Key.Tab &&
                      (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                bool reverse =
                    (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                int pageCount = Enum.GetValues(typeof(CenterPage)).Length;
                int next = ((int)currentPage + (reverse ? pageCount - 1 : 1)) %
                    pageCount;
                SwitchPage((CenterPage)next, true);
                e.Handled = true;
            }
        }

        private Grid BuildHeader()
        {
            var header = new Grid();
            header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            headerTitle = MakeText(
                "ЦЕНТР BOOSTIX",
                18,
                TextColor,
                semiboldFont,
                FontWeights.Bold);
            headerTitle.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetRow(headerTitle, 0);
            header.Children.Add(headerTitle);

            subtitle = MakeText(
                "Готовность системы, отчёт сессии и безопасные настройки.",
                BoostixDesignTokens.MetadataTextSize,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            subtitle.Margin = new Thickness(0, 4, 0, 0);
            subtitle.RenderTransform = subtitleTranslation;
            AutomationProperties.SetLiveSetting(subtitle, AutomationLiveSetting.Polite);
            Grid.SetRow(subtitle, 1);
            header.Children.Add(subtitle);
            return header;
        }

        private Grid BuildTabs()
        {
            tabStrip = new BoostCenterTabStrip();
            var tabs = tabStrip;
            AutomationProperties.SetName(tabs, "Разделы Центра Boostix");
            AutomationProperties.SetAutomationId(
                tabs,
                "Boostix.Center.Tabs");
            tabs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tabs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tabs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tabs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tabs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            AddTab(tabs, CenterPage.Readiness, "СЕАНС", 0);
            AddTab(tabs, CenterPage.Impact, "ВЛИЯНИЕ", 1);
            AddTab(tabs, CenterPage.Report, "ЭФФЕКТ", 2);
            AddTab(tabs, CenterPage.Profiles, "ПРОФИЛИ", 3);
            AddTab(tabs, CenterPage.Settings, "НАСТРОЙКИ", 4);
            return tabs;
        }

        private void AddTab(
            BoostCenterTabStrip tabs,
            CenterPage page,
            string title,
            int column)
        {
            var host = new Grid();
            host.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(BoostixDesignTokens.MinimumActionHeight)
            });
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2) });
            Grid.SetColumn(host, column);

            var button = new BoostCenterTabButton(
                tabs,
                delegate { return currentPage == page; },
                delegate(bool preserveTabFocus)
                {
                    SwitchPage(page, preserveTabFocus);
                })
            {
                Content = title,
                FontFamily = semiboldFont,
                FontSize = BoostixDesignTokens.MetadataTextSize,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(MutedColor),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Template = MakeFlatButtonTemplate(0),
                FocusVisualStyle = MakeKeyboardFocusVisualStyle(2)
            };
            tabs.Register(button);
            AutomationProperties.SetName(button, title.ToLowerInvariant());
            AutomationProperties.SetAutomationId(
                button,
                "Boostix.Center.Tab." + page);
            KeyboardNavigation.SetTabIndex(button, column);
            Grid.SetRow(button, 0);
            host.Children.Add(button);

            var indicator = new Border
            {
                Height = 2,
                Background = new SolidColorBrush(AccentColor),
                Opacity = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            var indicatorScale = new ScaleTransform(0.72, 1);
            indicator.RenderTransform = indicatorScale;
            Grid.SetRow(indicator, 1);
            host.Children.Add(indicator);

            tabButtons[page] = button;
            tabIndicators[page] = indicator;
            tabIndicatorScales[page] = indicatorScale;
            tabs.Children.Add(host);
        }

        private void Open(CenterPage page)
        {
            BoostCenterTabButton previousSelection =
                tabStrip == null ? null : tabStrip.SelectedTab;
            currentPage = page;
            Visibility = Visibility.Visible;
            IsHitTestVisible = true;
            RenderCurrentPage();
            UpdateTabs(false);
            NotifyTabSelectionChanged(previousSelection);

            if (SystemParameters.ClientAreaAnimation)
            {
                Opacity = 0;
                entranceTranslation.Y = 6;
                BeginAnimation(
                    OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
                entranceTranslation.BeginAnimation(
                    TranslateTransform.YProperty,
                    new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(180))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
            }
            else
            {
                BeginAnimation(OpacityProperty, null);
                entranceTranslation.BeginAnimation(TranslateTransform.YProperty, null);
                Opacity = 1;
                entranceTranslation.Y = 0;
            }
            FocusPreferredButton();
        }

        private void Close()
        {
            if (!IsOpen)
            {
                return;
            }
            CancelPageTransitionAnimations();
            CancelSmoothMouseWheelScroll();
            if (!SystemParameters.ClientAreaAnimation)
            {
                FinishClose();
                return;
            }

            IsHitTestVisible = false;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fade.Completed += delegate { FinishClose(); };
            BeginAnimation(OpacityProperty, fade);
        }

        private void FinishClose()
        {
            BeginAnimation(OpacityProperty, null);
            entranceTranslation.BeginAnimation(TranslateTransform.YProperty, null);
            Opacity = 1;
            entranceTranslation.Y = 0;
            Visibility = Visibility.Collapsed;
            IsHitTestVisible = true;
            Raise(CloseRequested);
        }

        private void SwitchPage(CenterPage page)
        {
            SwitchPage(page, false);
        }

        private void SwitchPage(CenterPage page, bool preserveTabFocus)
        {
            if (currentPage == page && IsOpen)
            {
                return;
            }

            FinishPageTransitionImmediately();
            CenterPage previousPage = renderedPage;
            BoostCenterTabButton previousSelection =
                tabStrip == null ? null : tabStrip.SelectedTab;
            currentPage = page;
            requireBoostDecision = false;
            UpdateTabs(true);
            NotifyTabSelectionChanged(previousSelection);

            if (!SystemParameters.ClientAreaAnimation)
            {
                RenderCurrentPage();
                if (preserveTabFocus)
                {
                    FocusSelectedTab(page);
                }
                else
                {
                    FocusPreferredButton();
                }
                return;
            }

            if (preserveTabFocus)
            {
                BeginPageTransition(previousPage, page, true);
            }
            else
            {
                BeginPageTransition(previousPage, page);
            }
        }

        private void NotifyTabSelectionChanged(
            BoostCenterTabButton previousSelection)
        {
            if (tabStrip == null)
            {
                return;
            }
            tabStrip.NotifySelectionChanged(
                previousSelection,
                tabStrip.SelectedTab);
        }

        private void UpdateTabs(bool animate)
        {
            foreach (KeyValuePair<CenterPage, BoostCenterTabButton> pair in tabButtons)
            {
                bool selected = pair.Key == currentPage;
                Color targetColor = selected ? AccentTextColor : MutedColor;
                var foreground = pair.Value.Foreground as SolidColorBrush;
                if (foreground == null)
                {
                    foreground = new SolidColorBrush(targetColor);
                    pair.Value.Foreground = foreground;
                }
                AnimateTabColor(foreground, targetColor, animate);

                AnimateTabIndicator(
                    tabIndicators[pair.Key],
                    tabIndicatorScales[pair.Key],
                    selected ? 1 : 0,
                    selected ? 1 : 0.72,
                    animate);
                AutomationProperties.SetHelpText(
                    pair.Value,
                    selected ? "Выбрано" : "Открыть раздел");
                AutomationProperties.SetItemStatus(
                    pair.Value,
                    selected ? "Выбрано" : string.Empty);
            }
        }

        private void BeginPageTransition(
            CenterPage previousPage,
            CenterPage nextPage)
        {
            BeginPageTransition(previousPage, nextPage, false);
        }

        private void BeginPageTransition(
            CenterPage previousPage,
            CenterPage nextPage,
            bool preserveTabFocus)
        {
            if (previousPage == nextPage)
            {
                RenderCurrentPage();
                if (preserveTabFocus)
                {
                    FocusSelectedTab(nextPage);
                }
                else
                {
                    FocusPreferredButton();
                }
                return;
            }

            CancelSmoothMouseWheelScroll();
            int direction = (int)nextPage > (int)previousPage ? 1 : -1;
            int generation = ++pageTransitionGeneration;
            pageTransitionAnimating = true;
            pageScroller.IsHitTestVisible = false;
            footerButtons.IsHitTestVisible = false;
            FocusSelectedTab(nextPage);

            AnimatePageVisual(
                subtitle,
                subtitleTranslation,
                0,
                -direction * 12,
                PageTransitionExitMilliseconds,
                EasingMode.EaseIn,
                null);
            AnimatePageVisual(
                footerButtons,
                footerTranslation,
                0,
                -direction * 18,
                PageTransitionExitMilliseconds,
                EasingMode.EaseIn,
                null);
            AnimatePageVisual(
                pageScroller,
                pageTranslation,
                0,
                -direction * 18,
                PageTransitionExitMilliseconds,
                EasingMode.EaseIn,
                delegate
                {
                    if (!pageTransitionAnimating ||
                        generation != pageTransitionGeneration)
                    {
                        return;
                    }

                    renderedPage = currentPage;
                    RenderCurrentPageCore();
                    PreparePageVisual(
                        subtitle,
                        subtitleTranslation,
                        direction * 12);
                    PreparePageVisual(
                        footerButtons,
                        footerTranslation,
                        direction * 18);
                    PreparePageVisual(
                        pageScroller,
                        pageTranslation,
                        direction * 18);

                    AnimatePageVisual(
                        subtitle,
                        subtitleTranslation,
                        1,
                        0,
                        PageTransitionEnterMilliseconds,
                        EasingMode.EaseOut,
                        null);
                    AnimatePageVisual(
                        footerButtons,
                        footerTranslation,
                        1,
                        0,
                        PageTransitionEnterMilliseconds,
                        EasingMode.EaseOut,
                        null);
                    AnimatePageVisual(
                        pageScroller,
                        pageTranslation,
                        1,
                        0,
                        PageTransitionEnterMilliseconds,
                        EasingMode.EaseOut,
                        delegate
                        {
                            if (!pageTransitionAnimating ||
                                generation != pageTransitionGeneration)
                            {
                                return;
                            }

                            pageTransitionAnimating = false;
                            ResetPageTransitionVisuals();
                            if (preserveTabFocus)
                            {
                                FocusSelectedTab(nextPage);
                            }
                            else
                            {
                                FocusPreferredButton();
                            }
                        });
                });
        }

        private void FinishPageTransitionImmediately()
        {
            if (!pageTransitionAnimating)
            {
                return;
            }

            CancelPageTransitionAnimations();
            if (renderedPage != currentPage)
            {
                renderedPage = currentPage;
                RenderCurrentPageCore();
            }
        }

        private void CancelPageTransitionAnimations()
        {
            ++pageTransitionGeneration;
            pageTransitionAnimating = false;
            ResetPageTransitionVisuals();
        }

        private void ResetPageTransitionVisuals()
        {
            ResetPageVisual(subtitle, subtitleTranslation);
            ResetPageVisual(footerButtons, footerTranslation);
            ResetPageVisual(pageScroller, pageTranslation);
            pageScroller.IsHitTestVisible = true;
            footerButtons.IsHitTestVisible = true;
        }

        private static void PreparePageVisual(
            FrameworkElement element,
            TranslateTransform translation,
            double offset)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            translation.BeginAnimation(TranslateTransform.XProperty, null);
            element.Opacity = 0;
            translation.X = offset;
        }

        private static void ResetPageVisual(
            FrameworkElement element,
            TranslateTransform translation)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            translation.BeginAnimation(TranslateTransform.XProperty, null);
            element.Opacity = 1;
            translation.X = 0;
        }

        private static void AnimatePageVisual(
            FrameworkElement element,
            TranslateTransform translation,
            double targetOpacity,
            double targetOffset,
            int milliseconds,
            EasingMode easingMode,
            EventHandler completed)
        {
            double startOpacity = element.Opacity;
            double startOffset = translation.X;
            element.BeginAnimation(UIElement.OpacityProperty, null);
            translation.BeginAnimation(TranslateTransform.XProperty, null);
            element.Opacity = targetOpacity;
            translation.X = targetOffset;

            var opacityAnimation = new DoubleAnimation(
                startOpacity,
                targetOpacity,
                TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = easingMode },
                FillBehavior = FillBehavior.Stop
            };
            if (completed != null)
            {
                opacityAnimation.Completed += completed;
            }

            element.BeginAnimation(
                UIElement.OpacityProperty,
                opacityAnimation,
                HandoffBehavior.SnapshotAndReplace);
            translation.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(
                    startOffset,
                    targetOffset,
                    TimeSpan.FromMilliseconds(milliseconds))
                {
                    EasingFunction = new CubicEase { EasingMode = easingMode },
                    FillBehavior = FillBehavior.Stop
                },
                HandoffBehavior.SnapshotAndReplace);
        }

        private static void AnimateTabColor(
            SolidColorBrush brush,
            Color target,
            bool animate)
        {
            Color start = brush.Color;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = target;
            if (!animate ||
                !SystemParameters.ClientAreaAnimation ||
                start == target)
            {
                return;
            }

            brush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(
                    start,
                    target,
                    TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new CubicEase
                    {
                        EasingMode = EasingMode.EaseInOut
                    },
                    FillBehavior = FillBehavior.Stop
                },
                HandoffBehavior.SnapshotAndReplace);
        }

        private static void AnimateTabIndicator(
            Border indicator,
            ScaleTransform scale,
            double targetOpacity,
            double targetScale,
            bool animate)
        {
            double startOpacity = indicator.Opacity;
            double startScale = scale.ScaleX;
            indicator.BeginAnimation(UIElement.OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            indicator.Opacity = targetOpacity;
            scale.ScaleX = targetScale;

            if (!animate || !SystemParameters.ClientAreaAnimation)
            {
                return;
            }

            indicator.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(
                    startOpacity,
                    targetOpacity,
                    TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new CubicEase
                    {
                        EasingMode = EasingMode.EaseInOut
                    },
                    FillBehavior = FillBehavior.Stop
                },
                HandoffBehavior.SnapshotAndReplace);
            scale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                new DoubleAnimation(
                    startScale,
                    targetScale,
                    TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new CubicEase
                    {
                        EasingMode = EasingMode.EaseInOut
                    },
                    FillBehavior = FillBehavior.Stop
                },
                HandoffBehavior.SnapshotAndReplace);
        }

        private void FocusSelectedTab(CenterPage page)
        {
            BoostCenterTabButton selectedTab;
            if (tabButtons.TryGetValue(page, out selectedTab) &&
                selectedTab.IsEnabled &&
                selectedTab.IsVisible)
            {
                selectedTab.Focus();
                Keyboard.Focus(selectedTab);
            }
        }

        private void RenderCurrentPage()
        {
            CancelPageTransitionAnimations();
            renderedPage = currentPage;
            RenderCurrentPageCore();
        }

        private void RenderCurrentPageCore()
        {
            CancelSmoothMouseWheelScroll();
            pageScroller.ScrollToVerticalOffset(0);
            smoothScrollTarget = 0;
            pageContent.Children.Clear();
            footerButtons.Children.Clear();
            preferredFocusButton = null;
            benchmarkNoticeTitleBlock = null;
            benchmarkNoticeDetailBlock = null;
            benchmarkProgressFill = null;
            benchmarkButton = null;
            reportStack = null;

            if (currentPage == CenterPage.Readiness)
            {
                RenderReadiness();
            }
            else if (currentPage == CenterPage.Impact)
            {
                RenderImpact();
            }
            else if (currentPage == CenterPage.Report)
            {
                RenderReport();
            }
            else if (currentPage == CenterPage.Profiles)
            {
                RenderProfiles();
            }
            else
            {
                RenderSettings();
            }
        }

        private void RenderReadiness()
        {
            subtitle.Text = preflight == null
                ? "Проверяем состояние системы."
                : string.Format(
                    CultureInfo.CurrentCulture,
                    "Последняя проверка: {0:HH:mm:ss}",
                    preflight.CapturedUtc.ToLocalTime());

            pageContent.Children.Add(BuildLiveSessionCard());

            if (diagnosticSnapshot != null)
            {
                pageContent.Children.Add(BuildResourceSnapshot(diagnosticSnapshot));
            }

            if (preflight == null)
            {
                pageContent.Children.Add(MakeEmptyState(
                    "ИДЁТ ПРОВЕРКА",
                    "Собираем только безопасные данные без изменения Windows."));
            }
            else
            {
                foreach (BoostCheckResult check in preflight.Checks)
                {
                    pageContent.Children.Add(BuildCheckRow(check));
                }
            }

            var refresh = MakeActionButton("ПРОВЕРИТЬ СНОВА", false, false);
            refresh.Width = 154;
            refresh.Click += delegate { Raise(RefreshRequested); };
            AutomationProperties.SetName(refresh, "Проверить готовность снова");
            footerButtons.Children.Add(refresh);
            preferredFocusButton = refresh;

            var choose = MakeActionButton("ВЫБРАТЬ ИГРУ", false, false);
            choose.Width = 142;
            choose.Margin = new Thickness(8, 0, 0, 0);
            choose.Click += delegate { Raise(TargetSelectionRequested); };
            AutomationProperties.SetName(choose, "Выбрать точную игру для сеанса");
            AutomationProperties.SetAutomationId(
                choose,
                "Boostix.Center.SelectTarget");
            footerButtons.Children.Add(choose);

            if (requireBoostDecision)
            {
                var proceed = MakeActionButton(
                    preflight != null && preflight.HasBlockers
                        ? "BOOSTIX НЕДОСТУПЕН"
                        : "ПРОДОЛЖИТЬ",
                    true,
                    false);
                proceed.Width = 168;
                proceed.Margin = new Thickness(10, 0, 0, 0);
                proceed.IsEnabled = preflight != null && !preflight.HasBlockers;
                proceed.IsDefault = proceed.IsEnabled;
                proceed.Click += delegate
                {
                    requireBoostDecision = false;
                    Close();
                    Raise(ProceedBoostRequested);
                };
                AutomationProperties.SetName(
                    proceed,
                    "Продолжить запуск сеанса Boostix");
                footerButtons.Children.Add(proceed);
                if (proceed.IsEnabled)
                {
                    preferredFocusButton = proceed;
                }
            }
        }

        private FrameworkElement BuildLiveSessionCard()
        {
            var card = new Border
            {
                Background = new SolidColorBrush(SurfaceColor),
                BorderBrush = new SolidColorBrush(BorderColor),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });

            string targetName = selectedTarget == null
                ? "ИГРА НЕ ВЫБРАНА"
                : (string.IsNullOrWhiteSpace(selectedTarget.ProcessName)
                    ? System.IO.Path.GetFileNameWithoutExtension(
                        selectedTarget.ExecutablePath)
                    : selectedTarget.ProcessName).ToUpperInvariant();
            var target = new StackPanel();
            target.Children.Add(MakeText(
                targetName,
                BoostixDesignTokens.BodyTextSize,
                TextColor,
                semiboldFont,
                FontWeights.Bold));
            string targetDetail = selectedTarget == null
                ? "Выберите запущенную игру — Boostix не угадывает процесс."
                : "PID " + selectedTarget.ProcessId.ToString(
                    CultureInfo.InvariantCulture) + " · точная цель";
            var detail = MakeText(
                targetDetail,
                BoostixDesignTokens.MetadataTextSize,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            detail.TextWrapping = TextWrapping.Wrap;
            detail.Margin = new Thickness(0, 3, 10, 0);
            target.Children.Add(detail);
            content.Children.Add(target);

            bool pressure = sessionGuardPressureState != null &&
                sessionGuardPressureState.CriticalAlertActive;
            string state = boostSessionActive
                ? (pressure ? "ДАВЛЕНИЕ" : "АКТИВЕН")
                : (selectedTarget == null ? "НЕТ ЦЕЛИ" : "ГОТОВ");
            AddSessionCardMetric(
                content,
                1,
                "СЕАНС",
                state,
                pressure ? WarningColor : (boostSessionActive ? SuccessColor : SecondaryColor));

            string commit = sessionGuardSample != null &&
                sessionGuardSample.SystemMetricsAvailable
                ? FormatBytesCompact(sessionGuardSample.CommitHeadroomBytes)
                : "—";
            AddSessionCardMetric(
                content,
                2,
                "COMMIT",
                commit,
                pressure ? WarningColor : TextColor);

            card.Child = content;
            AutomationProperties.SetName(
                card,
                targetName + ". Состояние: " + state + ". Запас commit: " + commit + ".");
            AutomationProperties.SetAutomationId(
                card,
                "Boostix.Center.LiveSession");
            return card;
        }

        private void AddSessionCardMetric(
            Grid host,
            int column,
            string label,
            string value,
            Color valueColor)
        {
            var block = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var labelText = MakeText(
                label,
                BoostixDesignTokens.MetadataTextSize,
                MutedColor,
                semiboldFont,
                FontWeights.Bold);
            labelText.HorizontalAlignment = HorizontalAlignment.Right;
            block.Children.Add(labelText);
            var valueText = MakeText(
                value,
                BoostixDesignTokens.BodyTextSize,
                valueColor,
                semiboldFont,
                FontWeights.Bold);
            valueText.HorizontalAlignment = HorizontalAlignment.Right;
            valueText.Margin = new Thickness(0, 3, 0, 0);
            block.Children.Add(valueText);
            Grid.SetColumn(block, column);
            host.Children.Add(block);
        }

        private void RenderImpact()
        {
            subtitle.Text =
                "Измеряем CPU, память и диск фоновых приложений без скрытого рейтинга и без изменений Windows.";

            if (!string.IsNullOrWhiteSpace(impactMessage))
            {
                var message = MakeText(
                    impactMessage,
                    BoostixDesignTokens.BodyTextSize,
                    impactScanBusy ? AccentTextColor : SecondaryColor,
                    regularFont,
                    FontWeights.Normal);
                message.TextWrapping = TextWrapping.Wrap;
                message.Margin = new Thickness(0, 0, 0, 10);
                AutomationProperties.SetLiveSetting(
                    message,
                    AutomationLiveSetting.Polite);
                pageContent.Children.Add(message);
            }

            if (impactResults.Count == 0)
            {
                pageContent.Children.Add(MakeEmptyState(
                    impactScanBusy ? "ИДЁТ ИЗМЕРЕНИЕ" : "НЕТ ЗАМЕРА",
                    impactScanBusy
                        ? "Не переключайте нагрузку: сравниваем два снимка в течение 15 секунд."
                        : "Запустите анализ — Boostix только наблюдает и ничего не закрывает автоматически."));
            }
            else
            {
                int index = 0;
                foreach (BackgroundImpactResult item in impactResults.Take(12))
                {
                    pageContent.Children.Add(BuildImpactRow(item, index++));
                }
            }

            var scan = MakeActionButton(
                impactScanBusy ? "ИЗМЕРЕНИЕ…" : "АНАЛИЗ · 15 СЕК",
                true,
                false);
            scan.Width = 166;
            scan.IsEnabled = !impactScanBusy;
            scan.Click += delegate { Raise(ImpactScanRequested); };
            AutomationProperties.SetName(
                scan,
                "Измерить влияние фоновых приложений за 15 секунд");
            AutomationProperties.SetAutomationId(
                scan,
                "Boostix.Center.ImpactScan");
            footerButtons.Children.Add(scan);
            preferredFocusButton = scan;
        }

        private FrameworkElement BuildImpactRow(
            BackgroundImpactResult item,
            int index)
        {
            var row = new Grid
            {
                MinHeight = 54,
                Margin = new Thickness(0, 0, 0, 1)
            };
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            string processName = string.IsNullOrWhiteSpace(item.Identity.ProcessName)
                ? "ПРИЛОЖЕНИЕ"
                : item.Identity.ProcessName.ToUpperInvariant();
            var name = MakeText(
                processName,
                BoostixDesignTokens.BodyTextSize,
                TextColor,
                semiboldFont,
                FontWeights.Bold);
            name.Margin = new Thickness(0, 6, 8, 0);
            row.Children.Add(name);

            var cpu = MakeText(
                "CPU " + item.CpuPercent.ToString("0.0", CultureInfo.CurrentCulture) + "%",
                BoostixDesignTokens.BodyTextSize,
                item.CpuPercent >= 5 ? WarningColor : AccentTextColor,
                semiboldFont,
                FontWeights.Bold);
            cpu.HorizontalAlignment = HorizontalAlignment.Right;
            cpu.Margin = new Thickness(8, 6, 0, 0);
            Grid.SetColumn(cpu, 1);
            row.Children.Add(cpu);

            var close = MakeActionButton("ЗАКРЫТЬ", false, false);
            close.Width = 82;
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.VerticalAlignment = VerticalAlignment.Center;
            close.Click += delegate
            {
                EventHandler<BackgroundImpactEventArgs> handler =
                    ImpactCloseRequested;
                if (handler != null)
                {
                    handler(
                        this,
                        new BackgroundImpactEventArgs(item.Identity));
                }
            };
            AutomationProperties.SetName(
                close,
                "Запросить обычное закрытие " + processName);
            AutomationProperties.SetHelpText(
                close,
                "Boostix отправит стандартный запрос закрытия и не будет завершать процесс принудительно.");
            AutomationProperties.SetAutomationId(
                close,
                "Boostix.Center.Impact.Close." +
                index.ToString(CultureInfo.InvariantCulture));
            Grid.SetColumn(close, 2);
            Grid.SetRowSpan(close, 2);
            row.Children.Add(close);

            string metrics = "Private " + FormatBytesCompact(item.PrivateBytes) +
                " · I/O " + FormatBytesCompact(item.ReadBytes + item.WriteBytes);
            var detail = MakeText(
                metrics,
                BoostixDesignTokens.MetadataTextSize,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            detail.Margin = new Thickness(0, 2, 8, 7);
            Grid.SetRow(detail, 1);
            Grid.SetColumnSpan(detail, 2);
            row.Children.Add(detail);

            var separator = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(DividerColor),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            Grid.SetRowSpan(separator, 2);
            Grid.SetColumnSpan(separator, 3);
            row.Children.Add(separator);
            AutomationProperties.SetName(
                row,
                processName + ". " + metrics + ". " + cpu.Text + ".");
            AutomationProperties.SetAutomationId(
                row,
                "Boostix.Center.Impact." + index.ToString(CultureInfo.InvariantCulture));
            return row;
        }

        private void RenderProfiles()
        {
            subtitle.Text =
                "Профиль привязан к точному пути EXE. Автозапуск доступен только после вашего явного выбора.";
            if (gameProfiles.Count == 0)
            {
                pageContent.Children.Add(MakeEmptyState(
                    "ПРОФИЛЕЙ ПОКА НЕТ",
                    "Выберите запущенную игру на главном экране — Boostix сохранит её как профиль."));
            }
            else
            {
                int index = 0;
                foreach (GameProfile profile in gameProfiles)
                {
                    pageContent.Children.Add(BuildProfileRow(profile, index++));
                }
            }

            var choose = MakeActionButton("ДОБАВИТЬ ИГРУ", true, false);
            choose.Width = 150;
            choose.Click += delegate { Raise(TargetSelectionRequested); };
            AutomationProperties.SetName(choose, "Выбрать запущенную игру и сохранить профиль");
            AutomationProperties.SetAutomationId(
                choose,
                "Boostix.Center.ProfileAdd");
            footerButtons.Children.Add(choose);
            preferredFocusButton = choose;
        }

        private FrameworkElement BuildProfileRow(GameProfile profile, int index)
        {
            var row = new Grid
            {
                MinHeight = 60,
                Margin = new Thickness(0, 0, 0, 1)
            };
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var name = MakeText(
                profile.DisplayName.ToUpperInvariant(),
                BoostixDesignTokens.BodyTextSize,
                TextColor,
                semiboldFont,
                FontWeights.Bold);
            name.Margin = new Thickness(0, 7, 8, 0);
            row.Children.Add(name);
            var path = MakeText(
                System.IO.Path.GetFileName(profile.ExecutablePath),
                BoostixDesignTokens.MetadataTextSize,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            path.Margin = new Thickness(0, 3, 8, 7);
            path.ToolTip = profile.ExecutablePath;
            Grid.SetRow(path, 1);
            row.Children.Add(path);

            var auto = new CheckBox
            {
                Content = "AUTO",
                IsChecked = profile.AutoBoost,
                FontFamily = semiboldFont,
                FontSize = BoostixDesignTokens.MetadataTextSize,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(
                    profile.AutoBoost ? AccentTextColor : SecondaryColor),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Cursor = Cursors.Hand,
                MinHeight = BoostixDesignTokens.MinimumActionHeight,
                FocusVisualStyle = MakeKeyboardFocusVisualStyle(6)
            };
            Grid.SetColumn(auto, 1);
            Grid.SetRowSpan(auto, 2);
            AutomationProperties.SetName(
                auto,
                "Автоматический Boost для " + profile.DisplayName);
            AutomationProperties.SetAutomationId(
                auto,
                "Boostix.Center.ProfileAuto." + index.ToString(CultureInfo.InvariantCulture));
            auto.Checked += delegate
            {
                RaiseProfile(ProfileAutoBoostChanged, profile.ExecutablePath, true);
            };
            auto.Unchecked += delegate
            {
                RaiseProfile(ProfileAutoBoostChanged, profile.ExecutablePath, false);
            };
            row.Children.Add(auto);

            var remove = MakeActionButton("УДАЛИТЬ", false, true);
            remove.Width = 70;
            remove.Height = BoostixDesignTokens.MinimumActionHeight;
            remove.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(remove, 2);
            Grid.SetRowSpan(remove, 2);
            AutomationProperties.SetName(
                remove,
                "Удалить профиль " + profile.DisplayName);
            AutomationProperties.SetAutomationId(
                remove,
                "Boostix.Center.ProfileRemove." + index.ToString(CultureInfo.InvariantCulture));
            remove.Click += delegate
            {
                RaiseProfile(ProfileRemoveRequested, profile.ExecutablePath, false);
            };
            row.Children.Add(remove);

            var separator = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(DividerColor),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            Grid.SetColumnSpan(separator, 3);
            Grid.SetRowSpan(separator, 2);
            Panel.SetZIndex(separator, -1);
            row.Children.Add(separator);
            return row;
        }

        private void RaiseProfile(
            EventHandler<BoostProfileEventArgs> handler,
            string executablePath,
            bool enabled)
        {
            if (handler != null)
            {
                handler(this, new BoostProfileEventArgs(executablePath, enabled));
            }
        }

        private FrameworkElement BuildCheckRow(BoostCheckResult check)
        {
            var row = new Grid
            {
                MinHeight = 51,
                Margin = new Thickness(0, 0, 0, 1),
                Background = Brushes.Transparent
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(27) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            string glyph;
            Color glyphColor;
            GetSeverityVisual(check.Severity, out glyph, out glyphColor);
            var status = MakeText(
                glyph,
                13,
                glyphColor,
                new FontFamily("Segoe UI Symbol"),
                FontWeights.Bold);
            status.VerticalAlignment = VerticalAlignment.Top;
            status.HorizontalAlignment = HorizontalAlignment.Center;
            status.Margin = new Thickness(0, 8, 0, 0);
            Grid.SetColumn(status, 0);
            Grid.SetRowSpan(status, 2);
            row.Children.Add(status);

            var title = MakeText(
                check.Title,
                BoostixDesignTokens.MetadataTextSize,
                TextColor,
                semiboldFont,
                FontWeights.Bold);
            title.Margin = new Thickness(0, 7, 0, 0);
            Grid.SetColumn(title, 1);
            Grid.SetRow(title, 0);
            row.Children.Add(title);

            var detail = MakeText(
                check.Detail,
                BoostixDesignTokens.MetadataTextSize,
                SecondaryColor,
                regularFont,
                FontWeights.Normal);
            detail.TextWrapping = TextWrapping.Wrap;
            detail.Margin = new Thickness(0, 2, 4, 7);
            Grid.SetColumn(detail, 1);
            Grid.SetRow(detail, 1);
            row.Children.Add(detail);

            var separator = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(DividerColor),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            Grid.SetColumnSpan(separator, 2);
            Grid.SetRowSpan(separator, 2);
            row.Children.Add(separator);

            AutomationProperties.SetName(
                row,
                check.Title + ". " + check.Detail);
            return row;
        }

        private void RenderReport()
        {
            subtitle.Text =
                "Действия Boostix и результаты последнего сеанса производительности.";
            reportStack = BuildReportPage();
            pageContent.Children.Add(reportStack);

            IEnumerable<BoostSessionReport> recent = sessionHistory
                .Where(item => item != null &&
                    (sessionReport == null ||
                     !string.Equals(
                         item.SessionId,
                         sessionReport.SessionId,
                         StringComparison.OrdinalIgnoreCase)))
                .Take(3);
            if (recent.Any())
            {
                var historyTitle = MakeText(
                    "НЕДАВНИЕ СЕССИИ",
                    BoostixDesignTokens.BodyTextSize,
                    TextColor,
                    semiboldFont,
                    FontWeights.Bold);
                historyTitle.Margin = new Thickness(0, 16, 0, 5);
                pageContent.Children.Add(historyTitle);
                int historyIndex = 0;
                foreach (BoostSessionReport report in recent)
                {
                    pageContent.Children.Add(
                        BuildHistoryRow(report, historyIndex++));
                }
            }

            var export = MakeActionButton("ЭКСПОРТ", false, false);
            export.Width = 112;
            export.Margin = new Thickness(0, 0, 8, 0);
            export.Click += delegate { Raise(ExportDiagnosticsRequested); };
            AutomationProperties.SetName(
                export,
                "Сохранить безопасный диагностический отчёт");
            AutomationProperties.SetAutomationId(
                export,
                "Boostix.Center.ExportDiagnostics");
            footerButtons.Children.Add(export);

            string proofButtonText = "PROOF MODE · 4×60 С";
            if (proofSnapshot != null &&
                proofSnapshot.State == PerformanceProofCoordinatorState.AwaitingRun &&
                proofSnapshot.NextStep != null)
            {
                proofButtonText = proofSnapshot.CompletedSteps == 0
                    ? "НАЧАТЬ PROOF MODE"
                    : "ШАГ " + proofSnapshot.NextStep.StepNumber.ToString(
                        CultureInfo.CurrentCulture) + "/" +
                        proofSnapshot.TotalSteps.ToString(CultureInfo.CurrentCulture);
            }
            else if (proofSnapshot != null)
            {
                proofButtonText = "НОВЫЙ PROOF MODE";
            }

            var benchmark = MakeActionButton(
                benchmarkNeedsElevation
                    ? "ПОВТОРИТЬ С UAC"
                    : (benchmarkBusy
                        ? "ЗАМЕР " + benchmarkPercent.ToString(CultureInfo.CurrentCulture) + "%"
                        : proofButtonText),
                true,
                false);
            benchmark.Width = 164;
            benchmark.IsEnabled = !benchmarkBusy;
            benchmark.Click += delegate
            {
                EventHandler<BoostBenchmarkRequestEventArgs> handler = BenchmarkRequested;
                if (handler != null)
                {
                    handler(this, new BoostBenchmarkRequestEventArgs(benchmarkNeedsElevation));
                }
            };
            AutomationProperties.SetName(
                benchmark,
                benchmarkNeedsElevation
                    ? "Повторить текущий этап Proof Mode с правами администратора"
                    : "Запустить следующий этап доказательного сравнения производительности");
            AutomationProperties.SetAutomationId(
                benchmark,
                "Boostix.Center.ReportBenchmark");
            benchmarkButton = benchmark;
            footerButtons.Children.Add(benchmark);
            preferredFocusButton = benchmark;

            if (proofSnapshot != null &&
                proofSnapshot.State == PerformanceProofCoordinatorState.AwaitingRun)
            {
                var cancelProof = MakeActionButton("СБРОСИТЬ", false, false);
                cancelProof.Width = 96;
                cancelProof.Margin = new Thickness(8, 0, 0, 0);
                cancelProof.IsEnabled = !benchmarkBusy;
                cancelProof.Click += delegate { Raise(ProofCancelRequested); };
                AutomationProperties.SetName(
                    cancelProof,
                    "Сбросить незавершённый Proof Mode");
                AutomationProperties.SetAutomationId(
                    cancelProof,
                    "Boostix.Center.CancelProof");
                footerButtons.Children.Add(cancelProof);
            }
        }

        private StackPanel BuildReportPage()
        {
            var report = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(
                report,
                "Содержимое отчёта Boostix");
            AutomationProperties.SetAutomationId(
                report,
                "Boostix.Center.ReportContent");

            if (!string.IsNullOrWhiteSpace(benchmarkTitle))
            {
                report.Children.Add(BuildBenchmarkNotice());
            }
            if (!string.IsNullOrWhiteSpace(exportMessageTitle))
            {
                report.Children.Add(BuildExportNotice());
            }
            if (proofSnapshot != null)
            {
                report.Children.Add(BuildPerformanceProofCard(proofSnapshot));
            }

            if (sessionReport == null)
            {
                report.Children.Add(MakeEmptyState(
                    "ЕЩЁ НЕТ ОТЧЁТА",
                    "Активируйте Boostix — выполненные действия появятся здесь."));
            }
            else
            {
                BoostCrashInsight crashInsight =
                    BoostCrashAssistant.Analyze(sessionReport);
                if (crashInsight.Category != BoostCrashCategory.None)
                {
                    report.Children.Add(BuildCrashInsight(crashInsight));
                }
                report.Children.Add(BuildSessionSummary(sessionReport));
                report.Children.Add(BuildSessionResourceSummary(sessionReport));
                if (sessionReport.Performance != null &&
                    sessionReport.Performance.Available)
                {
                    report.Children.Add(BuildPerformanceGrid(sessionReport.Performance));
                    BoostPerformanceComparison comparison =
                        BoostSessionComparison.Compare(
                            sessionReport,
                            sessionHistory);
                    if (comparison.Available)
                    {
                        report.Children.Add(
                            BuildPerformanceComparison(comparison));
                    }
                }

                var actionsTitle = MakeText(
                    "ДЕЙСТВИЯ",
                    BoostixDesignTokens.MetadataTextSize,
                    TextColor,
                    semiboldFont,
                    FontWeights.Bold);
                actionsTitle.Margin = new Thickness(0, 13, 0, 4);
                report.Children.Add(actionsTitle);

                IEnumerable<BoostActionRecord> actions =
                    (sessionReport.Actions ?? new List<BoostActionRecord>())
                        .OrderByDescending(item => item.TimestampUtc)
                        .Take(20);
                if (!actions.Any())
                {
                    report.Children.Add(MakeText(
                        "Действия ещё не зафиксированы.",
                        BoostixDesignTokens.MetadataTextSize,
                        MutedColor,
                        regularFont,
                        FontWeights.Normal));
                }
                else
                {
                    foreach (BoostActionRecord action in actions)
                    {
                        report.Children.Add(BuildActionRow(action));
                    }
                }
            }
            return report;
        }

        private FrameworkElement BuildPerformanceProofCard(
            PerformanceProofCoordinatorSnapshot snapshot)
        {
            bool failed = snapshot.State == PerformanceProofCoordinatorState.Failed;
            bool completed = snapshot.State == PerformanceProofCoordinatorState.Completed &&
                snapshot.FinalResult != null;
            Color edge = failed
                ? ErrorColor
                : (completed && snapshot.FinalResult.Conclusive
                    ? (snapshot.FinalResult.Verdict == PerformanceProofVerdict.Negative
                        ? ErrorColor
                        : SuccessColor)
                    : AccentColor);
            var host = new Border
            {
                Background = new SolidColorBrush(SurfaceColor),
                BorderBrush = new SolidColorBrush(edge),
                BorderThickness = new Thickness(2, 0, 0, 0),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var content = new StackPanel();
            string title;
            string detail;
            if (completed)
            {
                PerformanceProofResult result = snapshot.FinalResult;
                title = result.Verdict == PerformanceProofVerdict.Positive
                    ? "ЭФФЕКТ ПОДТВЕРЖДЁН"
                    : (result.Verdict == PerformanceProofVerdict.Negative
                        ? "BOOST УХУДШИЛ РЕЗУЛЬТАТ"
                        : "РАЗНИЦА НЕ ДОКАЗАНА");
                detail = string.Format(
                    CultureInfo.CurrentCulture,
                    "Средний FPS: {0:+0.0;-0.0;0.0}% · 1% low: {1:+0.0;-0.0;0.0} FPS · P95: {2:+0.0;-0.0;0.0} мс · разброс ±{3:0.0}%\n{4}",
                    result.AverageFpsDeltaPercent,
                    result.OnePercentLowFpsDelta,
                    result.P95FrameTimeReductionMs,
                    result.VariabilityPercent,
                    result.Summary ?? string.Empty);
            }
            else if (snapshot.State == PerformanceProofCoordinatorState.AwaitingRun &&
                     snapshot.NextStep != null)
            {
                title = "PROOF MODE · ШАГ " +
                    snapshot.NextStep.StepNumber.ToString(CultureInfo.CurrentCulture) + "/" +
                    snapshot.TotalSteps.ToString(CultureInfo.CurrentCulture) +
                    " · " + snapshot.NextStep.SequenceLabel;
                detail = snapshot.NextStep.Instruction;
            }
            else
            {
                title = failed ? "PROOF MODE ОСТАНОВЛЕН" : "PROOF MODE СБРОШЕН";
                detail = string.IsNullOrWhiteSpace(snapshot.Message)
                    ? "Начните новый тест, когда игра и сцена будут готовы."
                    : snapshot.Message;
            }

            content.Children.Add(MakeText(
                title,
                BoostixDesignTokens.BodyTextSize,
                TextColor,
                semiboldFont,
                FontWeights.Bold));
            TextBlock description = MakeText(
                detail,
                BoostixDesignTokens.MetadataTextSize,
                SecondaryColor,
                regularFont,
                FontWeights.Normal);
            description.TextWrapping = TextWrapping.Wrap;
            description.Margin = new Thickness(0, 4, 0, 0);
            content.Children.Add(description);
            host.Child = content;
            AutomationProperties.SetName(host, title + ". " + detail);
            AutomationProperties.SetLiveSetting(
                host,
                failed ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
            return host;
        }

        private FrameworkElement BuildBenchmarkNotice()
        {
            var notice = new Border
            {
                Background = new SolidColorBrush(SurfaceColor),
                BorderBrush = new SolidColorBrush(
                    benchmarkNeedsElevation || benchmarkPercent < 0
                        ? ErrorColor
                        : AccentColor),
                BorderThickness = new Thickness(1, 0, 0, 0),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var content = new StackPanel();
            var title = MakeText(
                benchmarkTitle,
                BoostixDesignTokens.MetadataTextSize,
                TextColor,
                semiboldFont,
                FontWeights.Bold);
            benchmarkNoticeTitleBlock = title;
            content.Children.Add(title);
            var detail = MakeText(
                benchmarkDetail,
                BoostixDesignTokens.MetadataTextSize,
                SecondaryColor,
                regularFont,
                FontWeights.Normal);
            benchmarkNoticeDetailBlock = detail;
            detail.TextWrapping = TextWrapping.Wrap;
            detail.Margin = new Thickness(0, 3, 0, 0);
            content.Children.Add(detail);

            if (benchmarkBusy)
            {
                var track = new Border
                {
                    Height = 3,
                    Background = new SolidColorBrush(ButtonColor),
                    CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(0, 8, 0, 0)
                };
                var fill = new Border
                {
                    Width = 3.2 * benchmarkPercent,
                    Height = 3,
                    Background = new SolidColorBrush(AccentColor),
                    CornerRadius = new CornerRadius(2),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                benchmarkProgressFill = fill;
                track.Child = fill;
                content.Children.Add(track);
            }
            notice.Child = content;
            AutomationProperties.SetLiveSetting(notice, AutomationLiveSetting.Polite);
            return notice;
        }

        private FrameworkElement BuildExportNotice()
        {
            var notice = new Border
            {
                Background = new SolidColorBrush(SurfaceColor),
                BorderBrush = new SolidColorBrush(
                    exportMessageError ? ErrorColor : SuccessColor),
                BorderThickness = new Thickness(2, 0, 0, 0),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var content = new StackPanel();
            content.Children.Add(MakeText(
                exportMessageTitle,
                BoostixDesignTokens.MetadataTextSize,
                TextColor,
                semiboldFont,
                FontWeights.Bold));
            var detail = MakeText(
                exportMessageDetail,
                BoostixDesignTokens.MetadataTextSize,
                SecondaryColor,
                regularFont,
                FontWeights.Normal);
            detail.TextWrapping = TextWrapping.Wrap;
            detail.Margin = new Thickness(0, 3, 0, 0);
            content.Children.Add(detail);
            notice.Child = content;
            AutomationProperties.SetLiveSetting(
                notice,
                exportMessageError
                    ? AutomationLiveSetting.Assertive
                    : AutomationLiveSetting.Polite);
            AutomationProperties.SetName(
                notice,
                exportMessageTitle + ". " + exportMessageDetail);
            return notice;
        }

        private FrameworkElement BuildCrashInsight(BoostCrashInsight insight)
        {
            var host = new Border
            {
                Background = new SolidColorBrush(SurfaceColor),
                BorderBrush = new SolidColorBrush(
                    insight.Category == BoostCrashCategory.MemoryPressure
                        ? WarningColor
                        : ErrorColor),
                BorderThickness = new Thickness(2, 0, 0, 0),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var content = new StackPanel();
            content.Children.Add(MakeText(
                insight.Title,
                BoostixDesignTokens.MetadataTextSize,
                TextColor,
                semiboldFont,
                FontWeights.Bold));

            var evidence = MakeText(
                insight.Evidence,
                BoostixDesignTokens.MetadataTextSize,
                insight.Category == BoostCrashCategory.MemoryPressure
                    ? WarningColor
                    : ErrorColor,
                semiboldFont,
                FontWeights.SemiBold);
            evidence.TextWrapping = TextWrapping.Wrap;
            evidence.Margin = new Thickness(0, 3, 0, 0);
            content.Children.Add(evidence);

            var summary = MakeText(
                insight.Summary,
                BoostixDesignTokens.MetadataTextSize,
                SecondaryColor,
                regularFont,
                FontWeights.Normal);
            summary.TextWrapping = TextWrapping.Wrap;
            summary.Margin = new Thickness(0, 4, 0, 0);
            content.Children.Add(summary);

            foreach (string step in insight.Steps.Take(3))
            {
                var stepText = MakeText(
                    "• " + step,
                    BoostixDesignTokens.MetadataTextSize,
                    MutedColor,
                    regularFont,
                    FontWeights.Normal);
                stepText.TextWrapping = TextWrapping.Wrap;
                stepText.Margin = new Thickness(0, 4, 0, 0);
                content.Children.Add(stepText);
            }
            host.Child = content;
            AutomationProperties.SetName(
                host,
                insight.Title + ". " + insight.Summary);
            return host;
        }

        private FrameworkElement BuildResourceSnapshot(DiagnosticSnapshot snapshot)
        {
            var section = new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 10)
            };
            var heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.Children.Add(MakeText(
                "РЕСУРСЫ СЕЙЧАС",
                BoostixDesignTokens.MetadataTextSize,
                TextColor,
                semiboldFont,
                FontWeights.Bold));
            var pressure = MakeText(
                FormatPressure(snapshot.Pressure.ToString()),
                BoostixDesignTokens.MetadataTextSize,
                GetPressureColor(snapshot.Pressure),
                semiboldFont,
                FontWeights.Bold);
            pressure.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(pressure, 1);
            heading.Children.Add(pressure);
            section.Children.Add(heading);

            var metrics = BuildResourceMetricGrid(new[]
            {
                new[]
                {
                    "СВОБОДНО RAM",
                    snapshot.MemoryAvailable
                        ? FormatBytesCompact(snapshot.PhysicalAvailableBytes) +
                            " / " + FormatBytesCompact(snapshot.PhysicalTotalBytes)
                        : "НЕДОСТУПНО"
                },
                new[]
                {
                    "ЗАПАС COMMIT",
                    snapshot.MemoryAvailable
                        ? FormatBytesCompact(snapshot.CommitHeadroomBytes) +
                            " / " + FormatBytesCompact(snapshot.CommitLimitBytes)
                        : "НЕДОСТУПНО"
                },
                new[]
                {
                    "ВИДЕОПАМЯТЬ",
                    snapshot.GpuUsageAvailable &&
                        snapshot.GpuTotalAvailable &&
                        snapshot.GpuDedicatedTotalBytes > 0
                        ? FormatBytesCompact(snapshot.GpuDedicatedUsageBytes) +
                            " / " +
                            FormatBytesCompact(snapshot.GpuDedicatedTotalBytes)
                        : "НЕДОСТУПНО"
                },
                new[]
                {
                    "ФАЙЛ ПОДКАЧКИ",
                    snapshot.PageFileAvailable
                        ? FormatBytesCompact(snapshot.PageFileUsedBytes) +
                            " / " + FormatBytesCompact(snapshot.PageFileAllocatedBytes)
                        : FormatUnavailablePageFile(snapshot)
                }
            });
            metrics.Margin = new Thickness(0, 6, 0, 0);
            section.Children.Add(metrics);

            if (!string.IsNullOrWhiteSpace(snapshot.PressureReason))
            {
                var reason = MakeText(
                    TranslatePressureReason(snapshot),
                    BoostixDesignTokens.MetadataTextSize,
                    MutedColor,
                    regularFont,
                    FontWeights.Normal);
                reason.TextWrapping = TextWrapping.Wrap;
                reason.Margin = new Thickness(0, 5, 0, 0);
                section.Children.Add(reason);
            }
            AutomationProperties.SetName(
                section,
                "Текущее состояние памяти. " +
                FormatPressure(snapshot.Pressure.ToString()));
            return section;
        }

        private FrameworkElement BuildSessionResourceSummary(BoostSessionReport report)
        {
            bool hasData = report.MinimumAvailableMemoryBytes > 0 ||
                report.MinimumCommitHeadroomBytes > 0 ||
                report.GpuMemorySamples > 0 ||
                report.PageFileAllocatedBytes > 0;
            if (!hasData)
            {
                return new Border { Height = 0 };
            }

            var section = new StackPanel
            {
                Margin = new Thickness(0, 10, 0, 0)
            };
            section.Children.Add(MakeText(
                "РЕСУРСЫ СЕССИИ",
                BoostixDesignTokens.MetadataTextSize,
                TextColor,
                semiboldFont,
                FontWeights.Bold));
            var metrics = BuildResourceMetricGrid(new[]
            {
                new[]
                {
                    "МИНИМУМ RAM",
                    report.MinimumAvailableMemoryBytes > 0
                        ? FormatBytesCompact(report.MinimumAvailableMemoryBytes)
                        : "НЕТ ДАННЫХ"
                },
                new[]
                {
                    "МИНИМУМ COMMIT",
                    report.MinimumCommitHeadroomBytes > 0
                        ? FormatBytesCompact(report.MinimumCommitHeadroomBytes)
                        : "НЕТ ДАННЫХ"
                },
                new[]
                {
                    "ПИК VRAM",
                    report.GpuMemorySamples > 0 &&
                        report.GpuDedicatedTotalBytes > 0
                        ? FormatBytesCompact(report.PeakGpuDedicatedUsageBytes) +
                            " / " +
                            FormatBytesCompact(report.GpuDedicatedTotalBytes)
                        : "НЕТ ДАННЫХ"
                },
                new[]
                {
                    "ПИК PAGEFILE",
                    report.PageFileAllocatedBytes > 0
                        ? FormatBytesCompact(report.PeakPageFileUsedBytes) +
                            " / " + FormatBytesCompact(report.PageFileAllocatedBytes)
                        : "НЕТ ДАННЫХ"
                }
            });
            metrics.Margin = new Thickness(0, 5, 0, 0);
            section.Children.Add(metrics);
            return section;
        }

        private FrameworkElement BuildPerformanceComparison(
            BoostPerformanceComparison comparison)
        {
            var section = new StackPanel
            {
                Margin = new Thickness(0, 10, 0, 0)
            };
            section.Children.Add(MakeText(
                "СРАВНЕНИЕ С ПРЕДЫДУЩИМ ЗАМЕРОМ",
                BoostixDesignTokens.MetadataTextSize,
                TextColor,
                semiboldFont,
                FontWeights.Bold));
            var metrics = BuildResourceMetricGrid(new[]
            {
                new[]
                {
                    "СРЕДНИЙ FPS (↑ ЛУЧШЕ)",
                    BoostSessionComparison.FormatSigned(
                        comparison.AverageFpsDelta,
                        string.Empty)
                },
                new[]
                {
                    "1% LOW (↑ ЛУЧШЕ)",
                    BoostSessionComparison.FormatSigned(
                        comparison.OnePercentLowFpsDelta,
                        string.Empty)
                },
                new[]
                {
                    "P95 FRAME TIME (↓ ЛУЧШЕ)",
                    BoostSessionComparison.FormatSigned(
                        comparison.P95FrameTimeDeltaMs,
                        " мс")
                },
                new[]
                {
                    "КАДРЫ > 50 МС (↓ ЛУЧШЕ)",
                    (comparison.FramesOver50MsDelta > 0 ? "+" : string.Empty) +
                        comparison.FramesOver50MsDelta.ToString(
                            CultureInfo.CurrentCulture)
                }
            });
            metrics.Margin = new Thickness(0, 5, 0, 0);
            section.Children.Add(metrics);
            return section;
        }

        private Grid BuildResourceMetricGrid(string[][] items)
        {
            var grid = new Grid
            {
                Background = Brushes.Transparent
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            int count = Math.Min(4, items == null ? 0 : items.Length);
            for (int index = 0; index < count; index++)
            {
                string[] item = items[index] ?? new string[0];
                var cell = BuildInlineMetric(
                    item.Length > 0 ? item[0] : string.Empty,
                    item.Length > 1 ? item[1] : string.Empty,
                    index % 2 == 1,
                    index >= 2);
                Grid.SetColumn(cell, index % 2);
                Grid.SetRow(cell, index / 2);
                grid.Children.Add(cell);
            }
            return grid;
        }

        private FrameworkElement BuildInlineMetric(
            string title,
            string value,
            bool rightColumn,
            bool secondRow)
        {
            var host = new Border
            {
                MinHeight = 47,
                BorderBrush = new SolidColorBrush(DividerColor),
                BorderThickness = new Thickness(
                    rightColumn ? 1 : 0,
                    secondRow ? 1 : 0,
                    0,
                    0),
                Padding = new Thickness(
                    rightColumn ? 10 : 0,
                    secondRow ? 7 : 5,
                    6,
                    5)
            };
            var content = new StackPanel();
            content.Children.Add(MakeText(
                title,
                BoostixDesignTokens.MetadataTextSize,
                MutedColor,
                semiboldFont,
                FontWeights.Bold));
            var valueText = MakeText(
                value,
                11.5,
                TextColor,
                semiboldFont,
                FontWeights.Bold);
            valueText.Margin = new Thickness(0, 2, 0, 0);
            content.Children.Add(valueText);
            host.Child = content;
            return host;
        }

        private void UpdateBenchmarkProgressVisuals()
        {
            if (benchmarkNoticeTitleBlock != null)
            {
                benchmarkNoticeTitleBlock.Text = benchmarkTitle ?? string.Empty;
            }
            if (benchmarkNoticeDetailBlock != null)
            {
                benchmarkNoticeDetailBlock.Text = benchmarkDetail ?? string.Empty;
            }
            if (benchmarkProgressFill != null)
            {
                benchmarkProgressFill.Width = 3.2 * benchmarkPercent;
            }
            if (benchmarkButton != null)
            {
                benchmarkButton.Content = "ЗАМЕР " +
                    benchmarkPercent.ToString(CultureInfo.CurrentCulture) + "%";
                benchmarkButton.IsEnabled = false;
            }
        }

        private FrameworkElement BuildSessionSummary(BoostSessionReport report)
        {
            var summary = new Grid
            {
                Margin = new Thickness(0, 0, 0, 5)
            };
            summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TimeSpan duration = (report.EndedUtc ?? DateTime.UtcNow) - report.StartedUtc;
            var durationBlock = BuildMetric(
                "ДЛИТЕЛЬНОСТЬ",
                FormatDuration(duration),
                false);
            Grid.SetColumn(durationBlock, 0);
            durationBlock.Margin = new Thickness(0, 0, 4, 0);
            summary.Children.Add(durationBlock);

            string commitReserve = report.MinimumCommitHeadroomBytes > 0
                ? FormatBytesCompact(report.MinimumCommitHeadroomBytes)
                : "—";
            var commitBlock = BuildMetric(
                "МИНИМАЛЬНЫЙ ЗАПАС COMMIT",
                commitReserve,
                false);
            Grid.SetColumn(commitBlock, 1);
            commitBlock.Margin = new Thickness(4, 0, 0, 0);
            summary.Children.Add(commitBlock);
            return summary;
        }

        private FrameworkElement BuildPerformanceGrid(BoostPerformanceResult result)
        {
            var host = new StackPanel
            {
                Margin = new Thickness(0, 9, 0, 0)
            };
            var title = MakeText(
                "ПОКАДРОВЫЙ ЗАМЕР",
                BoostixDesignTokens.MetadataTextSize,
                TextColor,
                semiboldFont,
                FontWeights.Bold);
            title.Margin = new Thickness(0, 0, 0, 5);
            host.Children.Add(title);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddPerformanceMetric(grid, "СРЕДНИЙ FPS", result.AverageFps.ToString("0.0", CultureInfo.CurrentCulture), 0, 0);
            AddPerformanceMetric(grid, "1% LOW", result.OnePercentLowFps.ToString("0.0", CultureInfo.CurrentCulture), 0, 1);
            AddPerformanceMetric(grid, "P95 FRAME TIME", result.P95FrameTimeMs.ToString("0.0", CultureInfo.CurrentCulture) + " мс", 1, 0);
            AddPerformanceMetric(grid, "P99 FRAME TIME", result.P99FrameTimeMs.ToString("0.0", CultureInfo.CurrentCulture) + " мс", 1, 1);
            AddPerformanceMetric(grid, "КАДРЫ > 50 МС", result.FramesOver50Ms.ToString(CultureInfo.CurrentCulture), 2, 0);
            AddPerformanceMetric(grid, "КАДРЫ > 100 МС", result.FramesOver100Ms.ToString(CultureInfo.CurrentCulture), 2, 1);
            host.Children.Add(grid);
            return host;
        }

        private void AddPerformanceMetric(
            Grid grid,
            string title,
            string value,
            int row,
            int column)
        {
            var metric = BuildMetric(title, value, false);
            metric.Margin = new Thickness(
                column == 0 ? 0 : 4,
                row == 0 ? 0 : 8,
                column == 0 ? 4 : 0,
                0);
            Grid.SetRow(metric, row);
            Grid.SetColumn(metric, column);
            grid.Children.Add(metric);
        }

        private Border BuildMetric(
            string title,
            string value,
            bool positive)
        {
            var host = new Border
            {
                Height = 61,
                Background = new SolidColorBrush(SurfaceColor),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(11, 8, 11, 7)
            };
            var content = new StackPanel();
            content.Children.Add(MakeText(
                title,
                BoostixDesignTokens.MetadataTextSize,
                MutedColor,
                semiboldFont,
                FontWeights.Bold));
            var valueText = MakeText(
                value,
                15,
                positive ? SuccessColor : TextColor,
                semiboldFont,
                FontWeights.Bold);
            valueText.Margin = new Thickness(0, 3, 0, 0);
            content.Children.Add(valueText);
            host.Child = content;
            return host;
        }

        private FrameworkElement BuildActionRow(BoostActionRecord action)
        {
            var row = new Grid
            {
                MinHeight = 42
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            string glyph;
            Color color;
            GetActionVisual(action.Outcome, out glyph, out color);
            var icon = MakeText(
                glyph,
                11,
                color,
                new FontFamily("Segoe UI Symbol"),
                FontWeights.Bold);
            icon.Margin = new Thickness(0, 5, 0, 0);
            Grid.SetColumn(icon, 0);
            Grid.SetRowSpan(icon, 2);
            row.Children.Add(icon);

            var title = MakeText(
                action.Title,
                BoostixDesignTokens.MetadataTextSize,
                TextColor,
                semiboldFont,
                FontWeights.SemiBold);
            title.Margin = new Thickness(0, 4, 0, 0);
            Grid.SetColumn(title, 1);
            row.Children.Add(title);

            if (!string.IsNullOrWhiteSpace(action.Detail))
            {
                var detail = MakeText(
                    action.Detail,
                    BoostixDesignTokens.MetadataTextSize,
                    MutedColor,
                    regularFont,
                    FontWeights.Normal);
                detail.TextWrapping = TextWrapping.Wrap;
                detail.Margin = new Thickness(0, 1, 0, 5);
                Grid.SetColumn(detail, 1);
                Grid.SetRow(detail, 1);
                row.Children.Add(detail);
            }

            var separator = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(DividerColor),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            Grid.SetColumnSpan(separator, 2);
            Grid.SetRowSpan(separator, 2);
            row.Children.Add(separator);
            return row;
        }

        private void RenderHistory()
        {
            subtitle.Text =
                "Последние 10 сеансов производительности и их измерения.";
            if (!string.IsNullOrWhiteSpace(exportMessageTitle))
            {
                pageContent.Children.Add(BuildExportNotice());
            }

            if (sessionHistory.Count == 0)
            {
                pageContent.Children.Add(MakeEmptyState(
                    "ИСТОРИЯ ПОКА ПУСТА",
                    "После сеанса здесь появятся длительность, состояние ресурсов и покадровые замеры."));
            }
            else
            {
                int index = 0;
                foreach (BoostSessionReport report in sessionHistory)
                {
                    Button row = BuildHistoryRow(report, index);
                    pageContent.Children.Add(row);
                    if (preferredFocusButton == null)
                    {
                        preferredFocusButton = row;
                    }
                    index++;
                }
            }

            var export = MakeActionButton("ЭКСПОРТ ДИАГНОСТИКИ", false, false);
            export.Width = 174;
            export.Click += delegate { Raise(ExportDiagnosticsRequested); };
            AutomationProperties.SetName(
                export,
                "Сохранить безопасный диагностический отчёт");
            AutomationProperties.SetAutomationId(
                export,
                "Boostix.Center.HistoryExport");
            footerButtons.Children.Add(export);
            if (preferredFocusButton == null)
            {
                preferredFocusButton = export;
            }
        }

        private Button BuildHistoryRow(BoostSessionReport report, int index)
        {
            var background = new SolidColorBrush(BackgroundColor);
            var button = new Button
            {
                MinHeight = 58,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = background,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 4, 8, 4),
                Cursor = Cursors.Hand,
                Template = MakeFlatButtonTemplate(6),
                FocusVisualStyle = MakeKeyboardFocusVisualStyle(6)
            };
            KeyboardNavigation.SetTabIndex(button, 10 + index);
            AutomationProperties.SetAutomationId(
                button,
                "Boostix.Center.History." +
                index.ToString(CultureInfo.InvariantCulture));

            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            content.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            DateTime localStart = report.StartedUtc.ToLocalTime();
            var title = MakeText(
                localStart.ToString("dd.MM · HH:mm", CultureInfo.CurrentCulture) +
                    "  —  СЕАНС ПРОИЗВОДИТЕЛЬНОСТИ",
                BoostixDesignTokens.MetadataTextSize,
                TextColor,
                semiboldFont,
                FontWeights.Bold);
            title.Margin = new Thickness(0, 4, 8, 0);
            Grid.SetColumn(title, 0);
            content.Children.Add(title);

            string status = FormatSessionStatus(report);
            Color statusColor = !string.IsNullOrWhiteSpace(report.TargetCrashCode)
                ? ErrorColor
                : (string.Equals(
                    report.WorstResourcePressure,
                    DiagnosticPressureLevel.Critical.ToString(),
                    StringComparison.OrdinalIgnoreCase)
                    ? WarningColor
                    : MutedColor);
            var statusText = MakeText(
                status,
                BoostixDesignTokens.MetadataTextSize,
                statusColor,
                semiboldFont,
                FontWeights.Bold);
            statusText.Margin = new Thickness(8, 4, 0, 0);
            statusText.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(statusText, 1);
            content.Children.Add(statusText);

            TimeSpan duration = (report.EndedUtc ?? DateTime.UtcNow) -
                report.StartedUtc;
            string detailText = "Сессия " + FormatDuration(duration);
            if (!string.IsNullOrWhiteSpace(report.WorstResourcePressure))
            {
                detailText += " · ресурсы " +
                    FormatPressure(report.WorstResourcePressure).ToLowerInvariant();
            }
            var detail = MakeText(
                detailText,
                BoostixDesignTokens.MetadataTextSize,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            detail.Margin = new Thickness(0, 2, 8, 4);
            Grid.SetColumn(detail, 0);
            Grid.SetRow(detail, 1);
            content.Children.Add(detail);

            string performance = report.Performance != null &&
                report.Performance.Available
                ? report.Performance.AverageFps.ToString(
                    "0.0",
                    CultureInfo.CurrentCulture) + " FPS"
                : "ОТКРЫТЬ";
            var performanceText = MakeText(
                performance,
                BoostixDesignTokens.MetadataTextSize,
                report.Performance != null && report.Performance.Available
                    ? SuccessColor
                    : SecondaryColor,
                semiboldFont,
                FontWeights.Bold);
            performanceText.Margin = new Thickness(8, 2, 0, 4);
            performanceText.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(performanceText, 1);
            Grid.SetRow(performanceText, 1);
            content.Children.Add(performanceText);

            button.Content = content;
            AutomationProperties.SetName(
                button,
                "Открыть отчёт сессии " +
                localStart.ToString("dd.MM HH:mm", CultureInfo.CurrentCulture));
            button.Click += delegate
            {
                sessionReport = report;
                if (currentPage == CenterPage.Report)
                {
                    RenderCurrentPage();
                }
                else
                {
                    SwitchPage(CenterPage.Report);
                }
            };
            button.MouseEnter += delegate
            {
                AnimateBrush(background, SurfaceColor, 180);
            };
            button.MouseLeave += delegate
            {
                AnimateBrush(background, BackgroundColor, 220);
            };
            return button;
        }

        private static string FormatSessionStatus(BoostSessionReport report)
        {
            if (report == null)
            {
                return "НЕТ ДАННЫХ";
            }
            if (!string.IsNullOrWhiteSpace(report.TargetCrashCode) ||
                string.Equals(
                    report.Status,
                    "TargetCrashed",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "СБОЙ";
            }
            if (string.Equals(report.Status, "Active", StringComparison.OrdinalIgnoreCase))
            {
                return "АКТИВЕН";
            }
            if (string.Equals(report.Status, "Preparing", StringComparison.OrdinalIgnoreCase))
            {
                return "ПОДГОТОВКА";
            }
            if (string.Equals(report.Status, "Interrupted", StringComparison.OrdinalIgnoreCase))
            {
                return "ПРЕРВАНО";
            }
            if (string.Equals(report.Status, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                return "ОШИБКА";
            }
            return "ЗАВЕРШЕНО";
        }

        private void RenderSettings()
        {
            subtitle.Text =
                "Настройки сеанса производительности применяются без перезагрузки.";
            settingsLoading = true;
            try
            {
                pageContent.Children.Add(BuildSettingToggle(
                    "ПРОВЕРКА ПЕРЕД ЗАПУСКОМ",
                    "Показывать предупреждения о питании, памяти и перезагрузке.",
                    settings.CheckBeforeBoost,
                    delegate(bool value) { settings.CheckBeforeBoost = value; }));
                pageContent.Children.Add(BuildSettingToggle(
                    "НЕ ЗАКРЫВАТЬ DISCORD",
                    "Оставить Discord запущенным во время одноразовой подготовки.",
                    settings.KeepDiscord,
                    delegate(bool value) { settings.KeepDiscord = value; }));
                pageContent.Children.Add(BuildSettingToggle(
                    "НЕ ЗАКРЫВАТЬ EPIC GAMES",
                    "Оставить Epic Games Launcher и его web-процессы запущенными.",
                    settings.KeepEpic,
                    delegate(bool value) { settings.KeepEpic = value; }));
                pageContent.Children.Add(BuildSettingToggle(
                    "НЕ ЗАКРЫВАТЬ STEAM",
                    "Оставить Steam и его overlay запущенными.",
                    settings.KeepSteam,
                    delegate(bool value) { settings.KeepSteam = value; }));
                pageContent.Children.Add(BuildSettingToggle(
                    "НЕ ЗАКРЫВАТЬ ONEDRIVE",
                    "Сохранить синхронизацию OneDrive во время сеанса Boostix.",
                    settings.KeepOneDrive,
                    delegate(bool value) { settings.KeepOneDrive = value; }));
                pageContent.Children.Add(BuildSettingToggle(
                    "НЕ ЗАКРЫВАТЬ MICROSOFT TEAMS",
                    "Оставить Teams запущенным.",
                    settings.KeepTeams,
                    delegate(bool value) { settings.KeepTeams = value; }));
                pageContent.Children.Add(BuildSettingToggle(
                    "НЕ ЗАКРЫВАТЬ WALLPAPER ENGINE",
                    "Оставить анимированные обои запущенными.",
                    settings.KeepWallpaper,
                    delegate(bool value) { settings.KeepWallpaper = value; }));
                pageContent.Children.Add(BuildSettingToggle(
                    "НЕ ЗАКРЫВАТЬ NVIDIA OVERLAY",
                    "Сохранить NVIDIA Overlay и запись клипов.",
                    settings.KeepNvidiaOverlay,
                    delegate(bool value) { settings.KeepNvidiaOverlay = value; }));
            }
            finally
            {
                settingsLoading = false;
            }

            var restore = MakeActionButton("ВОССТАНОВИТЬ WINDOWS", false, true);
            restore.Width = 178;
            restore.Click += delegate { Raise(RestoreRequested); };
            AutomationProperties.SetName(
                restore,
                "Открыть безопасное восстановление системных настроек");
            AutomationProperties.SetAutomationId(
                restore,
                "Boostix.Center.Restore");
            footerButtons.Children.Add(restore);
            preferredFocusButton = restore;

            var pagefile = MakeActionButton("ФАЙЛ ПОДКАЧКИ", false, false);
            pagefile.Width = 160;
            pagefile.Margin = new Thickness(8, 0, 0, 0);
            pagefile.Click += delegate
            {
                Raise(OpenPagefileSettingsRequested);
            };
            AutomationProperties.SetName(
                pagefile,
                "Открыть параметры быстродействия Windows и файла подкачки");
            AutomationProperties.SetAutomationId(
                pagefile,
                "Boostix.Center.PagefileSettings");
            footerButtons.Children.Add(pagefile);
        }

        private FrameworkElement BuildSettingToggle(
            string title,
            string detail,
            bool isChecked,
            Action<bool> apply)
        {
            var toggle = new CheckBox
            {
                MinHeight = 52,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Template = MakeTransparentCheckBoxTemplate(),
                IsChecked = isChecked,
                FocusVisualStyle = MakeKeyboardFocusVisualStyle(6)
            };
            AutomationProperties.SetName(toggle, title.ToLowerInvariant());
            AutomationProperties.SetHelpText(toggle, detail);
            AutomationProperties.SetAutomationId(
                toggle,
                "Boostix.Center.Setting." +
                pageContent.Children.Count.ToString(CultureInfo.InvariantCulture));
            KeyboardNavigation.SetTabIndex(
                toggle,
                10 + pageContent.Children.Count);

            var content = new Grid
            {
                UseLayoutRounding = true,
                SnapsToDevicePixels = true,
                ClipToBounds = false
            };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(36 + ToggleSafeGutter)
            });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var titleText = MakeText(
                title,
                BoostixDesignTokens.MetadataTextSize,
                TextColor,
                semiboldFont,
                FontWeights.Bold);
            titleText.Margin = new Thickness(0, 5, 8, 0);
            Grid.SetColumn(titleText, 0);
            Grid.SetRow(titleText, 0);
            content.Children.Add(titleText);

            var detailText = MakeText(
                detail,
                BoostixDesignTokens.MetadataTextSize,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            detailText.TextWrapping = TextWrapping.Wrap;
            detailText.Margin = new Thickness(0, 2, 8, 6);
            Grid.SetColumn(detailText, 0);
            Grid.SetRow(detailText, 1);
            content.Children.Add(detailText);

            var trackBrush = new SolidColorBrush(
                isChecked ? AccentColor : ButtonColor);
            var track = new Border
            {
                Width = 36,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = trackBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0),
                UseLayoutRounding = true,
                SnapsToDevicePixels = true,
                ClipToBounds = false
            };
            Grid.SetColumn(track, 1);
            Grid.SetRowSpan(track, 2);

            var knobBrush = new SolidColorBrush(
                isChecked
                    ? BoostixDesignTokens.ToggleKnobOn
                    : BoostixDesignTokens.ToggleKnobOff);
            var knob = new Ellipse
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(3, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = knobBrush,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true
            };
            var knobTranslation = new TranslateTransform(isChecked ? 14 : 0, 0);
            knob.RenderTransform = knobTranslation;
            track.Child = knob;
            content.Children.Add(track);

            var separator = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(DividerColor),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            Grid.SetColumnSpan(separator, 2);
            Grid.SetRowSpan(separator, 2);
            content.Children.Add(separator);

            toggle.Tag = new ToggleVisuals
            {
                TrackBrush = trackBrush,
                KnobBrush = knobBrush,
                KnobTranslation = knobTranslation
            };
            toggle.Content = content;
            toggle.Checked += delegate
            {
                AnimateToggle(toggle);
                apply(true);
                if (!settingsLoading)
                {
                    Raise(SettingsChanged);
                }
            };
            toggle.Unchecked += delegate
            {
                AnimateToggle(toggle);
                apply(false);
                if (!settingsLoading)
                {
                    Raise(SettingsChanged);
                }
            };
            toggle.MouseEnter += delegate { AnimateToggle(toggle); };
            toggle.MouseLeave += delegate { AnimateToggle(toggle); };
            return toggle;
        }

        private static void AnimateToggle(CheckBox toggle)
        {
            var visuals = toggle.Tag as ToggleVisuals;
            if (visuals == null)
            {
                return;
            }
            bool active = toggle.IsChecked == true;
            Color targetColor = active
                ? AccentColor
                : (toggle.IsMouseOver ? HoverColor : ButtonColor);
            Color knobColor = active || toggle.IsMouseOver
                ? BoostixDesignTokens.ToggleKnobOn
                : BoostixDesignTokens.ToggleKnobOff;
            double targetX = active ? 14 : 0;
            if (SystemParameters.HighContrast ||
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
                new ColorAnimation(targetColor, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = ease
                });
            visuals.KnobBrush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(knobColor, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = ease
                });
            visuals.KnobTranslation.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = ease
                });
        }

        private Border MakeEmptyState(string title, string detail)
        {
            var host = new Border
            {
                Background = Brushes.Transparent,
                MinHeight = 180,
                Padding = new Thickness(20)
            };
            var content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var titleText = MakeText(
                title,
                12,
                TextColor,
                semiboldFont,
                FontWeights.Bold);
            titleText.TextAlignment = TextAlignment.Center;
            content.Children.Add(titleText);
            var detailText = MakeText(
                detail,
                BoostixDesignTokens.MetadataTextSize,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            detailText.TextAlignment = TextAlignment.Center;
            detailText.TextWrapping = TextWrapping.Wrap;
            detailText.MaxWidth = 290;
            detailText.Margin = new Thickness(0, 6, 0, 0);
            content.Children.Add(detailText);
            host.Child = content;
            return host;
        }

        private Button MakeActionButton(
            string text,
            bool primary,
            bool destructive)
        {
            var background = new SolidColorBrush(
                primary ? AccentColor : ButtonColor);
            var foreground = new SolidColorBrush(
                primary ? AccentForegroundColor : TextColor);
            var border = new SolidColorBrush(
                primary ? AccentColor : BorderColor);
            var button = new Button
            {
                Height = BoostixDesignTokens.MinimumActionHeight,
                Padding = new Thickness(13, 0, 13, 0),
                Background = background,
                Foreground = foreground,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                FontFamily = semiboldFont,
                FontSize = BoostixDesignTokens.BodyTextSize,
                FontWeight = FontWeights.Bold,
                Cursor = Cursors.Hand,
                Template = MakeFlatButtonTemplate(6),
                FocusVisualStyle = MakeKeyboardFocusVisualStyle(6),
                Content = text
            };
            var lift = new TranslateTransform();
            button.RenderTransform = lift;

            button.MouseEnter += delegate
            {
                Color target = destructive
                    ? DestructiveColor
                    : (primary ? AccentPressedColor : AccentColor);
                AnimateBrush(background, target, 210);
                AnimateBrush(border, target, 210);
                AnimateBrush(foreground, AccentForegroundColor, 210);
                AnimateLift(
                    lift,
                    -BoostixDesignTokens.HoverLift,
                    BoostixDesignTokens.MotionStandardMilliseconds);
            };
            button.MouseLeave += delegate
            {
                AnimateBrush(background, primary ? AccentColor : ButtonColor, 240);
                AnimateBrush(border, primary ? AccentColor : BorderColor, 240);
                AnimateBrush(
                    foreground,
                    primary ? AccentForegroundColor : TextColor,
                    240);
                AnimateLift(
                    lift,
                    0,
                    BoostixDesignTokens.MotionSlowMilliseconds);
            };
            return button;
        }

        private static ControlTemplate MakeFlatButtonTemplate(double radius)
        {
            var template = new ControlTemplate(typeof(Button));
            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.Name = "Chrome";
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            chrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            presenter.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            chrome.AppendChild(presenter);
            template.VisualTree = chrome;
            return template;
        }

        private static ControlTemplate MakeTransparentCheckBoxTemplate()
        {
            var template = new ControlTemplate(typeof(CheckBox));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(0));

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            border.AppendChild(presenter);
            template.VisualTree = border;
            return template;
        }

        private static Style MakeKeyboardFocusVisualStyle(double radius)
        {
            var style = new Style(typeof(Control));
            var template = new ControlTemplate(typeof(Control));
            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.SetResourceReference(
                Border.BorderBrushProperty,
                BoostixDesignTokens.FocusBrushKey);
            chrome.SetValue(
                Border.BorderThicknessProperty,
                new Thickness(1));
            chrome.SetValue(
                Border.CornerRadiusProperty,
                new CornerRadius(radius));
            chrome.SetValue(
                Border.MarginProperty,
                new Thickness(-2));
            chrome.SetValue(
                UIElement.IsHitTestVisibleProperty,
                false);
            template.VisualTree = chrome;
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }

        private static Style MakeBoostixVerticalScrollBarStyle()
        {
            string trackColor = SystemParameters.HighContrast
                ? ColorToXaml(BackgroundColor)
                : "#257C3AED";
            string thumbColor = SystemParameters.HighContrast
                ? ColorToXaml(AccentColor)
                : ProductBrand.AccentVisualHex;
            string thumbHoverColor = SystemParameters.HighContrast
                ? ColorToXaml(AccentColor)
                : ProductBrand.AccentTextHex;
            string thumbDragColor = SystemParameters.HighContrast
                ? ColorToXaml(AccentColor)
                : ProductBrand.AccentHex;
            string xaml =
                "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" " +
                "TargetType=\"{x:Type ScrollBar}\">" +
                "<Setter Property=\"Width\" Value=\"16\"/>" +
                "<Setter Property=\"MinWidth\" Value=\"16\"/>" +
                "<Setter Property=\"Background\" Value=\"Transparent\"/>" +
                "<Setter Property=\"BorderThickness\" Value=\"0\"/>" +
                "<Setter Property=\"Focusable\" Value=\"False\"/>" +
                "<Setter Property=\"Template\">" +
                "<Setter.Value>" +
                "<ControlTemplate TargetType=\"{x:Type ScrollBar}\">" +
                "<Border Background=\"" + trackColor + "\" CornerRadius=\"8\" " +
                "SnapsToDevicePixels=\"True\">" +
                "<Track x:Name=\"PART_Track\" IsDirectionReversed=\"True\" Focusable=\"False\">" +
                "<Track.DecreaseRepeatButton>" +
                "<RepeatButton Command=\"{x:Static ScrollBar.PageUpCommand}\" " +
                "Focusable=\"False\" IsTabStop=\"False\" Opacity=\"0\">" +
                "<RepeatButton.Template>" +
                "<ControlTemplate TargetType=\"{x:Type RepeatButton}\">" +
                "<Border Background=\"Transparent\"/>" +
                "</ControlTemplate>" +
                "</RepeatButton.Template>" +
                "</RepeatButton>" +
                "</Track.DecreaseRepeatButton>" +
                "<Track.Thumb>" +
                "<Thumb Width=\"6\" MinHeight=\"40\" HorizontalAlignment=\"Center\" " +
                "Background=\"" + thumbColor + "\" Focusable=\"False\">" +
                "<Thumb.Template>" +
                "<ControlTemplate TargetType=\"{x:Type Thumb}\">" +
                "<Border x:Name=\"ThumbChrome\" Background=\"{TemplateBinding Background}\" " +
                "CornerRadius=\"3\"/>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property=\"IsMouseOver\" Value=\"True\">" +
                "<Setter TargetName=\"ThumbChrome\" Property=\"Background\" Value=\"" +
                    thumbHoverColor + "\"/>" +
                "</Trigger>" +
                "<Trigger Property=\"IsDragging\" Value=\"True\">" +
                "<Setter TargetName=\"ThumbChrome\" Property=\"Background\" Value=\"" +
                    thumbDragColor + "\"/>" +
                "</Trigger>" +
                "<Trigger Property=\"IsEnabled\" Value=\"False\">" +
                "<Setter TargetName=\"ThumbChrome\" Property=\"Opacity\" Value=\"0.35\"/>" +
                "</Trigger>" +
                "</ControlTemplate.Triggers>" +
                "</ControlTemplate>" +
                "</Thumb.Template>" +
                "</Thumb>" +
                "</Track.Thumb>" +
                "<Track.IncreaseRepeatButton>" +
                "<RepeatButton Command=\"{x:Static ScrollBar.PageDownCommand}\" " +
                "Focusable=\"False\" IsTabStop=\"False\" Opacity=\"0\">" +
                "<RepeatButton.Template>" +
                "<ControlTemplate TargetType=\"{x:Type RepeatButton}\">" +
                "<Border Background=\"Transparent\"/>" +
                "</ControlTemplate>" +
                "</RepeatButton.Template>" +
                "</RepeatButton>" +
                "</Track.IncreaseRepeatButton>" +
                "</Track>" +
                "</Border>" +
                "</ControlTemplate>" +
                "</Setter.Value>" +
                "</Setter>" +
                "</Style>";

            return (Style)XamlReader.Parse(xaml);
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

        private void PageScrollerPreviewMouseWheel(
            object sender,
            MouseWheelEventArgs args)
        {
            if (args.Delta == 0 ||
                pageScroller.ScrollableHeight <= 0 ||
                SystemParameters.WheelScrollLines == 0)
            {
                return;
            }

            double baseTarget = smoothScrollAnimating
                ? smoothScrollTarget
                : pageScroller.VerticalOffset;
            double target = CalculateSmoothScrollTarget(
                pageScroller.VerticalOffset,
                smoothScrollTarget,
                args.Delta,
                pageScroller.ScrollableHeight,
                pageScroller.ViewportHeight,
                SystemParameters.WheelScrollLines,
                smoothScrollAnimating);
            if (Math.Abs(target - baseTarget) < 0.01)
            {
                return;
            }

            args.Handled = true;
            double currentOffset = pageScroller.VerticalOffset;
            int generation = ++smoothScrollGeneration;
            smoothScrollTarget = target;

            scrollAnimationProxy.BeginAnimation(
                ScrollAnimationProxy.OffsetProperty,
                null);
            scrollAnimationProxy.Offset = currentOffset;

            if (SystemParameters.HighContrast ||
                !SystemParameters.ClientAreaAnimation)
            {
                scrollAnimationProxy.Offset = target;
                smoothScrollAnimating = false;
                return;
            }

            smoothScrollAnimating = true;
            var animation = new DoubleAnimation(
                currentOffset,
                target,
                TimeSpan.FromMilliseconds(175))
            {
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut
                },
                FillBehavior = FillBehavior.Stop
            };
            animation.Completed += delegate
            {
                if (generation != smoothScrollGeneration)
                {
                    return;
                }

                scrollAnimationProxy.BeginAnimation(
                    ScrollAnimationProxy.OffsetProperty,
                    null);
                scrollAnimationProxy.Offset = target;
                smoothScrollAnimating = false;
            };
            scrollAnimationProxy.BeginAnimation(
                ScrollAnimationProxy.OffsetProperty,
                animation,
                HandoffBehavior.SnapshotAndReplace);
        }

        internal static double CalculateSmoothScrollTarget(
            double currentOffset,
            double pendingTarget,
            int wheelDelta,
            double scrollableHeight,
            double viewportHeight,
            int wheelScrollLines,
            bool isAnimating)
        {
            double upperBound = Math.Max(0, scrollableHeight);
            double origin = isAnimating ? pendingTarget : currentOffset;
            if (wheelDelta == 0 || upperBound <= 0 || wheelScrollLines == 0)
            {
                return Math.Max(0, Math.Min(upperBound, origin));
            }

            double step = wheelScrollLines < 0
                ? Math.Max(48, viewportHeight * 0.82)
                : Math.Max(36, Math.Min(96, wheelScrollLines * 18.0));
            double target = origin -
                (((double)wheelDelta / Mouse.MouseWheelDeltaForOneLine) * step);
            return Math.Max(0, Math.Min(upperBound, target));
        }

        private void CancelSmoothMouseWheelScroll()
        {
            if (!smoothScrollAnimating)
            {
                return;
            }

            double currentOffset = pageScroller.VerticalOffset;
            ++smoothScrollGeneration;
            scrollAnimationProxy.BeginAnimation(
                ScrollAnimationProxy.OffsetProperty,
                null);
            scrollAnimationProxy.Offset = currentOffset;
            smoothScrollTarget = currentOffset;
            smoothScrollAnimating = false;
        }

        private static void AnimateBrush(
            SolidColorBrush brush,
            Color target,
            int milliseconds)
        {
            if (SystemParameters.HighContrast ||
                !SystemParameters.ClientAreaAnimation)
            {
                brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                brush.Color = target;
                return;
            }
            brush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(target, TimeSpan.FromMilliseconds(milliseconds))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                });
        }

        private static void AnimateLift(
            TranslateTransform transform,
            double target,
            int milliseconds)
        {
            if (SystemParameters.HighContrast ||
                !SystemParameters.ClientAreaAnimation)
            {
                transform.BeginAnimation(TranslateTransform.YProperty, null);
                transform.Y = SystemParameters.HighContrast ? 0 : target;
                return;
            }
            transform.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(target, TimeSpan.FromMilliseconds(milliseconds))
                {
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                });
        }

        private void FocusPreferredButton()
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (preferredFocusButton != null &&
                    preferredFocusButton.IsEnabled &&
                    preferredFocusButton.IsVisible)
                {
                    preferredFocusButton.Focus();
                    Keyboard.Focus(preferredFocusButton);
                }
            }));
        }

        private void OverlayPreviewKeyDown(object sender, KeyEventArgs e)
        {
            HandleKey(e);
        }

        private static void GetSeverityVisual(
            BoostCheckSeverity severity,
            out string glyph,
            out Color color)
        {
            if (severity == BoostCheckSeverity.Pass)
            {
                glyph = "✓";
                color = SuccessColor;
            }
            else if (severity == BoostCheckSeverity.Warning)
            {
                glyph = "!";
                color = WarningColor;
            }
            else if (severity == BoostCheckSeverity.Blocked)
            {
                glyph = "×";
                color = ErrorColor;
            }
            else if (severity == BoostCheckSeverity.Info)
            {
                glyph = "i";
                color = AccentColor;
            }
            else
            {
                glyph = "?";
                color = MutedColor;
            }
        }

        private static void GetActionVisual(
            BoostActionOutcome outcome,
            out string glyph,
            out Color color)
        {
            if (outcome == BoostActionOutcome.Changed ||
                outcome == BoostActionOutcome.Restored)
            {
                glyph = "✓";
                color = SuccessColor;
            }
            else if (outcome == BoostActionOutcome.Failed)
            {
                glyph = "×";
                color = ErrorColor;
            }
            else if (outcome == BoostActionOutcome.Preserved ||
                     outcome == BoostActionOutcome.ExternalOverridePreserved)
            {
                glyph = "•";
                color = AccentColor;
            }
            else
            {
                glyph = "–";
                color = MutedColor;
            }
        }

        private static string FormatPressure(string value)
        {
            DiagnosticPressureLevel pressure;
            if (!Enum.TryParse(value, true, out pressure))
            {
                pressure = DiagnosticPressureLevel.Unavailable;
            }
            switch (pressure)
            {
                case DiagnosticPressureLevel.Normal:
                    return "НОРМА";
                case DiagnosticPressureLevel.Elevated:
                    return "ПОВЫШЕННАЯ НАГРУЗКА";
                case DiagnosticPressureLevel.Critical:
                    return "КРИТИЧЕСКАЯ НАГРУЗКА";
                default:
                    return "НЕТ ДАННЫХ";
            }
        }

        private static Color GetPressureColor(DiagnosticPressureLevel pressure)
        {
            switch (pressure)
            {
                case DiagnosticPressureLevel.Normal:
                    return SuccessColor;
                case DiagnosticPressureLevel.Elevated:
                    return WarningColor;
                case DiagnosticPressureLevel.Critical:
                    return ErrorColor;
                default:
                    return MutedColor;
            }
        }

        private static string TranslatePressureReason(DiagnosticSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return "Показатели ресурсов недоступны.";
            }
            if (snapshot.Pressure == DiagnosticPressureLevel.Critical)
            {
                if (snapshot.MemoryAvailable &&
                    snapshot.CommitHeadroomBytes <=
                        Math.Max(
                            DiagnosticPressureClassifier.OneGibibyte,
                            snapshot.CommitLimitBytes / 20))
                {
                    return "Запас commit почти исчерпан — проверьте файл подкачки и свободное место на системном диске.";
                }
                if (snapshot.GpuUsageAvailable &&
                    snapshot.GpuTotalAvailable &&
                    snapshot.GpuDedicatedTotalBytes > 0 &&
                    (double)snapshot.GpuDedicatedUsageBytes /
                        snapshot.GpuDedicatedTotalBytes >= 0.95)
                {
                    return "Системное использование видеопамяти близко к ёмкости выбранного адаптера — высокое качество текстур может вызвать статтеры.";
                }
                return "Свободной физической памяти осталось критически мало.";
            }
            if (snapshot.Pressure == DiagnosticPressureLevel.Elevated)
            {
                return "Запас ресурсов ниже рекомендуемого; закройте тяжёлые фоновые приложения перед игрой.";
            }
            if (snapshot.Pressure == DiagnosticPressureLevel.Normal)
            {
                return "Запас физической памяти и commit находится в норме.";
            }
            return "Часть показателей недоступна в этой версии Windows или драйвера.";
        }

        private static string FormatBytesCompact(long bytes)
        {
            if (bytes < 0)
            {
                return "—";
            }
            if (bytes >= 1073741824L)
            {
                return (bytes / 1073741824d).ToString(
                    "0.0",
                    CultureInfo.CurrentCulture) + " ГБ";
            }
            if (bytes >= 1048576L)
            {
                return (bytes / 1048576d).ToString(
                    "0",
                    CultureInfo.CurrentCulture) + " МБ";
            }
            return bytes > 0
                ? (bytes / 1024d).ToString(
                    "0",
                    CultureInfo.CurrentCulture) + " КБ"
                : "0 МБ";
        }

        private static string FormatUnavailablePageFile(
            DiagnosticSnapshot snapshot)
        {
            if (snapshot != null &&
                string.Equals(
                    snapshot.PageFileError,
                    "No active Windows page file was reported.",
                    StringComparison.Ordinal))
            {
                return "НЕ АКТИВЕН";
            }
            return "НЕДОСТУПНО";
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1)
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    "{0}:{1:00}:{2:00}",
                    (int)duration.TotalHours,
                    duration.Minutes,
                    duration.Seconds);
            }
            return string.Format(
                CultureInfo.CurrentCulture,
                "{0}:{1:00}",
                Math.Max(0, (int)duration.TotalMinutes),
                Math.Max(0, duration.Seconds));
        }

        private static TextBlock MakeText(
            string text,
            double size,
            Color color,
            FontFamily font,
            FontWeight weight)
        {
            return new TextBlock
            {
                Text = text ?? string.Empty,
                FontSize = Math.Max(
                    BoostixDesignTokens.MetadataTextSize,
                    size),
                FontFamily = font,
                FontWeight = weight,
                Foreground = new SolidColorBrush(color),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
        }

        private static void Raise(EventHandler handler)
        {
            if (handler != null)
            {
                handler(null, EventArgs.Empty);
            }
        }
    }
}
