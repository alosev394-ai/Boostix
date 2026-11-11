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
using System.Windows.Shapes;

namespace Boostix
{
    /// <summary>
    /// A round meter: how full something is, at a glance. The arc fills from
    /// the top when it appears.
    /// </summary>
    internal sealed class MeterRing : FrameworkElement
    {
        public static readonly DependencyProperty FractionProperty =
            DependencyProperty.Register(
                "Fraction",
                typeof(double),
                typeof(MeterRing),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        private const double Thickness = 9;
        private readonly Color arcColor;
        private readonly string centerText;
        private readonly Typeface typeface;

        public MeterRing(double fraction, Color color, string center, FontFamily font)
        {
            arcColor = color;
            centerText = center ?? string.Empty;
            typeface = new Typeface(font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            SnapsToDevicePixels = true;
            double target = double.IsNaN(fraction) ? 0 : Math.Max(0, Math.Min(1, fraction));
            if (BoostixDesignTokens.MotionEnabled)
            {
                Loaded += delegate
                {
                    BeginAnimation(FractionProperty, new DoubleAnimation(0, target, TimeSpan.FromMilliseconds(760))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
                };
            }
            else
            {
                Fraction = target;
            }
        }

        public double Fraction
        {
            get { return (double)GetValue(FractionProperty); }
            set { SetValue(FractionProperty, value); }
        }

        protected override void OnRender(DrawingContext context)
        {
            double size = Math.Min(ActualWidth, ActualHeight);
            if (size < 20)
            {
                return;
            }
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            double radius = size / 2 - Thickness / 2 - 1;
            var trackPen = new Pen(new SolidColorBrush(BoostixDesignTokens.WithAlpha(Colors.White, 30)), Thickness);
            trackPen.Freeze();
            context.DrawEllipse(null, trackPen, center, radius, radius);

            double fraction = Fraction;
            if (fraction > 0.002)
            {
                var pen = new Pen(new SolidColorBrush(arcColor), Thickness)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round
                };
                pen.Freeze();
                if (fraction >= 0.999)
                {
                    context.DrawEllipse(null, pen, center, radius, radius);
                }
                else
                {
                    double angle = fraction * 2 * Math.PI;
                    var start = new Point(center.X, center.Y - radius);
                    var end = new Point(
                        center.X + radius * Math.Sin(angle),
                        center.Y - radius * Math.Cos(angle));
                    var arc = new StreamGeometry();
                    using (StreamGeometryContext geometry = arc.Open())
                    {
                        geometry.BeginFigure(start, false, false);
                        geometry.ArcTo(
                            end,
                            new Size(radius, radius),
                            0,
                            fraction > 0.5,
                            SweepDirection.Clockwise,
                            true,
                            false);
                    }
                    arc.Freeze();
                    context.DrawGeometry(null, pen, arc);
                }
            }

            var text = new FormattedText(
                centerText,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                size * 0.215,
                new SolidColorBrush(BoostixDesignTokens.Text),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            context.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
        }
    }

    internal sealed partial class BoostCenterOverlay
    {
        private const int MinimumDebriefMinutes = 3;
        private Button debriefBackButton;
        private bool demoLive;

        // ---------------------------------------------------------------
        // Journal: every game as a strip of its own art.
        // ---------------------------------------------------------------

        private void AddJournal(StackPanel report)
        {
            List<BoostSessionReport> sessions = sessionHistory
                .Where(item => item != null && item.EndedUtc.HasValue)
                .OrderByDescending(item => item.StartedUtc)
                .ToList();
            if (sessions.Count == 0)
            {
                report.Children.Add(MakeEmptyState(
                    BoostixIcons.Chart,
                    L.T("Журнал пока пуст"),
                    L.T("Сыграйте с Boost, и здесь появится каждая игра: сколько она длилась и как прошла.")));
                return;
            }

            report.Children.Add(BuildJournalSummary(sessions));
            List<ShelfGame> games = GameShelf.Build(gameProfiles, installedGames, sessionHistory);
            int index = 0;
            DateTime? day = null;
            foreach (BoostSessionReport session in sessions)
            {
                DateTime date = session.StartedUtc.ToLocalTime().Date;
                if (day != date)
                {
                    day = date;
                    var heading = MakeText(
                        FormatJournalDay(date),
                        BoostixDesignTokens.SectionTitleSize,
                        SecondaryColor,
                        semiboldFont,
                        FontWeights.SemiBold);
                    heading.Margin = new Thickness(2, index == 0 ? 6 : 16, 0, 8);
                    report.Children.Add(heading);
                }
                ShelfGame game = games.FirstOrDefault(item => item.Sessions.Contains(session)) ??
                    GameForReport(session);
                report.Children.Add(BuildJournalRow(session, game, index++));
            }
        }

        private static ShelfGame GameForReport(BoostSessionReport report)
        {
            string path = report.TargetExecutablePath ?? string.Empty;
            return new ShelfGame
            {
                Name = GameShelf.PrettyName(report.TargetName, path),
                ExecutablePath = path,
                SteamAppId = GameLibraryScanner.SteamAppIdForExecutable(path),
                Sessions = new List<BoostSessionReport> { report }
            };
        }

        private static string FormatJournalDay(DateTime date)
        {
            DateTime today = DateTime.Now.Date;
            if (date == today)
            {
                return L.T("Сегодня");
            }
            if (date == today.AddDays(-1))
            {
                return L.T("Вчера");
            }
            return date.ToString("d MMMM", L.DateCulture);
        }

        private FrameworkElement BuildJournalSummary(List<BoostSessionReport> sessions)
        {
            DateTime since = DateTime.UtcNow.AddDays(-7);
            GameStats week = GameStats.For(sessions.Where(item => item.StartedUtc >= since));
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(2, 0, 0, 12)
            };
            AutomationProperties.SetAutomationId(row, "Boostix.Center.JournalSummary");
            row.Children.Add(BuildBigStat(GameStage.FormatPlayed(week.Total), L.T("в играх за 7 дней")));
            row.Children.Add(BuildBigStat(week.Sessions.ToString(CultureInfo.CurrentCulture), L.T("запусков")));
            row.Children.Add(BuildBigStat(week.Crashes.ToString(CultureInfo.CurrentCulture), L.T("вылетов")));
            row.Children.Add(BuildBigStat(week.TightSessions.ToString(CultureInfo.CurrentCulture), L.T("раз не хватало памяти")));
            return row;
        }

        private FrameworkElement BuildBigStat(string value, string label)
        {
            var block = new StackPanel { Margin = new Thickness(0, 0, 40, 0) };
            block.Children.Add(MakeText(value, 24, TextColor, semiboldFont, FontWeights.SemiBold));
            var caption = MakeText(label, 12, SecondaryColor, regularFont, FontWeights.Normal);
            caption.Margin = new Thickness(0, 1, 0, 0);
            block.Children.Add(caption);
            AutomationProperties.SetName(block, value + " " + label);
            return block;
        }

        private Button BuildJournalRow(BoostSessionReport report, ShelfGame game, int index)
        {
            var strip = new Grid { Height = 88 };
            var art = new Border();
            strip.Children.Add(art);
            var veil = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            veil.GradientStops.Add(new GradientStop(BoostixDesignTokens.WithAlpha(SurfaceColor, 255), 0));
            veil.GradientStops.Add(new GradientStop(BoostixDesignTokens.WithAlpha(SurfaceColor, 235), 0.42));
            veil.GradientStops.Add(new GradientStop(BoostixDesignTokens.WithAlpha(SurfaceColor, 120), 1));
            strip.Children.Add(new Rectangle { Fill = veil });
            strip.Background = new SolidColorBrush(SurfaceColor);
            LoadJournalArt(art, game);

            var line = new Grid { Margin = new Thickness(18, 0, 16, 0) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TimeSpan length = (report.EndedUtc ?? report.StartedUtc) - report.StartedUtc;
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = MakeText(game.Name, 17, TextColor, semiboldFont, FontWeights.SemiBold);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            words.Children.Add(name);
            words.Children.Add(MakeText(
                report.StartedUtc.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture) + ", " + GameStage.FormatPlayed(length),
                BoostixDesignTokens.BodyTextSize,
                SecondaryColor,
                regularFont,
                FontWeights.Normal));
            SessionVerdict headline = HeadlineVerdict(report);
            if (headline != null)
            {
                var verdict = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                FrameworkElement dot = BoostixUi.StatusDot(headline.Tone, 7);
                dot.Margin = new Thickness(0, 1, 7, 0);
                verdict.Children.Add(dot);
                verdict.Children.Add(MakeText(headline.Title, BoostixDesignTokens.MetadataTextSize + 0.5, TextColor, regularFont, FontWeights.Normal));
                words.Children.Add(verdict);
            }
            line.Children.Add(words);

            var chart = new SessionChart(report.Timeline, report.PhysicalMemoryTotalBytes)
            {
                Width = 210,
                Height = 56,
                Margin = new Thickness(16, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            if (chart.HasData)
            {
                Grid.SetColumn(chart, 1);
                line.Children.Add(chart);
            }
            FrameworkElement chevron = BoostixIcons.Create(BoostixIcons.ChevronRight, 16, SecondaryColor);
            chevron.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(chevron, 2);
            line.Children.Add(chevron);
            strip.Children.Add(line);

            var frame = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(BorderColor),
                Child = strip
            };
            strip.SizeChanged += delegate
            {
                strip.Clip = new RectangleGeometry(new Rect(0, 0, strip.ActualWidth, strip.ActualHeight), 11, 11);
            };
            var button = new Button
            {
                Content = frame,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Cursor = Cursors.Hand,
                Template = MakeFlatButtonTemplate(12),
                FocusVisualStyle = MakeKeyboardFocusVisualStyle(12)
            };
            button.MouseEnter += delegate { frame.BorderBrush = new SolidColorBrush(stageAccent ?? AccentColor); };
            button.MouseLeave += delegate { frame.BorderBrush = new SolidColorBrush(BorderColor); };
            button.Click += delegate { OpenDebrief(report, game); };
            AutomationProperties.SetAutomationId(
                button,
                "Boostix.Center.History." + index.ToString(CultureInfo.InvariantCulture));
            AutomationProperties.SetName(
                button,
                game.Name + ". " + GameStage.FormatPlayed(length) + ". " +
                (headline == null ? string.Empty : headline.Title));
            return button;
        }

        private void LoadJournalArt(Border target, ShelfGame game)
        {
            if (SystemParameters.HighContrast)
            {
                return;
            }
            Task.Run(delegate { return GameArtwork.Hero(game); }).ContinueWith(delegate(Task<ImageSource> task)
            {
                if (task.IsFaulted || task.Result == null)
                {
                    return;
                }
                try
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        target.Background = new ImageBrush(task.Result)
                        {
                            Stretch = Stretch.UniformToFill,
                            AlignmentX = AlignmentX.Right,
                            AlignmentY = AlignmentY.Center
                        };
                    }));
                }
                catch (InvalidOperationException) { }
                catch (TaskCanceledException) { }
            }, TaskScheduler.Default);
        }

        private static SessionVerdict HeadlineVerdict(BoostSessionReport report)
        {
            List<SessionVerdict> verdicts = SessionVerdicts.Describe(report);
            return verdicts.FirstOrDefault(item => item.Tone == BoostixDesignTokens.Error) ??
                verdicts.FirstOrDefault();
        }

        // ---------------------------------------------------------------
        // Debrief: one finished game on the whole screen.
        // ---------------------------------------------------------------

        internal void OpenDebrief(BoostSessionReport report, ShelfGame game)
        {
            if (report == null)
            {
                return;
            }
            ShelfGame owner = game ?? GameForReport(report);
            debriefLayer.Children.Clear();
            debriefLayer.Children.Add(BuildDebrief(report, owner));
            debriefLayer.Visibility = Visibility.Visible;
            if (BoostixDesignTokens.MotionEnabled)
            {
                var slide = new TranslateTransform(0, 18);
                debriefLayer.RenderTransform = slide;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                TimeSpan time = TimeSpan.FromMilliseconds(300);
                debriefLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, time) { EasingFunction = ease });
                slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, time) { EasingFunction = ease });
            }
            else
            {
                debriefLayer.BeginAnimation(OpacityProperty, null);
                debriefLayer.Opacity = 1;
            }
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (debriefBackButton != null)
                {
                    debriefBackButton.Focus();
                }
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>
        /// --demo-live: the stage as it looks during a game, with the demo
        /// shelf's first session standing in for the running one.
        /// </summary>
        internal void ShowDemoLive()
        {
            List<ShelfGame> games = GameShelf.Build(gameProfiles, installedGames, sessionHistory);
            ShelfGame game = games.FirstOrDefault();
            if (game == null || game.LastSession == null)
            {
                return;
            }
            demoLive = true;
            BoostSessionReport session = game.LastSession;
            gameStage.SetLive(new StageLiveState
            {
                Game = game,
                Name = game.Name,
                StartedUtc = DateTime.UtcNow.AddMinutes(-42).AddSeconds(-17),
                AvailableBytes = 5368709120L,
                TotalBytes = session.PhysicalMemoryTotalBytes,
                CpuPercent = 46,
                GpuUsedBytes = 6442450944L,
                GpuTotalBytes = session.GpuDedicatedTotalBytes,
                Timeline = session.Timeline
            });
        }

        /// <summary>
        /// --demo-debrief: the debrief of the demo shelf's latest game.
        /// </summary>
        internal void ShowDemoDebrief()
        {
            ShelfGame game = GameShelf.Build(gameProfiles, installedGames, sessionHistory).FirstOrDefault();
            if (game != null && game.LastSession != null)
            {
                OpenDebrief(game.LastSession, game);
            }
        }

        /// <summary>
        /// A game that ran for a while ends with its debrief: at once when the
        /// window is in front, otherwise the next time it comes forward.
        /// </summary>
        private void ConsiderDebrief()
        {
            if (DateTime.UtcNow > debriefArmedUntilUtc ||
                sessionReport == null ||
                !sessionReport.EndedUtc.HasValue ||
                Math.Abs((sessionReport.StartedUtc - liveStartedUtc).TotalSeconds) > 5 ||
                sessionReport.EndedUtc.Value - sessionReport.StartedUtc < TimeSpan.FromMinutes(MinimumDebriefMinutes))
            {
                return;
            }
            debriefArmedUntilUtc = DateTime.MinValue;
            pendingDebrief = sessionReport;
            if (windowActive && IsVisible)
            {
                ShowPendingDebrief();
            }
        }

        private void ShowPendingDebrief()
        {
            BoostSessionReport report = pendingDebrief;
            pendingDebrief = null;
            if (report == null)
            {
                return;
            }
            // Crash evidence can arrive after the session ended.
            if (sessionReport != null && string.Equals(sessionReport.SessionId, report.SessionId, StringComparison.Ordinal))
            {
                report = sessionReport;
            }
            if (currentPage != CenterPage.Games)
            {
                SwitchPage(CenterPage.Games, false);
            }
            List<ShelfGame> games = GameShelf.Build(gameProfiles, installedGames, sessionHistory);
            OpenDebrief(report, games.FirstOrDefault(item => item.Sessions.Any(session =>
                string.Equals(session.SessionId, report.SessionId, StringComparison.Ordinal))));
        }

        private void ChooseGamePicture(ShelfGame game)
        {
            if (game == null || BoostSessionReportStore.PersistenceDisabled)
            {
                gameMessage = L.T("В демо-режиме картинки не сохраняются.");
                gameStage.SetMessage(gameMessage, false);
                return;
            }
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = L.T("Картинка для игры"),
                Filter = L.T("Картинки") + "|*.jpg;*.jpeg;*.png;*.bmp;*.webp",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            {
                return;
            }
            string error;
            if (GameArtwork.TrySetCustomArt(game, dialog.FileName, out error))
            {
                gameStage.ReloadArt();
            }
            else
            {
                gameMessage = error;
                gameMessageError = true;
                gameStage.SetMessage(error, true);
            }
        }

        private bool DebriefOpen
        {
            get { return debriefLayer.Visibility == Visibility.Visible; }
        }

        private void CloseDebrief()
        {
            if (!DebriefOpen)
            {
                return;
            }
            debriefLayer.Visibility = Visibility.Collapsed;
            debriefLayer.Children.Clear();
            debriefBackButton = null;
            if (GameHistoryOpen && gameHistoryBackButton != null)
            {
                FocusAfterLayout(gameHistoryBackButton);
            }
            else
            {
                FocusPreferredButton();
            }
        }

        private void FocusAfterLayout(Button button)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (button != null && button.IsVisible)
                {
                    button.Focus();
                }
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private Button MakeLink(string text, string automationId, string automationName)
        {
            var link = new Button
            {
                Padding = new Thickness(0, 4, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Template = MakeFlatButtonTemplate(6),
                FocusVisualStyle = MakeKeyboardFocusVisualStyle(6)
            };
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(MakeText(text, BoostixDesignTokens.BodyTextSize, TextColor, semiboldFont, FontWeights.SemiBold));
            FrameworkElement chevron = BoostixIcons.Create(BoostixIcons.ChevronRight, 14, TextColor);
            chevron.Margin = new Thickness(4, 1, 0, 0);
            line.Children.Add(chevron);
            link.Content = line;
            AutomationProperties.SetAutomationId(link, automationId);
            AutomationProperties.SetName(link, automationName);
            return link;
        }

        // ---------------------------------------------------------------
        // Game history: what keeps happening in one game.
        // ---------------------------------------------------------------

        private const int MaximumHistoryRows = 30;
        private Button gameHistoryBackButton;

        private bool GameHistoryOpen
        {
            get { return gameHistoryLayer.Visibility == Visibility.Visible; }
        }

        internal void OpenGameHistory(ShelfGame game)
        {
            if (game == null)
            {
                return;
            }
            gameHistoryLayer.Children.Clear();
            gameHistoryLayer.Children.Add(BuildGameHistory(game));
            gameHistoryLayer.Visibility = Visibility.Visible;
            if (BoostixDesignTokens.MotionEnabled)
            {
                var slide = new TranslateTransform(0, 18);
                gameHistoryLayer.RenderTransform = slide;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                TimeSpan time = TimeSpan.FromMilliseconds(300);
                gameHistoryLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, time) { EasingFunction = ease });
                slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, time) { EasingFunction = ease });
            }
            else
            {
                gameHistoryLayer.BeginAnimation(OpacityProperty, null);
                gameHistoryLayer.Opacity = 1;
            }
            FocusAfterLayout(gameHistoryBackButton);
        }

        /// <summary>
        /// --demo-game-history: the history of the demo shelf's first game.
        /// </summary>
        internal void ShowDemoGameHistory()
        {
            OpenGameHistory(GameShelf.Build(gameProfiles, installedGames, sessionHistory).FirstOrDefault());
        }

        private void CloseGameHistory()
        {
            if (!GameHistoryOpen)
            {
                return;
            }
            gameHistoryLayer.Visibility = Visibility.Collapsed;
            gameHistoryLayer.Children.Clear();
            gameHistoryBackButton = null;
            FocusPreferredButton();
        }

        private FrameworkElement BuildGameHistory(ShelfGame game)
        {
            GameHistorySummary summary = GameHistory.Summarize(game.Sessions);
            Color accent;
            StackPanel body;
            Grid root = MakeArtPage(game, out accent, out body);
            AutomationProperties.SetAutomationId(root, "Boostix.Center.GameHistory");

            var heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Button back = MakeBackButton("Boostix.Center.GameHistory.Back", L.T("Закрыть историю игры"));
            back.Click += delegate { CloseGameHistory(); };
            gameHistoryBackButton = back;
            heading.Children.Add(back);
            if (summary.Sessions.Count > 0)
            {
                DateTime first = summary.Sessions[summary.Sessions.Count - 1].StartedUtc.ToLocalTime();
                var since = MakeText(
                    string.Format(CultureInfo.CurrentCulture, L.T("История с {0}"), first.ToString("d MMMM", L.DateCulture)),
                    BoostixDesignTokens.BodyTextSize,
                    SecondaryColor,
                    regularFont,
                    FontWeights.Normal);
                since.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(since, 1);
                heading.Children.Add(since);
            }
            body.Children.Add(heading);

            var label = MakeText(game.Name, 16, SecondaryColor, semiboldFont, FontWeights.SemiBold);
            label.Margin = new Thickness(0, 14, 0, 0);
            AutomationProperties.SetAutomationId(label, "Boostix.Center.GameHistory.Game");
            body.Children.Add(label);
            var title = new TextBlock
            {
                Text = summary.Headline.Title,
                FontFamily = new FontFamily("Bahnschrift SemiBold Condensed, Bahnschrift, Segoe UI"),
                FontSize = 44,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0)
            };
            AutomationProperties.SetAutomationId(title, "Boostix.Center.GameHistory.Headline");
            body.Children.Add(title);
            var detail = MakeText(summary.Headline.Detail, 14.5, SecondaryColor, regularFont, FontWeights.Normal);
            detail.TextWrapping = TextWrapping.Wrap;
            detail.Margin = new Thickness(0, 2, 0, 0);
            body.Children.Add(detail);

            GameStats stats = summary.Stats;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
            row.Children.Add(BuildBigStat(stats.Sessions.ToString(CultureInfo.CurrentCulture), L.T("игр с Boost")));
            row.Children.Add(BuildBigStat(GameStage.FormatPlayed(stats.Total), L.T("в игре")));
            row.Children.Add(BuildBigStat(
                string.Format(CultureInfo.CurrentCulture, L.T("{0} из {1}"), stats.TightSessions, stats.Sessions),
                L.T("не хватало памяти")));
            row.Children.Add(BuildBigStat(stats.Crashes.ToString(CultureInfo.CurrentCulture), L.T("вылетов")));
            body.Children.Add(row);

            body.Children.Add(BuildGameHistoryChart(summary));
            FrameworkElement programs = BuildGameHistoryPrograms(summary);
            if (programs != null)
            {
                body.Children.Add(programs);
            }

            FrameworkElement section = MakeSectionHeading(L.T("Все игры"), null);
            section.Margin = new Thickness(0, 24, 0, 10);
            body.Children.Add(section);
            var list = MakeListCard();
            int index = 0;
            foreach (BoostSessionReport report in summary.Sessions.Take(MaximumHistoryRows))
            {
                AddListRow(list, BuildGameHistorySession(report, game, index++));
            }
            body.Children.Add(list);
            return root;
        }

        private FrameworkElement BuildGameHistoryChart(GameHistorySummary summary)
        {
            string caption = L.T("Минимум свободной памяти в каждой игре");
            var chart = new GameHistoryChart(summary.Sessions) { Height = 120 };
            AutomationProperties.SetAutomationId(chart, "Boostix.Center.GameHistory.Chart");
            AutomationProperties.SetName(chart, caption);
            var card = new Border
            {
                Padding = new Thickness(16, 14, 16, 12),
                Margin = new Thickness(0, 18, 0, 0),
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(BoostixDesignTokens.WithAlpha(Color.FromRgb(8, 14, 24), 170)),
                BorderBrush = new SolidColorBrush(BoostixDesignTokens.WithAlpha(Colors.White, 26)),
                BorderThickness = new Thickness(1)
            };
            var stack = new StackPanel();
            if (!chart.HasData)
            {
                var note = MakeText(
                    L.T("График есть у игр, сыгранных на Boostix 4.0 и новее."),
                    BoostixDesignTokens.BodyTextSize,
                    SecondaryColor,
                    regularFont,
                    FontWeights.Normal);
                note.TextWrapping = TextWrapping.Wrap;
                stack.Children.Add(note);
                card.Child = stack;
                return card;
            }
            var title = MakeText(caption, 12, SecondaryColor, regularFont, FontWeights.Normal);
            title.Margin = new Thickness(0, 0, 0, 8);
            stack.Children.Add(title);
            stack.Children.Add(chart);
            var dates = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            dates.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dates.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            IList<BoostSessionReport> bars = chart.Bars;
            dates.Children.Add(MakeText(
                bars[0].StartedUtc.ToLocalTime().ToString("d MMMM", L.DateCulture),
                11,
                MutedColor,
                regularFont,
                FontWeights.Normal));
            if (bars.Count > 1)
            {
                var last = MakeText(
                    bars[bars.Count - 1].StartedUtc.ToLocalTime().ToString("d MMMM", L.DateCulture),
                    11,
                    MutedColor,
                    regularFont,
                    FontWeights.Normal);
                Grid.SetColumn(last, 1);
                dates.Children.Add(last);
            }
            stack.Children.Add(dates);
            var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            legend.Children.Add(MakeLegendSwatch(BoostixDesignTokens.Success, L.T("памяти хватало")));
            legend.Children.Add(MakeLegendSwatch(BoostixDesignTokens.Warning, L.T("памяти не хватало")));
            legend.Children.Add(MakeLegendSwatch(BoostixDesignTokens.WithAlpha(Colors.White, 120), L.T("порог нехватки")));
            if (summary.Stats.Crashes > 0)
            {
                legend.Children.Add(MakeLegendSwatch(BoostixDesignTokens.Error, L.T("вылет")));
            }
            stack.Children.Add(legend);
            RevealChart(chart);
            card.Child = stack;
            return card;
        }

        private FrameworkElement BuildGameHistoryPrograms(GameHistorySummary summary)
        {
            if (summary.Programs.Count == 0)
            {
                return null;
            }
            var panel = new StackPanel();
            FrameworkElement section = MakeSectionHeading(L.T("Кто занимал память, когда её не хватало"), null);
            section.Margin = new Thickness(0, 24, 0, 10);
            panel.Children.Add(section);
            var list = MakeListCard();
            List<string> chosen = settings.CloseForGame ?? new List<string>();
            int index = 0;
            foreach (GameHistoryProgram program in summary.Programs)
            {
                var row = new Grid { MinHeight = 52 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 12, 8) };
                string facts = string.Format(
                    CultureInfo.CurrentCulture,
                    L.T("в {0} из {1} игр, до {2}"),
                    program.Sessions,
                    summary.ProgramSessions,
                    SessionVerdicts.FormatGigabytes(program.LargestBytes));
                // A panel has no automation peer; the name carries the row.
                TextBlock name = MakeText(program.Name, BoostixDesignTokens.BodyTextSize, TextColor, semiboldFont, FontWeights.SemiBold);
                AutomationProperties.SetAutomationId(name, "Boostix.Center.GameHistory.Program." + index.ToString(CultureInfo.InvariantCulture));
                AutomationProperties.SetName(name, program.Name + ": " + facts);
                words.Children.Add(name);
                words.Children.Add(MakeText(facts, BoostixDesignTokens.MetadataTextSize + 0.5, MutedColor, regularFont, FontWeights.Normal));
                row.Children.Add(words);

                FrameworkElement action;
                if (program.GameNeedsIt)
                {
                    action = MakeText(L.T("Нужен игре"), BoostixDesignTokens.MetadataTextSize + 0.5, MutedColor, regularFont, FontWeights.Normal);
                }
                else if (program.WouldNotClose)
                {
                    action = MakeText(L.T("Не закрылся в прошлый раз"), BoostixDesignTokens.MetadataTextSize + 0.5, MutedColor, regularFont, FontWeights.Normal);
                }
                else if (chosen.Contains(program.ProcessName, StringComparer.OrdinalIgnoreCase))
                {
                    action = MakeText(L.T("Закрывается на время игры"), BoostixDesignTokens.MetadataTextSize + 0.5, MutedColor, regularFont, FontWeights.Normal);
                }
                else if (SessionCloseList.IsValidName(program.ProcessName))
                {
                    action = BuildGameHistoryClose(program, index);
                }
                else
                {
                    action = null;
                }
                if (action != null)
                {
                    action.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(action, 1);
                    row.Children.Add(action);
                }
                AddListRow(list, row);
                index++;
            }
            panel.Children.Add(list);
            return panel;
        }

        private FrameworkElement BuildGameHistoryClose(GameHistoryProgram program, int index)
        {
            Button button = MakeActionButton(
                L.T("Закрывать на время игры"),
                BoostixButtonKind.Secondary,
                BoostixIcons.Shield);
            AutomationProperties.SetAutomationId(button, "Boostix.Center.GameHistory.Close." + index.ToString(CultureInfo.InvariantCulture));
            AutomationProperties.SetName(button, string.Format(CultureInfo.CurrentCulture, L.T("Закрывать {0} на время игры"), program.Name));
            button.Click += delegate
            {
                if (settings.CloseForGame == null)
                {
                    settings.CloseForGame = new List<string>();
                }
                if (!settings.CloseForGame.Contains(program.ProcessName, StringComparer.OrdinalIgnoreCase))
                {
                    settings.CloseForGame.Add(program.ProcessName);
                }
                button.IsEnabled = false;
                Raise(SettingsChanged);
            };
            return button;
        }

        private FrameworkElement BuildGameHistorySession(BoostSessionReport report, ShelfGame game, int index)
        {
            SessionVerdict headline = HeadlineVerdict(report);
            var button = new Button
            {
                Padding = new Thickness(0, 10, 0, 10),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Template = MakeFlatButtonTemplate(6),
                FocusVisualStyle = MakeKeyboardFocusVisualStyle(6)
            };
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            FrameworkElement dot = BoostixUi.StatusDot(headline == null ? BoostixDesignTokens.Data : headline.Tone, 8);
            dot.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(dot);
            string when = report.StartedUtc.ToLocalTime().ToString("d MMMM, HH:mm", L.DateCulture);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(MakeText(when, BoostixDesignTokens.BodyTextSize, TextColor, semiboldFont, FontWeights.SemiBold));
            if (headline != null)
            {
                words.Children.Add(MakeText(headline.Title, BoostixDesignTokens.MetadataTextSize + 0.5, MutedColor, regularFont, FontWeights.Normal));
            }
            Grid.SetColumn(words, 1);
            line.Children.Add(words);
            TimeSpan length = (report.EndedUtc ?? report.StartedUtc) - report.StartedUtc;
            var played = MakeText(GameStage.FormatPlayed(length), BoostixDesignTokens.BodyTextSize, SecondaryColor, regularFont, FontWeights.Normal);
            played.VerticalAlignment = VerticalAlignment.Center;
            played.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(played, 2);
            line.Children.Add(played);
            if (report.MinimumAvailableMemoryBytes > 0)
            {
                var free = MakeText(
                    string.Format(
                        CultureInfo.CurrentCulture,
                        L.T("свободно {0}"),
                        GameHistory.FormatLowest(report.MinimumAvailableMemoryBytes)),
                    BoostixDesignTokens.BodyTextSize,
                    SecondaryColor,
                    regularFont,
                    FontWeights.Normal);
                free.VerticalAlignment = VerticalAlignment.Center;
                free.MinWidth = 120;
                free.TextAlignment = TextAlignment.Right;
                Grid.SetColumn(free, 3);
                line.Children.Add(free);
            }
            button.Content = line;
            button.Click += delegate { OpenDebrief(report, game); };
            AutomationProperties.SetAutomationId(button, "Boostix.Center.GameHistory.Session." + index.ToString(CultureInfo.InvariantCulture));
            AutomationProperties.SetName(button, when + ". " + (headline == null ? string.Empty : headline.Title));
            return button;
        }

        private FrameworkElement BuildDebrief(BoostSessionReport report, ShelfGame game)
        {
            Color accent;
            StackPanel body;
            Grid root = MakeArtPage(game, out accent, out body);
            AutomationProperties.SetAutomationId(root, "Boostix.Center.Debrief");

            var heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Button back = MakeBackButton("Boostix.Center.Debrief.Back", L.T("Закрыть разбор"));
            back.Click += delegate { CloseDebrief(); };
            debriefBackButton = back;
            heading.Children.Add(back);
            var when = MakeText(
                report.StartedUtc.ToLocalTime().ToString("d MMMM, HH:mm", L.DateCulture),
                BoostixDesignTokens.BodyTextSize,
                SecondaryColor,
                regularFont,
                FontWeights.Normal);
            when.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(when, 1);
            heading.Children.Add(when);
            body.Children.Add(heading);
            return FinishDebrief(report, game, accent, root, body);
        }

        /// <summary>
        /// A full-screen page over the game's own art: the debrief and the
        /// game history.
        /// </summary>
        private Grid MakeArtPage(ShelfGame game, out Color accent, out StackPanel body)
        {
            StageArt art = GameArtwork.Stage(game);
            bool plain = SystemParameters.HighContrast;
            Color floor = plain ? BackgroundColor : art.Floor;
            accent = plain ? AccentColor : art.Accent;

            var root = new Grid { Background = new SolidColorBrush(floor) };
            if (!plain && (art.Hero ?? art.Ambient) != null)
            {
                root.Children.Add(new Border
                {
                    Height = 330,
                    VerticalAlignment = VerticalAlignment.Top,
                    IsHitTestVisible = false,
                    Background = new ImageBrush(art.Hero ?? art.Ambient)
                    {
                        Stretch = Stretch.UniformToFill,
                        AlignmentX = AlignmentX.Right,
                        AlignmentY = AlignmentY.Center
                    }
                });
                var fade = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
                fade.GradientStops.Add(new GradientStop(BoostixDesignTokens.WithAlpha(floor, 190), 0));
                fade.GradientStops.Add(new GradientStop(BoostixDesignTokens.WithAlpha(floor, 150), 0.4));
                fade.GradientStops.Add(new GradientStop(BoostixDesignTokens.WithAlpha(floor, 255), 1));
                root.Children.Add(new Rectangle
                {
                    Height = 332,
                    VerticalAlignment = VerticalAlignment.Top,
                    IsHitTestVisible = false,
                    Fill = fade
                });
                var side = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                side.GradientStops.Add(new GradientStop(BoostixDesignTokens.WithAlpha(floor, 225), 0));
                side.GradientStops.Add(new GradientStop(BoostixDesignTokens.WithAlpha(floor, 0), 0.75));
                root.Children.Add(new Rectangle
                {
                    Height = 332,
                    VerticalAlignment = VerticalAlignment.Top,
                    IsHitTestVisible = false,
                    Fill = side
                });
            }

            // The page scrolls below the top bar, so text never runs under the tabs.
            body = new StackPanel { Margin = new Thickness(44, 76 - TopBarHeight, 40, 30) };
            var scroller = new ScrollViewer
            {
                Margin = new Thickness(0, TopBarHeight, 0, 0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = body,
                Focusable = false
            };
            scroller.Resources[typeof(ScrollBar)] = MakeBoostixVerticalScrollBarStyle();
            root.Children.Add(scroller);
            return root;
        }

        private Button MakeBackButton(string automationId, string automationName)
        {
            var back = new Button
            {
                Padding = new Thickness(10, 7, 14, 7),
                Margin = new Thickness(-10, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Template = MakeFlatButtonTemplate(8),
                FocusVisualStyle = MakeKeyboardFocusVisualStyle(8)
            };
            var backLine = new StackPanel { Orientation = Orientation.Horizontal };
            FrameworkElement chevron = BoostixIcons.Create(BoostixIcons.ChevronRight, 15, TextColor);
            chevron.RenderTransformOrigin = new Point(0.5, 0.5);
            chevron.RenderTransform = new ScaleTransform(-1, 1);
            chevron.Margin = new Thickness(0, 1, 6, 0);
            backLine.Children.Add(chevron);
            backLine.Children.Add(MakeText(L.T("Назад"), 14, TextColor, semiboldFont, FontWeights.SemiBold));
            back.Content = backLine;
            AutomationProperties.SetAutomationId(back, automationId);
            AutomationProperties.SetName(back, automationName);
            return back;
        }

        private FrameworkElement FinishDebrief(
            BoostSessionReport report,
            ShelfGame game,
            Color accent,
            Grid root,
            StackPanel body)
        {
            var label = MakeText(game.Name, 16, SecondaryColor, semiboldFont, FontWeights.SemiBold);
            label.Margin = new Thickness(0, 14, 0, 0);
            AutomationProperties.SetAutomationId(label, "Boostix.Center.Debrief.Game");
            body.Children.Add(label);

            SessionVerdict headline = HeadlineVerdict(report);
            var title = new TextBlock
            {
                Text = headline == null ? L.T("Разбор игры") : headline.Title,
                FontFamily = new FontFamily("Bahnschrift SemiBold Condensed, Bahnschrift, Segoe UI"),
                FontSize = 44,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0)
            };
            AutomationProperties.SetAutomationId(title, "Boostix.Center.Debrief.Headline");
            body.Children.Add(title);
            if (headline != null)
            {
                var detail = MakeText(headline.Detail, 14.5, SecondaryColor, regularFont, FontWeights.Normal);
                detail.TextWrapping = TextWrapping.Wrap;
                detail.Margin = new Thickness(0, 2, 0, 0);
                body.Children.Add(detail);
            }

            body.Children.Add(BuildDebriefStats(report));

            string comparison = SessionComparison.Describe(report, sessionHistory);
            if (comparison.Length > 0)
            {
                var compared = MakeText(comparison, BoostixDesignTokens.BodyTextSize, SecondaryColor, regularFont, FontWeights.Normal);
                compared.TextWrapping = TextWrapping.Wrap;
                compared.Margin = new Thickness(0, 12, 0, 0);
                AutomationProperties.SetAutomationId(compared, "Boostix.Center.Debrief.Comparison");
                body.Children.Add(compared);
            }

            string stillRunning = SessionVerdicts.DescribeStillRunning(report);
            if (stillRunning.Length > 0)
            {
                var left = MakeText(stillRunning, BoostixDesignTokens.BodyTextSize, SecondaryColor, regularFont, FontWeights.Normal);
                left.TextWrapping = TextWrapping.Wrap;
                left.Margin = new Thickness(0, comparison.Length > 0 ? 4 : 12, 0, 0);
                AutomationProperties.SetAutomationId(left, "Boostix.Center.Debrief.StillRunning");
                body.Children.Add(left);
            }

            if (GameStats.For(game.Sessions).Sessions >= 2)
            {
                Button history = MakeLink(
                    L.T("История игры"),
                    "Boostix.Center.Debrief.History",
                    L.T("История игры"));
                history.Margin = new Thickness(0, 10, 0, 0);
                history.Click += delegate
                {
                    // Opened from the history, the debrief only has to step aside.
                    bool fromHistory = GameHistoryOpen;
                    CloseDebrief();
                    if (!fromHistory)
                    {
                        OpenGameHistory(game);
                    }
                };
                body.Children.Add(history);
            }

            var chart = new SessionChart(report.Timeline, report.PhysicalMemoryTotalBytes)
            {
                Height = 150,
                LineColor = accent
            };
            var chartCard = new Border
            {
                Padding = new Thickness(16, 14, 16, 12),
                Margin = new Thickness(0, 18, 0, 0),
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(BoostixDesignTokens.WithAlpha(Color.FromRgb(8, 14, 24), 170)),
                BorderBrush = new SolidColorBrush(BoostixDesignTokens.WithAlpha(Colors.White, 26)),
                BorderThickness = new Thickness(1)
            };
            var chartStack = new StackPanel();
            if (chart.HasData)
            {
                chartStack.Children.Add(chart);
                var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                legend.Children.Add(MakeLegendSwatch(accent, L.T("свободная память")));
                legend.Children.Add(MakeLegendSwatch(BoostixDesignTokens.SecondaryText, L.T("процессор")));
                legend.Children.Add(MakeLegendSwatch(BoostixDesignTokens.Warning, L.T("минимум памяти")));
                chartStack.Children.Add(legend);
                RevealChart(chart);
            }
            else
            {
                var note = MakeText(
                    L.T("График есть у игр, сыгранных на Boostix 4.0 и новее."),
                    BoostixDesignTokens.BodyTextSize,
                    SecondaryColor,
                    regularFont,
                    FontWeights.Normal);
                note.TextWrapping = TextWrapping.Wrap;
                chartStack.Children.Add(note);
            }
            chartCard.Child = chartStack;
            body.Children.Add(chartCard);

            // The headline is already at the top of the page.
            List<SessionVerdict> others = SessionVerdicts.Describe(report)
                .Where(verdict => headline == null || verdict.Title != headline.Title)
                .ToList();
            var verdicts = new UniformGrid { Columns = others.Count, Margin = new Thickness(0, 8, 0, 0) };
            foreach (SessionVerdict verdict in others)
            {
                FrameworkElement row = BuildVerdictRow(verdict);
                row.Margin = new Thickness(0, 10, 22, 0);
                verdicts.Children.Add(row);
            }
            body.Children.Add(verdicts);
            FrameworkElement knownProgram = BuildKnownProgramButton(report);
            if (knownProgram != null)
            {
                body.Children.Add(knownProgram);
            }
            FrameworkElement closeForGame = BuildCloseForGameButton(report);
            if (closeForGame != null)
            {
                body.Children.Add(closeForGame);
            }

            BoostCrashInsight crashInsight = BoostCrashAssistant.Analyze(report);
            if (crashInsight.Category != BoostCrashCategory.None)
            {
                FrameworkElement insight = BuildCrashInsight(crashInsight);
                insight.Margin = new Thickness(0, 18, 0, 0);
                body.Children.Add(insight);
            }
            if (report.Performance != null && report.Performance.Available)
            {
                FrameworkElement performance = BuildPerformanceGrid(report.Performance);
                performance.Margin = new Thickness(0, 18, 0, 0);
                body.Children.Add(performance);
            }

            List<BoostActionRecord> actions = (report.Actions ?? new List<BoostActionRecord>())
                .OrderByDescending(item => item.TimestampUtc)
                .Take(20)
                .ToList();
            if (actions.Count > 0)
            {
                FrameworkElement section = MakeSectionHeading(L.T("Что сделал Boostix"), null);
                section.Margin = new Thickness(0, 24, 0, 10);
                body.Children.Add(section);
                var list = MakeListCard();
                foreach (BoostActionRecord action in actions)
                {
                    AddListRow(list, BuildActionRow(action));
                }
                body.Children.Add(list);
            }
            return root;
        }

        /// <summary>
        /// The player knows this program: no more warnings about it. It stays
        /// in the list of who held the video memory.
        /// </summary>
        private FrameworkElement BuildKnownProgramButton(BoostSessionReport report)
        {
            BoostMemoryHog hog = (report.VideoMemoryHogs ?? new List<BoostMemoryHog>())
                .FirstOrDefault(item => VideoMemoryRanking.IsSuspicious(item.ProcessName));
            if (hog == null)
            {
                return null;
            }

            Button button = MakeActionButton(
                L.T("Это моя программа"),
                BoostixButtonKind.Secondary,
                BoostixIcons.Shield);
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.Margin = new Thickness(0, 14, 0, 0);
            AutomationProperties.SetAutomationId(button, "Boostix.Center.Debrief.KnownProgram");
            button.Click += delegate
            {
                if (settings.KnownPrograms == null)
                {
                    settings.KnownPrograms = new List<string>();
                }
                settings.KnownPrograms.Add(hog.ProcessName);
                button.IsEnabled = false;
                Raise(SettingsChanged);
            };
            return button;
        }

        /// <summary>
        /// One click from "who held the memory" to closing it for every game.
        /// </summary>
        private FrameworkElement BuildCloseForGameButton(BoostSessionReport report)
        {
            List<string> chosen = settings.CloseForGame ?? new List<string>();
            BoostMemoryHog hog = SessionVerdicts.BiggestClosable(
                (report.VideoMemoryHogs ?? new List<BoostMemoryHog>())
                    .Concat(report.MemoryHogs ?? new List<BoostMemoryHog>())
                    .Where(item => !string.IsNullOrWhiteSpace(item.ProcessName) &&
                        !VideoMemoryRanking.IsSystemTool(item.ProcessName) &&
                        !chosen.Contains(item.ProcessName, StringComparer.OrdinalIgnoreCase)),
                report.TargetExecutablePath);
            if (hog == null)
            {
                return null;
            }

            Button button = MakeActionButton(
                string.Format(CultureInfo.CurrentCulture, L.T("Закрывать {0} на время игры"), hog.Name),
                BoostixButtonKind.Secondary,
                BoostixIcons.Shield);
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.Margin = new Thickness(0, 14, 0, 0);
            AutomationProperties.SetAutomationId(button, "Boostix.Center.Debrief.CloseForGame");
            button.Click += delegate
            {
                if (settings.CloseForGame == null)
                {
                    settings.CloseForGame = new List<string>();
                }
                settings.CloseForGame.Add(hog.ProcessName);
                button.IsEnabled = false;
                Raise(SettingsChanged);
            };
            return button;
        }

        private FrameworkElement BuildDebriefStats(BoostSessionReport report)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
            TimeSpan length = (report.EndedUtc ?? report.StartedUtc) - report.StartedUtc;
            row.Children.Add(BuildBigStat(GameStage.FormatPlayed(length), L.T("длилась игра")));
            if (report.MinimumAvailableMemoryBytes > 0)
            {
                row.Children.Add(BuildBigStat(
                    SessionVerdicts.FormatGigabytes(report.MinimumAvailableMemoryBytes),
                    L.T("минимум свободной памяти")));
            }
            if (report.PeakGpuDedicatedUsageBytes > 0)
            {
                row.Children.Add(BuildBigStat(
                    SessionVerdicts.FormatGigabytes(report.PeakGpuDedicatedUsageBytes),
                    L.T("пик видеопамяти")));
            }
            if (report.Performance != null && report.Performance.Available)
            {
                row.Children.Add(BuildBigStat(
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "{0:0} / {1:0}",
                        report.Performance.AverageFps,
                        report.Performance.OnePercentLowFps),
                    L.T("FPS средний / 1% low")));
            }
            List<int> loads = (report.Timeline ?? new List<SessionTimelinePoint>())
                .Where(point => point.CpuPercent >= 0)
                .Select(point => (int)point.CpuPercent)
                .ToList();
            if (loads.Count > 0)
            {
                row.Children.Add(BuildBigStat(
                    ((int)Math.Round(loads.Average())).ToString(CultureInfo.CurrentCulture) + "%",
                    L.T("процессор в среднем")));
            }
            return row;
        }

        /// <summary>
        /// The chart draws itself from left to right when the debrief opens.
        /// </summary>
        private static void RevealChart(FrameworkElement chart)
        {
            if (!BoostixDesignTokens.MotionEnabled)
            {
                return;
            }
            chart.Loaded += delegate
            {
                double width = chart.ActualWidth;
                double height = chart.ActualHeight + 12;
                var clip = new RectangleGeometry(new Rect(0, -6, 0, height));
                chart.Clip = clip;
                var reveal = new RectAnimation(
                    new Rect(0, -6, 0, height),
                    new Rect(0, -6, width, height),
                    TimeSpan.FromMilliseconds(900))
                {
                    BeginTime = TimeSpan.FromMilliseconds(160),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                };
                reveal.Completed += delegate { chart.Clip = null; };
                clip.BeginAnimation(RectangleGeometry.RectProperty, reveal);
            };
        }

        // ---------------------------------------------------------------
        // Computer: one answer first, then the four meters behind it.
        // ---------------------------------------------------------------

        private FrameworkElement BuildPcVerdict()
        {
            string title;
            string detail;
            Color tone;
            if (preflight == null)
            {
                title = L.T("Проверяем компьютер…");
                detail = L.T("Собираем только безопасные данные без изменения Windows.");
                tone = SecondaryColor;
            }
            else if (preflight.HasBlockers)
            {
                title = L.T("Перед игрой нужно кое-что исправить");
                detail = L.T("Boostix не запустит Boost, пока это не решено. Подробности ниже.");
                tone = ErrorColor;
            }
            else if (preflight.Checks.Any(check => check.Severity == BoostCheckSeverity.Warning))
            {
                title = L.T("Играть можно, но есть замечания");
                detail = L.T("Они не мешают Boost, но могут влиять на игру. Подробности ниже.");
                tone = WarningColor;
            }
            else
            {
                title = L.T("Компьютер готов к игре");
                detail = L.T("Все проверки пройдены.");
                tone = SuccessColor;
            }
            var block = new StackPanel { Margin = new Thickness(0, -4, 0, 18) };
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            FrameworkElement dot = BoostixUi.StatusDot(tone, 12);
            dot.Margin = new Thickness(1, 3, 11, 0);
            line.Children.Add(dot);
            var heading = new TextBlock
            {
                Text = title,
                FontFamily = new FontFamily("Bahnschrift SemiBold Condensed, Bahnschrift, Segoe UI"),
                FontSize = 34,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(TextColor)
            };
            AutomationProperties.SetAutomationId(heading, "Boostix.Center.PcVerdict");
            AutomationProperties.SetLiveSetting(heading, AutomationLiveSetting.Polite);
            line.Children.Add(heading);
            block.Children.Add(line);
            var text = MakeText(detail, 14, SecondaryColor, regularFont, FontWeights.Normal);
            text.Margin = new Thickness(24, 2, 0, 0);
            text.TextWrapping = TextWrapping.Wrap;
            block.Children.Add(text);
            return block;
        }

        private FrameworkElement BuildPcMeters(DiagnosticSnapshot snapshot)
        {
            var row = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 12) };
            AutomationProperties.SetAutomationId(row, "Boostix.Center.Meters");
            row.Children.Add(BuildMeter(
                L.T("Оперативная память"),
                snapshot.MemoryAvailable && snapshot.PhysicalTotalBytes > 0
                    ? 1.0 - (double)snapshot.PhysicalAvailableBytes / snapshot.PhysicalTotalBytes
                    : double.NaN,
                snapshot.MemoryAvailable
                    ? FormatBytesCompact(snapshot.PhysicalAvailableBytes) + L.T(" свободно")
                    : L.T("Недоступно")));
            row.Children.Add(BuildMeter(
                L.T("Резерв Windows"),
                snapshot.MemoryAvailable && snapshot.CommitLimitBytes > 0
                    ? 1.0 - (double)snapshot.CommitHeadroomBytes / snapshot.CommitLimitBytes
                    : double.NaN,
                snapshot.MemoryAvailable
                    ? FormatBytesCompact(snapshot.CommitHeadroomBytes) + L.T(" в запасе")
                    : L.T("Недоступно")));
            bool gpuKnown = snapshot.GpuUsageAvailable &&
                snapshot.GpuTotalAvailable &&
                snapshot.GpuDedicatedTotalBytes > 0;
            row.Children.Add(BuildMeter(
                L.T("Видеопамять"),
                gpuKnown
                    ? (double)snapshot.GpuDedicatedUsageBytes / snapshot.GpuDedicatedTotalBytes
                    : double.NaN,
                gpuKnown
                    ? FormatBytesCompact(snapshot.GpuDedicatedUsageBytes) + L.T(" из ") + FormatBytesCompact(snapshot.GpuDedicatedTotalBytes)
                    : L.T("Недоступно")));
            row.Children.Add(BuildMeter(
                L.T("Файл подкачки"),
                snapshot.PageFileAvailable && snapshot.PageFileAllocatedBytes > 0
                    ? (double)snapshot.PageFileUsedBytes / snapshot.PageFileAllocatedBytes
                    : double.NaN,
                snapshot.PageFileAvailable
                    ? FormatBytesCompact(snapshot.PageFileUsedBytes) + L.T(" из ") + FormatBytesCompact(snapshot.PageFileAllocatedBytes)
                    : FormatUnavailablePageFile(snapshot)));
            return row;
        }

        private FrameworkElement BuildMeter(string title, double usedFraction, string caption)
        {
            bool known = !double.IsNaN(usedFraction);
            Color color = !known
                ? MutedColor
                : usedFraction >= 0.92
                    ? ErrorColor
                    : (usedFraction >= 0.8 ? WarningColor : (stageAccent ?? BoostixDesignTokens.Data));
            string center = known
                ? ((int)Math.Round(usedFraction * 100)).ToString(CultureInfo.CurrentCulture) + "%"
                : "—";
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(new MeterRing(usedFraction, color, center, semiboldFont)
            {
                Width = 112,
                Height = 112,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            var name = MakeText(title, BoostixDesignTokens.BodyTextSize + 0.5, TextColor, semiboldFont, FontWeights.SemiBold);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.Margin = new Thickness(0, 10, 0, 0);
            stack.Children.Add(name);
            var detail = MakeText(caption, BoostixDesignTokens.MetadataTextSize + 0.5, SecondaryColor, regularFont, FontWeights.Normal);
            detail.HorizontalAlignment = HorizontalAlignment.Center;
            detail.Margin = new Thickness(0, 1, 0, 0);
            stack.Children.Add(detail);
            AutomationProperties.SetName(stack, title + ": " + center + ", " + caption);
            return stack;
        }
    }
}
