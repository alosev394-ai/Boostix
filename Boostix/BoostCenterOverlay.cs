using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
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

    /// <summary>
    /// The application shell: a navigation sidebar plus the page host. The
    /// Home page hosts the Boost controls owned by the main window; every
    /// other section is rendered here from the data the window supplies.
    /// </summary>
    internal sealed class BoostCenterOverlay : Grid
    {
        internal const int PageTransitionExitMilliseconds = 90;
        internal const int PageTransitionEnterMilliseconds = 130;
        internal const int PageTransitionTotalMilliseconds =
            PageTransitionExitMilliseconds + PageTransitionEnterMilliseconds;
        private const double OuterContentInset = BoostixDesignTokens.ContentInset;
        private const double ScrollSafeInset = 18;
        private const double ScrollBarWidth = 16;
        private const double ToggleSafeGutter = 12;
        private const double NavigationItemHeight = 42;
        private const double ChromeReservedWidth = 96;

        private enum CenterPage
        {
            Home,
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

        private sealed class NavigationVisuals
        {
            public SolidColorBrush Background;
            public SolidColorBrush Label;
            public SolidColorBrush Icon;
            public Border Indicator;
            public ScaleTransform IndicatorScale;
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
        private static Color SidebarColor { get { return BoostixDesignTokens.Sidebar; } }
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
        private static Color ErrorColor { get { return BoostixDesignTokens.Error; } }
        private static Color SuccessColor { get { return BoostixDesignTokens.Success; } }
        private static Color WarningColor { get { return BoostixDesignTokens.Warning; } }

        private readonly FontFamily regularFont;
        private readonly FontFamily semiboldFont;
        private readonly Grid contentRoot;
        private readonly Border sidebar;
        private readonly StackPanel headerStack;
        private readonly Grid pageArea;
        private readonly Border homeHost;
        private readonly Grid pageStack;
        private readonly StackPanel pageContent;
        private readonly ScrollViewer pageScroller;
        private readonly ScrollAnimationProxy scrollAnimationProxy;
        private readonly Border footerBar;
        private readonly StackPanel footerButtons;
        private TextBlock headerTitle;
        private BoostCenterTabStrip tabStrip;
        private TextBlock subtitle;
        private StackPanel reportStack;
        private Border sidebarStatusCard;
        private readonly Dictionary<CenterPage, BoostCenterTabButton> tabButtons =
            new Dictionary<CenterPage, BoostCenterTabButton>();
        private readonly Dictionary<CenterPage, NavigationVisuals> navigationVisuals =
            new Dictionary<CenterPage, NavigationVisuals>();
        private readonly TranslateTransform headerTranslation =
            new TranslateTransform();
        private readonly TranslateTransform pageTranslation =
            new TranslateTransform();

        private CenterPage currentPage = CenterPage.Home;
        private CenterPage renderedPage = CenterPage.Home;
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
        private Grid benchmarkProgressHost;
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
            regularFont = normalFont ?? BoostixDesignTokens.TextFontFamily;
            semiboldFont = boldFont ?? BoostixDesignTokens.DisplayFontFamily;

            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            Background = new SolidColorBrush(BackgroundColor);
            Focusable = true;
            KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.Cycle);
            AutomationProperties.SetName(this, "Центр Boostix");
            AutomationProperties.SetAutomationId(
                this,
                "Boostix.Center");

            ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(BoostixDesignTokens.SidebarWidth)
            });
            ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });

            sidebar = BuildSidebar();
            Grid.SetColumn(sidebar, 0);
            Children.Add(sidebar);

            contentRoot = new Grid
            {
                Margin = new Thickness(OuterContentInset, 0, OuterContentInset, 0)
            };
            contentRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentRoot.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1, GridUnitType.Star)
            });
            Grid.SetColumn(contentRoot, 1);
            Children.Add(contentRoot);

            headerStack = BuildHeader();
            headerStack.RenderTransform = headerTranslation;
            Grid.SetRow(headerStack, 0);
            contentRoot.Children.Add(headerStack);

            pageArea = new Grid
            {
                RenderTransform = pageTranslation
            };
            Grid.SetRow(pageArea, 1);
            contentRoot.Children.Add(pageArea);

            homeHost = new Border
            {
                Margin = new Thickness(0, 0, 0, OuterContentInset)
            };
            AutomationProperties.SetAutomationId(homeHost, "Boostix.Center.Home");
            pageArea.Children.Add(homeHost);

            pageStack = new Grid
            {
                Visibility = Visibility.Collapsed
            };
            pageStack.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1, GridUnitType.Star)
            });
            pageStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            pageArea.Children.Add(pageStack);

            pageContent = new StackPanel
            {
                Margin = new Thickness(0, 0, ScrollSafeInset, 18),
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
                Margin = new Thickness(0, 0, -ScrollSafeInset, 0),
                Focusable = false
            };
            AutomationProperties.SetName(
                pageScroller,
                "Содержимое раздела Boostix");
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
            DependencyPropertyDescriptor.FromProperty(
                ScrollViewer.ComputedVerticalScrollBarVisibilityProperty,
                typeof(ScrollViewer)).AddValueChanged(
                    pageScroller,
                    delegate { UpdateScrollGutter(); });
            Grid.SetRow(pageScroller, 0);
            pageStack.Children.Add(pageScroller);

            footerButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(
                footerButtons,
                "Действия раздела");
            footerBar = new Border
            {
                BorderBrush = new SolidColorBrush(DividerColor),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 14, 0, 20),
                Child = footerButtons
            };
            AutomationProperties.SetAutomationId(footerBar, "Boostix.Center.Footer");
            Grid.SetRow(footerBar, 1);
            pageStack.Children.Add(footerBar);

            PreviewKeyDown += OverlayPreviewKeyDown;
            Loaded += delegate { RenderCurrentPage(); };
        }

        /// <summary>
        /// The scroller extends into the right content inset; its content keeps
        /// the same right edge as the footer whether or not the bar is shown.
        /// </summary>
        private void UpdateScrollGutter()
        {
            bool visible = pageScroller.ComputedVerticalScrollBarVisibility ==
                Visibility.Visible;
            pageContent.Margin = new Thickness(
                0,
                0,
                visible ? ScrollSafeInset - ScrollBarWidth : ScrollSafeInset,
                18);
        }

        public event EventHandler CloseRequested;
        public event EventHandler HomeNavigated;
        public event EventHandler PageOpening;
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

        /// <summary>
        /// True while a section other than Home is shown.
        /// </summary>
        public bool IsOpen
        {
            get { return currentPage != CenterPage.Home; }
        }

        public bool ConsumesApplicationInput
        {
            get { return IsOpen; }
        }

        public Button HomeTabButton
        {
            get
            {
                BoostCenterTabButton home;
                return tabButtons.TryGetValue(CenterPage.Home, out home)
                    ? home
                    : null;
            }
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
            sidebar.Background = new SolidColorBrush(SidebarColor);
            sidebar.BorderBrush = new SolidColorBrush(DividerColor);
            footerBar.BorderBrush = new SolidColorBrush(DividerColor);
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
                NavigationVisuals visuals;
                if (navigationVisuals.TryGetValue(pair.Key, out visuals))
                {
                    visuals.Background.BeginAnimation(SolidColorBrush.ColorProperty, null);
                    visuals.Label.BeginAnimation(SolidColorBrush.ColorProperty, null);
                    visuals.Icon.BeginAnimation(SolidColorBrush.ColorProperty, null);
                    visuals.Indicator.Background = new SolidColorBrush(TextColor);
                }
                pair.Value.FocusVisualStyle =
                    MakeKeyboardFocusVisualStyle(BoostixDesignTokens.RadiusSmall);
            }

            pageScroller.Resources[typeof(ScrollBar)] =
                MakeBoostixVerticalScrollBarStyle();
            UpdateSidebarStatus();
            FinishPageTransitionImmediately();
            RenderCurrentPage();
            UpdateTabs(false);
        }

        public void SetHomeContent(FrameworkElement content)
        {
            homeHost.Child = content;
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
            UpdateSidebarStatus();
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
                (currentPage == CenterPage.Report ||
                 currentPage == CenterPage.Settings))
            {
                RenderCurrentPage();
            }
        }

        public void OpenHome()
        {
            Close();
        }

        public void OpenReadiness(bool boostDecision)
        {
            Open(CenterPage.Readiness, boostDecision);
        }

        public void OpenReport()
        {
            Open(CenterPage.Report, false);
        }

        public void OpenHistory()
        {
            Open(CenterPage.Report, false);
        }

        public void OpenImpact()
        {
            Open(CenterPage.Impact, false);
        }

        public void OpenProfiles()
        {
            Open(CenterPage.Profiles, false);
        }

        public void OpenSettings()
        {
            Open(CenterPage.Settings, false);
        }

        public void SetBenchmarkProgress(
            string title,
            string detail,
            int percent)
        {
            bool wasBusy = benchmarkBusy;
            benchmarkBusy = true;
            benchmarkNeedsElevation = false;
            benchmarkTitle = title ?? "Тест производительности";
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
            benchmarkTitle = "Нужны права для замера";
            benchmarkDetail = detail ??
                "Windows разрешает покадровую телеметрию только администратору или участнику Performance Log Users.";
            benchmarkPercent = 0;
            Open(CenterPage.Report, false);
        }

        public void SetBenchmarkMessage(
            string title,
            string detail,
            bool isError)
        {
            benchmarkBusy = false;
            benchmarkNeedsElevation = false;
            benchmarkTitle = title ?? (isError ? "Замер не выполнен" : "Замер завершён");
            benchmarkDetail = detail ?? string.Empty;
            benchmarkPercent = isError ? -1 : 100;
            Open(CenterPage.Report, false);
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
            if (e == null)
            {
                return;
            }

            if (e.Key == Key.Tab &&
                (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                bool reverse =
                    (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                int pageCount = Enum.GetValues(typeof(CenterPage)).Length;
                int next = ((int)currentPage + (reverse ? pageCount - 1 : 1)) %
                    pageCount;
                SwitchPage((CenterPage)next, true);
                e.Handled = true;
                return;
            }
            if (!IsOpen)
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
        }

        private Border BuildSidebar()
        {
            var host = new Border
            {
                Background = new SolidColorBrush(SidebarColor),
                BorderBrush = new SolidColorBrush(DividerColor),
                BorderThickness = new Thickness(0, 0, 1, 0)
            };
            AutomationProperties.SetAutomationId(host, "Boostix.Sidebar");
            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1, GridUnitType.Star)
            });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            host.Child = layout;

            FrameworkElement brand = BuildBrand();
            Grid.SetRow(brand, 0);
            layout.Children.Add(brand);

            BoostCenterTabStrip tabs = BuildTabs();
            tabs.Margin = new Thickness(12, 26, 12, 0);
            Grid.SetRow(tabs, 1);
            layout.Children.Add(tabs);

            sidebarStatusCard = new Border
            {
                Margin = new Thickness(12, 0, 12, 12)
            };
            Grid.SetRow(sidebarStatusCard, 3);
            layout.Children.Add(sidebarStatusCard);

            var watermark = MakeText(
                "by Silas Suspect",
                BoostixDesignTokens.OverlineTextSize,
                MutedColor,
                semiboldFont,
                FontWeights.Normal);
            watermark.Margin = new Thickness(22, 0, 16, 18);
            watermark.Opacity = 0.8;
            watermark.IsHitTestVisible = false;
            AutomationProperties.SetName(watermark, "by Silas Suspect");
            AutomationProperties.SetAutomationId(watermark, "Boostix.Watermark");
            Grid.SetRow(watermark, 4);
            layout.Children.Add(watermark);
            UpdateSidebarStatus();
            return host;
        }

        private FrameworkElement BuildBrand()
        {
            var brand = new Grid
            {
                Margin = new Thickness(18, 22, 12, 0)
            };
            brand.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            brand.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });

            FrameworkElement mark = BoostixUi.BrandMark(36);
            mark.VerticalAlignment = VerticalAlignment.Center;
            brand.Children.Add(mark);

            var words = new StackPanel
            {
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var name = MakeText(
                ProductBrand.ProductName,
                19,
                TextColor,
                semiboldFont,
                FontWeights.SemiBold);
            AutomationProperties.SetAutomationId(name, "Boostix.Title");
            words.Children.Add(name);
            string versionLabel = string.IsNullOrEmpty(ProductBrand.ReleaseLabel)
                ? GetApplicationVersion()
                : GetApplicationVersion() + "  " + ProductBrand.ReleaseLabel;
            var version = MakeText(
                versionLabel,
                BoostixDesignTokens.MetadataTextSize,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            version.Margin = new Thickness(0, 1, 0, 0);
            AutomationProperties.SetName(
                version,
                "Версия приложения " + versionLabel);
            AutomationProperties.SetAutomationId(version, "Boostix.Version");
            words.Children.Add(version);
            Grid.SetColumn(words, 1);
            brand.Children.Add(words);
            return brand;
        }

        internal static string GetApplicationVersion()
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            return string.Format(
                CultureInfo.InvariantCulture,
                "v{0}.{1}.{2}",
                version.Major,
                version.Minor,
                Math.Max(0, version.Build));
        }

        private void UpdateSidebarStatus()
        {
            if (sidebarStatusCard == null)
            {
                return;
            }

            bool pressure = sessionGuardPressureState != null &&
                sessionGuardPressureState.CriticalAlertActive;
            Color dotColor = boostSessionActive
                ? (pressure ? WarningColor : SuccessColor)
                : MutedColor;
            string title = boostSessionActive
                ? (pressure ? "Мало памяти" : "Boost включён")
                : "Boost выключен";
            string detail = selectedTarget == null
                ? "Игра не выбрана"
                : DescribeTarget(selectedTarget);
            if (boostSessionActive && boostSessionStartedUtc.HasValue)
            {
                detail += ", " + FormatDuration(
                    DateTime.UtcNow - boostSessionStartedUtc.Value);
            }

            var card = new Border
            {
                Background = new SolidColorBrush(SurfaceColor),
                BorderBrush = new SolidColorBrush(
                    boostSessionActive
                        ? BoostixDesignTokens.WithAlpha(dotColor, 110)
                        : BorderColor),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(BoostixDesignTokens.RadiusMedium),
                Padding = new Thickness(12, 10, 12, 11)
            };
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            content.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            FrameworkElement dot = BoostixUi.StatusDot(dotColor, 8);
            dot.HorizontalAlignment = HorizontalAlignment.Left;
            content.Children.Add(dot);
            var titleText = MakeText(
                title,
                13,
                TextColor,
                semiboldFont,
                FontWeights.SemiBold);
            Grid.SetColumn(titleText, 1);
            // A Border has no automation peer; the title carries the status.
            AutomationProperties.SetAutomationId(titleText, "Boostix.Sidebar.Status");
            AutomationProperties.SetName(titleText, title + ". " + detail);
            AutomationProperties.SetLiveSetting(titleText, AutomationLiveSetting.Polite);
            content.Children.Add(titleText);
            var detailText = MakeText(
                detail,
                BoostixDesignTokens.OverlineTextSize,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            detailText.Margin = new Thickness(0, 3, 0, 0);
            Grid.SetColumn(detailText, 1);
            Grid.SetRow(detailText, 1);
            content.Children.Add(detailText);
            card.Child = content;
            sidebarStatusCard.Child = card;
        }

        private static string DescribeTarget(GameTargetIdentity target)
        {
            if (target == null)
            {
                return string.Empty;
            }
            return string.IsNullOrWhiteSpace(target.ProcessName)
                ? System.IO.Path.GetFileNameWithoutExtension(target.ExecutablePath)
                : target.ProcessName;
        }

        private StackPanel BuildHeader()
        {
            var header = new StackPanel
            {
                Margin = new Thickness(0, 30, ChromeReservedWidth, 18)
            };

            headerTitle = MakeText(
                "Игровой сеанс",
                BoostixDesignTokens.PageTitleSize,
                TextColor,
                semiboldFont,
                FontWeights.SemiBold);
            AutomationProperties.SetAutomationId(headerTitle, "Boostix.Center.Title");
            header.Children.Add(headerTitle);

            subtitle = MakeText(
                string.Empty,
                BoostixDesignTokens.BodyTextSize,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            subtitle.Margin = new Thickness(0, 5, 0, 0);
            subtitle.TextWrapping = TextWrapping.Wrap;
            subtitle.TextTrimming = TextTrimming.None;
            AutomationProperties.SetLiveSetting(subtitle, AutomationLiveSetting.Polite);
            header.Children.Add(subtitle);
            return header;
        }

        private BoostCenterTabStrip BuildTabs()
        {
            tabStrip = new BoostCenterTabStrip();
            var tabs = tabStrip;
            AutomationProperties.SetName(tabs, "Разделы Boostix");
            AutomationProperties.SetAutomationId(
                tabs,
                "Boostix.Center.Tabs");

            AddTab(tabs, CenterPage.Home, "Главная", BoostixIcons.Home, 0);
            AddTab(tabs, CenterPage.Readiness, "Проверка ПК", BoostixIcons.Activity, 1);
            AddTab(tabs, CenterPage.Impact, "Фоновые программы", BoostixIcons.Layers, 2);
            AddTab(tabs, CenterPage.Report, "История", BoostixIcons.Chart, 3);
            AddTab(tabs, CenterPage.Profiles, "Игры", BoostixIcons.Gamepad, 4);
            AddTab(tabs, CenterPage.Settings, "Настройки", BoostixIcons.Sliders, 5);
            return tabs;
        }

        private void AddTab(
            BoostCenterTabStrip tabs,
            CenterPage page,
            string title,
            string iconData,
            int row)
        {
            tabs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var visuals = new NavigationVisuals
            {
                Background = new SolidColorBrush(
                    BoostixDesignTokens.WithAlpha(SidebarColor, 0)),
                Label = new SolidColorBrush(SecondaryColor),
                Icon = new SolidColorBrush(MutedColor),
                IndicatorScale = new ScaleTransform(1, 0.4)
            };
            visuals.Indicator = new Border
            {
                Width = 3,
                Height = 20,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(TextColor),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(-1, 0, 0, 0),
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = visuals.IndicatorScale
            };

            var content = new Grid
            {
                Height = NavigationItemHeight
            };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            content.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            content.Children.Add(visuals.Indicator);
            FrameworkElement icon = BoostixIcons.Create(
                iconData,
                18,
                visuals.Icon,
                1.9);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(icon);
            var label = new TextBlock
            {
                Text = title,
                FontFamily = semiboldFont,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = visuals.Label,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 1);
            content.Children.Add(label);

            var button = new BoostCenterTabButton(
                tabs,
                delegate { return currentPage == page; },
                delegate(bool preserveTabFocus)
                {
                    SwitchPage(page, preserveTabFocus);
                })
            {
                Content = content,
                Height = NavigationItemHeight,
                Margin = new Thickness(0, 0, 0, 4),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = visuals.Background,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Template = MakeFlatButtonTemplate(BoostixDesignTokens.RadiusSmall + 2),
                FocusVisualStyle = MakeKeyboardFocusVisualStyle(BoostixDesignTokens.RadiusSmall)
            };
            tabs.Register(button);
            AutomationProperties.SetName(button, title.ToLowerInvariant());
            AutomationProperties.SetAutomationId(
                button,
                "Boostix.Center.Tab." + page);
            KeyboardNavigation.SetTabIndex(button, row);
            button.MouseEnter += delegate { UpdateTab(page, true); };
            button.MouseLeave += delegate { UpdateTab(page, true); };
            Grid.SetRow(button, row);
            tabs.Children.Add(button);

            tabButtons[page] = button;
            navigationVisuals[page] = visuals;
        }

        private void Open(CenterPage page, bool boostDecision)
        {
            if (currentPage == page)
            {
                requireBoostDecision = boostDecision;
                FinishPageTransitionImmediately();
                RenderCurrentPage();
                UpdateTabs(false);
                FocusPreferredButton();
                return;
            }
            SwitchPageCore(page, false, boostDecision);
        }

        private void Close()
        {
            if (!IsOpen)
            {
                return;
            }
            CancelSmoothMouseWheelScroll();
            SwitchPageCore(CenterPage.Home, false, false);
        }

        private void SwitchPage(CenterPage page)
        {
            SwitchPage(page, false);
        }

        private void SwitchPage(CenterPage page, bool preserveTabFocus)
        {
            SwitchPageCore(page, preserveTabFocus, false);
        }

        private void SwitchPageCore(
            CenterPage page,
            bool preserveTabFocus,
            bool boostDecision)
        {
            if (currentPage == page)
            {
                if (preserveTabFocus)
                {
                    FocusSelectedTab(page);
                }
                return;
            }
            if (benchmarkBusy && page == CenterPage.Home && !preserveTabFocus)
            {
                return;
            }

            FinishPageTransitionImmediately();
            CenterPage previousPage = renderedPage;
            bool leavingHome = currentPage == CenterPage.Home;
            BoostCenterTabButton previousSelection =
                tabStrip == null ? null : tabStrip.SelectedTab;
            if (leavingHome)
            {
                // Lets the window publish its latest data before rendering.
                Raise(PageOpening);
            }
            currentPage = page;
            requireBoostDecision = boostDecision;
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
            }
            else
            {
                BeginPageTransition(previousPage, page, preserveTabFocus);
            }

            if (page == CenterPage.Home)
            {
                Raise(preserveTabFocus ? HomeNavigated : CloseRequested);
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
                UpdateTab(pair.Key, animate);
                AutomationProperties.SetHelpText(
                    pair.Value,
                    selected ? "Выбрано" : "Открыть раздел");
                AutomationProperties.SetItemStatus(
                    pair.Value,
                    selected ? "Выбрано" : string.Empty);
            }
        }

        private void UpdateTab(CenterPage page, bool animate)
        {
            NavigationVisuals visuals;
            BoostCenterTabButton button;
            if (!navigationVisuals.TryGetValue(page, out visuals) ||
                !tabButtons.TryGetValue(page, out button))
            {
                return;
            }
            bool selected = page == currentPage;
            bool hovered = button.IsMouseOver;
            Color background = selected
                ? ButtonColor
                : (hovered
                    ? SurfaceColor
                    : BoostixDesignTokens.WithAlpha(SidebarColor, 0));
            if (SystemParameters.HighContrast)
            {
                background = selected ? HoverColor : SidebarColor;
            }
            AnimateTabColor(visuals.Background, background, animate);
            AnimateTabColor(
                visuals.Label,
                selected || hovered ? TextColor : SecondaryColor,
                animate);
            AnimateTabColor(
                visuals.Icon,
                selected ? AccentTextColor : (hovered ? SecondaryColor : MutedColor),
                animate);
            AnimateTabIndicator(
                visuals.Indicator,
                visuals.IndicatorScale,
                selected ? 1 : 0,
                selected ? 1 : 0.4,
                animate);
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
            pageArea.IsHitTestVisible = false;
            if (preserveTabFocus)
            {
                FocusSelectedTab(nextPage);
            }

            AnimatePageVisual(
                headerStack,
                headerTranslation,
                0,
                -direction * 10,
                PageTransitionExitMilliseconds,
                EasingMode.EaseIn,
                null);
            AnimatePageVisual(
                pageArea,
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
                        headerStack,
                        headerTranslation,
                        direction * 10);
                    PreparePageVisual(
                        pageArea,
                        pageTranslation,
                        direction * 18);

                    AnimatePageVisual(
                        headerStack,
                        headerTranslation,
                        1,
                        0,
                        PageTransitionEnterMilliseconds,
                        EasingMode.EaseOut,
                        null);
                    AnimatePageVisual(
                        pageArea,
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
            ResetPageVisual(headerStack, headerTranslation);
            ResetPageVisual(pageArea, pageTranslation);
            pageArea.IsHitTestVisible = true;
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
                    TimeSpan.FromMilliseconds(BoostixDesignTokens.MotionStandardMilliseconds))
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
            double startScale = scale.ScaleY;
            indicator.BeginAnimation(UIElement.OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            indicator.Opacity = targetOpacity;
            scale.ScaleY = targetScale;

            if (!animate || !SystemParameters.ClientAreaAnimation)
            {
                return;
            }

            indicator.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(
                    startOpacity,
                    targetOpacity,
                    TimeSpan.FromMilliseconds(BoostixDesignTokens.MotionStandardMilliseconds))
                {
                    EasingFunction = new CubicEase
                    {
                        EasingMode = EasingMode.EaseInOut
                    },
                    FillBehavior = FillBehavior.Stop
                },
                HandoffBehavior.SnapshotAndReplace);
            scale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                new DoubleAnimation(
                    startScale,
                    targetScale,
                    TimeSpan.FromMilliseconds(BoostixDesignTokens.MotionStandardMilliseconds))
                {
                    EasingFunction = new CubicEase
                    {
                        EasingMode = EasingMode.EaseOut
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
            benchmarkProgressHost = null;
            benchmarkButton = null;
            reportStack = null;

            bool home = currentPage == CenterPage.Home;
            homeHost.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
            pageStack.Visibility = home ? Visibility.Collapsed : Visibility.Visible;

            if (home)
            {
                headerTitle.Text = "Игровой сеанс";
                subtitle.Text =
                    "Выберите игру и запустите Boost перед игрой.";
            }
            else if (currentPage == CenterPage.Readiness)
            {
                headerTitle.Text = "Проверка ПК";
                RenderReadiness();
            }
            else if (currentPage == CenterPage.Impact)
            {
                headerTitle.Text = "Фоновые программы";
                RenderImpact();
            }
            else if (currentPage == CenterPage.Report)
            {
                headerTitle.Text = "История";
                RenderReport();
            }
            else if (currentPage == CenterPage.Profiles)
            {
                headerTitle.Text = "Игры";
                RenderProfiles();
            }
            else
            {
                headerTitle.Text = "Настройки";
                RenderSettings();
            }
            AutomationProperties.SetName(headerTitle, headerTitle.Text);
            footerBar.Visibility = footerButtons.Children.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void RenderReadiness()
        {
            subtitle.Text = preflight == null
                ? "Проверяем компьютер…"
                : string.Format(
                    CultureInfo.CurrentCulture,
                    "Проверено в {0:HH:mm}",
                    preflight.CapturedUtc.ToLocalTime());

            pageContent.Children.Add(BuildLiveSessionCard());

            if (diagnosticSnapshot != null)
            {
                pageContent.Children.Add(BuildResourceSnapshot(diagnosticSnapshot));
            }

            pageContent.Children.Add(MakeSectionHeading("Что проверили", null));
            if (preflight == null)
            {
                pageContent.Children.Add(MakeEmptyState(
                    BoostixIcons.Activity,
                    "Идёт проверка",
                    "Собираем только безопасные данные без изменения Windows."));
            }
            else
            {
                var list = MakeListCard();
                foreach (BoostCheckResult check in preflight.Checks)
                {
                    AddListRow(list, BuildCheckRow(check));
                }
                pageContent.Children.Add(list);
            }

            var refresh = MakeActionButton(
                "Проверить снова",
                BoostixButtonKind.Secondary,
                BoostixIcons.Refresh);
            refresh.Click += delegate { Raise(RefreshRequested); };
            AutomationProperties.SetName(refresh, "Проверить готовность снова");
            footerButtons.Children.Add(refresh);
            preferredFocusButton = refresh;

            var choose = MakeActionButton(
                "Выбрать игру",
                BoostixButtonKind.Secondary,
                BoostixIcons.Gamepad);
            choose.Margin = new Thickness(10, 0, 0, 0);
            choose.Click += delegate { Raise(TargetSelectionRequested); };
            AutomationProperties.SetName(choose, "Выбрать запущенную игру");
            AutomationProperties.SetAutomationId(
                choose,
                "Boostix.Center.SelectTarget");
            footerButtons.Children.Add(choose);

            if (requireBoostDecision)
            {
                bool blocked = preflight != null && preflight.HasBlockers;
                var proceed = MakeActionButton(
                    blocked ? "Boost недоступен" : "Запустить Boost",
                    BoostixButtonKind.Primary,
                    BoostixIcons.Zap);
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
                AutomationProperties.SetAutomationId(
                    proceed,
                    "Boostix.Center.ProceedBoost");
                footerButtons.Children.Add(proceed);
                if (proceed.IsEnabled)
                {
                    preferredFocusButton = proceed;
                }
            }
        }

        private FrameworkElement BuildLiveSessionCard()
        {
            var card = BoostixUi.Card(new Thickness(16, 14, 16, 14));
            card.Margin = new Thickness(0, 0, 0, 16);
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            FrameworkElement icon = MakeTargetIcon(
                selectedTarget == null ? null : selectedTarget.ExecutablePath,
                40);
            content.Children.Add(icon);

            string targetName = selectedTarget == null
                ? "Игра не выбрана"
                : DescribeTarget(selectedTarget);
            var target = new StackPanel
            {
                Margin = new Thickness(14, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            target.Children.Add(MakeText(
                targetName,
                15,
                TextColor,
                semiboldFont,
                FontWeights.SemiBold));
            string targetDetail = selectedTarget == null
                ? "Запустите игру и выберите её на главной."
                : "Игра запущена";
            var detail = MakeText(
                targetDetail,
                BoostixDesignTokens.MetadataTextSize,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            detail.Margin = new Thickness(0, 3, 0, 0);
            target.Children.Add(detail);
            Grid.SetColumn(target, 1);
            content.Children.Add(target);

            bool pressure = sessionGuardPressureState != null &&
                sessionGuardPressureState.CriticalAlertActive;
            string state = boostSessionActive
                ? (pressure ? "Мало памяти" : "Boost включён")
                : (selectedTarget == null ? "Игра не выбрана" : "Готов");
            Color stateColor = pressure
                ? WarningColor
                : (boostSessionActive ? SuccessColor : SecondaryColor);
            FrameworkElement stateMetric = MakeInlineStat("Сеанс", state, stateColor);
            Grid.SetColumn(stateMetric, 2);
            content.Children.Add(stateMetric);

            string commit = sessionGuardSample != null &&
                sessionGuardSample.SystemMetricsAvailable
                ? FormatBytesCompact(sessionGuardSample.CommitHeadroomBytes)
                : "—";
            FrameworkElement commitMetric = MakeInlineStat(
                "Запас памяти",
                commit,
                pressure ? WarningColor : TextColor);
            commitMetric.Margin = new Thickness(22, 0, 0, 0);
            Grid.SetColumn(commitMetric, 3);
            content.Children.Add(commitMetric);

            card.Child = content;
            AutomationProperties.SetName(
                card,
                targetName + ". Состояние: " + state + ". Запас памяти: " + commit + ".");
            AutomationProperties.SetAutomationId(
                card,
                "Boostix.Center.LiveSession");
            return card;
        }

        private FrameworkElement MakeInlineStat(string label, string value, Color valueColor)
        {
            var block = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            TextBlock labelText = BoostixUi.FieldLabel(label);
            labelText.HorizontalAlignment = HorizontalAlignment.Right;
            block.Children.Add(labelText);
            var valueText = MakeText(
                value,
                15,
                valueColor,
                semiboldFont,
                FontWeights.SemiBold);
            valueText.HorizontalAlignment = HorizontalAlignment.Right;
            valueText.Margin = new Thickness(0, 2, 0, 0);
            block.Children.Add(valueText);
            return block;
        }

        private void RenderImpact()
        {
            subtitle.Text =
                "Какие программы в фоне нагружают компьютер. Boostix ничего не закрывает без вашего решения.";

            if (!string.IsNullOrWhiteSpace(impactMessage))
            {
                var message = MakeNotice(
                    impactScanBusy ? BoostixIcons.Activity : BoostixIcons.Info,
                    impactScanBusy ? "Идёт измерение" : "Результат",
                    impactMessage,
                    impactScanBusy ? AccentTextColor : SecondaryColor);
                AutomationProperties.SetLiveSetting(
                    message,
                    AutomationLiveSetting.Polite);
                pageContent.Children.Add(message);
            }

            if (impactResults.Count == 0)
            {
                pageContent.Children.Add(MakeEmptyState(
                    BoostixIcons.Layers,
                    impactScanBusy ? "Идёт измерение" : "Ещё не проверяли",
                    impactScanBusy
                        ? "Подождите 15 секунд и пока не запускайте ничего нового."
                        : "Проверка займёт 15 секунд и ничего не закроет сама."));
            }
            else
            {
                var list = MakeListCard();
                int index = 0;
                foreach (BackgroundImpactResult item in impactResults.Take(12))
                {
                    AddListRow(list, BuildImpactRow(item, index++));
                }
                pageContent.Children.Add(list);
            }

            var scan = MakeActionButton(
                impactScanBusy ? "Проверяем…" : "Проверить фон",
                BoostixButtonKind.Primary,
                BoostixIcons.Activity);
            scan.IsEnabled = !impactScanBusy;
            scan.Click += delegate { Raise(ImpactScanRequested); };
            AutomationProperties.SetName(
                scan,
                "Проверить фоновые программы за 15 секунд");
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
                MinHeight = 60
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            FrameworkElement icon = MakeTargetIcon(item.Identity.ExecutablePath, 32);
            row.Children.Add(icon);

            string processName = string.IsNullOrWhiteSpace(item.Identity.ProcessName)
                ? "Программа"
                : item.Identity.ProcessName;
            var text = new StackPanel
            {
                Margin = new Thickness(12, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            text.Children.Add(MakeText(
                processName,
                BoostixDesignTokens.BodyTextSize,
                TextColor,
                semiboldFont,
                FontWeights.SemiBold));
            string metrics = "Память " + FormatBytesCompact(item.PrivateBytes) +
                ", диск " + FormatBytesCompact(item.ReadBytes + item.WriteBytes);
            var detail = MakeText(
                metrics,
                BoostixDesignTokens.MetadataTextSize,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            detail.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(detail);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            Color cpuColor = item.CpuPercent >= 5 ? WarningColor : AccentTextColor;
            var cpu = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0)
            };
            var cpuText = MakeText(
                "CPU " + item.CpuPercent.ToString("0.0", CultureInfo.CurrentCulture) + "%",
                BoostixDesignTokens.BodyTextSize,
                cpuColor,
                semiboldFont,
                FontWeights.SemiBold);
            cpu.Children.Add(cpuText);
            Grid cpuBar = BoostixUi.ProgressBar(
                Math.Min(1.0, item.CpuPercent / 25.0),
                cpuColor,
                4);
            cpuBar.Margin = new Thickness(0, 5, 0, 0);
            cpu.Children.Add(cpuBar);
            Grid.SetColumn(cpu, 2);
            row.Children.Add(cpu);

            var close = MakeActionButton(
                "Закрыть",
                BoostixButtonKind.Secondary,
                null);
            close.MinWidth = 88;
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
                "Программа получит обычную просьбу закрыться, как при нажатии на крестик.");
            AutomationProperties.SetAutomationId(
                close,
                "Boostix.Center.Impact.Close." +
                index.ToString(CultureInfo.InvariantCulture));
            Grid.SetColumn(close, 3);
            row.Children.Add(close);

            AutomationProperties.SetName(
                row,
                processName + ". " + metrics + ". " + cpuText.Text + ".");
            AutomationProperties.SetAutomationId(
                row,
                "Boostix.Center.Impact." + index.ToString(CultureInfo.InvariantCulture));
            return row;
        }

        private void RenderProfiles()
        {
            subtitle.Text =
                "Игры, которые вы выбирали. Включите «Авто», и Boost будет запускаться вместе с игрой.";
            if (gameProfiles.Count == 0)
            {
                pageContent.Children.Add(MakeEmptyState(
                    BoostixIcons.Gamepad,
                    "Игр пока нет",
                    "Запустите игру и выберите её на главной. Она появится здесь."));
            }
            else
            {
                var list = MakeListCard();
                int index = 0;
                foreach (GameProfile profile in gameProfiles)
                {
                    AddListRow(list, BuildProfileRow(profile, index++));
                }
                pageContent.Children.Add(list);
            }

            var choose = MakeActionButton(
                "Добавить игру",
                BoostixButtonKind.Primary,
                BoostixIcons.Plus);
            choose.Click += delegate { Raise(TargetSelectionRequested); };
            AutomationProperties.SetName(choose, "Выбрать запущенную игру");
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
                MinHeight = 62
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(MakeTargetIcon(profile.ExecutablePath, 36));

            var text = new StackPanel
            {
                Margin = new Thickness(14, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            text.Children.Add(MakeText(
                profile.DisplayName,
                14,
                TextColor,
                semiboldFont,
                FontWeights.SemiBold));
            var path = MakeText(
                System.IO.Path.GetFileName(profile.ExecutablePath),
                BoostixDesignTokens.MetadataTextSize,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            path.Margin = new Thickness(0, 2, 0, 0);
            path.ToolTip = profile.ExecutablePath;
            text.Children.Add(path);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            CheckBox auto = BuildSwitch(
                "Авто",
                profile.AutoBoost,
                delegate(bool value)
                {
                    RaiseProfile(ProfileAutoBoostChanged, profile.ExecutablePath, value);
                });
            auto.VerticalAlignment = VerticalAlignment.Center;
            auto.Margin = new Thickness(0, 0, 14, 0);
            AutomationProperties.SetName(
                auto,
                "Автоматический Boost для " + profile.DisplayName);
            AutomationProperties.SetAutomationId(
                auto,
                "Boostix.Center.ProfileAuto." + index.ToString(CultureInfo.InvariantCulture));
            Grid.SetColumn(auto, 2);
            row.Children.Add(auto);

            var remove = MakeActionButton(string.Empty, BoostixButtonKind.Destructive, BoostixIcons.Trash);
            remove.MinWidth = 0;
            remove.Width = BoostixDesignTokens.MinimumActionHeight;
            remove.Padding = new Thickness(0);
            remove.Height = BoostixDesignTokens.MinimumActionHeight;
            remove.VerticalAlignment = VerticalAlignment.Center;
            remove.ToolTip = "Убрать из списка";
            Grid.SetColumn(remove, 3);
            AutomationProperties.SetName(
                remove,
                "Убрать из списка " + profile.DisplayName);
            AutomationProperties.SetAutomationId(
                remove,
                "Boostix.Center.ProfileRemove." + index.ToString(CultureInfo.InvariantCulture));
            remove.Click += delegate
            {
                RaiseProfile(ProfileRemoveRequested, profile.ExecutablePath, false);
            };
            row.Children.Add(remove);
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
                MinHeight = 52,
                Background = Brushes.Transparent
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });

            string glyph;
            Color glyphColor;
            GetSeverityVisual(check.Severity, out glyph, out glyphColor);
            FrameworkElement status = MakeIconBadge(glyph, glyphColor, 28);
            status.VerticalAlignment = VerticalAlignment.Top;
            status.HorizontalAlignment = HorizontalAlignment.Left;
            status.Margin = new Thickness(0, 10, 0, 0);
            row.Children.Add(status);

            var text = new StackPanel
            {
                Margin = new Thickness(0, 10, 0, 10)
            };
            text.Children.Add(MakeText(
                BoostixUi.DisplayCase(check.Title),
                BoostixDesignTokens.BodyTextSize,
                TextColor,
                semiboldFont,
                FontWeights.SemiBold));
            var detail = MakeText(
                check.Detail,
                BoostixDesignTokens.MetadataTextSize,
                SecondaryColor,
                regularFont,
                FontWeights.Normal);
            detail.TextWrapping = TextWrapping.Wrap;
            detail.TextTrimming = TextTrimming.None;
            detail.Margin = new Thickness(0, 3, 4, 0);
            text.Children.Add(detail);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            AutomationProperties.SetName(
                row,
                check.Title + ". " + check.Detail);
            return row;
        }

        private void RenderReport()
        {
            subtitle.Text =
                "Что происходило во время игры и как изменился FPS.";
            reportStack = BuildReportPage();
            pageContent.Children.Add(reportStack);

            IEnumerable<BoostSessionReport> recent = sessionHistory
                .Where(item => item != null &&
                    (sessionReport == null ||
                     !string.Equals(
                         item.SessionId,
                         sessionReport.SessionId,
                         StringComparison.OrdinalIgnoreCase)))
                .Take(5);
            if (recent.Any())
            {
                pageContent.Children.Add(MakeSectionHeading("Прошлые сеансы", null));
                var list = MakeListCard();
                int historyIndex = 0;
                foreach (BoostSessionReport report in recent)
                {
                    AddListRow(list, BuildHistoryRow(report, historyIndex++));
                }
                pageContent.Children.Add(list);
            }

            var export = MakeActionButton(
                "Диагностика",
                BoostixButtonKind.Secondary,
                BoostixIcons.Export);
            export.Margin = new Thickness(0, 0, 10, 0);
            export.Click += delegate { Raise(ExportDiagnosticsRequested); };
            AutomationProperties.SetName(
                export,
                "Собрать диагностику: отчёт и журналы Boostix в zip-архиве");
            AutomationProperties.SetAutomationId(
                export,
                "Boostix.Center.ExportDiagnostics");
            footerButtons.Children.Add(export);

            if (proofSnapshot != null &&
                proofSnapshot.State == PerformanceProofCoordinatorState.AwaitingRun)
            {
                var cancelProof = MakeActionButton(
                    "Сбросить",
                    BoostixButtonKind.Ghost,
                    null);
                cancelProof.Margin = new Thickness(0, 0, 10, 0);
                cancelProof.IsEnabled = !benchmarkBusy;
                cancelProof.Click += delegate { Raise(ProofCancelRequested); };
                AutomationProperties.SetName(
                    cancelProof,
                    "Сбросить незавершённый замер FPS");
                AutomationProperties.SetAutomationId(
                    cancelProof,
                    "Boostix.Center.CancelProof");
                footerButtons.Children.Add(cancelProof);
            }

            string proofButtonText = "Замерить FPS";
            if (proofSnapshot != null &&
                proofSnapshot.State == PerformanceProofCoordinatorState.AwaitingRun &&
                proofSnapshot.NextStep != null)
            {
                proofButtonText = proofSnapshot.CompletedSteps == 0
                    ? "Замерить FPS: четыре прохода по минуте"
                    : "Шаг " + proofSnapshot.NextStep.StepNumber.ToString(
                        CultureInfo.CurrentCulture) + " из " +
                        proofSnapshot.TotalSteps.ToString(CultureInfo.CurrentCulture);
            }
            else if (proofSnapshot != null)
            {
                proofButtonText = "Новый замер FPS";
            }

            var benchmark = MakeActionButton(
                benchmarkNeedsElevation
                    ? "Повторить с правами"
                    : (benchmarkBusy
                        ? "Замер " + benchmarkPercent.ToString(CultureInfo.CurrentCulture) + "%"
                        : proofButtonText),
                BoostixButtonKind.Primary,
                BoostixIcons.Chart);
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
                    ? "Повторить текущий этап замера FPS с правами администратора"
                    : "Запустить следующий проход замера FPS");
            AutomationProperties.SetAutomationId(
                benchmark,
                "Boostix.Center.ReportBenchmark");
            benchmarkButton = benchmark;
            footerButtons.Children.Add(benchmark);
            preferredFocusButton = benchmark;
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
                    BoostixIcons.Chart,
                    "Здесь пока пусто",
                    "Запустите Boost перед игрой, и здесь появится отчёт."));
                return report;
            }

            BoostCrashInsight crashInsight =
                BoostCrashAssistant.Analyze(sessionReport);
            if (crashInsight.Category != BoostCrashCategory.None)
            {
                report.Children.Add(BuildCrashInsight(crashInsight));
            }

            report.Children.Add(MakeSectionHeading(
                "Последний сеанс",
                FormatSessionStatus(sessionReport)));
            report.Children.Add(BuildSessionSummary(sessionReport));
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

            report.Children.Add(MakeSectionHeading("Что сделал Boostix", null));
            IEnumerable<BoostActionRecord> actions =
                (sessionReport.Actions ?? new List<BoostActionRecord>())
                    .OrderByDescending(item => item.TimestampUtc)
                    .Take(20);
            if (!actions.Any())
            {
                report.Children.Add(MakeText(
                    "Пока ничего не сделано.",
                    BoostixDesignTokens.MetadataTextSize,
                    MutedColor,
                    regularFont,
                    FontWeights.Normal));
            }
            else
            {
                var list = MakeListCard();
                foreach (BoostActionRecord action in actions)
                {
                    AddListRow(list, BuildActionRow(action));
                }
                report.Children.Add(list);
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
                    : AccentTextColor);
            string title;
            string detail;
            if (completed)
            {
                PerformanceProofResult result = snapshot.FinalResult;
                title = result.Verdict == PerformanceProofVerdict.Positive
                    ? "FPS вырос"
                    : (result.Verdict == PerformanceProofVerdict.Negative
                        ? "FPS снизился"
                        : "Разница в пределах погрешности");
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
                title = "Замер FPS, шаг " +
                    snapshot.NextStep.StepNumber.ToString(CultureInfo.CurrentCulture) + " из " +
                    snapshot.TotalSteps.ToString(CultureInfo.CurrentCulture) +
                    ", " + snapshot.NextStep.SequenceLabel;
                detail = snapshot.NextStep.Instruction;
            }
            else
            {
                title = failed ? "Замер FPS остановлен" : "Замер FPS сброшен";
                detail = string.IsNullOrWhiteSpace(snapshot.Message)
                    ? "Начните новый замер, когда игра и сцена будут готовы."
                    : snapshot.Message;
            }

            Border host = MakeNotice(
                failed
                    ? BoostixIcons.CrossCircle
                    : (completed ? BoostixIcons.CheckCircle : BoostixIcons.Chart),
                title,
                detail,
                edge);
            AutomationProperties.SetName(host, title + ". " + detail);
            AutomationProperties.SetLiveSetting(
                host,
                failed ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
            return host;
        }

        private FrameworkElement BuildBenchmarkNotice()
        {
            Color tone = benchmarkNeedsElevation || benchmarkPercent < 0
                ? ErrorColor
                : AccentTextColor;
            Border notice = MakeNotice(
                benchmarkNeedsElevation || benchmarkPercent < 0
                    ? BoostixIcons.Alert
                    : BoostixIcons.Activity,
                BoostixUi.DisplayCase(benchmarkTitle),
                benchmarkDetail,
                tone);
            var grid = notice.Child as Grid;
            if (grid != null)
            {
                var texts = grid.Children.OfType<StackPanel>().FirstOrDefault();
                if (texts != null && texts.Children.Count >= 2)
                {
                    benchmarkNoticeTitleBlock = texts.Children[0] as TextBlock;
                    benchmarkNoticeDetailBlock = texts.Children[1] as TextBlock;
                    if (benchmarkBusy)
                    {
                        benchmarkProgressHost = new Grid
                        {
                            Margin = new Thickness(0, 10, 0, 0)
                        };
                        benchmarkProgressHost.Children.Add(BoostixUi.ProgressBar(
                            benchmarkPercent / 100.0,
                            AccentColor,
                            5));
                        texts.Children.Add(benchmarkProgressHost);
                    }
                }
            }
            AutomationProperties.SetLiveSetting(notice, AutomationLiveSetting.Polite);
            return notice;
        }

        private FrameworkElement BuildExportNotice()
        {
            Border notice = MakeNotice(
                exportMessageError ? BoostixIcons.Alert : BoostixIcons.CheckCircle,
                BoostixUi.DisplayCase(exportMessageTitle),
                exportMessageDetail,
                exportMessageError ? ErrorColor : SuccessColor);
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
            Color tone = insight.Category == BoostCrashCategory.MemoryPressure
                ? WarningColor
                : ErrorColor;
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(insight.Evidence))
            {
                lines.Add(insight.Evidence);
            }
            if (!string.IsNullOrWhiteSpace(insight.Summary))
            {
                lines.Add(insight.Summary);
            }
            foreach (string step in insight.Steps.Take(3))
            {
                lines.Add("• " + step);
            }
            Border host = MakeNotice(
                BoostixIcons.Alert,
                BoostixUi.DisplayCase(insight.Title),
                string.Join("\n", lines.ToArray()),
                tone);
            AutomationProperties.SetName(
                host,
                insight.Title + ". " + insight.Summary);
            return host;
        }

        private FrameworkElement BuildResourceSnapshot(DiagnosticSnapshot snapshot)
        {
            var section = new StackPanel();
            section.Children.Add(MakeSectionHeading(
                "Память сейчас",
                null,
                BoostixUi.Chip(
                    FormatPressure(snapshot.Pressure.ToString()),
                    GetPressureColor(snapshot.Pressure))));

            var grid = MakeTileGrid(2);
            AddTile(grid, 0, BuildResourceTile(
                BoostixIcons.Memory,
                "Оперативная память",
                snapshot.MemoryAvailable
                    ? FormatBytesCompact(snapshot.PhysicalAvailableBytes) + " свободно"
                    : "Недоступно",
                snapshot.MemoryAvailable
                    ? "из " + FormatBytesCompact(snapshot.PhysicalTotalBytes)
                    : string.Empty,
                snapshot.MemoryAvailable && snapshot.PhysicalTotalBytes > 0
                    ? 1.0 - (double)snapshot.PhysicalAvailableBytes / snapshot.PhysicalTotalBytes
                    : double.NaN));
            AddTile(grid, 1, BuildResourceTile(
                BoostixIcons.Layers,
                "Резерв памяти Windows",
                snapshot.MemoryAvailable
                    ? FormatBytesCompact(snapshot.CommitHeadroomBytes)
                    : "Недоступно",
                snapshot.MemoryAvailable
                    ? "из " + FormatBytesCompact(snapshot.CommitLimitBytes)
                    : string.Empty,
                snapshot.MemoryAvailable && snapshot.CommitLimitBytes > 0
                    ? 1.0 - (double)snapshot.CommitHeadroomBytes / snapshot.CommitLimitBytes
                    : double.NaN));
            bool gpuKnown = snapshot.GpuUsageAvailable &&
                snapshot.GpuTotalAvailable &&
                snapshot.GpuDedicatedTotalBytes > 0;
            AddTile(grid, 2, BuildResourceTile(
                BoostixIcons.Cpu,
                "Видеопамять",
                gpuKnown
                    ? FormatBytesCompact(snapshot.GpuDedicatedUsageBytes) + " занято"
                    : "Недоступно",
                gpuKnown
                    ? "из " + FormatBytesCompact(snapshot.GpuDedicatedTotalBytes)
                    : string.Empty,
                gpuKnown
                    ? (double)snapshot.GpuDedicatedUsageBytes / snapshot.GpuDedicatedTotalBytes
                    : double.NaN));
            AddTile(grid, 3, BuildResourceTile(
                BoostixIcons.File,
                "Файл подкачки",
                snapshot.PageFileAvailable
                    ? FormatBytesCompact(snapshot.PageFileUsedBytes) + " занято"
                    : FormatUnavailablePageFile(snapshot),
                snapshot.PageFileAvailable
                    ? "из " + FormatBytesCompact(snapshot.PageFileAllocatedBytes)
                    : string.Empty,
                snapshot.PageFileAvailable && snapshot.PageFileAllocatedBytes > 0
                    ? (double)snapshot.PageFileUsedBytes / snapshot.PageFileAllocatedBytes
                    : double.NaN));
            section.Children.Add(grid);

            if (!string.IsNullOrWhiteSpace(snapshot.PressureReason))
            {
                var reason = MakeText(
                    TranslatePressureReason(snapshot),
                    BoostixDesignTokens.MetadataTextSize,
                    MutedColor,
                    regularFont,
                    FontWeights.Normal);
                reason.TextWrapping = TextWrapping.Wrap;
                reason.TextTrimming = TextTrimming.None;
                reason.Margin = new Thickness(2, 10, 0, 0);
                section.Children.Add(reason);
            }
            AutomationProperties.SetName(
                section,
                "Память сейчас. " +
                FormatPressure(snapshot.Pressure.ToString()));
            return section;
        }

        private FrameworkElement BuildResourceTile(
            string iconData,
            string title,
            string value,
            string caption,
            double usedFraction)
        {
            var card = BoostixUi.Card(new Thickness(14, 12, 14, 14));
            var content = new StackPanel();
            var heading = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };
            FrameworkElement icon = BoostixIcons.Create(iconData, 15, MutedColor);
            icon.Margin = new Thickness(0, 0, 7, 0);
            heading.Children.Add(icon);
            heading.Children.Add(BoostixUi.FieldLabel(title));
            content.Children.Add(heading);

            var valueLine = new WrapPanel
            {
                Margin = new Thickness(0, 8, 0, 0)
            };
            var valueText = MakeText(
                value,
                17,
                TextColor,
                semiboldFont,
                FontWeights.SemiBold);
            valueLine.Children.Add(valueText);
            if (!string.IsNullOrEmpty(caption))
            {
                var captionText = MakeText(
                    " " + caption,
                    BoostixDesignTokens.MetadataTextSize,
                    MutedColor,
                    regularFont,
                    FontWeights.Normal);
                captionText.VerticalAlignment = VerticalAlignment.Bottom;
                captionText.Margin = new Thickness(2, 0, 0, 2);
                valueLine.Children.Add(captionText);
            }
            content.Children.Add(valueLine);

            if (!double.IsNaN(usedFraction))
            {
                Color fill = usedFraction >= 0.92
                    ? ErrorColor
                    : (usedFraction >= 0.8 ? WarningColor : SecondaryColor);
                Grid bar = BoostixUi.ProgressBar(usedFraction, fill, 4);
                bar.Margin = new Thickness(0, 10, 0, 0);
                content.Children.Add(bar);
            }
            card.Child = content;
            AutomationProperties.SetName(card, title + ": " + value + " " + caption);
            return card;
        }

        private FrameworkElement BuildPerformanceComparison(
            BoostPerformanceComparison comparison)
        {
            var section = new StackPanel();
            section.Children.Add(MakeSectionHeading("Сравнение с прошлым замером", null));
            var grid = MakeTileGrid(4);
            AddTile(grid, 0, BuildMetric(
                "Средний FPS",
                BoostSessionComparison.FormatSigned(
                    comparison.AverageFpsDelta,
                    string.Empty),
                DeltaColor(comparison.AverageFpsDelta, true)));
            AddTile(grid, 1, BuildMetric(
                "1% low",
                BoostSessionComparison.FormatSigned(
                    comparison.OnePercentLowFpsDelta,
                    string.Empty),
                DeltaColor(comparison.OnePercentLowFpsDelta, true)));
            AddTile(grid, 2, BuildMetric(
                "Время кадра, 95%",
                BoostSessionComparison.FormatSigned(
                    comparison.P95FrameTimeDeltaMs,
                    " мс"),
                DeltaColor(comparison.P95FrameTimeDeltaMs, false)));
            AddTile(grid, 3, BuildMetric(
                "Подлагивания",
                (comparison.FramesOver50MsDelta > 0 ? "+" : string.Empty) +
                    comparison.FramesOver50MsDelta.ToString(
                        CultureInfo.CurrentCulture),
                DeltaColor(comparison.FramesOver50MsDelta, false)));
            section.Children.Add(grid);
            var note = MakeText(
                "Зелёный значит лучше, красный хуже. Сравнивайте одну и ту же игру и сцену.",
                BoostixDesignTokens.MetadataTextSize,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            note.Margin = new Thickness(2, 8, 0, 0);
            section.Children.Add(note);
            return section;
        }

        private static Color DeltaColor(double delta, bool higherIsBetter)
        {
            if (Math.Abs(delta) < 0.0001)
            {
                return TextColor;
            }
            bool better = higherIsBetter ? delta > 0 : delta < 0;
            return better ? SuccessColor : ErrorColor;
        }

        private void UpdateBenchmarkProgressVisuals()
        {
            if (benchmarkNoticeTitleBlock != null)
            {
                benchmarkNoticeTitleBlock.Text = BoostixUi.DisplayCase(benchmarkTitle);
            }
            if (benchmarkNoticeDetailBlock != null)
            {
                benchmarkNoticeDetailBlock.Text = benchmarkDetail ?? string.Empty;
            }
            if (benchmarkProgressHost != null)
            {
                benchmarkProgressHost.Children.Clear();
                benchmarkProgressHost.Children.Add(BoostixUi.ProgressBar(
                    benchmarkPercent / 100.0,
                    AccentColor,
                    5));
            }
            if (benchmarkButton != null)
            {
                BoostixUi.SetButtonText(
                    benchmarkButton,
                    "Замер " + benchmarkPercent.ToString(CultureInfo.CurrentCulture) + "%");
                benchmarkButton.IsEnabled = false;
            }
        }

        private FrameworkElement BuildSessionSummary(BoostSessionReport report)
        {
            TimeSpan duration = (report.EndedUtc ?? DateTime.UtcNow) - report.StartedUtc;
            var grid = MakeTileGrid(3);
            AddTile(grid, 0, BuildMetric(
                "Длительность",
                FormatDuration(duration),
                TextColor));
            AddTile(grid, 1, BuildMetric(
                "Свободно памяти, минимум",
                report.MinimumAvailableMemoryBytes > 0
                    ? FormatBytesCompact(report.MinimumAvailableMemoryBytes)
                    : "—",
                TextColor));
            AddTile(grid, 2, BuildMetric(
                "Резерв памяти, минимум",
                report.MinimumCommitHeadroomBytes > 0
                    ? FormatBytesCompact(report.MinimumCommitHeadroomBytes)
                    : "—",
                TextColor));
            AddTile(grid, 3, BuildMetric(
                "Видеопамять, максимум",
                report.GpuMemorySamples > 0 &&
                    report.GpuDedicatedTotalBytes > 0
                    ? FormatBytesCompact(report.PeakGpuDedicatedUsageBytes) +
                        " / " +
                        FormatBytesCompact(report.GpuDedicatedTotalBytes)
                    : "Нет данных",
                TextColor));
            AddTile(grid, 4, BuildMetric(
                "Файл подкачки, максимум",
                report.PageFileAllocatedBytes > 0
                    ? FormatBytesCompact(report.PeakPageFileUsedBytes) +
                        " / " + FormatBytesCompact(report.PageFileAllocatedBytes)
                    : "Нет данных",
                TextColor));
            AddTile(grid, 5, BuildMetric(
                "Действий Boostix",
                (report.Actions == null ? 0 : report.Actions.Count).ToString(
                    CultureInfo.CurrentCulture),
                TextColor));
            return grid;
        }

        private FrameworkElement BuildPerformanceGrid(BoostPerformanceResult result)
        {
            var host = new StackPanel();
            host.Children.Add(MakeSectionHeading("Замер FPS", null));
            var grid = MakeTileGrid(3);
            AddTile(grid, 0, BuildMetric(
                "Средний FPS",
                result.AverageFps.ToString("0.0", CultureInfo.CurrentCulture),
                SuccessColor));
            AddTile(grid, 1, BuildMetric(
                "1% low",
                result.OnePercentLowFps.ToString("0.0", CultureInfo.CurrentCulture),
                TextColor));
            AddTile(grid, 2, BuildMetric(
                "Время кадра, 95%",
                result.P95FrameTimeMs.ToString("0.0", CultureInfo.CurrentCulture) + " мс",
                TextColor));
            AddTile(grid, 3, BuildMetric(
                "Время кадра, 99%",
                result.P99FrameTimeMs.ToString("0.0", CultureInfo.CurrentCulture) + " мс",
                TextColor));
            AddTile(grid, 4, BuildMetric(
                "Подлагивания",
                result.FramesOver50Ms.ToString(CultureInfo.CurrentCulture),
                TextColor));
            AddTile(grid, 5, BuildMetric(
                "Сильные подлагивания",
                result.FramesOver100Ms.ToString(CultureInfo.CurrentCulture),
                TextColor));
            host.Children.Add(grid);
            return host;
        }

        private Border BuildMetric(
            string title,
            string value,
            Color valueColor)
        {
            var host = BoostixUi.Card(new Thickness(14, 11, 14, 12));
            host.MinHeight = 66;
            var content = new StackPanel();
            content.Children.Add(BoostixUi.FieldLabel(title));
            var valueText = MakeText(
                value,
                BoostixDesignTokens.MetricValueSize,
                valueColor,
                semiboldFont,
                FontWeights.SemiBold);
            valueText.Margin = new Thickness(0, 4, 0, 0);
            content.Children.Add(valueText);
            host.Child = content;
            AutomationProperties.SetName(host, title + ": " + value);
            return host;
        }

        private Grid MakeTileGrid(int columns)
        {
            var grid = new Grid
            {
                Margin = new Thickness(0, 0, 0, 4)
            };
            for (int index = 0; index < columns; index++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });
            }
            return grid;
        }

        private static void AddTile(Grid grid, int index, FrameworkElement tile)
        {
            int columns = Math.Max(1, grid.ColumnDefinitions.Count);
            int row = index / columns;
            int column = index % columns;
            while (grid.RowDefinitions.Count <= row)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            tile.Margin = new Thickness(
                column == 0 ? 0 : 5,
                row == 0 ? 0 : 10,
                column == columns - 1 ? 0 : 5,
                0);
            Grid.SetRow(tile, row);
            Grid.SetColumn(tile, column);
            grid.Children.Add(tile);
        }

        private FrameworkElement BuildActionRow(BoostActionRecord action)
        {
            var row = new Grid
            {
                MinHeight = 46
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string glyph;
            Color color;
            GetActionVisual(action.Outcome, out glyph, out color);
            FrameworkElement icon = MakeIconBadge(glyph, color, 24);
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            icon.Margin = new Thickness(0, 10, 0, 0);
            row.Children.Add(icon);

            var text = new StackPanel
            {
                Margin = new Thickness(0, 9, 8, 9)
            };
            text.Children.Add(MakeText(
                BoostixUi.DisplayCase(action.Title),
                BoostixDesignTokens.BodyTextSize,
                TextColor,
                semiboldFont,
                FontWeights.SemiBold));
            if (!string.IsNullOrWhiteSpace(action.Detail))
            {
                var detail = MakeText(
                    action.Detail,
                    BoostixDesignTokens.MetadataTextSize,
                    MutedColor,
                    regularFont,
                    FontWeights.Normal);
                detail.TextWrapping = TextWrapping.Wrap;
                detail.TextTrimming = TextTrimming.None;
                detail.Margin = new Thickness(0, 2, 0, 0);
                text.Children.Add(detail);
            }
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            var time = MakeText(
                action.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture),
                BoostixDesignTokens.OverlineTextSize,
                MutedColor,
                semiboldFont,
                FontWeights.Normal);
            time.VerticalAlignment = VerticalAlignment.Top;
            time.Margin = new Thickness(0, 11, 0, 0);
            Grid.SetColumn(time, 2);
            row.Children.Add(time);
            return row;
        }

        private Button BuildHistoryRow(BoostSessionReport report, int index)
        {
            var background = new SolidColorBrush(BoostixDesignTokens.WithAlpha(SurfaceColor, 0));
            var button = new Button
            {
                MinHeight = 58,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = background,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(-10, 0, -10, 0),
                Cursor = Cursors.Hand,
                Template = MakeFlatButtonTemplate(BoostixDesignTokens.RadiusSmall),
                FocusVisualStyle = MakeKeyboardFocusVisualStyle(BoostixDesignTokens.RadiusSmall)
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
            string target = string.IsNullOrWhiteSpace(report.TargetName)
                ? "Сеанс"
                : report.TargetName;
            var title = MakeText(
                localStart.ToString("dd.MM · HH:mm", CultureInfo.CurrentCulture) +
                    "  ·  " + target,
                BoostixDesignTokens.BodyTextSize,
                TextColor,
                semiboldFont,
                FontWeights.SemiBold);
            title.Margin = new Thickness(0, 0, 8, 0);
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
            Border statusChip = BoostixUi.Chip(status, statusColor);
            statusChip.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(statusChip, 1);
            content.Children.Add(statusChip);

            TimeSpan duration = (report.EndedUtc ?? DateTime.UtcNow) -
                report.StartedUtc;
            string detailText = "Длительность " + FormatDuration(duration);
            if (!string.IsNullOrWhiteSpace(report.WorstResourcePressure))
            {
                detailText += ", память: " +
                    FormatPressure(report.WorstResourcePressure).ToLowerInvariant();
            }
            var detail = MakeText(
                detailText,
                BoostixDesignTokens.MetadataTextSize,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            detail.Margin = new Thickness(0, 3, 8, 0);
            Grid.SetRow(detail, 1);
            content.Children.Add(detail);

            string performance = report.Performance != null &&
                report.Performance.Available
                ? report.Performance.AverageFps.ToString(
                    "0.0",
                    CultureInfo.CurrentCulture) + " FPS"
                : "Открыть";
            var performanceText = MakeText(
                performance,
                BoostixDesignTokens.MetadataTextSize,
                report.Performance != null && report.Performance.Available
                    ? SuccessColor
                    : AccentTextColor,
                semiboldFont,
                FontWeights.SemiBold);
            performanceText.Margin = new Thickness(8, 4, 0, 0);
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
                BoostixUi.AnimateColor(background, ButtonColor, 160, true);
            };
            button.MouseLeave += delegate
            {
                BoostixUi.AnimateColor(
                    background,
                    BoostixDesignTokens.WithAlpha(SurfaceColor, 0),
                    220,
                    true);
            };
            return button;
        }

        private static string FormatSessionStatus(BoostSessionReport report)
        {
            if (report == null)
            {
                return "Нет данных";
            }
            if (!string.IsNullOrWhiteSpace(report.TargetCrashCode) ||
                string.Equals(
                    report.Status,
                    "TargetCrashed",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "Сбой";
            }
            if (string.Equals(report.Status, "Active", StringComparison.OrdinalIgnoreCase))
            {
                return "Boost включён";
            }
            if (string.Equals(report.Status, "Preparing", StringComparison.OrdinalIgnoreCase))
            {
                return "Подготовка";
            }
            if (string.Equals(report.Status, "Interrupted", StringComparison.OrdinalIgnoreCase))
            {
                return "Прервано";
            }
            if (string.Equals(report.Status, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                return "Ошибка";
            }
            return "Завершено";
        }

        private void RenderSettings()
        {
            subtitle.Text =
                "Изменения сохраняются сразу.";
            settingsLoading = true;
            int settingIndex = 0;
            if (!string.IsNullOrWhiteSpace(exportMessageTitle))
            {
                FrameworkElement exportNotice = BuildExportNotice();
                exportNotice.Margin = new Thickness(0, 0, 0, 16);
                pageContent.Children.Add(exportNotice);
            }
            try
            {
                pageContent.Children.Add(MakeSectionHeading("Запуск", null));
                var launch = MakeListCard();
                AddListRow(launch, BuildSettingToggle(
                    settingIndex++,
                    "Проверка перед запуском",
                    "Перед запуском Boost проверять компьютер и предупреждать о проблемах.",
                    settings.CheckBeforeBoost,
                    delegate(bool value) { settings.CheckBeforeBoost = value; }));
                pageContent.Children.Add(launch);

                pageContent.Children.Add(MakeSectionHeading(
                    "Закрывать при запуске Boost",
                    null));
                var hint = MakeText(
                    "Отмеченные программы закроются при запуске Boost. После игры откройте их сами.",
                    BoostixDesignTokens.MetadataTextSize,
                    MutedColor,
                    regularFont,
                    FontWeights.Normal);
                hint.TextWrapping = TextWrapping.Wrap;
                hint.TextTrimming = TextTrimming.None;
                hint.Margin = new Thickness(2, -4, 0, 10);
                pageContent.Children.Add(hint);

                var close = MakeListCard();
                AddListRow(close, BuildSettingToggle(
                    settingIndex++,
                    "Discord",
                    "Закрыть Discord на время сеанса.",
                    !settings.KeepDiscord,
                    delegate(bool value) { settings.KeepDiscord = !value; }));
                AddListRow(close, BuildSettingToggle(
                    settingIndex++,
                    "Epic Games Launcher",
                    "Закрыть Epic Games Launcher.",
                    !settings.KeepEpic,
                    delegate(bool value) { settings.KeepEpic = !value; }));
                AddListRow(close, BuildSettingToggle(
                    settingIndex++,
                    "Steam",
                    "Закрыть Steam. Не включайте, если игра из Steam.",
                    !settings.KeepSteam,
                    delegate(bool value) { settings.KeepSteam = !value; }));
                AddListRow(close, BuildSettingToggle(
                    settingIndex++,
                    "OneDrive",
                    "Приостановить синхронизацию OneDrive на время сеанса.",
                    !settings.KeepOneDrive,
                    delegate(bool value) { settings.KeepOneDrive = !value; }));
                AddListRow(close, BuildSettingToggle(
                    settingIndex++,
                    "Microsoft Teams",
                    "Закрыть Teams.",
                    !settings.KeepTeams,
                    delegate(bool value) { settings.KeepTeams = !value; }));
                AddListRow(close, BuildSettingToggle(
                    settingIndex++,
                    "Wallpaper Engine",
                    "Остановить анимированные обои.",
                    !settings.KeepWallpaper,
                    delegate(bool value) { settings.KeepWallpaper = !value; }));
                AddListRow(close, BuildSettingToggle(
                    settingIndex++,
                    "NVIDIA Overlay",
                    "Закрыть оверлей NVIDIA. Запись клипов будет недоступна.",
                    !settings.KeepNvidiaOverlay,
                    delegate(bool value) { settings.KeepNvidiaOverlay = !value; }));
                pageContent.Children.Add(close);

                pageContent.Children.Add(MakeSectionHeading("Управление", null));
                var control = MakeListCard();
                AddListRow(control, BuildSettingToggle(
                    settingIndex++,
                    "Горячая клавиша " + BoostHotkey.DisplayText,
                    "Включает и выключает Boost прямо из игры.",
                    settings.GlobalHotkey,
                    delegate(bool value) { settings.GlobalHotkey = value; }));
                AddListRow(control, BuildSettingToggle(
                    settingIndex++,
                    "Сворачивать в трей",
                    "Кнопка «Свернуть» прячет окно к часам. Boostix продолжает работать.",
                    settings.MinimizeToTray,
                    delegate(bool value) { settings.MinimizeToTray = value; }));
                pageContent.Children.Add(control);
            }
            finally
            {
                settingsLoading = false;
            }

            pageContent.Children.Add(MakeSectionHeading("Поддержка", null));
            var support = MakeListCard();
            AddListRow(support, BuildDiagnosticsRow());
            pageContent.Children.Add(support);

            var restore = MakeActionButton(
                "Восстановить Windows",
                BoostixButtonKind.Destructive,
                BoostixIcons.Shield);
            restore.Click += delegate { Raise(RestoreRequested); };
            AutomationProperties.SetName(
                restore,
                "Вернуть исходные настройки Windows");
            AutomationProperties.SetAutomationId(
                restore,
                "Boostix.Center.Restore");
            footerButtons.Children.Add(restore);
            preferredFocusButton = restore;

            var pagefile = MakeActionButton(
                "Файл подкачки",
                BoostixButtonKind.Secondary,
                BoostixIcons.File);
            pagefile.Margin = new Thickness(10, 0, 0, 0);
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

        private FrameworkElement BuildDiagnosticsRow()
        {
            var row = new Grid { Margin = new Thickness(0, 12, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0)
            };
            text.Children.Add(MakeText(
                "Диагностика для поддержки",
                BoostixDesignTokens.BodyTextSize + 0.5,
                TextColor,
                semiboldFont,
                FontWeights.SemiBold));
            var detail = MakeText(
                "Один zip-архив с отчётом и журналами Boostix. Имя пользователя, " +
                "личные папки и секреты удаляются перед сохранением.",
                BoostixDesignTokens.MetadataTextSize,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            detail.TextWrapping = TextWrapping.Wrap;
            detail.TextTrimming = TextTrimming.None;
            detail.Margin = new Thickness(0, 3, 0, 0);
            text.Children.Add(detail);
            row.Children.Add(text);

            var collect = MakeActionButton(
                "Собрать диагностику",
                BoostixButtonKind.Secondary,
                BoostixIcons.Export);
            collect.VerticalAlignment = VerticalAlignment.Center;
            collect.Click += delegate { Raise(ExportDiagnosticsRequested); };
            AutomationProperties.SetName(
                collect,
                "Собрать диагностику: отчёт и журналы Boostix в zip-архиве");
            AutomationProperties.SetAutomationId(
                collect,
                "Boostix.Center.CollectDiagnostics");
            Grid.SetColumn(collect, 1);
            row.Children.Add(collect);
            return row;
        }

        private FrameworkElement BuildSettingToggle(
            int index,
            string title,
            string detail,
            bool isChecked,
            Action<bool> apply)
        {
            var toggle = new CheckBox
            {
                MinHeight = 58,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Template = MakeTransparentCheckBoxTemplate(),
                IsChecked = isChecked,
                FocusVisualStyle = MakeKeyboardFocusVisualStyle(BoostixDesignTokens.RadiusSmall)
            };
            AutomationProperties.SetName(toggle, title);
            AutomationProperties.SetHelpText(toggle, detail);
            AutomationProperties.SetAutomationId(
                toggle,
                "Boostix.Center.Setting." +
                index.ToString(CultureInfo.InvariantCulture));
            KeyboardNavigation.SetTabIndex(toggle, 10 + index);

            var content = new Grid
            {
                UseLayoutRounding = true,
                SnapsToDevicePixels = true,
                ClipToBounds = false
            };
            content.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            content.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(44 + ToggleSafeGutter)
            });

            var text = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 10, 8, 10)
            };
            text.Children.Add(MakeText(
                title,
                BoostixDesignTokens.BodyTextSize + 0.5,
                TextColor,
                semiboldFont,
                FontWeights.SemiBold));
            var detailText = MakeText(
                detail,
                BoostixDesignTokens.MetadataTextSize,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            detailText.TextWrapping = TextWrapping.Wrap;
            detailText.TextTrimming = TextTrimming.None;
            detailText.Margin = new Thickness(0, 3, 0, 0);
            text.Children.Add(detailText);
            content.Children.Add(text);

            FrameworkElement track = BuildSwitchTrack(toggle, isChecked);
            track.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(track, 1);
            content.Children.Add(track);

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

        /// <summary>
        /// Compact labelled switch used inside list rows.
        /// </summary>
        private CheckBox BuildSwitch(string label, bool isChecked, Action<bool> apply)
        {
            var toggle = new CheckBox
            {
                MinHeight = BoostixDesignTokens.MinimumActionHeight,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Template = MakeTransparentCheckBoxTemplate(),
                IsChecked = isChecked,
                FocusVisualStyle = MakeKeyboardFocusVisualStyle(BoostixDesignTokens.RadiusSmall)
            };
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            var text = MakeText(
                label,
                BoostixDesignTokens.MetadataTextSize,
                SecondaryColor,
                semiboldFont,
                FontWeights.SemiBold);
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(0, 0, 10, 0);
            content.Children.Add(text);
            content.Children.Add(BuildSwitchTrack(toggle, isChecked));
            toggle.Content = content;
            toggle.Checked += delegate
            {
                AnimateToggle(toggle);
                apply(true);
            };
            toggle.Unchecked += delegate
            {
                AnimateToggle(toggle);
                apply(false);
            };
            toggle.MouseEnter += delegate { AnimateToggle(toggle); };
            toggle.MouseLeave += delegate { AnimateToggle(toggle); };
            return toggle;
        }

        private static FrameworkElement BuildSwitchTrack(CheckBox owner, bool isChecked)
        {
            var trackBrush = new SolidColorBrush(
                isChecked ? AccentColor : ButtonColor);
            var track = new Border
            {
                Width = 44,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = trackBrush,
                BorderBrush = new SolidColorBrush(BoostixDesignTokens.WithAlpha(Colors.White, 18)),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true,
                ClipToBounds = false
            };

            var knobBrush = new SolidColorBrush(
                isChecked
                    ? BoostixDesignTokens.ToggleKnobOn
                    : BoostixDesignTokens.ToggleKnobOff);
            var knob = new Ellipse
            {
                Width = 18,
                Height = 18,
                Margin = new Thickness(2, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = knobBrush,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true
            };
            var knobTranslation = new TranslateTransform(isChecked ? 20 : 0, 0);
            knob.RenderTransform = knobTranslation;
            track.Child = knob;
            owner.Tag = new ToggleVisuals
            {
                TrackBrush = trackBrush,
                KnobBrush = knobBrush,
                KnobTranslation = knobTranslation
            };
            return track;
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
            double targetX = active ? 20 : 0;
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
                new ColorAnimation(targetColor, TimeSpan.FromMilliseconds(BoostixDesignTokens.MotionStandardMilliseconds))
                {
                    EasingFunction = ease
                });
            visuals.KnobBrush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(knobColor, TimeSpan.FromMilliseconds(BoostixDesignTokens.MotionStandardMilliseconds))
                {
                    EasingFunction = ease
                });
            visuals.KnobTranslation.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(BoostixDesignTokens.MotionStandardMilliseconds))
                {
                    EasingFunction = ease
                });
        }

        private FrameworkElement MakeSectionHeading(string title, string trailing)
        {
            return MakeSectionHeading(
                title,
                trailing,
                null);
        }

        private FrameworkElement MakeSectionHeading(
            string title,
            string trailing,
            FrameworkElement trailingElement)
        {
            var heading = new Grid
            {
                Margin = new Thickness(0, 6, 0, 10)
            };
            heading.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleText = MakeText(
                title,
                BoostixDesignTokens.SectionTitleSize,
                TextColor,
                semiboldFont,
                FontWeights.SemiBold);
            titleText.VerticalAlignment = VerticalAlignment.Center;
            heading.Children.Add(titleText);
            FrameworkElement right = trailingElement;
            if (right == null && !string.IsNullOrEmpty(trailing))
            {
                right = BoostixUi.Chip(trailing, SecondaryColor);
            }
            if (right != null)
            {
                Grid.SetColumn(right, 1);
                heading.Children.Add(right);
            }
            return heading;
        }

        private StackPanel MakeListCard()
        {
            return new StackPanel();
        }

        /// <summary>
        /// Rows live in one card separated by hairline dividers. The card is
        /// created lazily so a list without rows renders nothing.
        /// </summary>
        private void AddListRow(StackPanel list, FrameworkElement row)
        {
            if (list.Tag == null)
            {
                list.Margin = new Thickness(0, 0, 0, 18);
                list.Tag = true;
            }
            var rowHost = new Border
            {
                Background = new SolidColorBrush(SurfaceColor),
                BorderBrush = new SolidColorBrush(BorderColor),
                Padding = new Thickness(16, 0, 14, 0),
                Child = row
            };
            bool first = list.Children.Count == 0;
            if (first)
            {
                rowHost.BorderThickness = new Thickness(1);
                rowHost.CornerRadius = new CornerRadius(
                    BoostixDesignTokens.RadiusMedium,
                    BoostixDesignTokens.RadiusMedium,
                    BoostixDesignTokens.RadiusMedium,
                    BoostixDesignTokens.RadiusMedium);
            }
            else
            {
                var previous = (Border)list.Children[list.Children.Count - 1];
                bool previousWasFirst = list.Children.Count == 1;
                previous.CornerRadius = previousWasFirst
                    ? new CornerRadius(
                        BoostixDesignTokens.RadiusMedium,
                        BoostixDesignTokens.RadiusMedium,
                        0,
                        0)
                    : new CornerRadius(0);
                previous.BorderThickness = previousWasFirst
                    ? new Thickness(1, 1, 1, 0)
                    : new Thickness(1, 0, 1, 0);
                rowHost.BorderThickness = new Thickness(1, 0, 1, 1);
                rowHost.CornerRadius = new CornerRadius(
                    0,
                    0,
                    BoostixDesignTokens.RadiusMedium,
                    BoostixDesignTokens.RadiusMedium);
                var divider = new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(DividerColor),
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0)
                };
                var withDivider = new Grid();
                rowHost.Child = null;
                withDivider.Children.Add(row);
                withDivider.Children.Add(divider);
                rowHost.Child = withDivider;
            }
            list.Children.Add(rowHost);
        }

        private Border MakeNotice(
            string iconData,
            string title,
            string detail,
            Color tone)
        {
            var notice = BoostixUi.Card(new Thickness(14, 12, 16, 13));
            notice.Margin = new Thickness(0, 0, 0, 12);
            notice.BorderBrush = new SolidColorBrush(
                BoostixDesignTokens.WithAlpha(tone, 90));
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            FrameworkElement badge = MakeIconBadge(iconData, tone, 30);
            badge.VerticalAlignment = VerticalAlignment.Top;
            grid.Children.Add(badge);
            var texts = new StackPanel
            {
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            texts.Children.Add(MakeText(
                title,
                BoostixDesignTokens.BodyTextSize + 0.5,
                TextColor,
                semiboldFont,
                FontWeights.SemiBold));
            var detailText = MakeText(
                detail,
                BoostixDesignTokens.MetadataTextSize,
                SecondaryColor,
                regularFont,
                FontWeights.Normal);
            detailText.TextWrapping = TextWrapping.Wrap;
            detailText.TextTrimming = TextTrimming.None;
            detailText.Margin = new Thickness(0, 3, 0, 0);
            texts.Children.Add(detailText);
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);
            notice.Child = grid;
            return notice;
        }

        private static FrameworkElement MakeIconBadge(string iconData, Color tone, double size)
        {
            FrameworkElement icon = BoostixIcons.Create(
                iconData,
                size * 0.55,
                new SolidColorBrush(tone),
                2.2);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2),
                Background = new SolidColorBrush(
                    SystemParameters.HighContrast
                        ? SurfaceColor
                        : BoostixDesignTokens.WithAlpha(tone, 34)),
                Child = icon
            };
        }

        private static FrameworkElement MakeTargetIcon(string executablePath, double size)
        {
            var frame = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(Math.Round(size * 0.28)),
                Background = new SolidColorBrush(ButtonColor),
                BorderBrush = new SolidColorBrush(BorderColor),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center
            };
            ImageSource image = BoostixUi.ExecutableIcon(executablePath);
            if (image != null)
            {
                var picture = new Image
                {
                    Source = image,
                    Width = size * 0.66,
                    Height = size * 0.66,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
                frame.Child = picture;
            }
            else
            {
                FrameworkElement glyph = BoostixIcons.Create(
                    BoostixIcons.Gamepad,
                    size * 0.5,
                    MutedColor);
                glyph.HorizontalAlignment = HorizontalAlignment.Center;
                glyph.VerticalAlignment = VerticalAlignment.Center;
                frame.Child = glyph;
            }
            return frame;
        }

        /// <summary>
        /// Empty sections say what to do next, left-aligned like the rest of
        /// the page; the action itself sits in the footer.
        /// </summary>
        private Border MakeEmptyState(string iconData, string title, string detail)
        {
            var host = BoostixUi.Card(new Thickness(20, 18, 20, 20));
            host.Margin = new Thickness(0, 0, 0, 16);
            var content = new StackPanel();
            var titleText = MakeText(
                title,
                BoostixDesignTokens.SectionTitleSize,
                TextColor,
                semiboldFont,
                FontWeights.SemiBold);
            content.Children.Add(titleText);
            var detailText = MakeText(
                detail,
                BoostixDesignTokens.BodyTextSize,
                SecondaryColor,
                regularFont,
                FontWeights.Normal);
            detailText.TextWrapping = TextWrapping.Wrap;
            detailText.TextTrimming = TextTrimming.None;
            detailText.MaxWidth = 460;
            detailText.HorizontalAlignment = HorizontalAlignment.Left;
            detailText.Margin = new Thickness(0, 6, 0, 0);
            content.Children.Add(detailText);
            host.Child = content;
            AutomationProperties.SetName(host, title + ". " + detail);
            return host;
        }

        private Button MakeActionButton(
            string text,
            BoostixButtonKind kind,
            string iconData)
        {
            return BoostixUi.ActionButton(text, kind, iconData);
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
            chrome.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
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
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(BoostixDesignTokens.RadiusSmall));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
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
            return BoostixUi.FocusVisual(radius);
        }

        internal static Style MakeBoostixVerticalScrollBarStyle()
        {
            string trackColor = SystemParameters.HighContrast
                ? ColorToXaml(BackgroundColor)
                : "#00000000";
            string thumbColor = SystemParameters.HighContrast
                ? ColorToXaml(AccentColor)
                : ColorToXaml(BoostixDesignTokens.Hover);
            string thumbHoverColor = SystemParameters.HighContrast
                ? ColorToXaml(AccentColor)
                : ColorToXaml(BoostixDesignTokens.MutedText);
            string thumbDragColor = SystemParameters.HighContrast
                ? ColorToXaml(AccentColor)
                : ColorToXaml(BoostixDesignTokens.SecondaryText);
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
                glyph = BoostixIcons.Check;
                color = SuccessColor;
            }
            else if (severity == BoostCheckSeverity.Warning)
            {
                glyph = BoostixIcons.Alert;
                color = WarningColor;
            }
            else if (severity == BoostCheckSeverity.Blocked)
            {
                glyph = BoostixIcons.Close;
                color = ErrorColor;
            }
            else if (severity == BoostCheckSeverity.Info)
            {
                glyph = BoostixIcons.Info;
                color = AccentTextColor;
            }
            else
            {
                glyph = BoostixIcons.Info;
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
                glyph = BoostixIcons.Check;
                color = SuccessColor;
            }
            else if (outcome == BoostActionOutcome.Failed)
            {
                glyph = BoostixIcons.Close;
                color = ErrorColor;
            }
            else if (outcome == BoostActionOutcome.Preserved ||
                     outcome == BoostActionOutcome.ExternalOverridePreserved)
            {
                glyph = BoostixIcons.Shield;
                color = AccentTextColor;
            }
            else
            {
                glyph = BoostixIcons.Minimize;
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
                    return "Норма";
                case DiagnosticPressureLevel.Elevated:
                    return "Повышенная нагрузка";
                case DiagnosticPressureLevel.Critical:
                    return "Критическая нагрузка";
                default:
                    return "Нет данных";
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
                    return "Windows почти исчерпала резерв памяти. Освободите место на диске C: или увеличьте файл подкачки.";
                }
                if (snapshot.GpuUsageAvailable &&
                    snapshot.GpuTotalAvailable &&
                    snapshot.GpuDedicatedTotalBytes > 0 &&
                    (double)snapshot.GpuDedicatedUsageBytes /
                        snapshot.GpuDedicatedTotalBytes >= 0.95)
                {
                    return "Видеопамять почти заполнена. Снизьте качество текстур, чтобы не было подлагиваний.";
                }
                return "Оперативной памяти почти не осталось.";
            }
            if (snapshot.Pressure == DiagnosticPressureLevel.Elevated)
            {
                return "Памяти маловато. Закройте тяжёлые программы перед игрой.";
            }
            if (snapshot.Pressure == DiagnosticPressureLevel.Normal)
            {
                return "Памяти достаточно.";
            }
            return "Часть показателей недоступна в этой версии Windows или драйвера.";
        }

        internal static string FormatBytesCompact(long bytes)
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
                return "Не активен";
            }
            return "Недоступно";
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
            var block = new TextBlock
            {
                Text = text ?? string.Empty,
                FontSize = Math.Max(
                    BoostixDesignTokens.OverlineTextSize,
                    size),
                FontFamily = font,
                FontWeight = weight,
                Foreground = new SolidColorBrush(color),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            TextOptions.SetTextFormattingMode(block, TextFormattingMode.Display);
            return block;
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
