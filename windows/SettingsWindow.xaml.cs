using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using MozartBrowser.Interop;
using MozartBrowser.Models;
using MozartBrowser.Services.Data;
using MozartBrowser.Services.Browser;
using MozartBrowser.Services.Theme;
using MessageBox = System.Windows.MessageBox;
using MozartBrowser.Dialogs;

namespace MozartBrowser.Windows
{
    /// <summary>
    /// Chromium/Cromite-style settings: a left sidebar switches between category
    /// panels (General/Appearance/Privacy &amp; Security/Downloads/About) on the
    /// right. Typing in the search box switches to a flattened view: every
    /// field group (the Border elements wrapping each control block, tagged
    /// with keywords) whose Tag contains the search text is shown across ALL
    /// categories at once, regardless of which sidebar item is selected —
    /// mirroring how chrome://settings' search behaves. Clearing the search
    /// box reverts to normal single-category sidebar navigation.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private List<StackPanel> _allPanels = null!;
        private List<Border> _allFieldGroups = null!;

        /// <summary>
        /// The active tab's CoreWebView2.Profile, passed in from MainWindow so
        /// "Delete browsing data..." can operate on the real normal-profile
        /// store. Null when there's genuinely no tab yet (shouldn't happen in
        /// practice — MainWindow always has at least one tab — but guarded
        /// anyway so Settings never crashes over it).
        /// </summary>
        private readonly CoreWebView2Profile? _activeProfile;

        /// <summary>Live URLs of every currently open normal tab, for "Use current pages".</summary>
        private readonly IReadOnlyList<string> _currentTabUrls;

        public SettingsWindow() : this(null, Array.Empty<string>())
        {
        }

        public SettingsWindow(CoreWebView2Profile? activeProfile, IReadOnlyList<string> currentTabUrls)
        {
            InitializeComponent();
            Interop.WindowMaximizeFix.Apply(this);

            _activeProfile = activeProfile;
            _currentTabUrls = currentTabUrls;

            _allPanels = new List<StackPanel> { PanelSearchEngine, PanelAppearance, PanelPrivacy, PanelAutofillPasswords, PanelDefaultBrowser, PanelOnStartup, PanelDownloads, PanelCustomThemes, PanelExtensions, PanelAbout };
            _allFieldGroups = _allPanels.SelectMany(p => p.Children.OfType<Border>()).ToList();

            LoadFromSettings();
        }

        // ============================= Custom title bar =============================

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) { MaximizeButton_Click(sender, e); return; }
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "▢";
        }

        private void LoadFromSettings()
        {
            var s = App.Settings.Current;

            SearchEngineCombo.ItemsSource = s.SearchEngines;
            SearchEngineCombo.SelectedItem = s.SearchEngines.FirstOrDefault(e => e.Name == s.DefaultSearchEngineName);

            // ---- On startup ----
            switch (s.Startup.Mode)
            {
                case StartupMode.Continue: StartupContinueOption.IsChecked = true; break;
                case StartupMode.ContinueAndNewTab: StartupContinueAndNewTabOption.IsChecked = true; break;
                case StartupMode.SpecificPages: StartupSpecificPagesOption.IsChecked = true; break;
                default: StartupNewTabOption.IsChecked = true; break;
            }
            StartupPagesList.Items.Clear();
            foreach (var page in s.Startup.Pages)
                StartupPagesList.Items.Add(page);
            UpdateStartupPagesPanelVisibility();

            // ---- Default browser ----
            RefreshDefaultBrowserStatus();

            ShowBookmarkBarCheck.IsChecked = s.ShowBookmarkBar;
            ThemeCombo.SelectedIndex = (int)s.Theme;

            switch (s.SecureDns.Mode)
            {
                case SecureDnsMode.Off: DnsOffOption.IsChecked = true; break;
                case SecureDnsMode.Automatic: DnsAutomaticOption.IsChecked = true; break;
                case SecureDnsMode.Custom: DnsCustomOption.IsChecked = true; break;
            }
            SelectComboItemByTag(DnsProviderCombo, s.SecureDns.Provider);
            DnsCustomTemplateBox.Text = s.SecureDns.CustomTemplate ?? string.Empty;

            // ---- Passwords and autofill ----
            OfferSavePasswordsCheck.IsChecked = s.PasswordManager.OfferToSavePasswords;
            AutofillEnabledCheck.IsChecked = s.PasswordManager.AutofillEnabled;

            // ---- Clear on close ----
            ClearOnCloseCheck.IsChecked = s.ClearOnClose.Enabled;
            ClearOnCloseHistoryCheck.IsChecked = s.ClearOnClose.Types.History;
            ClearOnCloseCookiesCheck.IsChecked = s.ClearOnClose.Types.Cookies;
            ClearOnCloseCacheCheck.IsChecked = s.ClearOnClose.Types.Cache;
            ClearOnCloseDownloadHistoryCheck.IsChecked = s.ClearOnClose.Types.DownloadHistory;
            ClearOnCloseAutofillCheck.IsChecked = s.ClearOnClose.Types.AutofillData;
            ClearOnClosePasswordsCheck.IsChecked = s.ClearOnClose.Types.Passwords;
            UpdateClearOnClosePanelVisibility();

            DownloadLocationBox.Text = s.Downloads.Location;
            AskWhereToSaveCheck.IsChecked = s.Downloads.AskWhereToSaveEachFile;
            ShowDownloadsWhenDoneCheck.IsChecked = s.Downloads.ShowDownloadsWhenDone;

            CustomThemeEnabledCheck.IsChecked = s.CustomTheme.IsEnabled;
            GradientCanvas.LoadStops(s.CustomTheme.ColorStops);

            AboutText.Text = $"Mozart Browser {App.Updates.CurrentVersion}\n" +
                              "Open source (MIT License) — github.com/vinhdubaii/mozart-browser";
        }

        private static void SelectComboItemByTag(ComboBox combo, string tag)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if ((string?)item.Tag == tag)
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        // ============================= Sidebar navigation =============================

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            // Same InitializeComponent-ordering guard as LibraryPanel: NavSearchEngine has
            // IsChecked="True" in XAML, which fires this Checked handler once during
            // InitializeComponent(), before _allPanels has been built yet (it's built
            // in the constructor body, right after InitializeComponent() returns).
            if (_allPanels == null) return;
            if (!string.IsNullOrEmpty(SearchBox.Text)) return; // search view takes priority

            ShowOnlyPanel(sender switch
            {
                _ when ReferenceEquals(sender, NavSearchEngine) => PanelSearchEngine,
                _ when ReferenceEquals(sender, NavAppearance) => PanelAppearance,
                _ when ReferenceEquals(sender, NavPrivacy) => PanelPrivacy,
                _ when ReferenceEquals(sender, NavAutofillPasswords) => PanelAutofillPasswords,
                _ when ReferenceEquals(sender, NavDefaultBrowser) => PanelDefaultBrowser,
                _ when ReferenceEquals(sender, NavOnStartup) => PanelOnStartup,
                _ when ReferenceEquals(sender, NavDownloads) => PanelDownloads,
                _ when ReferenceEquals(sender, NavCustomThemes) => PanelCustomThemes,
                _ when ReferenceEquals(sender, NavExtensions) => PanelExtensions,
                _ when ReferenceEquals(sender, NavAbout) => PanelAbout,
                _ => PanelSearchEngine
            });

            if (ReferenceEquals(sender, NavExtensions))
                _ = RefreshExtensionsListAsync();
        }

        private void ShowOnlyPanel(StackPanel target)
        {
            foreach (var panel in _allPanels)
                panel.Visibility = ReferenceEquals(panel, target) ? Visibility.Visible : Visibility.Collapsed;

            foreach (var group in _allFieldGroups)
                group.Visibility = Visibility.Visible; // reset any leftover filtering from a previous search
        }

        // ============================= Search filter =============================

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var query = SearchBox.Text.Trim().ToLowerInvariant();

            if (string.IsNullOrEmpty(query))
            {
                // Revert to whichever sidebar category is currently selected.
                var selected = SidebarNav.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
                if (selected != null) Nav_Checked(selected, new RoutedEventArgs());
                return;
            }

            // Flattened search mode: every panel becomes visible, but only the
            // field groups (Border with matching Tag keywords) inside them show.
            foreach (var panel in _allPanels)
                panel.Visibility = Visibility.Visible;

            foreach (var group in _allFieldGroups)
            {
                var keywords = (group.Tag as string) ?? string.Empty;
                group.Visibility = keywords.ToLowerInvariant().Contains(query)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            // Hide whole panels that end up with zero visible field groups, so the
            // section headers ("General", "Appearance"...) don't show up empty.
            foreach (var panel in _allPanels)
            {
                var anyVisible = panel.Children.OfType<Border>().Any(b => b.Visibility == Visibility.Visible);
                if (!anyVisible) panel.Visibility = Visibility.Collapsed;
            }
        }

        // ============================= Field handlers =============================

        // ============================= On startup =============================

        private void StartupOption_Checked(object sender, RoutedEventArgs e) => UpdateStartupPagesPanelVisibility();

        private void UpdateStartupPagesPanelVisibility()
        {
            // Guard: this can fire from XAML during InitializeComponent(), before
            // StartupPagesPanel itself has been assigned yet (same pattern as
            // Nav_Checked's _allPanels guard above).
            if (StartupPagesPanel == null) return;

            StartupPagesPanel.Visibility = StartupSpecificPagesOption.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void AddStartupPageButton_Click(object sender, RoutedEventArgs e) => AddStartupPageFromTextBox();

        private void StartupPageUrlBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
                AddStartupPageFromTextBox();
        }

        private void AddStartupPageFromTextBox()
        {
            var url = StartupPageUrlBox.Text.Trim();
            if (string.IsNullOrEmpty(url)) return;

            if (!url.Contains("://"))
                url = "https://" + url;

            StartupPagesList.Items.Add(url);
            StartupPageUrlBox.Text = string.Empty;
        }

        private void RemoveStartupPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (StartupPagesList.SelectedItem != null)
                StartupPagesList.Items.Remove(StartupPagesList.SelectedItem);
        }

        private void UseCurrentPagesButton_Click(object sender, RoutedEventArgs e)
        {
            StartupPagesList.Items.Clear();
            foreach (var url in _currentTabUrls)
            {
                if (!string.IsNullOrWhiteSpace(url) && url != "about:newtab")
                    StartupPagesList.Items.Add(url);
            }
        }

        // ============================= Default browser =============================

        private void RefreshDefaultBrowserStatus()
        {
            var isDefault = DefaultBrowserService.IsDefaultBrowser();

            DefaultBrowserStatusText.Text = isDefault
                ? "✓ Mozart is your default browser."
                : "Mozart is not currently your default browser.";

            MakeDefaultButton.Visibility = isDefault ? Visibility.Collapsed : Visibility.Visible;
        }

        private void MakeDefaultButton_Click(object sender, RoutedEventArgs e)
        {
            DefaultBrowserService.OpenDefaultAppsSettings();
        }

        /// <summary>
        /// The user picks the default browser in the separate Windows Settings
        /// app, then comes back here — re-check status whenever this window
        /// regains focus so the checkmark updates without needing to reopen
        /// Settings entirely.
        /// </summary>
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (IsLoaded) RefreshDefaultBrowserStatus();
        }

        // ============================= Extensions =============================

        /// <summary>
        /// Called from MainWindow.OpenSettingsToExtensions when a pinned
        /// extension with no options page is clicked — switches to this
        /// panel and, if an ID was given, scrolls to and briefly flashes
        /// that extension's card.
        /// </summary>
        public void SelectExtensionsPanel(string? highlightExtensionId = null)
        {
            NavExtensions.IsChecked = true;
            _ = RefreshExtensionsListAsync(highlightExtensionId);
        }

        private async Task RefreshExtensionsListAsync(string? highlightExtensionId = null)
        {
            var extensions = await App.Extensions.GetInstalledAsync();

            ExtensionsList.Items.Clear();
            NoExtensionsText.Visibility = extensions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            Border? cardToHighlight = null;
            foreach (var ext in extensions)
            {
                var card = BuildExtensionCard(ext);
                ExtensionsList.Items.Add(card);
                if (highlightExtensionId != null && ext.Id == highlightExtensionId)
                    cardToHighlight = card;
            }

            if (cardToHighlight != null)
            {
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    cardToHighlight.BringIntoView();
                    var original = cardToHighlight.Background;
                    cardToHighlight.Background = (System.Windows.Media.Brush)FindResource("SidebarSelectedBrush");
                    await Task.Delay(900);
                    cardToHighlight.Background = original;
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        /// <summary>
        /// Layout, top to bottom, mirroring chrome://extensions:
        ///   [icon] Name  v{Version}                [Enable/Disable toggle]
        ///   Description (if any)
        ///   Permissions: storage, tabs, ...          (only if any)
        ///   Site access: https://*.example.com/*, ... (only if any)
        ///   [Remove]
        /// </summary>
        private Border BuildExtensionCard(InstalledExtension ext)
        {
            var content = new StackPanel();

            // ---- Header row: icon, name, version, toggle ----
            var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (ext.IconPath != null && File.Exists(ext.IconPath))
            {
                var icon = new Image
                {
                    Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(ext.IconPath)),
                    Width = 28,
                    Height = 28,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                Grid.SetColumn(icon, 0);
                header.Children.Add(icon);
            }

            var nameRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            nameRow.Children.Add(new TextBlock { Text = ext.Name, FontSize = 14, FontWeight = FontWeights.SemiBold });
            nameRow.Children.Add(new TextBlock
            {
                Text = $"  v{ext.Version}",
                FontSize = 11,
                Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(nameRow, 1);
            header.Children.Add(nameRow);

            var enableToggle = new CheckBox
            {
                Style = (Style)FindResource("ExtensionToggleSwitch"),
                IsChecked = ext.IsEnabled,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Enable/disable this extension"
            };
            enableToggle.Click += async (_, _) => await App.Extensions.SetEnabledAsync(ext.Id, enableToggle.IsChecked == true);
            Grid.SetColumn(enableToggle, 2);
            header.Children.Add(enableToggle);

            content.Children.Add(header);

            // ---- Description ----
            if (!string.IsNullOrWhiteSpace(ext.Description))
            {
                content.Children.Add(new TextBlock
                {
                    Text = ext.Description,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                    Margin = new Thickness(0, 0, 0, 6)
                });
            }

            // ---- Permissions ----
            if (ext.Permissions.Count > 0)
            {
                content.Children.Add(new TextBlock
                {
                    Text = "Permissions: " + string.Join(", ", ext.Permissions),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                    Margin = new Thickness(0, 0, 0, 2)
                });
            }

            // ---- Site access ----
            if (ext.SiteAccess.Count > 0)
            {
                content.Children.Add(new TextBlock
                {
                    Text = "Site access: " + string.Join(", ", ext.SiteAccess),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                    Margin = new Thickness(0, 0, 0, 8)
                });
            }

            // ---- Remove ----
            var removeButton = new Button
            {
                Content = "Remove",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 4, 10, 4)
            };
            removeButton.Click += async (_, _) =>
            {
                var confirm = MessageBox.Show(this,
                    $"Remove \"{ext.Name}\"? This can't be undone.",
                    "Remove extension", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;

                await App.Extensions.RemoveAsync(ext.Id);
                await RefreshExtensionsListAsync();
            };
            content.Children.Add(removeButton);

            return new Border
            {
                Tag = $"extension {ext.Name} {ext.Description}",
                Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("ChromeBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 10),
                Child = content
            };
        }

        // ============================= Passwords / Delete browsing data / Clear on close =============================

        private void OpenPasswordManagerButton_Click(object sender, RoutedEventArgs e)
        {
            new PasswordManagerWindow { Owner = this }.Show();
        }

        private void DeleteBrowsingDataButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeProfile == null)
            {
                MessageBox.Show("No active browsing profile is available right now.", "Delete browsing data");
                return;
            }

            var dialog = new DeleteBrowsingDataDialog(_activeProfile) { Owner = this };
            dialog.ShowDialog();
        }

        private void ClearOnCloseCheck_CheckedChanged(object sender, RoutedEventArgs e) => UpdateClearOnClosePanelVisibility();

        private void UpdateClearOnClosePanelVisibility()
        {
            if (ClearOnClosePanel == null) return;
            ClearOnClosePanel.Visibility = ClearOnCloseCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ManageEnginesButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "Add a custom search engine by name and URL template (must contain %s for the query).\n" +
                "This dialog is a placeholder for the next iteration.",
                "Manage search engines");
        }

        private void ChangeDownloadLocationButton_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                SelectedPath = DownloadLocationBox.Text
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                DownloadLocationBox.Text = dialog.SelectedPath;
        }

        private async void CheckForUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            var info = await App.Updates.CheckForUpdateAsync();
            MessageBox.Show(info == null
                ? "You're on the latest version."
                : $"Version {info.Version} is available. Choose Update from the notification to install it.");
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var s = App.Settings.Current;

            if (SearchEngineCombo.SelectedItem is SearchEngine engine)
                s.DefaultSearchEngineName = engine.Name;

            // ---- On startup ----
            s.Startup.Mode = StartupContinueOption.IsChecked == true ? StartupMode.Continue
                : StartupContinueAndNewTabOption.IsChecked == true ? StartupMode.ContinueAndNewTab
                : StartupSpecificPagesOption.IsChecked == true ? StartupMode.SpecificPages
                : StartupMode.NewTab;
            s.Startup.Pages = StartupPagesList.Items.Cast<string>().ToList();

            s.ShowBookmarkBar = ShowBookmarkBarCheck.IsChecked == true;

            var newTheme = (AppTheme)ThemeCombo.SelectedIndex;
            s.Theme = newTheme;

            s.SecureDns.Mode = DnsCustomOption.IsChecked == true ? SecureDnsMode.Custom
                : DnsAutomaticOption.IsChecked == true ? SecureDnsMode.Automatic
                : SecureDnsMode.Off;

            if (DnsProviderCombo.SelectedItem is ComboBoxItem dnsItem && dnsItem.Tag is string tag)
                s.SecureDns.Provider = tag;
            s.SecureDns.CustomTemplate = DnsCustomTemplateBox.Text.Trim();

            // ---- Passwords and autofill ----
            s.PasswordManager.OfferToSavePasswords = OfferSavePasswordsCheck.IsChecked == true;
            s.PasswordManager.AutofillEnabled = AutofillEnabledCheck.IsChecked == true;

            // ---- Clear on close ----
            s.ClearOnClose.Enabled = ClearOnCloseCheck.IsChecked == true;
            s.ClearOnClose.Types.History = ClearOnCloseHistoryCheck.IsChecked == true;
            s.ClearOnClose.Types.Cookies = ClearOnCloseCookiesCheck.IsChecked == true;
            s.ClearOnClose.Types.Cache = ClearOnCloseCacheCheck.IsChecked == true;
            s.ClearOnClose.Types.DownloadHistory = ClearOnCloseDownloadHistoryCheck.IsChecked == true;
            s.ClearOnClose.Types.AutofillData = ClearOnCloseAutofillCheck.IsChecked == true;
            s.ClearOnClose.Types.Passwords = ClearOnClosePasswordsCheck.IsChecked == true;

            s.Downloads.Location = DownloadLocationBox.Text;
            s.Downloads.AskWhereToSaveEachFile = AskWhereToSaveCheck.IsChecked == true;
            s.Downloads.ShowDownloadsWhenDone = ShowDownloadsWhenDoneCheck.IsChecked == true;

            s.CustomTheme.IsEnabled = CustomThemeEnabledCheck.IsChecked == true;
            s.CustomTheme.ColorStops = GradientCanvas.ColorStops;

            await App.Settings.SaveAsync();

            // Theme takes effect immediately — no restart needed, unlike Secure DNS.
            ThemeService.Apply(newTheme);

            // Password/autofill toggles now control Mozart's own vault
            // (PasswordCaptureService reads App.Settings.Current.PasswordManager
            // live on every capture/autofill, no extra wiring needed here) —
            // the native CoreWebView2Profile autosave is always forced off in
            // MainWindow.CreateNewTabAsync, so it's deliberately not touched here.

            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
