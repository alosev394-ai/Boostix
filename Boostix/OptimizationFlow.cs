using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Boostix
{
    internal sealed class OptimizationFlowOverlay : Grid
    {
        private enum FlowState
        {
            Hidden,
            Consent,
            Applying,
            RebootRequired,
            RecoveryRequired,
            ManualRestoreConfirmation,
            Restoring,
            Failure
        }

        private sealed class ScriptResult
        {
            public int ExitCode;
            public Exception Error;
            public bool ElevationCanceled;
            public bool TimedOut;
        }

        private sealed class GlobalTransactionInfo
        {
            public string StatePath;
            public int Version;
            public string Status;
        }

        private static readonly Color BackgroundColor = BoostixDesignTokens.Background;
        private static readonly Color TextColor = BoostixDesignTokens.Text;
        private static readonly Color MutedColor = BoostixDesignTokens.MutedText;
        private static readonly Color AccentColor = BoostixDesignTokens.Accent;
        private static readonly Color ErrorColor = BoostixDesignTokens.Error;
        private const int ElevatedScriptTimeoutMilliseconds = 10 * 60 * 1000;
        private const string ApplyScriptSha256 =
            "27CE23AD18B12C025FA5079AAC06680652A0B0D03555B39554825891E6711C3E";
        private const string RestoreScriptSha256 =
            "BFE02A3B3C4DBC58F215A62ABFA80EBD7B3CCC40C7A2C488A62ED06D0F1C0D76";

        private readonly Window owner;
        private readonly string[] arguments;
        private readonly FontFamily regularFont;
        private readonly FontFamily semiboldFont;
        private readonly Border card;
        private readonly Grid cardContent;
        private readonly string stateDirectory;
        private readonly string pendingMarkerPath;
        private readonly string completedMarkerPath;
        private readonly string legacyPendingMarkerPath;
        private readonly string legacyCompletedMarkerPath;
        private readonly bool demoMode;

        private FlowState state;
        private string pendingStatePath;
        private bool rollbackRequested;
        private bool allowOwnerClose;
        private Button preferredFocusButton;

        public OptimizationFlowOverlay(
            Window ownerWindow,
            string[] launchArguments,
            FontFamily normalFont,
            FontFamily boldFont)
        {
            owner = ownerWindow;
            arguments = launchArguments ?? new string[0];
            regularFont = normalFont ?? new FontFamily("Segoe UI");
            semiboldFont = boldFont ?? regularFont;
            demoMode = HasArgument("--demo-setup");

            stateDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Boostix");
            pendingMarkerPath = Path.Combine(stateDirectory, "optimization-pending.json");
            completedMarkerPath = Path.Combine(stateDirectory, "optimization-completed-v1.marker");
            string legacyStateDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MajesticBoost");
            legacyPendingMarkerPath = Path.Combine(
                legacyStateDirectory,
                "optimization-pending.json");
            legacyCompletedMarkerPath = Path.Combine(
                legacyStateDirectory,
                "optimization-completed-v1.marker");

            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            Background = new SolidColorBrush(SystemParameters.HighContrast
                ? BackgroundColor
                : BoostixDesignTokens.WithAlpha(BoostixDesignTokens.Sidebar, 222));
            Visibility = Visibility.Collapsed;
            Focusable = true;
            KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.Cycle);
            AutomationProperties.SetName(this, "Первоначальная настройка производительности");

            card = new Border
            {
                Width = 500,
                MaxHeight = 520,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(BoostixDesignTokens.Surface),
                BorderBrush = new SolidColorBrush(BoostixDesignTokens.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(30, 28, 30, 26)
            };
            if (!SystemParameters.HighContrast)
            {
                card.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 40,
                    ShadowDepth = 8,
                    Opacity = 0.55
                };
            }
            Children.Add(card);

            cardContent = new Grid();
            card.Child = cardContent;

            PreviewKeyDown += OverlayPreviewKeyDown;
            owner.Activated += delegate
            {
                if (IsFlowVisible)
                {
                    FocusPreferredButton();
                }
            };
        }

        public event EventHandler RequestApplicationClose;

        public bool IsFlowVisible
        {
            get { return Visibility == Visibility.Visible; }
        }

        internal bool IsInitializedForUpdateHealth()
        {
            // This check is intentionally in-memory only. The installer health
            // probe can run elevated and must not traverse profile marker paths.
            return owner != null &&
                regularFont != null &&
                semiboldFont != null &&
                card != null &&
                cardContent != null;
        }

        internal string GetOptimizationStatus()
        {
            GlobalTransactionInfo transaction;
            if (TryReadGlobalTransaction(out transaction))
            {
                if (transaction.Version < 2)
                {
                    return "Unknown";
                }

                if (string.Equals(transaction.Status, "Active", StringComparison.OrdinalIgnoreCase))
                {
                    return "Active";
                }

                if (IsRecoveryStatus(transaction.Status))
                {
                    return "NeedsRecovery";
                }

                if (IsRestoredStatus(transaction.Status))
                {
                    return "Restored";
                }

                return "Unknown";
            }

            bool pointerExists;
            if (!TryGetGlobalTransactionPointerPresence(out pointerExists) || pointerExists)
            {
                return "Unknown";
            }

            string completedStatePath;
            if (TryReadCompletedMarker(out completedStatePath))
            {
                return "Restored";
            }

            DateTime appliedUtc;
            string pendingStatePathFromMarker;
            if (TryReadPendingMarker(out pendingStatePathFromMarker, out appliedUtc))
            {
                return "Unknown";
            }

            return "None";
        }

        internal bool ShowManualRestore()
        {
            if (IsFlowVisible)
            {
                return false;
            }

            GlobalTransactionInfo transaction;
            if (!TryReadGlobalTransaction(out transaction) ||
                transaction.Version < 2 ||
                !string.Equals(transaction.Status, "Active", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            pendingStatePath = transaction.StatePath;
            allowOwnerClose = false;
            rollbackRequested = false;
            ShowManualRestoreConfirmation();
            return true;
        }

        public void ShowIfRequired()
        {
            if (IsFlowVisible)
            {
                return;
            }

            allowOwnerClose = false;
            rollbackRequested = false;

            if (HasArgument("--skip-setup"))
            {
                HideFlow();
                return;
            }

            if (demoMode)
            {
                if (HasArgument("--demo-recovery"))
                {
                    pendingStatePath = Path.Combine(Path.GetTempPath(), "Boostix-demo-state.json");
                    ShowRecoveryRequired("ApplyFailed");
                    return;
                }
                ShowConsent(null);
                return;
            }

            DateTime appliedUtc;
            string markerStatePath;
            bool hasLocalPending = TryReadPendingMarker(out markerStatePath, out appliedUtc);

            GlobalTransactionInfo globalTransaction;
            bool hasGlobalTransaction = TryReadGlobalTransaction(out globalTransaction);
            bool localMatchesGlobal =
                hasLocalPending &&
                hasGlobalTransaction &&
                PathsEqual(markerStatePath, globalTransaction.StatePath);

            string completedStatePath;
            bool completedMatchesGlobal =
                hasGlobalTransaction &&
                TryReadCompletedMarker(out completedStatePath) &&
                PathsEqual(completedStatePath, globalTransaction.StatePath);

            if (hasGlobalTransaction && globalTransaction.Version >= 2)
            {
                if (string.Equals(globalTransaction.Status, "Active", StringComparison.OrdinalIgnoreCase))
                {
                    pendingStatePath = globalTransaction.StatePath;
                    if (localMatchesGlobal && HasRestartedSince(appliedUtc))
                    {
                        if (TryMarkCompleted(globalTransaction.StatePath))
                        {
                            TryDeleteFile(pendingMarkerPath);
                            TryDeleteFile(legacyPendingMarkerPath);
                            HideFlow();
                            return;
                        }
                    }

                    if (completedMatchesGlobal)
                    {
                        HideFlow();
                        return;
                    }

                    ShowRebootRequired();
                    return;
                }

                if (IsRecoveryStatus(globalTransaction.Status))
                {
                    pendingStatePath = globalTransaction.StatePath;
                    ShowRecoveryRequired(globalTransaction.Status);
                    return;
                }
            }

            // A pointer is authoritative when it exists. Do not act on an unrelated
            // per-user marker if the machine-wide transaction points elsewhere.
            if (hasGlobalTransaction && hasLocalPending && !localMatchesGlobal)
            {
                hasLocalPending = false;
            }

            // Version 1 backups predate transactional recovery. Without a matching
            // local marker they remain available for the Apply script to adopt, but
            // this unelevated UI must not guess that they are an interrupted run.
            if (hasGlobalTransaction && globalTransaction.Version < 2 && !hasLocalPending)
            {
                ShowConsent(null);
                return;
            }

            if (hasLocalPending)
            {
                pendingStatePath = markerStatePath;
                if (HasRestartedSince(appliedUtc))
                {
                    if (TryMarkCompleted(markerStatePath))
                    {
                        TryDeleteFile(pendingMarkerPath);
                        TryDeleteFile(legacyPendingMarkerPath);
                        HideFlow();
                        return;
                    }
                }

                ShowRebootRequired();
                return;
            }

            if (File.Exists(completedMarkerPath) ||
                File.Exists(legacyCompletedMarkerPath))
            {
                HideFlow();
                return;
            }

            ShowConsent(null);
        }

        public void HandleEscape()
        {
            if (!IsFlowVisible)
            {
                return;
            }

            if (state == FlowState.Consent)
            {
                CloseApplication();
            }
            else if (state == FlowState.RebootRequired)
            {
                BeginRestoreAndClose();
            }
            else if (state == FlowState.Applying)
            {
                rollbackRequested = true;
                SetApplyingCancellationMessage();
            }
            else if (state == FlowState.RecoveryRequired)
            {
                FocusPreferredButton();
            }
            else if (state == FlowState.ManualRestoreConfirmation)
            {
                CancelManualRestore();
            }
            else if (state == FlowState.Failure)
            {
                CloseApplication();
            }
        }

        public bool ShouldCancelWindowClose()
        {
            if (!IsFlowVisible || allowOwnerClose)
            {
                return false;
            }

            owner.Dispatcher.BeginInvoke(new Action(HandleEscape));
            return true;
        }

        private bool HasArgument(string expected)
        {
            for (int index = 0; index < arguments.Length; index++)
            {
                if (string.Equals(arguments[index], expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private void ShowConsent(string notice)
        {
            state = FlowState.Consent;
            Visibility = Visibility.Visible;
            cardContent.Children.Clear();
            cardContent.RowDefinitions.Clear();
            cardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = BuildHeader(
                "Перед первым запуском",
                "Boostix применит обратимый профиль производительности Windows.");
            Grid.SetRow(header, 0);
            cardContent.Children.Add(header);

            var body = new StackPanel { Margin = new Thickness(0, 4, 0, 12) };
            body.Children.Add(MakeText(
                "Сохраним исходные значения, затем программа:",
                10.5,
                TextColor,
                semiboldFont,
                FontWeights.SemiBold));
            body.Children.Add(MakeConsentBullet("создаст и включит отдельный план Boostix Performance для питания от сети;"));
            body.Children.Add(MakeConsentBullet("выберет максимум производительности CPU, отключит парковку ядер, простой диска и USB-селективное отключение;"));
            body.Children.Add(MakeConsentBullet("включит Game Mode, отключит DVR-запись и ограничение мощности, снизит резерв CPU для фоновых задач до 10%;"));
            body.Children.Add(MakeConsentBullet("выберет быстрые визуальные эффекты, отключит прозрачность и фоновые виджеты Windows;"));
            body.Children.Add(MakeConsentBullet("попытается создать точку восстановления; после применения потребуется перезагрузка."));

            var safetyText = MakeText(
                "Меняются только отличающиеся значения. Резервная копия позволяет безопасно вернуть изменения.",
                9.4,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            safetyText.Margin = new Thickness(0, 10, 0, 0);
            body.Children.Add(safetyText);

            if (!string.IsNullOrWhiteSpace(notice))
            {
                var noticeText = MakeText(notice, 10.5, ErrorColor, semiboldFont, FontWeights.SemiBold);
                noticeText.Margin = new Thickness(0, 9, 0, 0);
                body.Children.Add(noticeText);
            }

            Grid.SetRow(body, 1);
            cardContent.Children.Add(body);

            var buttons = BuildButtonRow();
            var cancelButton = MakeActionButton("Закрыть", false);
            cancelButton.Click += delegate { CloseApplication(); };
            AutomationProperties.SetName(cancelButton, "Отменить первоначальную настройку и закрыть программу");
            buttons.Children.Add(cancelButton);

            var skipButton = MakeActionButton("Без настройки", false);
            skipButton.Margin = new Thickness(10, 0, 0, 0);
            skipButton.Click += delegate { HideFlow(); };
            AutomationProperties.SetName(
                skipButton,
                "Продолжить без системной оптимизации");
            buttons.Children.Add(skipButton);

            var continueButton = MakeActionButton("Применить", true);
            continueButton.Margin = new Thickness(10, 0, 0, 0);
            continueButton.IsDefault = true;
            continueButton.Click += ContinueButtonClick;
            AutomationProperties.SetName(continueButton, "Продолжить и применить оптимизацию производительности");
            buttons.Children.Add(continueButton);
            preferredFocusButton = continueButton;

            Grid.SetRow(buttons, 2);
            cardContent.Children.Add(buttons);
            FocusPreferredButton();
        }

        private async void ContinueButtonClick(object sender, RoutedEventArgs e)
        {
            await ApplyOptimizationAsync();
        }

        private async Task ApplyOptimizationAsync()
        {
            state = FlowState.Applying;
            rollbackRequested = false;
            ShowWorking(
                "Применяем настройки",
                "Windows запросит права администратора. Не закрывайте это окно до завершения.",
                "Отменить");

            if (demoMode)
            {
                await Task.Delay(900);
                pendingStatePath = Path.Combine(Path.GetTempPath(), "Boostix-demo-state.json");
                if (rollbackRequested)
                {
                    await RestoreAndCloseAsync();
                }
                else if (HasArgument("--demo-apply-failure"))
                {
                    await RestoreAfterFailedApplyAsync();
                }
                else if (HasArgument("--demo-setup-notes"))
                {
                    ShowRebootRequired(OptimizationApplyNotes.Describe(
                        new[] { @"HKLM:\SOFTWARE\Policies\Microsoft\Dsh::AllowNewsAndInterests" },
                        false));
                }
                else
                {
                    ShowRebootRequired();
                }
                return;
            }

            string scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MaxFPS-Apply.ps1");
            if (!File.Exists(scriptPath))
            {
                ShowFailure(
                    "Файл MaxFPS-Apply.ps1 не найден рядом с программой.",
                    false);
                return;
            }

            string resultPath = MakeTemporaryResultPath("apply");
            string expectedUserSid = GetCurrentUserSid();
            ScriptResult execution = await RunElevatedPowerShellAsync(
                scriptPath,
                "-AdoptExistingState -ResultPath " + QuoteArgument(resultPath) +
                " -ExpectedUserSid " + QuoteArgument(expectedUserSid),
                ApplyScriptSha256);

            if (execution.ElevationCanceled)
            {
                TryDeleteFile(resultPath);
                ShowConsent("Запрос прав администратора был отменён. Изменения не внесены.");
                return;
            }

            if (execution.Error != null || execution.ExitCode != 0)
            {
                string failedStatePath;
                bool canRestorePartialChanges =
                    TryReadStatePath(resultPath, out failedStatePath) &&
                    Path.IsPathRooted(failedStatePath) &&
                    File.Exists(failedStatePath);
                TryDeleteFile(resultPath);
                if (canRestorePartialChanges)
                {
                    pendingStatePath = failedStatePath;
                    try
                    {
                        WritePendingMarker(failedStatePath, DateTime.UtcNow);
                    }
                    catch
                    {
                    }
                    await RestoreAfterFailedApplyAsync();
                    return;
                }
                ShowFailure(
                    execution.TimedOut
                        ? "Применение заняло слишком много времени и было остановлено. " +
                          "Если часть настроек успела измениться, резервная копия сохранена для восстановления."
                        : "Не удалось применить оптимизацию. Изменения не считаются завершёнными.",
                    false);
                return;
            }

            string statePath;
            if (!TryReadStatePath(resultPath, out statePath) ||
                !Path.IsPathRooted(statePath) ||
                !File.Exists(statePath))
            {
                TryDeleteFile(resultPath);
                ShowFailure(
                    "Оптимизация завершилась без корректного файла резервной копии. Перезапустите настройку.",
                    false);
                return;
            }

            string resultJson = TryReadAllText(resultPath);
            IList<string> applyNotes = OptimizationApplyNotes.Describe(
                OptimizationApplyNotes.ReadSkippedSettings(resultJson),
                OptimizationApplyNotes.ReadRestorePointCreated(resultJson));
            TryDeleteFile(resultPath);
            pendingStatePath = statePath;
            bool pendingMarkerWritten = true;
            try
            {
                WritePendingMarker(statePath, DateTime.UtcNow);
            }
            catch
            {
                pendingMarkerWritten = false;
            }
            if (!pendingMarkerWritten)
            {
                // Without a durable per-user marker the next launch cannot prove
                // whether a reboot happened. Roll back immediately instead of
                // leaving an untracked machine-wide optimization behind.
                await RestoreAndCloseAsync();
                return;
            }

            if (rollbackRequested)
            {
                await RestoreAndCloseAsync();
                return;
            }

            ShowRebootRequired(applyNotes);
        }

        private void ShowRebootRequired()
        {
            ShowRebootRequired(null);
        }

        private void ShowRebootRequired(IList<string> notes)
        {
            bool hasNotes = notes != null && notes.Count > 0;
            state = FlowState.RebootRequired;
            Visibility = Visibility.Visible;
            cardContent.Children.Clear();
            cardContent.RowDefinitions.Clear();
            cardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = BuildHeader(
                "Оптимизация готова",
                "Настройки применены и сохранены для безопасного отката.");
            Grid.SetRow(header, 0);
            cardContent.Children.Add(header);

            var body = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Width = hasNotes ? 400 : 308,
                Margin = new Thickness(0, 0, 0, 12)
            };

            var rebootBadge = new Border
            {
                Width = 58,
                Height = 58,
                CornerRadius = new CornerRadius(29),
                BorderBrush = new SolidColorBrush(AccentColor),
                BorderThickness = new Thickness(1),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            };
            var rebootGlyph = MakeText("↻", 31, TextColor, semiboldFont, FontWeights.SemiBold);
            rebootGlyph.TextAlignment = TextAlignment.Center;
            rebootGlyph.HorizontalAlignment = HorizontalAlignment.Center;
            rebootGlyph.VerticalAlignment = VerticalAlignment.Center;
            rebootGlyph.Margin = new Thickness(0, -3, 0, 0);
            rebootBadge.Child = rebootGlyph;
            body.Children.Add(rebootBadge);

            var title = MakeText(
                "Нужна перезагрузка",
                14,
                TextColor,
                semiboldFont,
                FontWeights.Bold);
            title.TextAlignment = TextAlignment.Center;
            body.Children.Add(title);

            var description = MakeText(
                "Перезагрузите компьютер, чтобы Windows полностью применила изменения производительности.",
                11,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            description.TextAlignment = TextAlignment.Center;
            description.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(description);

            if (hasNotes)
            {
                // What Windows refused or skipped, so it is not only in the log.
                var notesStack = new StackPanel();
                foreach (string note in notes)
                {
                    var noteText = MakeText(note, 10.5, TextColor, regularFont, FontWeights.Normal);
                    if (notesStack.Children.Count > 0)
                    {
                        noteText.Margin = new Thickness(0, 6, 0, 0);
                    }
                    notesStack.Children.Add(noteText);
                }
                body.Children.Add(new Border
                {
                    Background = new SolidColorBrush(BoostixDesignTokens.SurfaceRaised),
                    BorderBrush = new SolidColorBrush(BoostixDesignTokens.Warning),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 10, 12, 10),
                    Margin = new Thickness(0, 14, 0, 0),
                    Child = notesStack
                });
            }

            Grid.SetRow(body, 1);
            cardContent.Children.Add(body);

            var buttons = BuildButtonRow();
            var cancelButton = MakeActionButton("Отменить", false);
            cancelButton.Click += delegate { BeginRestoreAndClose(); };
            AutomationProperties.SetName(cancelButton, "Отменить оптимизацию, восстановить настройки и закрыть программу");
            buttons.Children.Add(cancelButton);

            var restartButton = MakeActionButton("Перезагрузить сейчас", true);
            restartButton.Margin = new Thickness(10, 0, 0, 0);
            restartButton.IsDefault = true;
            restartButton.Click += RestartButtonClick;
            AutomationProperties.SetName(restartButton, "Перезагрузить компьютер сейчас");
            buttons.Children.Add(restartButton);
            preferredFocusButton = restartButton;

            Grid.SetRow(buttons, 2);
            cardContent.Children.Add(buttons);
            FocusPreferredButton();
        }

        private void ShowRecoveryRequired(string transactionStatus)
        {
            state = FlowState.RecoveryRequired;
            Visibility = Visibility.Visible;
            cardContent.Children.Clear();
            cardContent.RowDefinitions.Clear();
            cardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = BuildHeader(
                "Нужно восстановление",
                "Найдена незавершённая системная транзакция.");
            Grid.SetRow(header, 0);
            cardContent.Children.Add(header);

            string detail;
            if (string.Equals(transactionStatus, "RestoreIncomplete", StringComparison.OrdinalIgnoreCase))
            {
                detail = "Предыдущий откат завершился не полностью. Резервная копия сохранена для безопасного повтора.";
            }
            else if (string.Equals(transactionStatus, "Restoring", StringComparison.OrdinalIgnoreCase))
            {
                detail = "Предыдущее восстановление было прервано. Его нужно продолжить по той же резервной копии.";
            }
            else if (string.Equals(transactionStatus, "Applying", StringComparison.OrdinalIgnoreCase))
            {
                detail = "Применение настроек было прервано до завершения. Изменённые параметры нужно вернуть.";
            }
            else
            {
                detail = "Применение настроек завершилось ошибкой. Изменённые параметры нужно вернуть из резервной копии.";
            }

            var body = new StackPanel
            {
                Width = 306,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            };

            var recoveryBadge = new Border
            {
                Width = 58,
                Height = 58,
                CornerRadius = new CornerRadius(29),
                BorderBrush = new SolidColorBrush(AccentColor),
                BorderThickness = new Thickness(1),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 15)
            };
            var recoveryGlyph = MakeText("↶", 30, TextColor, semiboldFont, FontWeights.SemiBold);
            recoveryGlyph.TextAlignment = TextAlignment.Center;
            recoveryGlyph.HorizontalAlignment = HorizontalAlignment.Center;
            recoveryGlyph.VerticalAlignment = VerticalAlignment.Center;
            recoveryGlyph.Margin = new Thickness(0, -2, 0, 0);
            recoveryBadge.Child = recoveryGlyph;
            body.Children.Add(recoveryBadge);

            var title = MakeText(
                "Нужен безопасный откат",
                13.5,
                TextColor,
                semiboldFont,
                FontWeights.Bold);
            title.TextAlignment = TextAlignment.Center;
            body.Children.Add(title);

            var description = MakeText(detail, 10.7, MutedColor, regularFont, FontWeights.Normal);
            description.TextAlignment = TextAlignment.Center;
            description.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(description);

            var closeNotice = MakeText(
                "Сначала выполните попытку восстановления.",
                10,
                ErrorColor,
                semiboldFont,
                FontWeights.SemiBold);
            closeNotice.TextAlignment = TextAlignment.Center;
            closeNotice.Margin = new Thickness(0, 9, 0, 0);
            body.Children.Add(closeNotice);

            Grid.SetRow(body, 1);
            cardContent.Children.Add(body);

            var buttons = BuildButtonRow();
            var restoreButton = MakeActionButton("Восстановить", true);
            restoreButton.IsDefault = true;
            restoreButton.Click += delegate { BeginRestoreAndClose(); };
            AutomationProperties.SetName(restoreButton, "Восстановить исходные настройки из найденной резервной копии");
            buttons.Children.Add(restoreButton);
            preferredFocusButton = restoreButton;

            Grid.SetRow(buttons, 2);
            cardContent.Children.Add(buttons);
            FocusPreferredButton();
        }

        private void ShowManualRestoreConfirmation()
        {
            state = FlowState.ManualRestoreConfirmation;
            Visibility = Visibility.Visible;
            cardContent.Children.Clear();
            cardContent.RowDefinitions.Clear();
            cardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = BuildHeader(
                "ВОССТАНОВИТЬ WINDOWS",
                "Будут возвращены только параметры, изменённые Boostix.");
            Grid.SetRow(header, 0);
            cardContent.Children.Add(header);

            var body = new StackPanel
            {
                Width = 306,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            };

            var title = MakeText(
                "Откат системной оптимизации",
                13.5,
                TextColor,
                semiboldFont,
                FontWeights.Bold);
            title.TextAlignment = TextAlignment.Center;
            body.Children.Add(title);

            var description = MakeText(
                "Программа использует сохранённую резервную копию и не трогает параметры, которые с тех пор изменили другие приложения или вы сами.",
                10.7,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            description.TextAlignment = TextAlignment.Center;
            description.Margin = new Thickness(0, 9, 0, 0);
            body.Children.Add(description);

            var notice = MakeText(
                "Для восстановления Windows запросит права администратора.",
                10,
                MutedColor,
                semiboldFont,
                FontWeights.SemiBold);
            notice.TextAlignment = TextAlignment.Center;
            notice.Margin = new Thickness(0, 10, 0, 0);
            body.Children.Add(notice);

            Grid.SetRow(body, 1);
            cardContent.Children.Add(body);

            var buttons = BuildButtonRow();
            var cancelButton = MakeActionButton("Отмена", false);
            cancelButton.Click += delegate { CancelManualRestore(); };
            AutomationProperties.SetName(cancelButton, "Отменить ручное восстановление");
            buttons.Children.Add(cancelButton);

            var restoreButton = MakeActionButton("Восстановить", true);
            restoreButton.Margin = new Thickness(10, 0, 0, 0);
            restoreButton.IsDefault = true;
            restoreButton.Click += delegate { BeginRestoreAndClose(); };
            AutomationProperties.SetName(
                restoreButton,
                "Восстановить исходные настройки из активной резервной копии");
            buttons.Children.Add(restoreButton);
            preferredFocusButton = restoreButton;

            Grid.SetRow(buttons, 2);
            cardContent.Children.Add(buttons);
            FocusPreferredButton();
        }

        private void CancelManualRestore()
        {
            pendingStatePath = null;
            HideFlow();
        }

        private void RestartButtonClick(object sender, RoutedEventArgs e)
        {
            if (demoMode)
            {
                CloseApplication();
                return;
            }

            try
            {
                allowOwnerClose = true;
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                    Arguments = "/r /t 0",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch (Exception ex)
            {
                allowOwnerClose = false;
                ShowFailure("Не удалось запустить перезагрузку: " + ex.Message, true);
            }
        }

        private async void BeginRestoreAndClose()
        {
            if (state == FlowState.Restoring)
            {
                return;
            }
            await RestoreAndCloseAsync();
        }

        private async Task RestoreAndCloseAsync()
        {
            if (await RestoreAsync())
            {
                CloseApplication();
            }
        }

        // A failed apply is rolled back automatically. Closing afterwards
        // looked like a crash, so keep Boostix open and say what happened.
        private async Task RestoreAfterFailedApplyAsync()
        {
            if (await RestoreAsync())
            {
                ShowFailure(
                    "Не удалось применить настройки, поэтому Boostix отменил все изменения. " +
                    "Система в исходном состоянии: можно продолжить без настройки или попробовать ещё раз.",
                    false);
            }
        }

        // Returns true once the original settings are back; otherwise the
        // failure card explaining the rollback problem is already shown.
        private async Task<bool> RestoreAsync()
        {
            state = FlowState.Restoring;
            ShowWorking(
                "Восстанавливаем настройки",
                "Возвращаем только те параметры, которые Boostix изменил при настройке.",
                null);

            if (demoMode)
            {
                await Task.Delay(700);
                return true;
            }

            string validatedStatePath;
            if (!TryValidateStatePath(pendingStatePath, out validatedStatePath))
            {
                ShowFailure(
                    "Файл резервной копии не найден. Автоматический откат остановлен, чтобы не менять систему вслепую.",
                    true);
                return false;
            }
            pendingStatePath = validatedStatePath;

            string scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MaxFPS-Restore.ps1");
            if (!File.Exists(scriptPath))
            {
                ShowFailure("Файл MaxFPS-Restore.ps1 не найден рядом с программой.", true);
                return false;
            }

            string resultPath = MakeTemporaryResultPath("restore");
            string scriptArguments =
                "-StatePath " + QuoteArgument(pendingStatePath) +
                " -ResultPath " + QuoteArgument(resultPath) +
                " -ExpectedUserSid " + QuoteArgument(GetCurrentUserSid());
            ScriptResult execution = await RunElevatedPowerShellAsync(
                scriptPath,
                scriptArguments,
                RestoreScriptSha256);

            if (execution.ElevationCanceled)
            {
                TryDeleteFile(resultPath);
                ShowFailure(
                    "Откат отменён в запросе прав администратора. Настройки пока остаются применёнными.",
                    true);
                return false;
            }

            if (execution.Error != null || execution.ExitCode != 0)
            {
                TryDeleteFile(resultPath);
                ShowFailure(
                    execution.TimedOut
                        ? "Восстановление заняло слишком много времени и было остановлено. " +
                          "Резервная копия сохранена для повторной попытки."
                        : "Не удалось полностью восстановить исходные настройки. " +
                          "Резервная копия сохранена для повторной попытки.",
                    true);
                return false;
            }

            TryDeleteFile(resultPath);
            TryDeleteFile(pendingMarkerPath);
            TryDeleteFile(completedMarkerPath);
            TryDeleteFile(legacyPendingMarkerPath);
            TryDeleteFile(legacyCompletedMarkerPath);
            return true;
        }

        private void ShowWorking(string title, string description, string cancelText)
        {
            Visibility = Visibility.Visible;
            cardContent.Children.Clear();
            cardContent.RowDefinitions.Clear();
            cardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = BuildHeader(title, description);
            Grid.SetRow(header, 0);
            cardContent.Children.Add(header);

            var body = new StackPanel
            {
                Width = 300,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            };

            var progressTrack = new Border
            {
                Width = 300,
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(BoostixDesignTokens.Divider),
                ClipToBounds = true
            };
            var progressIndicator = new Border
            {
                Width = 82,
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(AccentColor),
                HorizontalAlignment = HorizontalAlignment.Left,
                RenderTransform = new TranslateTransform(-82, 0)
            };
            progressTrack.Child = progressIndicator;
            body.Children.Add(progressTrack);

            var statusText = MakeText(
                "Это может занять несколько минут.",
                11,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            statusText.Name = "WorkingStatusText";
            statusText.TextAlignment = TextAlignment.Center;
            statusText.Margin = new Thickness(0, 18, 0, 0);
            AutomationProperties.SetName(statusText, statusText.Text);
            body.Children.Add(statusText);

            var progressTranslation =
                (TranslateTransform)progressIndicator.RenderTransform;
            if (SystemParameters.ClientAreaAnimation)
            {
                var movement = new DoubleAnimation
                {
                    From = -82,
                    To = 300,
                    Duration = TimeSpan.FromMilliseconds(1250),
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                progressTranslation.BeginAnimation(
                    TranslateTransform.XProperty,
                    movement);
            }
            else
            {
                progressTranslation.X = 109;
            }

            Grid.SetRow(body, 1);
            cardContent.Children.Add(body);

            if (!string.IsNullOrEmpty(cancelText))
            {
                var buttons = BuildButtonRow();
                var cancelButton = MakeActionButton(cancelText, false);
                cancelButton.Click += delegate
                {
                    if (state == FlowState.Applying)
                    {
                        rollbackRequested = true;
                        cancelButton.IsEnabled = false;
                        SetApplyingCancellationMessage();
                    }
                };
                AutomationProperties.SetName(cancelButton, "Отменить настройку и восстановить исходные параметры");
                buttons.Children.Add(cancelButton);
                preferredFocusButton = cancelButton;
                Grid.SetRow(buttons, 2);
                cardContent.Children.Add(buttons);
                FocusPreferredButton();
            }
            else
            {
                preferredFocusButton = null;
                Focus();
            }
        }

        private void SetApplyingCancellationMessage()
        {
            var statusText = FindVisualChildByName<TextBlock>(cardContent, "WorkingStatusText");
            if (statusText == null)
            {
                return;
            }
            statusText.Text = "Завершаем текущий шаг, затем безопасно вернём изменения.";
            AutomationProperties.SetName(statusText, statusText.Text);
        }

        private void ShowFailure(string message, bool canRetryRestore)
        {
            state = FlowState.Failure;
            Visibility = Visibility.Visible;
            cardContent.Children.Clear();
            cardContent.RowDefinitions.Clear();
            cardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = BuildHeader("Нужно ваше внимание", "Boostix остановил операцию безопасно.");
            Grid.SetRow(header, 0);
            cardContent.Children.Add(header);

            var messageText = MakeText(message, 11.5, TextColor, regularFont, FontWeights.Normal);
            messageText.TextAlignment = TextAlignment.Center;
            messageText.VerticalAlignment = VerticalAlignment.Center;
            messageText.Margin = new Thickness(16, 0, 16, 0);
            Grid.SetRow(messageText, 1);
            cardContent.Children.Add(messageText);

            var buttons = BuildButtonRow();
            if (canRetryRestore)
            {
                var retryButton = MakeActionButton("Повторить откат", true);
                retryButton.IsDefault = true;
                retryButton.Click += delegate { BeginRestoreAndClose(); };
                AutomationProperties.SetName(retryButton, "Повторить восстановление исходных настроек");
                buttons.Children.Add(retryButton);
                preferredFocusButton = retryButton;
            }
            else
            {
                var closeButton = MakeActionButton("Закрыть", false);
                closeButton.Click += delegate { CloseApplication(); };
                buttons.Children.Add(closeButton);

                var skipButton = MakeActionButton("Без настройки", false);
                skipButton.Margin = new Thickness(10, 0, 0, 0);
                skipButton.Click += delegate { HideFlow(); };
                AutomationProperties.SetName(
                    skipButton,
                    "Продолжить без системной оптимизации");
                buttons.Children.Add(skipButton);

                var retryButton = MakeActionButton("Повторить", true);
                retryButton.Margin = new Thickness(10, 0, 0, 0);
                retryButton.IsDefault = true;
                retryButton.Click += ContinueButtonClick;
                buttons.Children.Add(retryButton);
                preferredFocusButton = retryButton;
            }

            Grid.SetRow(buttons, 2);
            cardContent.Children.Add(buttons);
            FocusPreferredButton();
        }

        private StackPanel BuildHeader(string title, string subtitle)
        {
            var header = new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 16)
            };
            var badge = new Border
            {
                Width = 44,
                Height = 44,
                CornerRadius = new CornerRadius(13),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = BoostixDesignTokens.Gradient(
                    BoostixDesignTokens.Accent,
                    BoostixDesignTokens.AccentSecondary),
                Margin = new Thickness(0, 0, 0, 14)
            };
            FrameworkElement icon = BoostixIcons.Create(
                BoostixIcons.Zap,
                22,
                new SolidColorBrush(BoostixDesignTokens.AccentForeground),
                2);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            badge.Child = icon;
            header.Children.Add(badge);
            TextBlock titleText = MakeText(title, 22, TextColor, semiboldFont, FontWeights.SemiBold);
            AutomationProperties.SetHeadingLevel(titleText, AutomationHeadingLevel.Level1);
            header.Children.Add(titleText);
            TextBlock subtitleText = MakeText(
                subtitle,
                12.5,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            subtitleText.Margin = new Thickness(0, 6, 0, 0);
            header.Children.Add(subtitleText);
            return header;
        }

        private TextBlock MakeConsentBullet(string text)
        {
            var bullet = MakeText("•  " + text, 10.5, TextColor, regularFont, FontWeights.Normal);
            bullet.Margin = new Thickness(0, 5, 0, 0);
            return bullet;
        }

        private static TextBlock MakeText(
            string text,
            double size,
            Color color,
            FontFamily font,
            FontWeight weight)
        {
            // Dialog copy is set slightly larger than the old compact card.
            double readable = size < 12.5 ? Math.Max(11.5, size + 1.5) : size;
            return new TextBlock
            {
                Text = text,
                FontSize = readable,
                FontFamily = font,
                FontWeight = weight,
                Foreground = new SolidColorBrush(color),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None,
                LineHeight = Math.Round(readable * 1.42),
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight
            };
        }

        private static StackPanel BuildButtonRow()
        {
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 20, 0, 0)
            };
        }

        private Button MakeActionButton(string text, bool accentOnHover)
        {
            return BoostixUi.ActionButton(
                text,
                accentOnHover
                    ? BoostixButtonKind.Primary
                    : BoostixButtonKind.Secondary,
                null);
        }

        private void OverlayPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                HandleEscape();
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Tab)
            {
                return;
            }

            var focused = Keyboard.FocusedElement as UIElement;
            if (focused != null)
            {
                var direction = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift
                    ? FocusNavigationDirection.Previous
                    : FocusNavigationDirection.Next;
                focused.MoveFocus(new TraversalRequest(direction));
            }
            else
            {
                FocusPreferredButton();
            }
            e.Handled = true;
        }

        private void FocusPreferredButton()
        {
            if (preferredFocusButton == null)
            {
                return;
            }
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (preferredFocusButton != null && preferredFocusButton.IsVisible)
                {
                    preferredFocusButton.Focus();
                    Keyboard.Focus(preferredFocusButton);
                }
            }));
        }

        private async Task<ScriptResult> RunElevatedPowerShellAsync(
            string scriptPath,
            string scriptArguments,
            string expectedSha256)
        {
            return await Task.Run(delegate
            {
                var result = new ScriptResult { ExitCode = -1 };
                try
                {
                    using (FileStream verifiedScript = OpenVerifiedScript(
                        scriptPath,
                        expectedSha256))
                    {
                        string powershellPath = Path.Combine(
                            Environment.SystemDirectory,
                            "WindowsPowerShell\\v1.0\\powershell.exe");
                        var startInfo = new ProcessStartInfo
                        {
                            FileName = powershellPath,
                            Arguments =
                                "-NoProfile -ExecutionPolicy Bypass -File " +
                                QuoteArgument(scriptPath) + " " + scriptArguments,
                            WorkingDirectory = Path.GetDirectoryName(scriptPath),
                            UseShellExecute = true,
                            Verb = "runas",
                            WindowStyle = ProcessWindowStyle.Hidden
                        };

                        using (Process process = Process.Start(startInfo))
                        {
                            if (process == null)
                            {
                                throw new InvalidOperationException("Не удалось запустить PowerShell.");
                            }
                            result.TimedOut = WaitForProcessExitSafely(
                                process,
                                ElevatedScriptTimeoutMilliseconds,
                                1000,
                                TryTerminateProcess);
                            result.ExitCode = process.ExitCode;
                        }
                    }
                }
                catch (Win32Exception ex)
                {
                    if (ex.NativeErrorCode == 1223)
                    {
                        result.ElevationCanceled = true;
                    }
                    else
                    {
                        result.Error = ex;
                    }
                }
                catch (Exception ex)
                {
                    result.Error = ex;
                }
                return result;
            });
        }

        private static bool WaitForProcessExitSafely(
            Process process,
            int timeoutMilliseconds,
            int pollMilliseconds,
            Action<Process> terminateProcess)
        {
            if (process == null)
            {
                throw new ArgumentNullException("process");
            }
            if (timeoutMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            }
            if (pollMilliseconds <= 0)
            {
                throw new ArgumentOutOfRangeException("pollMilliseconds");
            }
            if (terminateProcess == null)
            {
                throw new ArgumentNullException("terminateProcess");
            }

            if (process.WaitForExit(timeoutMilliseconds))
            {
                return false;
            }

            // The caller keeps the verified script handle open while this method
            // waits. If termination is denied (for example because UAC used other
            // credentials), do not release that protection and do not consume a
            // transaction/result that the elevated process may still be writing.
            terminateProcess(process);
            while (!process.WaitForExit(pollMilliseconds))
            {
                // Bounded polling runs on Task.Run, so the WPF dispatcher remains
                // responsive while close/retry stays blocked by Applying/Restoring.
            }
            return true;
        }

        private static void TryTerminateProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
                // A differently credentialed elevated process may reject Kill.
                // WaitForProcessExitSafely will retain protection until real exit.
            }
        }

        private static FileStream OpenVerifiedScript(
            string scriptPath,
            string expectedSha256)
        {
            string fullPath = Path.GetFullPath(scriptPath ?? string.Empty);
            string baseDirectory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(
                    Path.GetDirectoryName(fullPath),
                    baseDirectory,
                    StringComparison.OrdinalIgnoreCase) ||
                !IsPathFreeOfReparsePoints(fullPath))
            {
                throw new InvalidDataException(
                    "Сценарий оптимизации должен находиться рядом с программой и не может быть ссылкой.");
            }

            var script = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            try
            {
                if (script.Length <= 0 || script.Length > 4 * 1024 * 1024)
                {
                    throw new InvalidDataException("Размер сценария оптимизации недопустим.");
                }

                string actualHash;
                using (SHA256 sha256 = SHA256.Create())
                {
                    actualHash = BitConverter.ToString(sha256.ComputeHash(script))
                        .Replace("-", string.Empty);
                }
                if (!string.Equals(actualHash, expectedSha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Сценарий оптимизации изменён или повреждён. Переустановите Boostix.");
                }
                script.Position = 0;
                return script;
            }
            catch
            {
                script.Dispose();
                throw;
            }
        }

        private static string GetCurrentUserSid()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                if (identity == null || identity.User == null)
                {
                    throw new InvalidOperationException(
                        "Не удалось определить текущую учётную запись Windows.");
                }
                return identity.User.Value;
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static string MakeTemporaryResultPath(string operation)
        {
            return Path.Combine(
                Path.GetTempPath(),
                "Boostix-" + operation + "-" + Guid.NewGuid().ToString("N") + ".json");
        }

        private static string TryReadAllText(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryReadStatePath(string resultPath, out string statePath)
        {
            statePath = null;
            try
            {
                if (!File.Exists(resultPath))
                {
                    return false;
                }
                string json = File.ReadAllText(resultPath, Encoding.UTF8);
                string candidatePath;
                return
                    TryReadJsonString(json, "StateFile", out candidatePath) &&
                    TryValidateStatePath(candidatePath, out statePath);
            }
            catch
            {
                return false;
            }
        }

        private bool TryReadPendingMarker(out string statePath, out DateTime appliedUtc)
        {
            statePath = null;
            appliedUtc = DateTime.MinValue;
            try
            {
                string[] markerPaths = { pendingMarkerPath, legacyPendingMarkerPath };
                for (int index = 0; index < markerPaths.Length; index++)
                {
                    string markerPath = markerPaths[index];
                    if (!File.Exists(markerPath))
                    {
                        continue;
                    }

                    string json = File.ReadAllText(markerPath, Encoding.UTF8);
                    string candidatePath;
                    string utcText;
                    if (!TryReadJsonString(json, "StatePath", out candidatePath) ||
                        !TryReadJsonString(json, "AppliedUtc", out utcText))
                    {
                        return false;
                    }

                    return
                        DateTime.TryParse(
                            utcText,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out appliedUtc) &&
                        TryValidateStatePath(candidatePath, out statePath);
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private bool TryReadCompletedMarker(out string statePath)
        {
            statePath = null;
            try
            {
                string[] markerPaths = { completedMarkerPath, legacyCompletedMarkerPath };
                for (int index = 0; index < markerPaths.Length; index++)
                {
                    string markerPath = markerPaths[index];
                    if (!File.Exists(markerPath))
                    {
                        continue;
                    }

                    string json = File.ReadAllText(markerPath, Encoding.UTF8);
                    string candidatePath;
                    string completedUtc;
                    return
                        TryReadJsonString(json, "StatePath", out candidatePath) &&
                        TryReadJsonString(json, "CompletedUtc", out completedUtc) &&
                        TryValidateStatePath(candidatePath, out statePath);
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadGlobalTransaction(out GlobalTransactionInfo transaction)
        {
            transaction = null;
            try
            {
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                if (string.IsNullOrWhiteSpace(programData))
                {
                    return false;
                }

                string[] stateRoots =
                {
                    Path.GetFullPath(Path.Combine(programData, "BoostixOptimization")),
                    Path.GetFullPath(Path.Combine(programData, "CodexGamingOptimization"))
                };
                for (int rootIndex = 0; rootIndex < stateRoots.Length; rootIndex++)
                {
                    string pointerPath = Path.Combine(stateRoots[rootIndex], "latest-state.txt");
                    if (!File.Exists(pointerPath))
                    {
                        continue;
                    }
                    if (!IsPathFreeOfReparsePoints(pointerPath) ||
                        new FileInfo(pointerPath).Length <= 0 ||
                        new FileInfo(pointerPath).Length > 4096)
                    {
                        return false;
                    }

                    string candidatePath = File.ReadAllText(pointerPath, Encoding.UTF8).Trim();
                    string validatedPath;
                    if (!TryValidateStatePath(candidatePath, out validatedPath))
                    {
                        return false;
                    }
                    string pointerBackupsRoot = Path.GetFullPath(
                        Path.Combine(stateRoots[rootIndex], "Backups"));
                    string stateBackupsRoot = Path.GetDirectoryName(
                        Path.GetDirectoryName(validatedPath));
                    if (!string.Equals(
                            pointerBackupsRoot,
                            stateBackupsRoot,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    var stateFile = new FileInfo(validatedPath);
                    if (stateFile.Length <= 0 || stateFile.Length > 8 * 1024 * 1024)
                    {
                        return false;
                    }

                    string json = File.ReadAllText(validatedPath, Encoding.UTF8);
                    int version;
                    if (!TryReadJsonInteger(json, "Version", out version))
                    {
                        version = 1;
                    }

                    string status;
                    if (!TryReadJsonString(json, "Status", out status))
                    {
                        status = string.Empty;
                    }

                    transaction = new GlobalTransactionInfo
                    {
                        StatePath = validatedPath,
                        Version = version,
                        Status = status
                    };
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryValidateStatePath(string candidatePath, out string validatedPath)
        {
            validatedPath = null;
            try
            {
                if (string.IsNullOrWhiteSpace(candidatePath))
                {
                    return false;
                }

                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                string fullPath = Path.GetFullPath(candidatePath.Trim());
                if (!string.Equals(
                        Path.GetFileName(fullPath),
                        "state.json",
                        StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(fullPath) ||
                    !IsPathFreeOfReparsePoints(fullPath))
                {
                    return false;
                }

                string transactionDirectory = Path.GetDirectoryName(fullPath);
                string candidateBackupsRoot = Path.GetDirectoryName(transactionDirectory);
                string[] allowedBackupsRoots =
                {
                    Path.GetFullPath(Path.Combine(programData, "BoostixOptimization", "Backups")),
                    Path.GetFullPath(Path.Combine(programData, "CodexGamingOptimization", "Backups"))
                };
                bool allowed = false;
                for (int index = 0; index < allowedBackupsRoots.Length; index++)
                {
                    if (string.Equals(
                            candidateBackupsRoot,
                            allowedBackupsRoots[index],
                            StringComparison.OrdinalIgnoreCase))
                    {
                        allowed = true;
                        break;
                    }
                }
                if (!allowed)
                {
                    return false;
                }

                validatedPath = fullPath;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPathFreeOfReparsePoints(string fullPath)
        {
            try
            {
                string normalized = Path.GetFullPath(fullPath);
                string root = Path.GetPathRoot(normalized);
                if (string.IsNullOrEmpty(root))
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

                    FileAttributes attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
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

        private static bool PathsEqual(string left, string right)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(left),
                    Path.GetFullPath(right),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsRecoveryStatus(string status)
        {
            return
                string.Equals(status, "Applying", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "ApplyFailed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "RestoreIncomplete", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "Restoring", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRestoredStatus(string status)
        {
            return
                string.Equals(status, "Restored", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "RestoredWithConflicts", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "AlreadyRestored", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "AbortedNoChanges", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "SupersededLegacy", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetGlobalTransactionPointerPresence(out bool exists)
        {
            exists = false;
            try
            {
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                if (string.IsNullOrWhiteSpace(programData))
                {
                    return false;
                }

                string boostixPointer = Path.Combine(
                    Path.GetFullPath(Path.Combine(programData, "BoostixOptimization")),
                    "latest-state.txt");
                string legacyPointer = Path.Combine(
                    Path.GetFullPath(Path.Combine(programData, "CodexGamingOptimization")),
                    "latest-state.txt");
                exists = File.Exists(boostixPointer) || File.Exists(legacyPointer);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadJsonInteger(string json, string propertyName, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(json))
            {
                return false;
            }

            string pattern =
                "\\\"" + Regex.Escape(propertyName) +
                "\\\"\\s*:\\s*(?<value>-?[0-9]+)";
            Match match = Regex.Match(json, pattern, RegexOptions.CultureInvariant);
            return
                match.Success &&
                int.TryParse(
                    match.Groups["value"].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out value);
        }

        private static bool TryReadJsonString(string json, string propertyName, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(json))
            {
                return false;
            }

            string pattern =
                "\\\"" + Regex.Escape(propertyName) +
                "\\\"\\s*:\\s*\\\"(?<value>(?:\\\\.|[^\\\"\\\\])*)\\\"";
            Match match = Regex.Match(json, pattern, RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                return false;
            }

            value = DecodeJsonString(match.Groups["value"].Value);
            return true;
        }

        private static string DecodeJsonString(string encoded)
        {
            var result = new StringBuilder(encoded.Length);
            for (int index = 0; index < encoded.Length; index++)
            {
                char current = encoded[index];
                if (current != '\\' || index + 1 >= encoded.Length)
                {
                    result.Append(current);
                    continue;
                }

                char escape = encoded[++index];
                switch (escape)
                {
                    case '\"': result.Append('\"'); break;
                    case '\\': result.Append('\\'); break;
                    case '/': result.Append('/'); break;
                    case 'b': result.Append('\b'); break;
                    case 'f': result.Append('\f'); break;
                    case 'n': result.Append('\n'); break;
                    case 'r': result.Append('\r'); break;
                    case 't': result.Append('\t'); break;
                    case 'u':
                        if (index + 4 < encoded.Length)
                        {
                            int codePoint;
                            if (int.TryParse(
                                encoded.Substring(index + 1, 4),
                                NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture,
                                out codePoint))
                            {
                                result.Append((char)codePoint);
                                index += 4;
                                break;
                            }
                        }
                        result.Append('u');
                        break;
                    default:
                        result.Append(escape);
                        break;
                }
            }
            return result.ToString();
        }

        private void WritePendingMarker(string statePath, DateTime appliedUtc)
        {
            Directory.CreateDirectory(stateDirectory);
            string json =
                "{\"StatePath\":\"" + EncodeJsonString(statePath) +
                "\",\"AppliedUtc\":\"" + appliedUtc.ToString("o", CultureInfo.InvariantCulture) + "\"}";
            WriteUtf8Atomically(pendingMarkerPath, json);
        }

        private bool TryMarkCompleted(string statePath)
        {
            try
            {
                Directory.CreateDirectory(stateDirectory);
                string json =
                    "{\"StatePath\":\"" + EncodeJsonString(statePath) +
                    "\",\"CompletedUtc\":\"" +
                    DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\"}";
                WriteUtf8Atomically(
                    completedMarkerPath,
                    json);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string EncodeJsonString(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        private static void ReplaceFileWithoutRetainedBackup(
            string sourcePath,
            string destinationPath)
        {
            string discardBackupPath = destinationPath +
                ".replace-backup-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Replace(sourcePath, destinationPath, discardBackupPath, true);
            }
            finally
            {
                TryDeleteFile(discardBackupPath);
            }
        }

        private static void WriteUtf8Atomically(string path, string text)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporaryPath, text, new UTF8Encoding(false));
            try
            {
                if (File.Exists(path))
                {
                    ReplaceFileWithoutRetainedBackup(temporaryPath, path);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                TryDeleteFile(temporaryPath);
            }
        }

        private static bool HasRestartedSince(DateTime appliedUtc)
        {
            try
            {
                DateTime bootUtc = DateTime.UtcNow.Subtract(
                    TimeSpan.FromMilliseconds(GetTickCount64()));
                return bootUtc > appliedUtc.AddSeconds(1);
            }
            catch
            {
                return false;
            }
        }

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        private void HideFlow()
        {
            state = FlowState.Hidden;
            Visibility = Visibility.Collapsed;
            preferredFocusButton = null;
        }

        private void CloseApplication()
        {
            allowOwnerClose = true;
            EventHandler handler = RequestApplicationClose;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static T FindVisualChildByName<T>(DependencyObject root, string name)
            where T : FrameworkElement
        {
            if (root == null)
            {
                return null;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, index);
                T typed = child as T;
                if (typed != null && string.Equals(typed.Name, name, StringComparison.Ordinal))
                {
                    return typed;
                }

                T nested = FindVisualChildByName<T>(child, name);
                if (nested != null)
                {
                    return nested;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Turns the apply script's SkippedSettings and RestorePointCreated result
    /// fields into plain-language notes for the "reboot required" card.
    /// </summary>
    internal static class OptimizationApplyNotes
    {
        private static readonly Dictionary<string, string> FriendlyNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "AllowNewsAndInterests", "фоновые виджеты" },
                { "IsPrelaunchEnabled", "фоновые виджеты" },
                { "EnableTransparency", "прозрачность окон" },
                { "VisualFXSetting", "быстрые визуальные эффекты" },
                { "AppCaptureEnabled", "отключение записи игр (Game DVR)" },
                { "HistoricalCaptureEnabled", "отключение записи игр (Game DVR)" },
                { "GameDVR_Enabled", "отключение записи игр (Game DVR)" },
                { "AutoGameModeEnabled", "игровой режим" },
                { "AllowAutoGameMode", "игровой режим" }
            };

        internal static string[] ReadSkippedSettings(string resultJson)
        {
            var values = new List<string>();
            if (string.IsNullOrEmpty(resultJson))
            {
                return values.ToArray();
            }
            Match field = Regex.Match(
                resultJson,
                "\"SkippedSettings\"\\s*:\\s*",
                RegexOptions.CultureInvariant);
            if (!field.Success)
            {
                return values.ToArray();
            }
            int index = field.Index + field.Length;
            if (index < resultJson.Length && resultJson[index] == '"')
            {
                string single = ReadJsonString(resultJson, ref index);
                if (!string.IsNullOrEmpty(single))
                {
                    values.Add(single);
                }
                return values.ToArray();
            }
            if (index >= resultJson.Length || resultJson[index] != '[')
            {
                return values.ToArray();
            }
            index++;
            while (index < resultJson.Length)
            {
                char current = resultJson[index];
                if (current == ']')
                {
                    break;
                }
                if (current == '"')
                {
                    string item = ReadJsonString(resultJson, ref index);
                    if (item == null)
                    {
                        break;
                    }
                    if (item.Length > 0)
                    {
                        values.Add(item);
                    }
                    continue;
                }
                index++;
            }
            return values.ToArray();
        }

        internal static bool ReadRestorePointCreated(string resultJson)
        {
            if (string.IsNullOrEmpty(resultJson))
            {
                return true;
            }
            Match match = Regex.Match(
                resultJson,
                "\"RestorePointCreated\"\\s*:\\s*(?<value>true|false)",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            return !match.Success ||
                !string.Equals(match.Groups["value"].Value, "false", StringComparison.OrdinalIgnoreCase);
        }

        internal static IList<string> Describe(IEnumerable<string> skippedSettings, bool restorePointCreated)
        {
            var notes = new List<string>();
            var names = new List<string>();
            foreach (string setting in skippedSettings ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(setting))
                {
                    continue;
                }
                int separator = setting.LastIndexOf("::", StringComparison.Ordinal);
                string valueName = separator >= 0 ? setting.Substring(separator + 2) : setting;
                string friendly;
                if (!FriendlyNames.TryGetValue(valueName, out friendly))
                {
                    friendly = "параметр Windows «" + valueName + "»";
                }
                if (!names.Contains(friendly))
                {
                    names.Add(friendly);
                }
            }
            if (names.Count > 0)
            {
                notes.Add(
                    "Windows не разрешила изменить: " + string.Join(", ", names) +
                    ". Этот пункт пропущен, остальные настройки применены.");
            }
            if (!restorePointCreated)
            {
                notes.Add(
                    "Точка восстановления Windows не создана: обычно Windows делает " +
                    "её не чаще раза в сутки. Откат Boostix работает и без неё.");
            }
            return notes;
        }

        private static string ReadJsonString(string json, ref int index)
        {
            var builder = new StringBuilder();
            index++;
            while (index < json.Length)
            {
                char current = json[index++];
                if (current == '"')
                {
                    return builder.ToString();
                }
                if (current != '\\' || index >= json.Length)
                {
                    builder.Append(current);
                    continue;
                }
                char escape = json[index++];
                switch (escape)
                {
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'u':
                        if (index + 4 <= json.Length)
                        {
                            int code;
                            if (int.TryParse(
                                json.Substring(index, 4),
                                NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture,
                                out code))
                            {
                                builder.Append((char)code);
                            }
                            index += 4;
                        }
                        break;
                    default: builder.Append(escape); break;
                }
            }
            return null;
        }
    }
}
