using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Boostix
{
    internal sealed class ShelfAutoEventArgs : EventArgs
    {
        public ShelfAutoEventArgs(ShelfGame game, bool enabled)
        {
            Game = game;
            Enabled = enabled;
        }

        public ShelfGame Game { get; private set; }
        public bool Enabled { get; private set; }
    }

    internal sealed class SessionReportEventArgs : EventArgs
    {
        public SessionReportEventArgs(BoostSessionReport report, ShelfGame game)
        {
            Report = report;
            Game = game;
        }

        public BoostSessionReport Report { get; private set; }
        public ShelfGame Game { get; private set; }
    }

    internal sealed class StageArtEventArgs : EventArgs
    {
        public StageArtEventArgs(StageArt art)
        {
            Art = art;
        }

        public StageArt Art { get; private set; }
    }

    /// <summary>
    /// What the stage shows while a game runs with Boost.
    /// </summary>
    internal sealed class StageLiveState
    {
        public ShelfGame Game;
        public string Name;
        public DateTime StartedUtc;
        public long AvailableBytes;
        public long TotalBytes;
        public int CpuPercent = -1;
        public long GpuUsedBytes;
        public long GpuTotalBytes;
        public bool LowMemory;
        public IList<SessionTimelinePoint> Timeline;
    }

    /// <summary>
    /// The main screen: the whole window dresses as the selected game. Its art
    /// fills the stage, its colours repaint the actions, and a rail of covers
    /// runs along the floor. While a game runs the same stage shows the live
    /// session instead.
    /// </summary>
    internal sealed class GameStage : Grid
    {
        internal const double TopInset = 64;
        private const double HeroHeight = 512;
        private const double HeroBleed = 26;
        private const double SideInset = 44;
        private const double CoverWidth = 98;
        private const double CoverHeight = 147;
        private const double CoverGap = 14;
        private const double SelectedCoverScale = 1.1;
        private const double RailHeight = 200;
        private const int ArtFadeMilliseconds = 560;
        private const int ArtSettleSeconds = 8;
        private const int LaunchTimeoutSeconds = 120;

        private readonly FontFamily regularFont;
        private readonly FontFamily displayFont;
        private readonly FontFamily condensedFont;
        private readonly Func<string, bool, Action<bool>, CheckBox> makeSwitch;
        private readonly SolidColorBrush floorBrush;
        private readonly SolidColorBrush accentBrush;
        private readonly SolidColorBrush accentRingBrush;
        private readonly List<KeyValuePair<GradientStop, byte>> scrimStops =
            new List<KeyValuePair<GradientStop, byte>>();
        private readonly Grid heroHost;
        private readonly TranslateTransform heroParallax = new TranslateTransform();
        private readonly TranslateTransform heroDrift = new TranslateTransform();
        private readonly TranslateTransform infoParallax = new TranslateTransform();
        private Border heroFront;
        private Border heroBack;
        private readonly Grid stageContent;
        private readonly StackPanel info;
        private readonly Border lastGameHost;
        private readonly Grid railHost;
        private readonly ScrollViewer railScroller;
        private readonly StackPanel rail;
        private readonly Grid liveLayer;
        private readonly HorizontalScrollProxy railScroll;
        private readonly DispatcherTimer launchTimer;
        private readonly Dictionary<string, RailItem> railItems =
            new Dictionary<string, RailItem>(StringComparer.Ordinal);

        private List<ShelfGame> games = new List<ShelfGame>();
        private ShelfGame selected;
        private StageArt currentArt;
        private string railSignature = string.Empty;
        private string infoSignature = string.Empty;
        private string message;
        private bool messageIsError;
        private bool scanning;
        private bool backgroundTip;
        private bool windowActive = true;
        private TranslateTransform launchRunner;
        private int artGeneration;
        private ShelfGame launching;
        private DateTime launchStartedUtc;
        private StageLiveState live;
        private Button playButton;
        private TextBlock liveTimer;
        private TextBlock liveStateText;
        private Border liveChartHost;
        private int liveChartPoints = -1;
        private LiveMeter memoryMeter;
        private LiveMeter cpuMeter;
        private LiveMeter gpuMeter;
        private FrameworkElement liveHeading;
        private string liveHeadingKey;

        private sealed class RailItem
        {
            public ShelfGame Game;
            public Button Button;
            public Border Ring;
            public ScaleTransform Scale;
        }

        private sealed class LiveMeter
        {
            public string Title;
            public TextBlock Value;
            public Border Fill;
            public Grid Track;
        }

        /// <summary>
        /// Animates ScrollViewer.HorizontalOffset, which has no setter of its
        /// own that WPF could animate.
        /// </summary>
        private sealed class HorizontalScrollProxy : FrameworkElement
        {
            public static readonly DependencyProperty OffsetProperty =
                DependencyProperty.Register(
                    "Offset",
                    typeof(double),
                    typeof(HorizontalScrollProxy),
                    new PropertyMetadata(0.0, OffsetChanged));

            private readonly ScrollViewer target;

            public HorizontalScrollProxy(ScrollViewer scroller)
            {
                target = scroller;
            }

            private static void OffsetChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
            {
                ((HorizontalScrollProxy)owner).target.ScrollToHorizontalOffset((double)e.NewValue);
            }
        }

        public GameStage(
            FontFamily normalFont,
            FontFamily boldFont,
            Func<string, bool, Action<bool>, CheckBox> switchFactory)
        {
            regularFont = normalFont;
            displayFont = boldFont;
            condensedFont = new FontFamily("Bahnschrift SemiBold Condensed, Bahnschrift, Segoe UI");
            makeSwitch = switchFactory;

            Color startFloor = BoostixDesignTokens.Background;
            floorBrush = new SolidColorBrush(startFloor);
            accentBrush = new SolidColorBrush(BoostixDesignTokens.Accent);
            accentRingBrush = new SolidColorBrush(BoostixDesignTokens.Accent);
            Background = floorBrush;
            ClipToBounds = true;
            Focusable = false;
            AutomationProperties.SetAutomationId(this, "Boostix.Games");
            AutomationProperties.SetName(this, L.T("Ваши игры"));

            var heroTransform = new TransformGroup();
            heroTransform.Children.Add(heroDrift);
            heroTransform.Children.Add(heroParallax);
            heroHost = new Grid
            {
                Height = HeroHeight + HeroBleed,
                Margin = new Thickness(-HeroBleed, -HeroBleed, -HeroBleed, 0),
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                ClipToBounds = true,
                RenderTransform = heroTransform
            };
            heroBack = new Border { Opacity = 0 };
            heroFront = new Border { Opacity = 0 };
            heroBack.RenderTransformOrigin = new Point(0.5, 0.5);
            heroFront.RenderTransformOrigin = new Point(0.5, 0.5);
            heroBack.RenderTransform = new ScaleTransform(1, 1);
            heroFront.RenderTransform = new ScaleTransform(1, 1);
            heroHost.Children.Add(heroBack);
            heroHost.Children.Add(heroFront);
            Children.Add(heroHost);

            Children.Add(MakeScrim(new Point(0, 0.5), new Point(1, 0.5), new[]
            {
                new KeyValuePair<double, byte>(0, 244),
                new KeyValuePair<double, byte>(0.34, 196),
                new KeyValuePair<double, byte>(0.62, 40),
                new KeyValuePair<double, byte>(0.8, 0)
            }));
            Children.Add(MakeScrim(new Point(0.5, 0), new Point(0.5, 1), new[]
            {
                new KeyValuePair<double, byte>(0, 205),
                new KeyValuePair<double, byte>(0.24, 0),
                new KeyValuePair<double, byte>(0.5, 0),
                new KeyValuePair<double, byte>(0.86, 235),
                new KeyValuePair<double, byte>(1, 255)
            }));

            stageContent = new Grid();
            stageContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TopInset) });
            stageContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            stageContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RailHeight) });
            Children.Add(stageContent);

            var middle = new Grid { Margin = new Thickness(SideInset, 0, SideInset - 8, 0) };
            middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            middle.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetRow(middle, 1);
            stageContent.Children.Add(middle);

            info = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Left,
                MaxWidth = 600,
                RenderTransform = infoParallax
            };
            middle.Children.Add(info);

            lastGameHost = new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(lastGameHost, 1);
            middle.Children.Add(lastGameHost);

            railHost = new Grid();
            Grid.SetRow(railHost, 2);
            stageContent.Children.Add(railHost);
            rail = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(SideInset, 0, SideInset, 22)
            };
            AutomationProperties.SetAutomationId(rail, "Boostix.Games.Shelf");
            AutomationProperties.SetName(rail, L.T("Полка игр"));
            railScroller = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = rail,
                Focusable = false,
                CanContentScroll = false
            };
            railScroller.PreviewMouseWheel += RailMouseWheel;
            railScroll = new HorizontalScrollProxy(railScroller);
            railHost.Children.Add(railScroller);

            liveLayer = new Grid
            {
                Visibility = Visibility.Collapsed,
                Opacity = 0
            };
            AutomationProperties.SetAutomationId(liveLayer, "Boostix.Games.Live");
            Grid.SetRow(liveLayer, 1);
            Grid.SetRowSpan(liveLayer, 2);
            stageContent.Children.Add(liveLayer);

            launchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            launchTimer.Tick += delegate { CheckLaunchTimeout(); };

            MouseMove += StageMouseMove;
            MouseLeave += delegate { MoveParallax(0, 0); };
            IsVisibleChanged += delegate { ApplyWindowState(); };
            PreviewKeyDown += StagePreviewKeyDown;
        }

        public event EventHandler<ShelfGameEventArgs> PlayRequested;
        public event EventHandler<ShelfAutoEventArgs> AutoChanged;
        public event EventHandler<ShelfGameEventArgs> RemoveRequested;
        public event EventHandler<ShelfGameEventArgs> PictureRequested;
        public event EventHandler<SessionReportEventArgs> DebriefRequested;
        public event EventHandler<StageArtEventArgs> ArtChanged;
        public event EventHandler OtherGameRequested;
        public event EventHandler StopRequested;
        public event EventHandler StartWithWindowsRequested;

        internal Button PreferredButton
        {
            get { return playButton; }
        }

        internal StageArt CurrentArt
        {
            get { return currentArt; }
        }

        internal ShelfGame SelectedGame
        {
            get { return selected; }
        }

        internal bool IsLive
        {
            get { return live != null; }
        }

        /// <summary>
        /// Publishes the shelf. The rail is rebuilt only when its games change,
        /// so the window can call this as often as its data refreshes.
        /// </summary>
        public void SetGames(IEnumerable<ShelfGame> shelf, string selectedKey, bool isScanning)
        {
            games = (shelf ?? Enumerable.Empty<ShelfGame>()).ToList();
            scanning = isScanning;
            string signature = string.Join("|", games.Select(game =>
                game.Key + (game.AutoBoost ? "+" : "-")).ToArray());
            if (!string.Equals(signature, railSignature, StringComparison.Ordinal))
            {
                railSignature = signature;
                BuildRail();
            }

            ShelfGame next = games.FirstOrDefault(game =>
                string.Equals(game.Key, selectedKey ?? (selected == null ? null : selected.Key), StringComparison.Ordinal)) ??
                games.FirstOrDefault();
            if (next == null)
            {
                selected = null;
                ShowEmptyStage();
                return;
            }
            bool sameGame = selected != null &&
                string.Equals(selected.Key, next.Key, StringComparison.Ordinal);
            selected = next;
            if (sameGame && currentArt != null)
            {
                RefreshInfo();
                return;
            }
            UpdateRailSelection(false);
            LoadArt(next, false);
        }

        /// <summary>
        /// Redraws the selected game and its cover after its art changed.
        /// </summary>
        public void ReloadArt()
        {
            railSignature = string.Empty;
            string key = selected == null ? null : selected.Key;
            currentArt = null;
            SetGames(games, key, scanning);
        }

        public void SetMessage(string text, bool isError)
        {
            message = text;
            messageIsError = isError;
            RefreshInfo();
        }

        /// <summary>
        /// Shows a short note that Auto needs Boostix running, with a way to
        /// start it with Windows.
        /// </summary>
        public void SetBackgroundTip(bool show)
        {
            if (backgroundTip == show)
            {
                return;
            }
            backgroundTip = show;
            RefreshInfo();
        }

        /// <summary>
        /// Nothing on the stage moves while a game has the screen.
        /// </summary>
        public void SetWindowActive(bool active)
        {
            windowActive = active;
            ApplyWindowState();
        }

        private void ApplyWindowState()
        {
            if (!windowActive || !IsVisible)
            {
                heroDrift.BeginAnimation(TranslateTransform.XProperty, null);
            }
            UpdateLaunchRunner();
        }

        public void SetLaunching(ShelfGame game)
        {
            launching = game;
            launchStartedUtc = DateTime.UtcNow;
            message = null;
            if (game == null)
            {
                launchTimer.Stop();
            }
            else
            {
                launchTimer.Start();
            }
            UpdateLaunchRunner();
            infoSignature = string.Empty;
            RefreshInfo();
        }

        private void CheckLaunchTimeout()
        {
            if (launching == null)
            {
                launchTimer.Stop();
                return;
            }
            if (DateTime.UtcNow - launchStartedUtc >= TimeSpan.FromSeconds(LaunchTimeoutSeconds))
            {
                ShelfGame game = launching;
                SetLaunching(null);
                SetMessage(
                    string.Format(
                        CultureInfo.CurrentCulture,
                        L.T("{0} ещё не открылась. Boost включится сам, как только игра появится."),
                        game.Name),
                    false);
            }
        }

        // ---------------------------------------------------------------
        // Art.
        // ---------------------------------------------------------------

        private Rectangle MakeScrim(Point start, Point end, KeyValuePair<double, byte>[] stops)
        {
            var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
            foreach (KeyValuePair<double, byte> stop in stops)
            {
                var gradient = new GradientStop(
                    BoostixDesignTokens.WithAlpha(BoostixDesignTokens.Background, stop.Value),
                    stop.Key);
                brush.GradientStops.Add(gradient);
                scrimStops.Add(new KeyValuePair<GradientStop, byte>(gradient, stop.Value));
            }
            return new Rectangle
            {
                Height = HeroHeight,
                VerticalAlignment = VerticalAlignment.Top,
                Fill = brush,
                IsHitTestVisible = false
            };
        }

        private void LoadArt(ShelfGame game, bool animate)
        {
            int generation = ++artGeneration;
            Task.Run(delegate
            {
                try
                {
                    return GameArtwork.Stage(game);
                }
                catch (Exception ex)
                {
                    CrashLog.Write("Could not prepare the art of " + game.Name + ".", ex);
                    return null;
                }
            }).ContinueWith(delegate(Task<StageArt> task)
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (generation == artGeneration)
                        {
                            ApplyArt(game, task.Result ?? new StageArt
                            {
                                Accent = BoostixDesignTokens.Accent,
                                Floor = BoostixDesignTokens.Background
                            }, animate);
                        }
                    }));
                }
                catch (InvalidOperationException) { }
                catch (TaskCanceledException) { }
            }, TaskScheduler.Default);
        }

        private void ApplyArt(ShelfGame game, StageArt art, bool animate)
        {
            currentArt = art;
            bool motion = animate && BoostixDesignTokens.MotionEnabled;
            bool plain = SystemParameters.HighContrast;

            Border next = heroBack;
            heroBack = heroFront;
            heroFront = next;
            ImageSource picture = plain ? null : (art.Hero ?? art.Ambient);
            next.Background = picture == null
                ? null
                : new ImageBrush(picture)
                {
                    Stretch = Stretch.UniformToFill,
                    AlignmentX = art.Hero == null ? AlignmentX.Center : AlignmentX.Right,
                    AlignmentY = AlignmentY.Center
                };
            // Without real art the picture is a tiny sample: blurring it turns
            // it into a soft wash of the game's colours.
            next.Effect = picture != null && art.Hero == null
                ? new BlurEffect { Radius = 60 }
                : null;
            double targetOpacity = picture == null ? 0 : (art.Hero == null ? 0.7 : 1);
            Panel.SetZIndex(next, 1);
            Panel.SetZIndex(heroBack, 0);
            var scale = (ScaleTransform)next.RenderTransform;
            if (motion)
            {
                TimeSpan fade = TimeSpan.FromMilliseconds(ArtFadeMilliseconds);
                next.BeginAnimation(OpacityProperty, new DoubleAnimation(0, targetOpacity, fade)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
                heroBack.BeginAnimation(OpacityProperty, new DoubleAnimation(0, fade));
                var zoom = new DoubleAnimation(1.05, 1, TimeSpan.FromMilliseconds(ArtFadeMilliseconds + 420))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
            }
            else
            {
                next.BeginAnimation(OpacityProperty, null);
                next.Opacity = targetOpacity;
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = 1;
                scale.ScaleY = 1;
                heroBack.BeginAnimation(OpacityProperty, null);
                heroBack.Opacity = 0;
            }

            Color accent = plain ? BoostixDesignTokens.Accent : art.Accent;
            Color floor = plain ? BoostixDesignTokens.Background : art.Floor;
            AnimateBrush(accentBrush, accent, motion);
            AnimateBrush(accentRingBrush, accent, motion);
            AnimateBrush(floorBrush, floor, motion);
            foreach (KeyValuePair<GradientStop, byte> stop in scrimStops)
            {
                Color target = BoostixDesignTokens.WithAlpha(floor, stop.Value);
                stop.Key.BeginAnimation(GradientStop.ColorProperty, null);
                if (motion)
                {
                    Color from = stop.Key.Color;
                    stop.Key.Color = target;
                    stop.Key.BeginAnimation(
                        GradientStop.ColorProperty,
                        new ColorAnimation(from, target, TimeSpan.FromMilliseconds(ArtFadeMilliseconds))
                        {
                            FillBehavior = FillBehavior.Stop
                        });
                }
                else
                {
                    stop.Key.Color = target;
                }
            }

            infoSignature = string.Empty;
            RefreshInfo();
            if (motion)
            {
                AnimateInfoIn();
                SettleArt();
            }
            EventHandler<StageArtEventArgs> handler = ArtChanged;
            if (handler != null)
            {
                handler(this, new StageArtEventArgs(art));
            }
        }

        private static void AnimateBrush(SolidColorBrush brush, Color target, bool animate)
        {
            Color from = brush.Color;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = target;
            if (animate && from != target)
            {
                brush.BeginAnimation(
                    SolidColorBrush.ColorProperty,
                    new ColorAnimation(from, target, TimeSpan.FromMilliseconds(ArtFadeMilliseconds))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                        FillBehavior = FillBehavior.Stop
                    });
            }
        }

        /// <summary>
        /// New art slides into place once and stops. It must never loop: a
        /// moving picture redraws the whole window on every frame, which cost
        /// nearly half a processor core.
        /// </summary>
        private void SettleArt()
        {
            heroDrift.BeginAnimation(TranslateTransform.XProperty, null);
            if (!BoostixDesignTokens.MotionEnabled ||
                SystemParameters.HighContrast ||
                !IsVisible ||
                !windowActive ||
                currentArt == null ||
                currentArt.Hero == null)
            {
                return;
            }
            heroDrift.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(-HeroBleed * 0.5, 0, TimeSpan.FromSeconds(ArtSettleSeconds))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop
                });
        }

        private void StageMouseMove(object sender, MouseEventArgs e)
        {
            if (!BoostixDesignTokens.MotionEnabled || ActualWidth < 1 || live != null)
            {
                return;
            }
            Point position = e.GetPosition(this);
            double x = (position.X / ActualWidth - 0.5) * 2;
            double y = (position.Y / ActualHeight - 0.5) * 2;
            MoveParallax(-x * 11, -y * 6);
        }

        private void MoveParallax(double x, double y)
        {
            if (!BoostixDesignTokens.MotionEnabled)
            {
                return;
            }
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            TimeSpan time = TimeSpan.FromMilliseconds(420);
            heroParallax.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, time) { EasingFunction = ease });
            heroParallax.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, time) { EasingFunction = ease });
            infoParallax.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-x * 0.28, time) { EasingFunction = ease });
            infoParallax.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-y * 0.28, time) { EasingFunction = ease });
        }

        // ---------------------------------------------------------------
        // Rail of covers.
        // ---------------------------------------------------------------

        private void BuildRail()
        {
            rail.Children.Clear();
            railItems.Clear();
            int index = 0;
            foreach (ShelfGame game in games)
            {
                RailItem item = BuildCover(game, index++);
                railItems[game.Key] = item;
                rail.Children.Add(item.Button);
            }
            rail.Children.Add(BuildOtherGameTile());
            UpdateRailSelection(false);
        }

        private RailItem BuildCover(ShelfGame game, int index)
        {
            var cover = new Border
            {
                Width = CoverWidth,
                Height = CoverHeight,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(BoostixDesignTokens.Surface)
            };
            ImageSource art = GameArtwork.Cover(game);
            if (art != null)
            {
                cover.Background = new ImageBrush(art)
                {
                    Stretch = Stretch.UniformToFill,
                    AlignmentY = AlignmentY.Top
                };
            }
            var layers = new Grid();
            if (!GameArtwork.HasOwnCover(game))
            {
                ImageSource gameIcon = GameArtwork.Icon(game);
                if (gameIcon != null)
                {
                    var iconImage = new Image
                    {
                        Source = gameIcon,
                        Width = 58,
                        Height = 58,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 24, 0, 0)
                    };
                    RenderOptions.SetBitmapScalingMode(iconImage, BitmapScalingMode.HighQuality);
                    layers.Children.Add(iconImage);
                }
                // Boostix art carries the title the way a real cover would.
                var title = Label(game.Name, 13, Color.FromRgb(238, 244, 250), displayFont, FontWeights.SemiBold);
                title.TextWrapping = TextWrapping.Wrap;
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                title.MaxHeight = 58;
                title.VerticalAlignment = VerticalAlignment.Bottom;
                title.Margin = new Thickness(9, 0, 9, 10);
                layers.Children.Add(title);
            }
            if (game.AutoBoost)
            {
                var badge = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(11),
                    Background = new SolidColorBrush(BoostixDesignTokens.WithAlpha(Color.FromRgb(8, 17, 29), 214)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 6, 6, 0)
                };
                FrameworkElement icon = BoostixIcons.Create(BoostixIcons.Zap, 13, accentBrush, 2);
                icon.HorizontalAlignment = HorizontalAlignment.Center;
                icon.VerticalAlignment = VerticalAlignment.Center;
                badge.Child = icon;
                layers.Children.Add(badge);
            }
            cover.Child = layers;

            var ring = new Border
            {
                CornerRadius = new CornerRadius(11),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Padding = new Thickness(0),
                Child = cover
            };
            var scale = new ScaleTransform(1, 1);
            var button = new Button
            {
                Content = ring,
                Margin = new Thickness(0, 0, CoverGap, 0),
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Template = BoostCenterOverlay.FlatButtonTemplate(11),
                FocusVisualStyle = BoostixUi.FocusVisual(11),
                VerticalAlignment = VerticalAlignment.Bottom,
                RenderTransformOrigin = new Point(0.5, 1),
                RenderTransform = scale,
                Tag = game.Key
            };
            AutomationProperties.SetName(button, game.Name + ". " + DescribeShort(game));
            AutomationProperties.SetAutomationId(
                button,
                "Boostix.Games.Card." + index.ToString(CultureInfo.InvariantCulture));
            button.MouseEnter += delegate { UpdateCover(game.Key, true); };
            button.MouseLeave += delegate { UpdateCover(game.Key, true); };
            button.Click += delegate { SelectGame(game, true); };
            button.GotKeyboardFocus += delegate { BringIntoView(game.Key, true); };
            return new RailItem { Game = game, Button = button, Ring = ring, Scale = scale };
        }

        private Button BuildOtherGameTile()
        {
            var inner = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            FrameworkElement plus = BoostixIcons.Create(BoostixIcons.Plus, 22, BoostixDesignTokens.SecondaryText);
            plus.HorizontalAlignment = HorizontalAlignment.Center;
            inner.Children.Add(plus);
            var text = Label(L.T("Другая программа"), 12, BoostixDesignTokens.SecondaryText, regularFont, FontWeights.Normal);
            text.TextWrapping = TextWrapping.Wrap;
            text.TextAlignment = TextAlignment.Center;
            text.Margin = new Thickness(8, 8, 8, 0);
            inner.Children.Add(text);
            var tile = new Border
            {
                Width = CoverWidth,
                Height = CoverHeight,
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(BoostixDesignTokens.WithAlpha(Colors.White, 44)),
                Background = new SolidColorBrush(BoostixDesignTokens.WithAlpha(Colors.White, 10)),
                Child = inner
            };
            var button = new Button
            {
                Content = tile,
                Margin = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Template = BoostCenterOverlay.FlatButtonTemplate(9),
                FocusVisualStyle = BoostixUi.FocusVisual(9),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            AutomationProperties.SetAutomationId(button, "Boostix.Games.Manual");
            AutomationProperties.SetName(button, L.T("Выбрать запущенную программу для Boost"));
            button.Click += delegate
            {
                EventHandler handler = OtherGameRequested;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            };
            return button;
        }

        private void SelectGame(ShelfGame game, bool animate)
        {
            if (game == null ||
                (selected != null && string.Equals(selected.Key, game.Key, StringComparison.Ordinal)))
            {
                return;
            }
            selected = game;
            message = null;
            UpdateRailSelection(animate);
            BringIntoView(game.Key, animate);
            LoadArt(game, animate);
        }

        private void UpdateRailSelection(bool animate)
        {
            foreach (string key in railItems.Keys)
            {
                UpdateCover(key, animate);
            }
        }

        private void UpdateCover(string key, bool animate)
        {
            RailItem item;
            if (!railItems.TryGetValue(key, out item))
            {
                return;
            }
            bool chosen = selected != null &&
                string.Equals(selected.Key, key, StringComparison.Ordinal);
            bool hovered = item.Button.IsMouseOver;
            item.Ring.BorderBrush = chosen ? (Brush)accentRingBrush : Brushes.Transparent;
            AutomationProperties.SetItemStatus(item.Button, chosen ? L.T("Выбрано") : string.Empty);
            double scale = chosen ? SelectedCoverScale : (hovered ? 1.05 : 1);
            double opacity = chosen || hovered ? 1 : 0.66;
            if (animate && BoostixDesignTokens.MotionEnabled)
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                TimeSpan time = TimeSpan.FromMilliseconds(chosen ? 260 : 180);
                item.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, time) { EasingFunction = ease });
                item.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, time) { EasingFunction = ease });
                item.Button.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, time) { EasingFunction = ease });
            }
            else
            {
                item.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                item.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                item.Button.BeginAnimation(OpacityProperty, null);
                item.Scale.ScaleX = scale;
                item.Scale.ScaleY = scale;
                item.Button.Opacity = opacity;
            }
        }

        private void BringIntoView(string key, bool animate)
        {
            RailItem item;
            if (!railItems.TryGetValue(key, out item) || railScroller.ViewportWidth <= 0)
            {
                return;
            }
            double left = item.Button.TranslatePoint(new Point(0, 0), rail).X + rail.Margin.Left;
            double target = left - (railScroller.ViewportWidth - CoverWidth) / 2;
            ScrollRail(target, animate);
        }

        private void ScrollRail(double offset, bool animate)
        {
            double target = Math.Max(0, Math.Min(railScroller.ScrollableWidth, offset));
            if (animate && BoostixDesignTokens.MotionEnabled)
            {
                railScroll.BeginAnimation(HorizontalScrollProxy.OffsetProperty, null);
                railScroll.SetValue(HorizontalScrollProxy.OffsetProperty, railScroller.HorizontalOffset);
                railScroll.BeginAnimation(
                    HorizontalScrollProxy.OffsetProperty,
                    new DoubleAnimation(target, TimeSpan.FromMilliseconds(320))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
            }
            else
            {
                railScroll.BeginAnimation(HorizontalScrollProxy.OffsetProperty, null);
                railScroller.ScrollToHorizontalOffset(target);
            }
        }

        private void RailMouseWheel(object sender, MouseWheelEventArgs e)
        {
            ScrollRail(railScroller.HorizontalOffset - e.Delta * 0.9, true);
            e.Handled = true;
        }

        private void StagePreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (live != null || games.Count == 0 || selected == null)
            {
                return;
            }
            var focused = Keyboard.FocusedElement as Button;
            bool onRail = focused != null && focused.Tag is string && rail.Children.Contains(focused);
            if (!onRail)
            {
                return;
            }
            int index = games.FindIndex(game => string.Equals(game.Key, selected.Key, StringComparison.Ordinal));
            int next = index;
            if (e.Key == Key.Right)
            {
                next = Math.Min(games.Count - 1, index + 1);
            }
            else if (e.Key == Key.Left)
            {
                next = Math.Max(0, index - 1);
            }
            else if (e.Key == Key.Home)
            {
                next = 0;
            }
            else if (e.Key == Key.End)
            {
                next = games.Count - 1;
            }
            else
            {
                return;
            }
            e.Handled = true;
            if (next != index)
            {
                SelectGame(games[next], true);
                RailItem item;
                if (railItems.TryGetValue(games[next].Key, out item))
                {
                    item.Button.Focus();
                }
            }
        }

        // ---------------------------------------------------------------
        // The selected game.
        // ---------------------------------------------------------------

        private void RefreshInfo()
        {
            if (selected == null || currentArt == null)
            {
                return;
            }
            GameStats stats = GameStats.For(selected.Sessions);
            BoostSessionReport last = selected.LastSession;
            string signature = string.Join("|", new[]
            {
                selected.Key,
                selected.Name ?? string.Empty,
                selected.AutoBoost ? "1" : "0",
                selected.Saved ? "1" : "0",
                stats.Sessions.ToString(CultureInfo.InvariantCulture),
                last == null ? string.Empty : last.SessionId ?? string.Empty,
                message ?? string.Empty,
                messageIsError ? "1" : "0",
                launching == null ? string.Empty : launching.Key,
                backgroundTip ? "1" : "0"
            });
            if (string.Equals(signature, infoSignature, StringComparison.Ordinal))
            {
                return;
            }
            infoSignature = signature;
            RenderInfo(selected, currentArt, stats);
            lastGameHost.Child = BuildLastGame(selected);
        }

        private void RenderInfo(ShelfGame game, StageArt art, GameStats stats)
        {
            info.Children.Clear();
            playButton = null;

            FrameworkElement mark = BuildTitleMark(game, art);
            info.Children.Add(mark);

            // The logo says the name to the eye; this line says it to screen
            // readers and stays out of sight.
            var name = Label(game.Name, 1, BoostixDesignTokens.SecondaryText, regularFont, FontWeights.Normal);
            name.Height = 1;
            name.Opacity = 0;
            AutomationProperties.SetAutomationId(name, "Boostix.Games.Title");
            info.Children.Add(name);
            if (art.Logo == null)
            {
                string origin = string.IsNullOrWhiteSpace(game.Source) ? L.T("Добавлена вами") : game.Source;
                var source = Label(origin, 13, BoostixDesignTokens.SecondaryText, regularFont, FontWeights.Normal);
                source.Margin = new Thickness(1, 4, 0, 0);
                info.Children.Add(source);
            }

            FrameworkElement facts = BuildFacts(game, stats);
            facts.Margin = new Thickness(0, 16, 0, 0);
            info.Children.Add(facts);

            FrameworkElement actions = launching != null &&
                string.Equals(launching.Key, game.Key, StringComparison.Ordinal)
                ? BuildLaunchRow(game)
                : BuildActions(game);
            actions.Margin = new Thickness(0, 18, 0, 0);
            info.Children.Add(actions);

            if (!string.IsNullOrWhiteSpace(message))
            {
                var note = Label(
                    message,
                    BoostixDesignTokens.BodyTextSize,
                    messageIsError ? BoostixDesignTokens.Error : BoostixDesignTokens.SecondaryText,
                    regularFont,
                    FontWeights.Normal);
                note.TextWrapping = TextWrapping.Wrap;
                note.Margin = new Thickness(1, 12, 0, 0);
                AutomationProperties.SetAutomationId(note, "Boostix.Games.Message");
                AutomationProperties.SetLiveSetting(note, AutomationLiveSetting.Assertive);
                info.Children.Add(note);
            }
            else if (backgroundTip)
            {
                info.Children.Add(BuildBackgroundTip());
            }
        }

        private FrameworkElement BuildTitleMark(ShelfGame game, StageArt art)
        {
            if (art.Logo != null)
            {
                return BuildLogo(art, 136, 430, game.Name, true);
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (art.Icon != null)
            {
                var icon = new Image
                {
                    Source = art.Icon,
                    Width = 84,
                    Height = 84,
                    Margin = new Thickness(0, 0, 18, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                row.Children.Add(icon);
            }
            var title = Label(game.Name, 50, StageText, condensedFont, FontWeights.SemiBold);
            title.TextWrapping = TextWrapping.Wrap;
            title.LineHeight = 52;
            title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            title.MaxWidth = art.Icon == null ? 560 : 460;
            title.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(title);
            AutomationProperties.SetName(row, game.Name);
            return row;
        }

        /// <summary>
        /// Logos are drawn for bright art; a dark one vanishes on the stage's
        /// scrim, so it is painted white through its own shape.
        /// </summary>
        private static FrameworkElement BuildLogo(StageArt art, double height, double width, string name, bool shadow)
        {
            var source = art.Logo as System.Windows.Media.Imaging.BitmapSource;
            FrameworkElement logo;
            if (art.DarkLogo && source != null)
            {
                double fit = Math.Min(height / source.PixelHeight, width / source.PixelWidth);
                logo = new Rectangle
                {
                    Width = source.PixelWidth * fit,
                    Height = source.PixelHeight * fit,
                    Fill = Brushes.White,
                    OpacityMask = new ImageBrush(source) { Stretch = Stretch.Uniform }
                };
            }
            else
            {
                logo = new Image
                {
                    Source = art.Logo,
                    Stretch = Stretch.Uniform,
                    MaxHeight = height,
                    MaxWidth = width
                };
            }
            logo.HorizontalAlignment = HorizontalAlignment.Left;
            if (shadow)
            {
                logo.Effect = new DropShadowEffect
                {
                    BlurRadius = 26,
                    ShadowDepth = 0,
                    Opacity = 0.65,
                    Color = Colors.Black
                };
            }
            RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
            AutomationProperties.SetName(logo, name);
            return logo;
        }

        private FrameworkElement BuildFacts(ShelfGame game, GameStats stats)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            BoostSessionReport last = game.LastSession;
            if (last == null)
            {
                var none = Label(
                    game.AutoBoost
                        ? L.T("Boost включится сам, когда вы запустите игру.")
                        : L.T("С Boost ещё не играли. После первой игры здесь появится её разбор."),
                    BoostixDesignTokens.BodyTextSize + 1,
                    BoostixDesignTokens.SecondaryText,
                    regularFont,
                    FontWeights.Normal);
                none.TextWrapping = TextWrapping.Wrap;
                none.MaxWidth = 480;
                row.Children.Add(none);
                return row;
            }
            row.Children.Add(BuildFact(L.T("Последняя игра"), FormatWhen(last.StartedUtc)));
            row.Children.Add(BuildFact(L.T("Всего с Boost"), FormatPlayed(stats.Total)));
            row.Children.Add(BuildFact(L.T("Запусков"), stats.Sessions.ToString(CultureInfo.CurrentCulture)));
            if (stats.Crashes > 0)
            {
                row.Children.Add(BuildFact(L.T("Вылетов"), stats.Crashes.ToString(CultureInfo.CurrentCulture)));
            }
            return row;
        }

        private FrameworkElement BuildFact(string label, string value)
        {
            var block = new StackPanel { Margin = new Thickness(1, 0, 30, 0) };
            block.Children.Add(Label(value, 20, StageText, displayFont, FontWeights.SemiBold));
            var caption = Label(label, 12, BoostixDesignTokens.SecondaryText, regularFont, FontWeights.Normal);
            caption.Margin = new Thickness(0, 1, 0, 0);
            block.Children.Add(caption);
            AutomationProperties.SetName(block, label + ": " + value);
            return block;
        }

        private FrameworkElement BuildActions(ShelfGame game)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            playButton = BuildPlayButton(game);
            row.Children.Add(playButton);

            CheckBox auto = makeSwitch(
                L.T("Авто Boost"),
                game.AutoBoost,
                delegate(bool enabled)
                {
                    EventHandler<ShelfAutoEventArgs> handler = AutoChanged;
                    if (handler != null)
                    {
                        handler(this, new ShelfAutoEventArgs(game, enabled));
                    }
                });
            AutomationProperties.SetAutomationId(auto, "Boostix.Games.Auto");
            AutomationProperties.SetName(auto, L.T("Включать Boost вместе с игрой"));
            auto.ToolTip = L.T("Boost включается сам, когда игра открывается");
            var pill = new Border
            {
                Margin = new Thickness(12, 0, 0, 0),
                Padding = new Thickness(14, 0, 10, 0),
                Height = 52,
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(BoostixDesignTokens.WithAlpha(Color.FromRgb(8, 17, 29), 150)),
                BorderBrush = new SolidColorBrush(BoostixDesignTokens.WithAlpha(Colors.White, 34)),
                BorderThickness = new Thickness(1),
                Child = auto
            };
            row.Children.Add(pill);

            if (string.IsNullOrWhiteSpace(game.SteamAppId))
            {
                Button picture = BuildSquareButton(BoostixIcons.Picture, L.T("Поставить свою картинку"));
                picture.Click += delegate
                {
                    EventHandler<ShelfGameEventArgs> handler = PictureRequested;
                    if (handler != null)
                    {
                        handler(this, new ShelfGameEventArgs(game));
                    }
                };
                AutomationProperties.SetAutomationId(picture, "Boostix.Games.Picture");
                row.Children.Add(picture);
            }

            if (game.Saved)
            {
                Button remove = BuildSquareButton(BoostixIcons.Trash, L.T("Убрать из списка"));
                remove.Click += delegate
                {
                    EventHandler<ShelfGameEventArgs> handler = RemoveRequested;
                    if (handler != null)
                    {
                        handler(this, new ShelfGameEventArgs(game));
                    }
                };
                AutomationProperties.SetAutomationId(remove, "Boostix.Games.Remove");
                row.Children.Add(remove);
            }
            return row;
        }

        private static Button BuildSquareButton(string iconData, string name)
        {
            FrameworkElement icon = BoostixIcons.Create(iconData, 18, BoostixDesignTokens.SecondaryText);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            var button = new Button
            {
                Content = icon,
                Width = 52,
                Height = 52,
                Margin = new Thickness(12, 0, 0, 0),
                Background = new SolidColorBrush(BoostixDesignTokens.WithAlpha(Color.FromRgb(8, 17, 29), 150)),
                BorderBrush = new SolidColorBrush(BoostixDesignTokens.WithAlpha(Colors.White, 34)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Template = BoostCenterOverlay.FlatButtonTemplate(12),
                FocusVisualStyle = BoostixUi.FocusVisual(12),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                ToolTip = name
            };
            AutomationProperties.SetName(button, name);
            return button;
        }

        private Button BuildPlayButton(ShelfGame game)
        {
            var line = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Brush ink = SystemParameters.HighContrast
                ? new SolidColorBrush(BoostixDesignTokens.AccentForeground)
                : new SolidColorBrush(Color.FromRgb(8, 17, 29));
            FrameworkElement icon = BoostixIcons.Create(BoostixIcons.Play, 20, (SolidColorBrush)ink, 2.4);
            icon.Margin = new Thickness(0, 0, 10, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(icon);
            line.Children.Add(new TextBlock
            {
                Text = L.T("Играть"),
                FontFamily = displayFont,
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                Foreground = ink,
                VerticalAlignment = VerticalAlignment.Center
            });

            var scale = new ScaleTransform(1, 1);
            var button = new Button
            {
                Content = line,
                Width = 196,
                Height = 52,
                Background = accentBrush,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Cursor = Cursors.Hand,
                Template = BoostCenterOverlay.FlatButtonTemplate(12),
                FocusVisualStyle = BoostixUi.FocusVisual(12),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale
            };
            button.Click += delegate
            {
                EventHandler<ShelfGameEventArgs> handler = PlayRequested;
                if (handler != null)
                {
                    handler(this, new ShelfGameEventArgs(game));
                }
            };
            button.MouseEnter += delegate { AnimateScale(scale, 1.035, 140); };
            button.MouseLeave += delegate { AnimateScale(scale, 1, 180); };
            button.PreviewMouseLeftButtonDown += delegate { AnimateScale(scale, 0.97, 90); };
            button.PreviewMouseLeftButtonUp += delegate { AnimateScale(scale, 1.035, 140); };
            AutomationProperties.SetAutomationId(button, "Boostix.Games.Play");
            AutomationProperties.SetName(button, L.T("Играть в ") + game.Name);
            AutomationProperties.SetHelpText(button, L.T("Запускает игру и включает «Авто», чтобы Boost стартовал вместе с ней."));
            return button;
        }

        private static void AnimateScale(ScaleTransform scale, double target, int milliseconds)
        {
            if (!BoostixDesignTokens.MotionEnabled)
            {
                return;
            }
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            TimeSpan time = TimeSpan.FromMilliseconds(milliseconds);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(target, time) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(target, time) { EasingFunction = ease });
        }

        private FrameworkElement BuildLaunchRow(ShelfGame game)
        {
            var row = new StackPanel { MinWidth = 360, HorizontalAlignment = HorizontalAlignment.Left };
            var title = Label(
                string.Format(CultureInfo.CurrentCulture, L.T("Запускаем {0}…"), game.Name),
                19,
                Colors.White,
                displayFont,
                FontWeights.SemiBold);
            AutomationProperties.SetAutomationId(title, "Boostix.Games.Launching");
            AutomationProperties.SetLiveSetting(title, AutomationLiveSetting.Polite);
            row.Children.Add(title);
            var hint = Label(
                L.T("Boost включится сам, как только игра откроется."),
                BoostixDesignTokens.BodyTextSize,
                BoostixDesignTokens.SecondaryText,
                regularFont,
                FontWeights.Normal);
            hint.Margin = new Thickness(0, 4, 0, 10);
            row.Children.Add(hint);

            var track = new Grid
            {
                Height = 4,
                Width = 360,
                HorizontalAlignment = HorizontalAlignment.Left,
                ClipToBounds = true,
                Background = new SolidColorBrush(BoostixDesignTokens.WithAlpha(Colors.White, 34))
            };
            var runnerShift = new TranslateTransform(-120, 0);
            var runner = new Border
            {
                Width = 120,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(2),
                Background = accentBrush,
                RenderTransform = runnerShift
            };
            track.Children.Add(runner);
            row.Children.Add(track);
            if (launchRunner != null)
            {
                launchRunner.BeginAnimation(TranslateTransform.XProperty, null);
            }
            launchRunner = runnerShift;
            UpdateLaunchRunner();
            return row;
        }

        /// <summary>
        /// The one endless animation on the stage. It stops as soon as the
        /// game opens over the window, and with the launch itself.
        /// </summary>
        private void UpdateLaunchRunner()
        {
            if (launchRunner == null)
            {
                return;
            }
            launchRunner.BeginAnimation(TranslateTransform.XProperty, null);
            if (launching == null)
            {
                launchRunner = null;
            }
            else if (BoostixDesignTokens.MotionEnabled && IsVisible && windowActive)
            {
                launchRunner.BeginAnimation(
                    TranslateTransform.XProperty,
                    new DoubleAnimation(-120, 360, TimeSpan.FromMilliseconds(1300))
                    {
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    });
            }
            else
            {
                launchRunner.X = 120;
            }
        }

        private FrameworkElement BuildBackgroundTip()
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(1, 14, 0, 0)
            };
            var text = Label(
                L.T("Авто работает, пока Boostix запущен."),
                BoostixDesignTokens.BodyTextSize,
                BoostixDesignTokens.SecondaryText,
                regularFont,
                FontWeights.Normal);
            text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(text);
            var link = new Button
            {
                Content = Label(L.T("Запускать вместе с Windows"), BoostixDesignTokens.BodyTextSize, StageText, displayFont, FontWeights.SemiBold),
                Margin = new Thickness(10, 0, 0, 0),
                Padding = new Thickness(6, 3, 6, 3),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Template = BoostCenterOverlay.FlatButtonTemplate(6),
                FocusVisualStyle = BoostixUi.FocusVisual(6)
            };
            link.Click += delegate
            {
                EventHandler handler = StartWithWindowsRequested;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            };
            AutomationProperties.SetAutomationId(link, "Boostix.Games.StartWithWindows");
            AutomationProperties.SetName(link, L.T("Запускать Boostix вместе с Windows"));
            row.Children.Add(link);
            return row;
        }

        private FrameworkElement BuildLastGame(ShelfGame game)
        {
            BoostSessionReport last = game.LastSession;
            if (last == null)
            {
                return null;
            }
            var panel = new Border
            {
                Width = 286,
                Padding = new Thickness(16, 13, 16, 12),
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(BoostixDesignTokens.WithAlpha(Color.FromRgb(8, 14, 24), 196)),
                BorderBrush = new SolidColorBrush(BoostixDesignTokens.WithAlpha(Colors.White, 30)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 2)
            };
            AutomationProperties.SetAutomationId(panel, "Boostix.Games.Debrief");
            var stack = new StackPanel();
            List<SessionVerdict> verdicts = SessionVerdicts.Describe(last);
            SessionVerdict headline = verdicts.FirstOrDefault(item =>
                item.Tone == BoostixDesignTokens.Error) ?? verdicts.FirstOrDefault();
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            if (headline != null)
            {
                FrameworkElement dot = BoostixUi.StatusDot(headline.Tone, 9);
                dot.Margin = new Thickness(0, 1, 8, 0);
                top.Children.Add(dot);
                top.Children.Add(Label(headline.Title, 14.5, StageText, displayFont, FontWeights.SemiBold));
            }
            stack.Children.Add(top);
            TimeSpan length = (last.EndedUtc ?? DateTime.UtcNow) - last.StartedUtc;
            var when = Label(
                FormatPlayed(length) + ", " + FormatWhen(last.StartedUtc),
                BoostixDesignTokens.MetadataTextSize + 0.5,
                BoostixDesignTokens.SecondaryText,
                regularFont,
                FontWeights.Normal);
            when.Margin = new Thickness(0, 2, 0, 0);
            stack.Children.Add(when);

            var chart = new SessionChart(last.Timeline, last.PhysicalMemoryTotalBytes)
            {
                Height = 62,
                Margin = new Thickness(0, 10, 0, 4),
                LineColor = accentBrush.Color
            };
            if (chart.HasData)
            {
                stack.Children.Add(chart);
            }

            var open = new Button
            {
                Padding = new Thickness(0, 6, 0, 2),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Template = BoostCenterOverlay.FlatButtonTemplate(6),
                FocusVisualStyle = BoostixUi.FocusVisual(6),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            var openLine = new StackPanel { Orientation = Orientation.Horizontal };
            openLine.Children.Add(Label(L.T("Открыть разбор"), BoostixDesignTokens.BodyTextSize, StageText, displayFont, FontWeights.SemiBold));
            FrameworkElement chevron = BoostixIcons.Create(BoostixIcons.ChevronRight, 14, StageText);
            chevron.Margin = new Thickness(4, 1, 0, 0);
            openLine.Children.Add(chevron);
            open.Content = openLine;
            open.Click += delegate
            {
                EventHandler<SessionReportEventArgs> handler = DebriefRequested;
                if (handler != null)
                {
                    handler(this, new SessionReportEventArgs(last, game));
                }
            };
            AutomationProperties.SetAutomationId(open, "Boostix.Games.OpenDebrief");
            AutomationProperties.SetName(open, L.T("Открыть разбор последней игры"));
            stack.Children.Add(open);
            panel.Child = stack;
            return panel;
        }

        private void AnimateInfoIn()
        {
            int index = 0;
            foreach (UIElement child in info.Children)
            {
                var shift = new TranslateTransform(-18, 0);
                child.RenderTransform = shift;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                TimeSpan delay = TimeSpan.FromMilliseconds(40 * index);
                TimeSpan time = TimeSpan.FromMilliseconds(340);
                child.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, time)
                {
                    BeginTime = delay,
                    EasingFunction = ease,
                    FillBehavior = FillBehavior.Stop
                });
                shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-18, 0, time)
                {
                    BeginTime = delay,
                    EasingFunction = ease
                });
                index++;
            }
            var panel = lastGameHost;
            panel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
            {
                BeginTime = TimeSpan.FromMilliseconds(120),
                FillBehavior = FillBehavior.Stop
            });
        }

        private void ShowEmptyStage()
        {
            info.Children.Clear();
            lastGameHost.Child = null;
            playButton = null;
            infoSignature = string.Empty;
            var title = Label(
                scanning ? L.T("Ищем ваши игры") : L.T("Здесь появятся ваши игры"),
                38,
                Colors.White,
                condensedFont,
                FontWeights.SemiBold);
            AutomationProperties.SetAutomationId(title, "Boostix.Games.Title");
            info.Children.Add(title);
            var detail = Label(
                L.T("Boostix находит игры Steam, Epic Games и GOG. Игры, запущенные с Boost, тоже попадают сюда."),
                BoostixDesignTokens.BodyTextSize + 1,
                BoostixDesignTokens.SecondaryText,
                regularFont,
                FontWeights.Normal);
            detail.TextWrapping = TextWrapping.Wrap;
            detail.MaxWidth = 480;
            detail.Margin = new Thickness(0, 8, 0, 0);
            AutomationProperties.SetAutomationId(detail, "Boostix.Games.Empty");
            info.Children.Add(detail);
            ImageSource art = GameArtwork.EmptyShelf();
            heroFront.Background = art == null || SystemParameters.HighContrast
                ? null
                : new ImageBrush(art) { Stretch = Stretch.UniformToFill };
            heroFront.Opacity = 1;
        }

        // ---------------------------------------------------------------
        // The live session.
        // ---------------------------------------------------------------

        /// <summary>
        /// Switches the stage to the running game, updates it, or returns to
        /// the shelf when state is null.
        /// </summary>
        public void SetLive(StageLiveState state)
        {
            bool wasLive = live != null;
            live = state;
            if (state == null)
            {
                if (wasLive)
                {
                    ShowLiveLayer(false);
                    infoSignature = string.Empty;
                    RefreshInfo();
                }
                return;
            }

            if (launching != null)
            {
                SetLaunching(null);
            }
            if (state.Game != null &&
                (selected == null || !string.Equals(selected.Key, state.Game.Key, StringComparison.Ordinal)))
            {
                ShelfGame running = games.FirstOrDefault(game =>
                    string.Equals(game.Key, state.Game.Key, StringComparison.Ordinal));
                selected = running ?? state.Game;
                UpdateRailSelection(true);
                LoadArt(selected, true);
            }
            if (!wasLive || liveTimer == null)
            {
                BuildLiveLayer();
                ShowLiveLayer(true);
            }
            UpdateLiveHeading();
            UpdateLiveValues(state);
        }

        private void ShowLiveLayer(bool show)
        {
            FrameworkElement shelfLayerA = (FrameworkElement)info.Parent;
            if (show)
            {
                liveLayer.Visibility = Visibility.Visible;
            }
            if (BoostixDesignTokens.MotionEnabled)
            {
                TimeSpan time = TimeSpan.FromMilliseconds(320);
                var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
                var shelfFade = new DoubleAnimation(show ? 0 : 1, time) { EasingFunction = ease };
                shelfLayerA.BeginAnimation(OpacityProperty, shelfFade);
                railHost.BeginAnimation(OpacityProperty, shelfFade);
                var liveFade = new DoubleAnimation(show ? 1 : 0, time) { EasingFunction = ease };
                if (!show)
                {
                    liveFade.Completed += delegate
                    {
                        if (live == null)
                        {
                            liveLayer.Visibility = Visibility.Collapsed;
                        }
                    };
                }
                liveLayer.BeginAnimation(OpacityProperty, liveFade);
            }
            else
            {
                shelfLayerA.BeginAnimation(OpacityProperty, null);
                railHost.BeginAnimation(OpacityProperty, null);
                liveLayer.BeginAnimation(OpacityProperty, null);
                shelfLayerA.Opacity = show ? 0 : 1;
                railHost.Opacity = show ? 0 : 1;
                liveLayer.Opacity = show ? 1 : 0;
                if (!show)
                {
                    liveLayer.Visibility = Visibility.Collapsed;
                }
            }
            shelfLayerA.IsHitTestVisible = !show;
            railHost.IsHitTestVisible = !show;
            railScroller.Visibility = show ? Visibility.Hidden : Visibility.Visible;
            ((FrameworkElement)info.Parent).Visibility = show ? Visibility.Hidden : Visibility.Visible;
        }

        private void BuildLiveLayer()
        {
            liveLayer.Children.Clear();
            liveLayer.RowDefinitions.Clear();
            liveLayer.ColumnDefinitions.Clear();
            liveChartPoints = -1;
            liveHeadingKey = null;
            liveLayer.Margin = new Thickness(SideInset, 0, SideInset - 8, 0);
            liveLayer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            liveLayer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            liveLayer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            liveLayer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(286) });

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            var status = new StackPanel { Orientation = Orientation.Horizontal };
            var dot = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = new SolidColorBrush(BoostixDesignTokens.Success),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(1, 1, 9, 0)
            };
            status.Children.Add(dot);
            liveStateText = Label(L.T("Идёт игра, Boost работает"), 15, StageText, displayFont, FontWeights.SemiBold);
            AutomationProperties.SetAutomationId(liveStateText, "Boostix.Games.Live.State");
            AutomationProperties.SetLiveSetting(liveStateText, AutomationLiveSetting.Polite);
            status.Children.Add(liveStateText);
            left.Children.Add(status);

            liveHeading = new Border { Margin = new Thickness(0, 12, 0, 0) };
            left.Children.Add(liveHeading);

            liveTimer = Label("00:00", 74, StageText, condensedFont, FontWeights.SemiBold);
            liveTimer.Margin = new Thickness(-2, 2, 0, 0);
            AutomationProperties.SetAutomationId(liveTimer, "Boostix.Games.Live.Timer");
            left.Children.Add(liveTimer);

            var stop = BoostixUi.ActionButton(L.T("Остановить Boost"), BoostixButtonKind.Secondary, BoostixIcons.Stop);
            stop.HorizontalAlignment = HorizontalAlignment.Left;
            stop.Margin = new Thickness(0, 12, 0, 20);
            stop.Click += delegate
            {
                EventHandler handler = StopRequested;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            };
            AutomationProperties.SetAutomationId(stop, "Boostix.Games.Live.Stop");
            left.Children.Add(stop);
            liveLayer.Children.Add(left);

            var meters = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 20) };
            memoryMeter = BuildMeter(meters, L.T("Свободная память"), "Boostix.Games.Live.Memory");
            cpuMeter = BuildMeter(meters, L.T("Процессор"), "Boostix.Games.Live.Cpu");
            gpuMeter = BuildMeter(meters, L.T("Видеопамять"), "Boostix.Games.Live.Gpu");
            Grid.SetColumn(meters, 1);
            liveLayer.Children.Add(meters);

            liveChartHost = new Border { Height = 118, Margin = new Thickness(0, 0, 8, 26) };
            AutomationProperties.SetAutomationId(liveChartHost, "Boostix.Games.Live.Chart");
            Grid.SetRow(liveChartHost, 1);
            Grid.SetColumnSpan(liveChartHost, 2);
            liveLayer.Children.Add(liveChartHost);
        }

        private LiveMeter BuildMeter(Panel parent, string title, string automationId)
        {
            var card = new Border
            {
                Padding = new Thickness(14, 10, 14, 12),
                Margin = new Thickness(0, 8, 0, 0),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(BoostixDesignTokens.WithAlpha(Color.FromRgb(8, 14, 24), 188)),
                BorderBrush = new SolidColorBrush(BoostixDesignTokens.WithAlpha(Colors.White, 26)),
                BorderThickness = new Thickness(1)
            };
            var stack = new StackPanel();
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var caption = Label(title, 12, BoostixDesignTokens.SecondaryText, regularFont, FontWeights.Normal);
            caption.VerticalAlignment = VerticalAlignment.Bottom;
            head.Children.Add(caption);
            var value = Label("—", 20, StageText, displayFont, FontWeights.SemiBold);
            Grid.SetColumn(value, 1);
            head.Children.Add(value);
            stack.Children.Add(head);
            var track = new Grid
            {
                Height = 4,
                Margin = new Thickness(0, 7, 0, 0),
                Background = new SolidColorBrush(BoostixDesignTokens.WithAlpha(Colors.White, 30))
            };
            var fill = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(2),
                Background = accentBrush,
                Width = 0
            };
            track.Children.Add(fill);
            stack.Children.Add(track);
            card.Child = stack;
            AutomationProperties.SetAutomationId(value, automationId);
            parent.Children.Add(card);
            return new LiveMeter { Title = title, Value = value, Fill = fill, Track = track };
        }

        private void UpdateLiveHeading()
        {
            if (live == null || liveHeading == null)
            {
                return;
            }
            string key = (live.Game == null ? live.Name : live.Game.Key) +
                (currentArt == null || currentArt.Logo == null ? "-" : "+");
            if (string.Equals(key, liveHeadingKey, StringComparison.Ordinal))
            {
                return;
            }
            liveHeadingKey = key;
            FrameworkElement content;
            if (currentArt != null && currentArt.Logo != null)
            {
                content = BuildLogo(currentArt, 84, 300, live.Name, false);
            }
            else
            {
                content = Label(live.Name, 34, StageText, condensedFont, FontWeights.SemiBold);
            }
            AutomationProperties.SetName(content, live.Name);
            ((Border)liveHeading).Child = content;
        }

        private void UpdateLiveValues(StageLiveState state)
        {
            TimeSpan elapsed = DateTime.UtcNow - state.StartedUtc;
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }
            liveTimer.Text = elapsed.TotalHours >= 1
                ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)elapsed.TotalHours, elapsed.Minutes, elapsed.Seconds)
                : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", elapsed.Minutes, elapsed.Seconds);
            AutomationProperties.SetName(liveTimer, L.T("Время в игре: ") + liveTimer.Text);
            liveStateText.Text = state.LowMemory
                ? L.T("Идёт игра, памяти мало")
                : L.T("Идёт игра, Boost работает");

            SetMeter(
                memoryMeter,
                state.TotalBytes > 0 ? SessionVerdicts.FormatGigabytes(state.AvailableBytes) : "—",
                state.TotalBytes > 0 ? (double)state.AvailableBytes / state.TotalBytes : -1);
            SetMeter(
                cpuMeter,
                state.CpuPercent >= 0 ? state.CpuPercent.ToString(CultureInfo.CurrentCulture) + "%" : "—",
                state.CpuPercent >= 0 ? state.CpuPercent / 100.0 : -1);
            SetMeter(
                gpuMeter,
                state.GpuTotalBytes > 0 ? SessionVerdicts.FormatGigabytes(state.GpuUsedBytes) : "—",
                state.GpuTotalBytes > 0 ? (double)state.GpuUsedBytes / state.GpuTotalBytes : -1);

            int points = state.Timeline == null ? 0 : state.Timeline.Count;
            if (points != liveChartPoints)
            {
                liveChartPoints = points;
                liveChartHost.Child = new SessionChart(state.Timeline, state.TotalBytes)
                {
                    LineColor = accentBrush.Color
                };
            }
        }

        private static void SetMeter(LiveMeter meter, string value, double fraction)
        {
            meter.Value.Text = value;
            AutomationProperties.SetName(meter.Value, meter.Title + ": " + value);
            double width = meter.Track.ActualWidth;
            if (width <= 0)
            {
                width = 256;
            }
            meter.Fill.Width = fraction < 0 ? 0 : Math.Max(0, Math.Min(1, fraction)) * width;
        }

        // ---------------------------------------------------------------
        // Text.
        // ---------------------------------------------------------------

        private static string DescribeShort(ShelfGame game)
        {
            BoostSessionReport last = game.LastSession;
            if (last != null)
            {
                return FormatPlayed((last.EndedUtc ?? DateTime.UtcNow) - last.StartedUtc) + ", " + FormatWhen(last.StartedUtc);
            }
            if (!string.IsNullOrWhiteSpace(game.Source))
            {
                return game.Source;
            }
            return game.Saved ? L.T("Добавлена вами") : L.T("Найдена на диске");
        }

        internal static string FormatPlayed(TimeSpan played)
        {
            if (played.TotalMinutes < 1)
            {
                return L.T("меньше минуты");
            }
            if (played.TotalHours >= 1)
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    L.T("{0} ч {1:00} мин"),
                    (int)played.TotalHours,
                    played.Minutes);
            }
            return string.Format(CultureInfo.CurrentCulture, L.T("{0} мин"), (int)played.TotalMinutes);
        }

        internal static string FormatWhen(DateTime startedUtc)
        {
            DateTime local = startedUtc.ToLocalTime();
            DateTime today = DateTime.Now.Date;
            if (local.Date == today)
            {
                return L.T("сегодня");
            }
            if (local.Date == today.AddDays(-1))
            {
                return L.T("вчера");
            }
            return local.ToString("d MMM", L.DateCulture);
        }

        private static Color StageText
        {
            get { return SystemParameters.HighContrast ? BoostixDesignTokens.Text : Colors.White; }
        }

        private static TextBlock Label(string text, double size, Color color, FontFamily font, FontWeight weight)
        {
            return new TextBlock
            {
                Text = text ?? string.Empty,
                FontSize = size,
                FontFamily = font,
                FontWeight = weight,
                Foreground = new SolidColorBrush(color),
                TextTrimming = TextTrimming.None
            };
        }
    }
}
