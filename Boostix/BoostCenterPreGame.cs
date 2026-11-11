using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Boostix
{
    /// <summary>
    /// Before the game: Play stops here when the game is known to need more
    /// memory than is free. The memory bar fills as programs close.
    /// </summary>
    internal sealed partial class BoostCenterOverlay
    {
        private const int PreGameRefreshMilliseconds = 2000;
        private const int PreGameCloseWaitMilliseconds = 4000;
        private Grid preGameLayer;
        private ShelfGame preGameGame;
        private Func<PreGamePlan> preGameCapture;
        private DispatcherTimer preGameTimer;
        private bool preGameRefreshing;
        private Button preGameBackButton;
        private PreGameMeter preGameMeter;
        private TextBlock preGameTitle;
        private TextBlock preGameDetail;
        private TextBlock preGameNumbers;
        private StackPanel preGameRows;
        private StackPanel preGameLegend;
        private string preGameRowsSignature = string.Empty;
        private readonly HashSet<string> preGameClosed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool preGameDemo;

        public event EventHandler<ShelfGameEventArgs> PreGamePlayRequested;

        private bool PreGameOpen
        {
            get { return preGameLayer != null && preGameLayer.Visibility == Visibility.Visible; }
        }

        internal void OpenPreGame(ShelfGame game, PreGamePlan plan, Func<PreGamePlan> capture)
        {
            if (game == null || plan == null)
            {
                return;
            }
            CloseDebrief();
            CloseGameHistory();
            if (preGameLayer == null)
            {
                preGameLayer = new Grid { Visibility = Visibility.Collapsed };
                Grid.SetRowSpan(preGameLayer, 2);
                Panel.SetZIndex(preGameLayer, 3);
                Children.Add(preGameLayer);
            }
            preGameGame = game;
            preGameCapture = capture;
            preGameClosed.Clear();
            preGameRowsSignature = string.Empty;
            preGameLayer.Children.Clear();
            preGameLayer.Children.Add(BuildPreGame(game));
            ApplyPreGamePlan(plan);
            preGameLayer.Visibility = Visibility.Visible;
            if (BoostixDesignTokens.MotionEnabled)
            {
                var slide = new TranslateTransform(0, 18);
                preGameLayer.RenderTransform = slide;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                TimeSpan time = TimeSpan.FromMilliseconds(300);
                preGameLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, time) { EasingFunction = ease });
                slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, time) { EasingFunction = ease });
            }
            else
            {
                preGameLayer.BeginAnimation(OpacityProperty, null);
                preGameLayer.Opacity = 1;
            }
            // Ticks only while the screen is open: it is stopped on close.
            if (preGameTimer == null)
            {
                preGameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PreGameRefreshMilliseconds) };
                preGameTimer.Tick += delegate { RefreshPreGame(); };
            }
            preGameTimer.Start();
            FocusAfterLayout(preGameBackButton);
        }

        /// <summary>
        /// --demo-pregame: the screen for the demo shelf's first game.
        /// </summary>
        internal void ShowDemoPreGame()
        {
            ShelfGame game = GameShelf.Build(gameProfiles, installedGames, sessionHistory).FirstOrDefault();
            if (game == null)
            {
                return;
            }
            preGameDemo = true;
            var closed = preGameClosed;
            OpenPreGame(
                game,
                PreGameCheck.Demo(game.Name, game.ExecutablePath, closed),
                delegate { return PreGameCheck.Demo(game.Name, game.ExecutablePath, closed); });
        }

        private void ClosePreGame()
        {
            if (preGameTimer != null)
            {
                preGameTimer.Stop();
            }
            if (!PreGameOpen)
            {
                return;
            }
            preGameLayer.Visibility = Visibility.Collapsed;
            preGameLayer.Children.Clear();
            preGameCapture = null;
            preGameBackButton = null;
            FocusPreferredButton();
        }

        private FrameworkElement BuildPreGame(ShelfGame game)
        {
            Color accent;
            StackPanel body;
            Grid root = MakeArtPage(game, out accent, out body);
            AutomationProperties.SetAutomationId(root, "Boostix.Center.PreGame");

            Button back = MakeBackButton("Boostix.Center.PreGame.Back", L.T("Не запускать игру"));
            back.Click += delegate { ClosePreGame(); };
            preGameBackButton = back;
            body.Children.Add(back);

            var label = MakeText(game.Name, 16, SecondaryColor, semiboldFont, FontWeights.SemiBold);
            label.Margin = new Thickness(0, 14, 0, 0);
            body.Children.Add(label);
            preGameTitle = new TextBlock
            {
                FontFamily = new FontFamily("Bahnschrift SemiBold Condensed, Bahnschrift, Segoe UI"),
                FontSize = 44,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0)
            };
            AutomationProperties.SetAutomationId(preGameTitle, "Boostix.Center.PreGame.Headline");
            body.Children.Add(preGameTitle);
            preGameDetail = MakeText(string.Empty, 14.5, SecondaryColor, regularFont, FontWeights.Normal);
            preGameDetail.TextWrapping = TextWrapping.Wrap;
            preGameDetail.Margin = new Thickness(0, 2, 0, 0);
            body.Children.Add(preGameDetail);

            var card = new Border
            {
                Padding = new Thickness(16, 16, 16, 12),
                Margin = new Thickness(0, 18, 0, 0),
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(BoostixDesignTokens.WithAlpha(Color.FromRgb(8, 14, 24), 170)),
                BorderBrush = new SolidColorBrush(BoostixDesignTokens.WithAlpha(Colors.White, 26)),
                BorderThickness = new Thickness(1)
            };
            var meterStack = new StackPanel();
            preGameMeter = new PreGameMeter { Height = 14, Margin = new Thickness(0, 4, 0, 0) };
            AutomationProperties.SetAutomationId(preGameMeter, "Boostix.Center.PreGame.Meter");
            meterStack.Children.Add(preGameMeter);
            preGameNumbers = MakeText(string.Empty, 12, SecondaryColor, regularFont, FontWeights.Normal);
            preGameNumbers.TextWrapping = TextWrapping.Wrap;
            preGameNumbers.Margin = new Thickness(0, 10, 0, 0);
            meterStack.Children.Add(preGameNumbers);
            preGameLegend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            meterStack.Children.Add(preGameLegend);
            card.Child = meterStack;
            body.Children.Add(card);

            // Play right under the bar: the list below can be long.
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
            Button play = MakeActionButton(L.T("Играть"), BoostixButtonKind.Primary, BoostixIcons.Play);
            AutomationProperties.SetAutomationId(play, "Boostix.Center.PreGame.Play");
            play.Click += delegate
            {
                ShelfGame chosen = preGameGame;
                ClosePreGame();
                EventHandler<ShelfGameEventArgs> handler = PreGamePlayRequested;
                if (handler != null && chosen != null)
                {
                    handler(this, new ShelfGameEventArgs(chosen));
                }
            };
            actions.Children.Add(play);
            var note = MakeText(
                L.T("Boost закроет программы из вашего списка сам, когда игра запустится."),
                BoostixDesignTokens.MetadataTextSize + 0.5,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            note.TextWrapping = TextWrapping.Wrap;
            note.MaxWidth = 380;
            note.VerticalAlignment = VerticalAlignment.Center;
            note.Margin = new Thickness(16, 0, 0, 0);
            actions.Children.Add(note);
            body.Children.Add(actions);

            FrameworkElement section = MakeSectionHeading(L.T("Что занимает память сейчас"), null);
            section.Margin = new Thickness(0, 24, 0, 10);
            body.Children.Add(section);
            preGameRows = MakeListCard();
            body.Children.Add(preGameRows);
            return root;
        }

        private void ApplyPreGamePlan(PreGamePlan plan)
        {
            if (plan == null || preGameTitle == null)
            {
                return;
            }
            SessionVerdict verdict = PreGameCheck.Describe(plan);
            preGameTitle.Text = verdict.Title;
            AutomationProperties.SetName(preGameTitle, verdict.Title);
            preGameDetail.Text = verdict.Detail;
            preGameMeter.Plan = plan;
            preGameLegend.Children.Clear();
            preGameLegend.Children.Add(MakeLegendSwatch(BoostixDesignTokens.WithAlpha(Colors.White, 92), L.T("заняли программы")));
            preGameLegend.Children.Add(MakeLegendSwatch(
                plan.IsShort ? BoostixDesignTokens.Warning : BoostixDesignTokens.Success,
                L.T("нужно игре")));
            if (plan.IsShort)
            {
                preGameLegend.Children.Add(MakeLegendSwatch(BoostixDesignTokens.Error, L.T("не поместится")));
            }
            AutomationProperties.SetName(preGameMeter, verdict.Detail);
            preGameNumbers.Text = plan.IsShort
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    L.T("Не хватает {0}. Всего памяти {1}."),
                    SessionVerdicts.FormatGigabytes(plan.MissingBytes),
                    SessionVerdicts.FormatGigabytes(plan.TotalBytes))
                : string.Format(
                    CultureInfo.CurrentCulture,
                    L.T("После запуска останется около {0}. Всего памяти {1}."),
                    SessionVerdicts.FormatGigabytes(plan.AvailableBytes - plan.TypicalBytes),
                    SessionVerdicts.FormatGigabytes(plan.TotalBytes));

            // Rebuilt only when the list itself changes, so focus stays put.
            List<PreGameProgram> programs = plan.Programs
                .Select(program => preGameClosed.Contains(program.ProcessName) && program.Kind == PreGameProgramKind.Closable
                    ? new PreGameProgram { Name = program.Name, ProcessName = program.ProcessName, Bytes = program.Bytes, Kind = PreGameProgramKind.Closed }
                    : program)
                .ToList();
            string signature = string.Join("|", programs.Select(program => program.ProcessName + ":" + program.Kind).ToArray());
            if (signature == preGameRowsSignature)
            {
                return;
            }
            preGameRowsSignature = signature;
            preGameRows.Children.Clear();
            preGameRows.Tag = null;
            int index = 0;
            foreach (PreGameProgram program in programs)
            {
                AddListRow(preGameRows, BuildPreGameRow(program, index++));
            }
        }

        private FrameworkElement BuildPreGameRow(PreGameProgram program, int index)
        {
            var row = new Grid { MinHeight = 52 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 12, 8) };
            string size = SessionVerdicts.FormatGigabytes(program.Bytes);
            TextBlock name = MakeText(program.Name, BoostixDesignTokens.BodyTextSize, TextColor, semiboldFont, FontWeights.SemiBold);
            AutomationProperties.SetAutomationId(name, "Boostix.Center.PreGame.Program." + index.ToString(CultureInfo.InvariantCulture));
            AutomationProperties.SetName(name, program.Name + ": " + size);
            words.Children.Add(name);
            words.Children.Add(MakeText(size, BoostixDesignTokens.MetadataTextSize + 0.5, MutedColor, regularFont, FontWeights.Normal));
            row.Children.Add(words);

            FrameworkElement action;
            switch (program.Kind)
            {
                case PreGameProgramKind.Closable:
                    action = BuildPreGameClose(program, index);
                    break;
                case PreGameProgramKind.GameNeedsIt:
                    action = MakeText(L.T("Нужен игре"), BoostixDesignTokens.MetadataTextSize + 0.5, MutedColor, regularFont, FontWeights.Normal);
                    break;
                case PreGameProgramKind.ClosesForGame:
                    action = MakeText(L.T("Boost уберёт на время игры"), BoostixDesignTokens.MetadataTextSize + 0.5, MutedColor, regularFont, FontWeights.Normal);
                    break;
                case PreGameProgramKind.Closed:
                    action = MakeText(L.T("Закрыт"), BoostixDesignTokens.MetadataTextSize + 0.5, MutedColor, regularFont, FontWeights.Normal);
                    break;
                default:
                    action = MakeText(L.T("Закрывается только у часов"), BoostixDesignTokens.MetadataTextSize + 0.5, MutedColor, regularFont, FontWeights.Normal);
                    break;
            }
            action.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(action, 1);
            row.Children.Add(action);
            return row;
        }

        private FrameworkElement BuildPreGameClose(PreGameProgram program, int index)
        {
            Button button = MakeActionButton(L.T("Закрыть"), BoostixButtonKind.Secondary, BoostixIcons.Shield);
            AutomationProperties.SetAutomationId(button, "Boostix.Center.PreGame.Close." + index.ToString(CultureInfo.InvariantCulture));
            AutomationProperties.SetName(button, string.Format(CultureInfo.CurrentCulture, L.T("Закрыть {0}"), program.Name));
            button.Click += async delegate
            {
                button.IsEnabled = false;
                string processName = program.ProcessName;
                bool closed = preGameDemo || await Task.Run(delegate
                {
                    return PreGameCheck.RequestClose(processName, PreGameCloseWaitMilliseconds);
                });
                if (closed)
                {
                    preGameClosed.Add(processName);
                }
                else
                {
                    button.IsEnabled = true;
                    preGameNumbers.Text = string.Format(
                        CultureInfo.CurrentCulture,
                        L.T("{0} не закрылся: возможно, он спрашивает о сохранении или прячется у часов."),
                        program.Name);
                }
                RefreshPreGame();
            };
            return button;
        }

        private void RefreshPreGame()
        {
            Func<PreGamePlan> capture = preGameCapture;
            if (!PreGameOpen || capture == null || preGameRefreshing)
            {
                return;
            }
            preGameRefreshing = true;
            Task.Run(capture).ContinueWith(task =>
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        preGameRefreshing = false;
                        if (PreGameOpen && task.Status == TaskStatus.RanToCompletion)
                        {
                            ApplyPreGamePlan(task.Result);
                        }
                    }));
                }
                catch (InvalidOperationException) { }
                catch (TaskCanceledException) { }
            }, TaskScheduler.Default);
        }
    }
}
