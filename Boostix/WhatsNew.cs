using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Boostix.Branding;

namespace Boostix
{
    /// <summary>
    /// The "What's new" text comes from the CHANGELOG entry embedded in the
    /// build, so the release notes and the in-app summary cannot drift apart.
    /// </summary>
    internal static class WhatsNewContent
    {
        internal const string ChangelogResourceName = "Boostix.Changelog.md";
        private static readonly Regex LinkPattern = new Regex(
            @"\[(?<text>[^\]]+)\]\([^)]*\)",
            RegexOptions.CultureInvariant);

        internal static IList<string> LoadHighlights(string version)
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(ChangelogResourceName))
                {
                    if (stream == null)
                    {
                        return new List<string>();
                    }
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        return ParseHighlights(reader.ReadToEnd(), version);
                    }
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("Could not read the embedded changelog.", ex);
                return new List<string>();
            }
        }

        /// <summary>
        /// Returns the bullet items of the "## {version} ..." section as plain
        /// text; wrapped continuation lines are joined to their bullet.
        /// </summary>
        internal static IList<string> ParseHighlights(string changelog, string version)
        {
            var items = new List<string>();
            if (string.IsNullOrWhiteSpace(changelog) || string.IsNullOrWhiteSpace(version))
            {
                return items;
            }
            string[] lines = changelog.Replace("\r\n", "\n").Split('\n');
            bool inSection = false;
            StringBuilder current = null;
            foreach (string line in lines)
            {
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    if (inSection)
                    {
                        break;
                    }
                    string heading = line.Substring(3).Trim();
                    inSection =
                        heading.StartsWith(version, StringComparison.Ordinal) &&
                        (heading.Length == version.Length || heading[version.Length] == ' ');
                    continue;
                }
                if (!inSection)
                {
                    continue;
                }
                if (line.StartsWith("- ", StringComparison.Ordinal))
                {
                    AddItem(items, current);
                    current = new StringBuilder(line.Substring(2).Trim());
                }
                else if (current != null && line.StartsWith("  ", StringComparison.Ordinal) &&
                    line.Trim().Length > 0)
                {
                    current.Append(' ').Append(line.Trim());
                }
                else
                {
                    AddItem(items, current);
                    current = null;
                }
            }
            AddItem(items, current);
            return items;
        }

        /// <summary>
        /// Shows once per update. A missing marker means either a fresh install
        /// (no earlier Boostix data: stay quiet) or an update from a version
        /// that did not write the marker yet (earlier data exists: show).
        /// </summary>
        internal static bool ShouldShow(string lastSeenVersion, string currentVersion, bool hasEarlierData)
        {
            Version current;
            if (!Version.TryParse(currentVersion ?? string.Empty, out current))
            {
                return false;
            }
            Version lastSeen;
            if (!Version.TryParse((lastSeenVersion ?? string.Empty).Trim(), out lastSeen))
            {
                return hasEarlierData;
            }
            return current > lastSeen;
        }

        internal static bool HasEarlierData(string dataDirectory)
        {
            try
            {
                return
                    File.Exists(Path.Combine(dataDirectory, "boost-preferences.ini")) ||
                    File.Exists(Path.Combine(dataDirectory, "update.log")) ||
                    File.Exists(Path.Combine(dataDirectory, "last-session.report")) ||
                    Directory.Exists(Path.Combine(dataDirectory, "Sessions"));
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void AddItem(List<string> items, StringBuilder item)
        {
            if (item == null)
            {
                return;
            }
            string text = LinkPattern.Replace(item.ToString(), "${text}")
                .Replace("**", string.Empty)
                .Replace("`", string.Empty)
                .Trim();
            if (text.Length > 0)
            {
                items.Add(text);
            }
        }
    }

    /// <summary>
    /// One-time card shown over the home page after an update.
    /// </summary>
    internal sealed class WhatsNewOverlay : Grid
    {
        private readonly Border card;
        private readonly StackPanel itemsPanel;
        private readonly TextBlock titleText;
        private readonly Button closeButton;
        private IInputElement focusReturn;

        public WhatsNewOverlay()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            Background = new SolidColorBrush(SystemParameters.HighContrast
                ? BoostixDesignTokens.Background
                : BoostixDesignTokens.WithAlpha(BoostixDesignTokens.Sidebar, 222));
            Visibility = Visibility.Collapsed;
            KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
            AutomationProperties.SetAutomationId(this, "Boostix.WhatsNew");
            AutomationProperties.SetName(this, "Что нового в Boostix");

            card = new Border
            {
                Width = 460,
                MaxHeight = 500,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(BoostixDesignTokens.Surface),
                BorderBrush = new SolidColorBrush(BoostixDesignTokens.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(28, 26, 28, 24)
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

            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            card.Child = layout;

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            header.Children.Add(BoostixUi.FieldLabel("Boostix обновился"));
            titleText = BoostixUi.Text(
                string.Empty,
                20,
                BoostixDesignTokens.Text,
                true,
                FontWeights.SemiBold);
            titleText.Margin = new Thickness(0, 4, 0, 0);
            AutomationProperties.SetAutomationId(titleText, "Boostix.WhatsNew.Title");
            header.Children.Add(titleText);
            Grid.SetRow(header, 0);
            layout.Children.Add(header);

            itemsPanel = new StackPanel();
            var scroller = new ScrollViewer
            {
                Content = itemsPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false
            };
            scroller.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] =
                BoostCenterOverlay.MakeBoostixVerticalScrollBarStyle();
            Grid.SetRow(scroller, 1);
            layout.Children.Add(scroller);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            closeButton = BoostixUi.ActionButton("Понятно", BoostixButtonKind.Primary, null);
            closeButton.IsDefault = true;
            closeButton.IsCancel = true;
            closeButton.Click += delegate { Hide(); };
            AutomationProperties.SetAutomationId(closeButton, "Boostix.WhatsNew.Close");
            AutomationProperties.SetName(closeButton, "Закрыть список изменений");
            buttons.Children.Add(closeButton);
            Grid.SetRow(buttons, 2);
            layout.Children.Add(buttons);
        }

        public bool IsShown
        {
            get { return Visibility == Visibility.Visible; }
        }

        // A plain Grid has no automation peer; expose the card as a named
        // pane so screen readers announce it and UI tests can find it.
        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new WhatsNewAutomationPeer(this);
        }

        private sealed class WhatsNewAutomationPeer : FrameworkElementAutomationPeer
        {
            public WhatsNewAutomationPeer(WhatsNewOverlay owner)
                : base(owner)
            {
            }

            protected override AutomationControlType GetAutomationControlTypeCore()
            {
                return AutomationControlType.Pane;
            }

            protected override string GetClassNameCore()
            {
                return "WhatsNewOverlay";
            }
        }

        public void Show(string version, IList<string> highlights)
        {
            titleText.Text = "Что нового в " + version;
            AutomationProperties.SetName(titleText, titleText.Text);
            itemsPanel.Children.Clear();
            foreach (string highlight in highlights ?? new List<string>())
            {
                itemsPanel.Children.Add(BuildItem(highlight));
            }
            focusReturn = Keyboard.FocusedElement;
            Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(
                new Action(delegate { closeButton.Focus(); }),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        public void Hide()
        {
            Visibility = Visibility.Collapsed;
            var element = focusReturn as UIElement;
            focusReturn = null;
            if (element != null && element.IsVisible && element.Focusable)
            {
                element.Focus();
            }
        }

        private static FrameworkElement BuildItem(string text)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            var dot = new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(BoostixDesignTokens.Accent),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(2, 8, 0, 0)
            };
            row.Children.Add(dot);
            var body = BoostixUi.Text(
                text,
                BoostixDesignTokens.BodyTextSize,
                BoostixDesignTokens.SecondaryText,
                false,
                FontWeights.Normal);
            body.TextWrapping = TextWrapping.Wrap;
            body.TextTrimming = TextTrimming.None;
            body.LineHeight = Math.Round(BoostixDesignTokens.BodyTextSize * 1.45);
            body.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            AutomationProperties.SetAutomationId(body, "Boostix.WhatsNew.Item");
            AutomationProperties.SetName(body, text);
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            return row;
        }
    }
}
