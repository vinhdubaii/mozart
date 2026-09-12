using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using MozartBrowser.Models;
using MozartBrowser.Dialogs;
using MozartBrowser.Windows;
using MozartBrowser.Controls;
using MozartBrowser.Services.Data;
using MozartBrowser.Services.Browser;
using MozartBrowser.Services.Theme;

namespace MozartBrowser.Chrome
{
    /// <summary>
    /// Shell window for normal (non-private) browsing. Owns the tab collection,
    /// the unified toolbar/title bar, the collapsible tab strip, the optional
    /// bookmark bar, and the slide-out Library panel. Private windows use the
    /// separate PrivateWindow class instead of this one.
    /// </summary>
    public partial class MainWindow : Window
    {
        public ObservableCollection<BrowserTab> Tabs { get; } = new();
        private BrowserTab? _activeTab;

        private bool _isFullScreen;
        private WindowState _preFullScreenWindowState;
        private Rect _preFullScreenBounds;

        /// <summary>
        /// Total height of the toolbar + tab strip + bookmark bar currently
        /// showing at the top of the window, in this window's own coordinate
        /// space. Each row's ActualHeight is naturally 0 while collapsed (tab
        /// strip below 2 tabs, bookmark bar toggled off in Settings), so this
        /// always reflects whatever's actually visible right now without
        /// needing to duplicate that visibility logic here. Used by dialogs
        /// like SavePasswordPromptDialog to anchor themselves just below the
        /// real chrome instead of guessing a fixed offset.
        /// </summary>
        public double ChromeHeight =>
            ToolbarRow.ActualHeight + TabStripBorder.ActualHeight + BookmarkBarHost.ActualHeight;

        public MainWindow()
        {
            InitializeComponent();

            // Correct fix for the WindowChrome + WindowStyle="None" + Maximized
            // rendering bug (previously patched with a margin-compensation hack
            // that overcompensated and caused visible black gaps — see
            // Interop/WindowMaximizeFix.cs for the full story).
            Interop.WindowMaximizeFix.Apply(this);

            Width = App.Settings.Current.Window.Width;
            Height = App.Settings.Current.Window.Height;
            if (App.Settings.Current.Window.IsMaximized)
                WindowState = WindowState.Maximized;

            LibraryPanelControl.OpenUrlRequested += (_, url) => NavigateActiveTab(url);
            LibraryPanelControl.CloseRequested += (_, _) => LibraryPanelControl.Visibility = Visibility.Collapsed;

            RebuildBookmarkBar();
            // Resolved lazily on every call (see ExtensionService.SetProfileResolver's
            // own comment) instead of the old per-tab AttachProfile approach, which
            // went stale - and started throwing - the moment whichever tab most
            // recently called it got closed, even with other Normal tabs still open.
            App.Extensions.SetProfileResolver(ResolveExtensionProfile);
            App.Extensions.ExtensionRemoved += (_, _) => RebuildPinnedExtensionIcons();
            ApplyCustomThemeBackground();

            // settings.html now owns saving (see App.RegisterBridgeHandlers'
            // "settings.save" action) instead of the old native SettingsWindow
            // calling ApplyCustomThemeBackground()/RebuildBookmarkBar() itself
            // right after ShowDialog() returned. Fires on every save
            // (including unrelated ones, e.g. window size) - both methods are
            // cheap no-ops when nothing relevant changed, so that's fine.
            App.Settings.Saved += () => Dispatcher.Invoke(() =>
            {
                ApplyCustomThemeBackground();
                RebuildBookmarkBar();
            });
            RegisterWindowBridgeHandlers();

            PreviewKeyDown += MainWindow_PreviewKeyDown;
            PreviewMouseWheel += MainWindow_PreviewMouseWheel;
            Closing += MainWindow_Closing;

            _ = InitializeStartupTabsAsync();
        }

        // ============================= Startup / session =============================

        /// <summary>
        /// Opens the initial set of tabs according to Settings.Startup.Mode
        /// (General settings, Chromium-style "On startup" section). Falls back
        /// to a single New Tab page whenever the configured mode has nothing to
        /// restore (e.g. first-ever launch, or "Specific pages" with an empty list).
        /// </summary>
        private async System.Threading.Tasks.Task InitializeStartupTabsAsync()
        {
            var startup = App.Settings.Current.Startup;

            List<string> urlsToOpen = startup.Mode switch
            {
                StartupMode.Continue => App.Settings.Current.LastSessionTabs,
                StartupMode.ContinueAndNewTab => App.Settings.Current.LastSessionTabs,
                StartupMode.SpecificPages => startup.Pages,
                _ => new List<string>()
            };

            if (urlsToOpen == null || urlsToOpen.Count == 0)
            {
                await CreateNewTabAsync();
            }
            else
            {
                foreach (var url in urlsToOpen)
                {
                    if (string.IsNullOrWhiteSpace(url) || url == "about:newtab")
                        await CreateNewTabAsync();
                    else
                        await CreateNewTabAsync(url);
                }
            }

            if (startup.Mode == StartupMode.ContinueAndNewTab)
                await CreateNewTabAsync();

            // Deliberately not called any earlier than this (e.g. not from the
            // constructor) - before the first tab above exists, ResolveExtensionProfile()
            // has nothing to return, so any earlier attempt would just render an
            // empty pinned-icons row and never get asked to try again.
            RebuildPinnedExtensionIcons();
        }

        /// <summary>
        /// Supplies ExtensionService with a currently-live Normal-profile
        /// CoreWebView2Profile, re-resolved on every call rather than cached -
        /// prefers the active tab, falls back to any other open tab. Tabs are
        /// removed from the Tabs collection at the same time their WebView2 is
        /// disposed (see CloseTab), so anything still in Tabs is guaranteed live.
        /// </summary>
        private CoreWebView2Profile? ResolveExtensionProfile() =>
            _activeTab?.WebView.CoreWebView2?.Profile ?? Tabs.FirstOrDefault()?.WebView.CoreWebView2?.Profile;

        /// <summary>
        /// Captures window size/state and the currently open tab URLs into
        /// App.Settings.Current, in memory only — App.OnExit performs the actual
        /// blocking disk write once every window has finished closing.
        /// </summary>
        private void SaveWindowAndSessionState()
        {
            App.Settings.Current.Window.Width = Width;
            App.Settings.Current.Window.Height = Height;
            App.Settings.Current.Window.IsMaximized = WindowState == WindowState.Maximized;

            App.Settings.Current.LastSessionTabs = Tabs
                .Select(t => t.IsNewTabPage ? "about:newtab" : t.Url)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .ToList();
        }

        private bool _closeConfirmed;

        /// <summary>
        /// Runs on every close path (X button, Alt+F4, taskbar close, or the
        /// last tab closing itself via CloseTab) — not just CloseButton_Click —
        /// so session save and "clear on close" reliably happen no matter how
        /// the window is closed. Cancels the first Closing pass, awaits the
        /// async cleanup, then closes for real.
        /// </summary>
        private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_closeConfirmed) return;
            e.Cancel = true;

            SaveWindowAndSessionState();
            await App.Settings.SaveAsync();

            if (App.Settings.Current.ClearOnClose.Enabled)
            {
                try
                {
                    var profile = _activeTab?.WebView.CoreWebView2?.Profile;
                    if (profile != null)
                    {
                        var kinds = BrowsingDataService.BuildKinds(App.Settings.Current.ClearOnClose.Types);
                        await BrowsingDataService.ClearAsync(profile, kinds, BrowsingDataService.TimeRange.AllTime);
                    }
                    await BrowsingDataService.ClearVaultIfSelectedAsync(App.Settings.Current.ClearOnClose.Types);
                }
                catch
                {
                    // Best-effort: never block app exit over a cleanup failure.
                }
            }

            // Dispose every remaining tab's WebView2 so the Chromium child
            // processes (renderer, GPU, network/storage utility) receive a
            // real shutdown signal instead of lingering in Task Manager after
            // the window disappears — matters most when the user closes the
            // whole app via X/Alt+F4 with 2+ tabs still open, since CloseTab
            // (used when closing tabs one at a time) already disposes as it
            // goes and would otherwise be the only place this happens.
            foreach (var tab in Tabs)
            {
                try
                {
                    TabContentHost.Children.Remove(tab.WebView);
                    tab.WebView.Dispose();
                }
                catch
                {
                    // Best-effort per tab: one bad WebView2 shouldn't block app exit.
                }
            }
            Tabs.Clear();

            _closeConfirmed = true;
            Close();
        }

        // ============================= Custom Themes (gradient toolbar/tab strip) =============================

        /// <summary>
        /// Applies (or clears) the Custom Themes gradient on the toolbar (Row 0)
        /// and tab strip (Row 1). When the gradient is off/empty, SetResourceReference
        /// restores the normal DynamicResource-style binding to ChromeBackgroundBrush
        /// (rather than just grabbing its current color once), so the toolbar keeps
        /// re-coloring automatically on future Light/Dark/System theme changes —
        /// exactly like it did before Custom Themes existed.
        /// </summary>
        private void ApplyCustomThemeBackground()
        {
            var gradient = GradientThemeService.BuildBackgroundBrush(App.Settings.Current.CustomTheme);

            if (gradient != null)
            {
                ToolbarRow.Background = gradient;
                TabStripBorder.Background = gradient;
            }
            else
            {
                // Grid's Background is Panel.BackgroundProperty; Border's is its own
                // Border.BackgroundProperty — these are two distinct DependencyProperty
                // registrations that happen to share the name "Background", so each
                // must be passed explicitly (an unqualified BackgroundProperty would
                // resolve to Control.BackgroundProperty via this class's own Window
                // base type, which neither Grid nor Border is registered against, and
                // would throw at runtime).
                ToolbarRow.SetResourceReference(Panel.BackgroundProperty, "ChromeBackgroundBrush");
                TabStripBorder.SetResourceReference(Border.BackgroundProperty, "ChromeBackgroundBrush");
            }
        }

        // ============================= Tab management =============================

        private async System.Threading.Tasks.Task CreateNewTabAsync(string? initialUrl = null)
        {
            var tab = new BrowserTab { IsPrivate = false };
            Tabs.Add(tab);

            TabContentHost.Children.Add(tab.WebView);
            tab.WebView.Visibility = Visibility.Collapsed;

            await tab.WebView.EnsureCoreWebView2Async(App.WebViewEnvironments.NormalEnvironment);
            WireTabEvents(tab);

            // Chromium has its own built-in Ctrl+Wheel/Ctrl+Plus/Ctrl+Minus zoom
            // handling, which would otherwise fight with our own zoom menu/shortcuts
            // below and pop up its own little "125%" bubble that doesn't match this
            // app's UI. WebView2.ZoomFactor still works perfectly with this off —
            // it's the same underlying zoom, just driven by us instead of Chromium.
            tab.WebView.CoreWebView2.Settings.IsZoomControlEnabled = false;

            // Mozart's own password vault (PasswordVaultService) replaces WebView2/
            // Chromium's built-in autosave entirely — that native store has no
            // public API to list, view, or delete individual entries, so keeping
            // it on alongside our own vault would just mean two separate,
            // invisible-to-each-other systems both offering to save the same
            // login. Always forced off here; the PasswordManager checkboxes in
            // Settings now control Mozart's own vault instead (OfferToSavePasswords
            // gates the "Save password?" prompt, AutofillEnabled gates the
            // click-to-fill icon) — see PasswordCaptureService.
            tab.WebView.CoreWebView2.Profile.IsPasswordAutosaveEnabled = false;
            tab.WebView.CoreWebView2.Profile.IsGeneralAutofillEnabled = false;
            await PasswordCaptureService.AttachAsync(tab.WebView.CoreWebView2, this);

            App.Downloads.Attach(tab.WebView.CoreWebView2);
            App.Bridge.Attach(tab.WebView.CoreWebView2);

            if (initialUrl != null)
            {
                tab.IsNewTabPage = false;
                InternalPages.TryResolveScheme(initialUrl, out var resolvedUrl);
                tab.WebView.CoreWebView2.Navigate(resolvedUrl);
            }

            RebuildTabStrip();
            SetActiveTab(tab);
        }

        private void WireTabEvents(BrowserTab tab)
        {
            tab.WebView.CoreWebView2.NavigationStarting += (_, _) => tab.IsLoading = true;

            tab.WebView.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                tab.IsLoading = false;
                tab.Url = tab.WebView.Source?.ToString() ?? tab.Url;
                tab.CanGoBack = tab.WebView.CoreWebView2.CanGoBack;
                tab.CanGoForward = tab.WebView.CoreWebView2.CanGoForward;

                if (e.IsSuccess && !tab.IsPrivate && tab.Url != "about:blank")
                {
                    await App.History.AddVisitAsync(tab.Url, tab.Title, tab.FaviconUrl);
                }

                if (ReferenceEquals(tab, _activeTab))
                    UpdateAddressBarAndButtons(tab);
            };

            tab.WebView.CoreWebView2.DocumentTitleChanged += (_, _) =>
            {
                tab.Title = string.IsNullOrWhiteSpace(tab.WebView.CoreWebView2.DocumentTitle)
                    ? tab.Url
                    : tab.WebView.CoreWebView2.DocumentTitle;
                RebuildTabStrip();
            };

            tab.WebView.CoreWebView2.FaviconChanged += (_, _) =>
            {
                tab.FaviconUrl = tab.WebView.CoreWebView2.FaviconUri;
            };

            // Without this handler, WebView2 falls back to its own default
            // behavior for target="_blank" links / window.open() (including
            // Ctrl+Click): it spawns a bare, chrome-less native window of its
            // own instead of a tab in this window. Handling the event and
            // opening a tab here is what makes those links behave like a
            // normal browser tab instead.
            tab.WebView.CoreWebView2.NewWindowRequested += async (_, e) =>
            {
                e.Handled = true;
                var deferral = e.GetDeferral();
                try
                {
                    await CreateNewTabAsync(e.Uri);
                }
                finally
                {
                    deferral.Complete();
                }
            };

            // Fully replaces WebView2's default right-click menu with Mozart's
            // own (guide Part 2, "Decision made": full replacement, not a
            // partial supplement). e.Handled = true suppresses the native
            // menu entirely regardless of what happens below. A Deferral is
            // required because ContextMenuBuilder needs to await one JS
            // round-trip (reading the live loop/controls state for Video/
            // Audio targets - not exposed on ContextMenuTarget itself, see
            // ContextMenuBuilder's own notes) before it can finish building
            // the menu.
            tab.WebView.CoreWebView2.ContextMenuRequested += async (_, e) =>
            {
                e.Handled = true;
                var deferral = e.GetDeferral();
                try
                {
                    var host = new ContextMenuHost
                    {
                        WebView = tab.WebView,
                        OwnerWindow = this,
                        OpenInNewTab = url => _ = CreateNewTabAsync(url)
                    };

                    var menu = await ContextMenuBuilder.BuildMenuAsync(
                        host, e.ContextMenuTarget, new Point(e.Location.X, e.Location.Y));

                    menu.PlacementTarget = tab.WebView;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.RelativePoint;
                    menu.HorizontalOffset = e.Location.X;
                    menu.VerticalOffset = e.Location.Y;
                    menu.IsOpen = true;
                }
                catch
                {
                    // BuildMenuAsync already falls back internally on a
                    // build failure (see its own try/catch); this is the
                    // last line of defense for anything past that point
                    // (Render, opening the menu itself). e.Handled is
                    // already true above, so the native menu stays
                    // suppressed either way - worst case here is simply no
                    // menu appears for this one right-click, which is far
                    // better than the app-wide crash dialog this used to
                    // produce.
                }
                finally
                {
                    deferral.Complete();
                }
            };
        }

        private void SetActiveTab(BrowserTab tab)
        {
            if (_activeTab != null)
            {
                _activeTab.WebView.Visibility = Visibility.Collapsed;
            }

            _activeTab = tab;
            tab.WebView.Visibility = tab.IsNewTabPage ? Visibility.Collapsed : Visibility.Visible;

            ShowOrHideNewTabPage(tab);
            UpdateAddressBarAndButtons(tab);
            RebuildTabStrip();
        }

        private NewTabPage? _newTabPageControl;

        private void ShowOrHideNewTabPage(BrowserTab tab)
        {
            if (tab.IsNewTabPage)
            {
                if (_newTabPageControl == null)
                {
                    _newTabPageControl = new NewTabPage();
                    _newTabPageControl.NavigateRequested += (_, url) => NavigateActiveTab(url);
                    TabContentHost.Children.Add(_newTabPageControl);
                }

                _newTabPageControl.Visibility = Visibility.Visible;
                _ = _newTabPageControl.RefreshAsync();
            }
            else if (_newTabPageControl != null)
            {
                _newTabPageControl.Visibility = Visibility.Collapsed;
            }
        }

        private void CloseTab(BrowserTab tab)
        {
            var index = Tabs.IndexOf(tab);
            Tabs.Remove(tab);
            TabContentHost.Children.Remove(tab.WebView);
            if (tab.WebView.CoreWebView2 != null)
                App.Bridge.Detach(tab.WebView.CoreWebView2);
            tab.WebView.Dispose();

            if (Tabs.Count == 0)
            {
                Close();
                return;
            }

            if (ReferenceEquals(_activeTab, tab))
            {
                var newIndex = Math.Min(index, Tabs.Count - 1);
                SetActiveTab(Tabs[newIndex]);
            }

            RebuildTabStrip();
        }

        /// <summary>Builds the tab strip UI directly (no DataTemplate binding) — only visible when >= 2 tabs, Epiphany-style.</summary>
        private void RebuildTabStrip()
        {
            TabStripItems.Items.Clear();
            TabStripBorder.Visibility =
                Tabs.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var tab in Tabs)
            {
                var isActive = ReferenceEquals(tab, _activeTab);

                var border = new Border
                {
                    Height = 28,
                    Margin = new Thickness(2, 3, 2, 3),
                    Padding = new Thickness(10, 0, 4, 0),
                    CornerRadius = new CornerRadius(6),
                    Background = isActive
                        ? (Brush)FindResource("TabActiveBrush")
                        : (Brush)FindResource("TabInactiveBrush"),
                    Cursor = Cursors.Hand
                };

                var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                stack.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrEmpty(tab.Title) ? "New Tab" : tab.Title,
                    MaxWidth = 160,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12,
                    Foreground = (Brush)FindResource("TextPrimaryBrush")
                });

                var closeButton = new Button
                {
                    Content = "✕",
                    Width = 20,
                    Height = 20,
                    Margin = new Thickness(6, 0, 0, 0),
                    Style = (Style)FindResource("ChromeIconButton"),
                    FontSize = 10
                };
                closeButton.Click += (_, _) => CloseTab(tab);
                stack.Children.Add(closeButton);

                border.Child = stack;
                border.MouseLeftButtonDown += (_, _) => SetActiveTab(tab);

                TabStripItems.Items.Add(border);
            }
        }

        // ============================= Address bar =============================

        private void UpdateAddressBarAndButtons(BrowserTab tab)
        {
            AddressBarTextBox.Text = tab.IsNewTabPage ? string.Empty : tab.Url;
            BackButton.IsEnabled = tab.CanGoBack;
            ForwardButton.IsEnabled = tab.CanGoForward;
            LockIcon.Visibility = tab.Url.StartsWith("https://") ? Visibility.Visible : Visibility.Collapsed;
            _ = UpdateBookmarkStarAsync(tab.Url);
        }

        private async System.Threading.Tasks.Task UpdateBookmarkStarAsync(string url)
        {
            if (string.IsNullOrEmpty(url)) { BookmarkStarPath.Fill = (Brush)FindResource("TextPrimaryBrush"); return; }
            var bookmarked = await App.Bookmarks.IsBookmarkedAsync(url);
            BookmarkStarPath.Fill = bookmarked ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("TextPrimaryBrush");
        }

        private void AddressBarTextBox_GotFocus(object sender, RoutedEventArgs e) => AddressBarTextBox.SelectAll();

        private void AddressBarTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            NavigateActiveTab(App.SearchEngines.Resolve(AddressBarTextBox.Text));
        }

        private void NavigateActiveTab(string url)
        {
            if (_activeTab == null) return;

            if (url == "about:newtab")
            {
                _activeTab.IsNewTabPage = true;
                _activeTab.WebView.Visibility = Visibility.Collapsed;
                // Merely hiding the WebView leaves whatever page was open still
                // fully alive underneath (video/audio playing, JS timers, network
                // activity) — navigating it to about:blank actually tears down
                // that page's document/JS context instead of just hiding it.
                _activeTab.WebView.CoreWebView2?.Navigate("about:blank");
                ShowOrHideNewTabPage(_activeTab);
                AddressBarTextBox.Text = string.Empty;
                return;
            }

            // Resolves Mozart's own "mozart://history" / "mozart://downloads" /
            // "mozart://settings" shortcuts to their real virtual-host URL.
            // (about:newtab is handled above, ahead of the New Tab Page rewrite.)
            InternalPages.TryResolveScheme(url, out var resolvedUrl);

            _activeTab.IsNewTabPage = false;
            ShowOrHideNewTabPage(_activeTab);
            _activeTab.WebView.Visibility = Visibility.Visible;
            _activeTab.WebView.CoreWebView2.Navigate(resolvedUrl);
        }

        // ============================= Toolbar button handlers =============================

        private void BackButton_Click(object sender, RoutedEventArgs e) => _activeTab?.WebView.CoreWebView2.GoBack();
        private void ForwardButton_Click(object sender, RoutedEventArgs e) => _activeTab?.WebView.CoreWebView2.GoForward();
        private void ReloadButton_Click(object sender, RoutedEventArgs e) => _activeTab?.WebView.CoreWebView2.Reload();
        private void HomeButton_Click(object sender, RoutedEventArgs e) => NavigateActiveTab("about:newtab");
        private async void NewTabButton_Click(object sender, RoutedEventArgs e) => await CreateNewTabAsync();

        private async void BookmarkStarButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeTab == null || string.IsNullOrEmpty(_activeTab.Url)) return;

            var isBookmarked = await App.Bookmarks.IsBookmarkedAsync(_activeTab.Url);
            if (isBookmarked)
                await App.Bookmarks.RemoveAsync(_activeTab.Url);
            else
                await App.Bookmarks.AddAsync(_activeTab.Url, _activeTab.Title, _activeTab.FaviconUrl);

            await UpdateBookmarkStarAsync(_activeTab.Url);
            RebuildBookmarkBar();
        }

        private void LibraryButton_Click(object sender, RoutedEventArgs e)
        {
            LibraryPanelControl.Visibility = LibraryPanelControl.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;

            if (LibraryPanelControl.Visibility == Visibility.Visible)
                _ = LibraryPanelControl.RefreshAsync();
        }

        // ============================= Extensions (puzzle icon + pinned icons) =============================

        private async void ExtensionsButton_Click(object sender, RoutedEventArgs e)
        {
            var extensions = await App.Extensions.GetInstalledAsync();
            var popup = new ContextMenu();

            if (extensions.Count == 0)
            {
                popup.Items.Add(new MenuItem { Header = "No extensions installed", IsEnabled = false });
            }
            else
            {
                foreach (var ext in extensions)
                {
                    var isPinned = App.Settings.Current.PinnedExtensionIds.Contains(ext.Id);
                    var item = new MenuItem
                    {
                        Header = ext.Name,
                        IsCheckable = true,
                        IsChecked = isPinned,
                        StaysOpenOnClick = true,
                        Icon = new ContentControl { ContentTemplate = (DataTemplate)FindResource(isPinned ? "Icon.Ext.Unpin" : "Icon.Ext.Pin") }
                    };
                    item.Click += async (_, _) =>
                    {
                        var pinned = App.Settings.Current.PinnedExtensionIds;
                        if (item.IsChecked && !pinned.Contains(ext.Id)) pinned.Add(ext.Id);
                        else if (!item.IsChecked) pinned.Remove(ext.Id);
                        item.Icon = new ContentControl { ContentTemplate = (DataTemplate)FindResource(item.IsChecked ? "Icon.Ext.Unpin" : "Icon.Ext.Pin") };
                        await App.Settings.SaveAsync();
                        RebuildPinnedExtensionIcons();
                    };
                    popup.Items.Add(item);
                }
            }

            popup.Items.Add(new Separator());
            var manageItem = new MenuItem
            {
                Header = "Manage extensions",
                Icon = new ContentControl { ContentTemplate = (DataTemplate)FindResource("Icon.Ext.Manage") }
            };
            manageItem.Click += (_, _) => OpenSettingsToExtensions();
            popup.Items.Add(manageItem);

            popup.PlacementTarget = ExtensionsButton;
            popup.IsOpen = true;
        }

        /// <summary>
        /// Renders one toolbar icon per pinned extension (in pin order),
        /// immediately to the left of the puzzle icon — same "rebuild from
        /// settings + live service state" shape as RebuildBookmarkBar().
        /// Called once at startup, whenever pin state changes, and whenever
        /// ExtensionService.ExtensionRemoved fires.
        /// </summary>
        private async void RebuildPinnedExtensionIcons()
        {
            PinnedExtensionsPanel.Children.Clear();

            var installed = await App.Extensions.GetInstalledAsync();
            var byId = installed.ToDictionary(x => x.Id);

            foreach (var id in App.Settings.Current.PinnedExtensionIds)
            {
                if (!byId.TryGetValue(id, out var ext)) continue;

                var button = new Button
                {
                    Style = (Style)FindResource("NavIconButton"),
                    ToolTip = ext.Name
                };

                if (ext.IconPath != null && File.Exists(ext.IconPath))
                {
                    button.Content = new Image
                    {
                        Source = new BitmapImage(new Uri(ext.IconPath)),
                        Width = 18,
                        Height = 18
                    };
                }
                else
                {
                    // Fallback to the same puzzle-piece glyph as ExtensionsButton
                    // when the extension declared no icon of its own.
                    button.Content = new System.Windows.Shapes.Path
                    {
                        Style = (Style)FindResource("NavIconPath"),
                        Data = Geometry.Parse(
                            "M2.5 6.5C2.5 8.70914 4.29086 10.5 6.5 10.5H9.16667C9.47666 10.5 9.63165 10.5 9.75882 10.4659C10.1039 10.3735 10.3735 10.1039 10.4659 9.75882C10.5 9.63165 10.5 9.47666 10.5 9.16667V6.5C10.5 4.29086 8.70914 2.5 6.5 2.5M17.5 21.5C15.2909 21.5 13.5 19.7091 13.5 17.5V14.8333C13.5 14.5233 13.5 14.3683 13.5341 14.2412C13.6265 13.8961 13.8961 13.6265 14.2412 13.5341C14.3683 13.5 14.5233 13.5 14.8333 13.5H17.5C19.7091 13.5 21.5 15.2909 21.5 17.5M2.5 17.5C2.5 15.2909 4.29086 13.5 6.5 13.5H8.9C9.46005 13.5 9.74008 13.5 9.95399 13.609C10.1422 13.7049 10.2951 13.8578 10.391 14.046C10.5 14.2599 10.5 14.5399 10.5 15.1V17.5C10.5 19.7091 8.70914 21.5 6.5 21.5C4.29086 21.5 2.5 19.7091 2.5 17.5ZM13.5 6.5C13.5 4.29086 15.2909 2.5 17.5 2.5C19.7091 2.5 21.5 4.29086 21.5 6.5C21.5 8.70914 19.7091 10.5 17.5 10.5H14.6429C14.5102 10.5 14.4438 10.5 14.388 10.4937C13.9244 10.4415 13.5585 10.0756 13.5063 9.61196C13.5 9.55616 13.5 9.48982 13.5 9.35714V6.5Z")
                    };
                }

                button.Click += (_, _) => OnPinnedExtensionClicked(ext);
                PinnedExtensionsPanel.Children.Add(button);
            }
        }

        /// <summary>
        /// Hybrid click behavior (WebView2 has no public API to render an
        /// extension's own toolbar-popup HTML): if the extension declares an
        /// options page, open it in a new tab; otherwise land on
        /// Settings → Extensions, scrolled to that extension's card. A bare
        /// click that did nothing would be the wrong default.
        /// </summary>
        private void OnPinnedExtensionClicked(InstalledExtension ext)
        {
            if (ext.HasOptionsPage)
                _ = CreateNewTabAsync(ext.OptionsPageUrl);
            else
                OpenSettingsToExtensions(ext.Id);
        }

        /// <summary>
        /// Extensions is its own internal page now (mozart://extensions), not
        /// a panel inside Settings — see REWRITE_PLAN §6. Deep-links straight
        /// to one extension's card via InternalPages.ExtensionsUrlFor when a
        /// specific extension triggered this (pinned-icon click).
        /// </summary>
        private void OpenSettingsToExtensions(string? highlightExtensionId = null)
        {
            var url = highlightExtensionId != null
                ? InternalPages.ExtensionsUrlFor(highlightExtensionId)
                : InternalPages.ExtensionsUrl;
            _ = CreateNewTabAsync(url);
        }

        /// <summary>
        /// Registers bridge actions that need this specific MainWindow — to
        /// anchor a native dialog (Owner=this) or read the currently-active
        /// tab's profile — rather than App-level services alone. Called once
        /// from the constructor; App.RegisterBridgeHandlers covers everything
        /// that doesn't need a window.
        /// </summary>
        private void RegisterWindowBridgeHandlers()
        {
            App.Bridge.RegisterHandler("settings.openClearBrowsingData", _ =>
            {
                var profile = _activeTab?.WebView.CoreWebView2?.Profile;
                if (profile != null)
                {
                    Dispatcher.Invoke(() => new DeleteBrowsingDataDialog(profile) { Owner = this }.ShowDialog());
                }
                return System.Threading.Tasks.Task.FromResult<object?>(null);
            });

            App.Bridge.RegisterHandler("settings.openPasswordManager", _ =>
            {
                Dispatcher.Invoke(async () => await CreateNewTabAsync(InternalPages.PasswordsUrl));
                return System.Threading.Tasks.Task.FromResult<object?>(null);
            });
        }

        // ============================= Hamburger menu (☰) =============================

        private async void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();

            menu.Items.Add(MenuItem("New Tab", "Ctrl+T", async (_, _) => await CreateNewTabAsync(), "Icon.Menu.NewTab"));
            menu.Items.Add(MenuItem("New Private Window", "Ctrl+Shift+N", (_, _) => OpenPrivateWindow(), "Icon.Menu.NewPrivateWindow"));
            menu.Items.Add(new Separator());
            menu.Items.Add(BuildZoomMenuItem());
            menu.Items.Add(new Separator());

            var showBookmarkBar = new MenuItem
            {
                Header = "Show Bookmark Bar",
                IsCheckable = true,
                IsChecked = App.Settings.Current.ShowBookmarkBar,
                Icon = new ContentControl { ContentTemplate = (DataTemplate)FindResource("Icon.Menu.ShowBookmarkBar") }
            };
            showBookmarkBar.Click += async (_, _) =>
            {
                App.Settings.Current.ShowBookmarkBar = showBookmarkBar.IsChecked;
                await App.Settings.SaveAsync();
                RebuildBookmarkBar();
            };
            menu.Items.Add(showBookmarkBar);
            menu.Items.Add(new Separator());

            menu.Items.Add(MenuItem("Find in Page...", "Ctrl+F", (_, _) => _activeTab?.WebView.CoreWebView2.ExecuteScriptAsync("undefined"), "Icon.Menu.FindInPage"));
            menu.Items.Add(MenuItem("Print...", "Ctrl+P", (_, _) => _activeTab?.WebView.CoreWebView2.ShowPrintUI(), "Icon.Print"));
            menu.Items.Add(new Separator());

            menu.Items.Add(MenuItem("Settings", null, (_, _) => _ = CreateNewTabAsync(InternalPages.SettingsUrl), "Icon.Menu.Settings"));
            menu.Items.Add(MenuItem("About Mozart Browser", null, (_, _) => ShowAbout(), "Icon.Menu.About"));
            menu.Items.Add(new Separator());

            menu.Items.Add(MenuItem("Exit", null, (_, _) => Close(), "Icon.Menu.Exit"));

            menu.PlacementTarget = MenuButton;
            menu.IsOpen = true;
            await System.Threading.Tasks.Task.CompletedTask;
        }

        private static MenuItem MenuItem(string header, string? gesture, RoutedEventHandler handler, string? iconKey = null)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? string.Empty };
            item.Click += handler;
            if (iconKey is not null && Application.Current.TryFindResource(iconKey) is DataTemplate iconTemplate)
                item.Icon = new ContentControl { ContentTemplate = iconTemplate };
            return item;
        }

        /// <summary>
        /// Builds the "🔍 Zoom  −  100%  +  ⛶" row shown inside the hamburger menu,
        /// matching Chrome/Edge's own zoom row. It's a single MenuItem whose Header
        /// is a custom StackPanel (WPF allows arbitrary content there) so the +/−/
        /// full-screen buttons can be clicked without the menu closing each time —
        /// StaysOpenOnClick handles that, same as it does for the "Show Bookmark Bar"
        /// checkable item just below it.
        /// </summary>
        private MenuItem BuildZoomMenuItem()
        {
            var textBrush = (Brush)FindResource("TextPrimaryBrush");
            var borderBrush = (Brush)FindResource("ChromeBorderBrush");
            var smallIconStyle = (Style)FindResource("ChromeIconButton");

            var percentText = new TextBlock
            {
                Text = $"{Math.Round(CurrentZoom * 100)}%",
                Width = 42,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                Foreground = textBrush
            };

            void RefreshPercentText() => percentText.Text = $"{Math.Round(CurrentZoom * 100)}%";

            System.Windows.Shapes.Path IconPath(string resourceKey, double size = 14) => new()
            {
                Width = size, Height = size, Stretch = Stretch.Uniform,
                Fill = textBrush, Stroke = null,
                Data = (Geometry)FindResource(resourceKey)
            };

            var zoomOutButton = new Button { Content = IconPath("Icon.Menu.ZoomOut"), Style = smallIconStyle, Width = 26, Height = 26, ToolTip = "Zoom out (Ctrl+-)" };
            zoomOutButton.Click += (_, _) => { ZoomOut(); RefreshPercentText(); };

            var zoomInButton = new Button { Content = IconPath("Icon.Menu.ZoomIn"), Style = smallIconStyle, Width = 26, Height = 26, ToolTip = "Zoom in (Ctrl++)" };
            zoomInButton.Click += (_, _) => { ZoomIn(); RefreshPercentText(); };

            var fullScreenIcon = IconPath("Icon.Menu.Fullscreen");
            var fullScreenButton = new Button { Content = fullScreenIcon, Style = smallIconStyle, Width = 26, Height = 26, ToolTip = "Full screen (F11)" };
            fullScreenButton.Click += (_, _) =>
            {
                ToggleFullScreen();
                fullScreenIcon.Data = (Geometry)FindResource(_isFullScreen ? "Icon.Menu.FullscreenExit" : "Icon.Menu.Fullscreen");
            };

            // Margins/label width trimmed down from their original values now
            // that the icon/checkmark gutter columns (see ControlStyles.xaml's
            // MenuItem template triggers) no longer pad every row out by 48px
            // regardless of content - this row doesn't need to compensate for
            // that anymore to land close to Chrome/Edge's own Zoom row width.
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2), VerticalAlignment = VerticalAlignment.Center };
            var searchIcon = IconPath("Icon.Menu.Search", 12);
            searchIcon.Margin = new Thickness(0, 0, 6, 0);
            searchIcon.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(searchIcon);
            row.Children.Add(new TextBlock { Text = "Zoom", Width = 46, VerticalAlignment = VerticalAlignment.Center, Foreground = textBrush });
            row.Children.Add(zoomOutButton);
            row.Children.Add(percentText);
            row.Children.Add(zoomInButton);
            row.Children.Add(new Border { Width = 1, Height = 20, Background = borderBrush, Margin = new Thickness(6, 0, 6, 0) });
            row.Children.Add(fullScreenButton);

            return new MenuItem { Header = row, StaysOpenOnClick = true, Focusable = false };
        }

        private void ShowAbout()
        {
            var version = App.Updates.CurrentVersion;
            // Must pass the same browserExecutableFolder used by NormalEnvironment/
            // PrivateEnvironment — the parameterless overload always looks for the
            // system Evergreen Runtime, which throws WebView2RuntimeNotFoundException
            // on machines that only have our bundled Fixed Version runtime.
            var engineVersion = CoreWebView2Environment.GetAvailableBrowserVersionString(
                WebViewEnvironmentService.FixedRuntimeFolder);
            MessageBox.Show(
                $"Mozart Browser {version}\nEngine (Chromium/WebView2): {engineVersion}\n\nOpen source under the MIT License.\ngithub.com/vinhdubaii/mozart-browser",
                "About Mozart Browser", MessageBoxButton.OK, MessageBoxImage.None);
        }

        private void OpenPrivateWindow()
        {
            var window = new PrivateWindow();
            window.Show();
        }

        // ============================= Bookmark bar =============================

        private async void RebuildBookmarkBar()
        {
            BookmarkBarHost.Visibility = App.Settings.Current.ShowBookmarkBar ? Visibility.Visible : Visibility.Collapsed;
            if (!App.Settings.Current.ShowBookmarkBar) return;

            BookmarkBarItems.Items.Clear();
            var bookmarks = await App.Bookmarks.GetAllAsync();

            foreach (var bookmark in bookmarks.Take(20))
            {
                var button = new Button
                {
                    Content = string.IsNullOrEmpty(bookmark.Title) ? bookmark.Url : bookmark.Title,
                    Margin = new Thickness(2, 0, 2, 0),
                    Padding = new Thickness(8, 2, 8, 2),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    FontSize = 12,
                    Cursor = Cursors.Hand
                };
                button.Click += (_, _) => NavigateActiveTab(bookmark.Url);
                BookmarkBarItems.Items.Add(button);
            }
        }

        // ============================= Zoom & Full Screen =============================

        // Same step levels Chrome/Edge cycle through on Ctrl+Plus/Minus.
        private static readonly double[] ZoomLevels =
        {
            0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1.0,
            1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 5.0
        };

        private const double DefaultZoom = 1.0;

        // Zoom lives on each tab's own WebView2.ZoomFactor, so it's naturally
        // per-tab already (matches real browsers) — no extra state to track here.
        private double CurrentZoom => _activeTab?.WebView.ZoomFactor ?? DefaultZoom;

        private void ZoomIn()
        {
            if (_activeTab is not { } tab) return;
            var next = ZoomLevels.FirstOrDefault(level => level > tab.WebView.ZoomFactor + 0.001);
            tab.WebView.ZoomFactor = next > 0 ? next : ZoomLevels[^1];
        }

        private void ZoomOut()
        {
            if (_activeTab is not { } tab) return;
            var prev = ZoomLevels.LastOrDefault(level => level < tab.WebView.ZoomFactor - 0.001);
            tab.WebView.ZoomFactor = prev > 0 ? prev : ZoomLevels[0];
        }

        private void ZoomReset()
        {
            if (_activeTab is { } tab)
                tab.WebView.ZoomFactor = DefaultZoom;
        }

        /// <summary>
        /// True F11-style full screen: hides the toolbar/tab strip/bookmark bar and
        /// resizes the window to cover the *entire* monitor, taskbar included — not
        /// just "maximized", which this app intentionally restricts to the work area
        /// (see WindowMaximizeFix.cs).
        ///
        /// Deliberately does NOT use WindowState.Maximized for this, even though
        /// that would seem simpler: Windows still treats a truly-maximized window as
        /// "maximized" at the shell/DWM level even when it's borderless, which is
        /// what caused two real bugs when this used WindowState.Maximized — the
        /// taskbar not actually hiding (WM_GETMINMAXINFO isn't reliably re-queried
        /// by toggling WindowState twice in the same tick) and Windows 11 drawing
        /// its own hover-triggered restore/"X" affordance at the top edge, which
        /// this app never rendered itself. Keeping WindowState.Normal and manually
        /// setting Left/Top/Width/Height to the monitor's full bounds sidesteps both:
        /// the OS never considers the window maximized, so no stray hover control
        /// appears, and the bounds are applied directly with no dependency on the
        /// OS re-sending any message.
        /// </summary>
        private void ToggleFullScreen()
        {
            _isFullScreen = !_isFullScreen;

            if (_isFullScreen)
            {
                _preFullScreenWindowState = WindowState;
                _preFullScreenBounds = new Rect(Left, Top, Width, Height);

                ToolbarRow.Visibility = Visibility.Collapsed;
                TabStripBorder.Visibility = Visibility.Collapsed;
                BookmarkBarHost.Visibility = Visibility.Collapsed;

                // Manual bounds only take effect from Normal — WPF ignores
                // Left/Top/Width/Height while WindowState is Maximized.
                if (WindowState != WindowState.Normal)
                    WindowState = WindowState.Normal;

                var monitorBounds = Interop.WindowMaximizeFix.GetMonitorBoundsInDips(this);
                if (!monitorBounds.IsEmpty)
                {
                    Left = monitorBounds.Left;
                    Top = monitorBounds.Top;
                    Width = monitorBounds.Width;
                    Height = monitorBounds.Height;
                }
            }
            else
            {
                WindowState = WindowState.Normal;
                if (_preFullScreenWindowState == WindowState.Maximized)
                {
                    WindowState = WindowState.Maximized;
                }
                else
                {
                    Left = _preFullScreenBounds.Left;
                    Top = _preFullScreenBounds.Top;
                    Width = _preFullScreenBounds.Width;
                    Height = _preFullScreenBounds.Height;
                }
                MaximizeIconPath.Data = (Geometry)FindResource(WindowState == WindowState.Maximized ? "Icon.Nav.Restore" : "Icon.Nav.Maximize");

                ToolbarRow.Visibility = Visibility.Visible;
                TabStripBorder.Visibility = Tabs.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
                RebuildBookmarkBar(); // restores its own Settings-driven visibility
            }
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F11)
            {
                ToggleFullScreen();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape && _isFullScreen)
            {
                ToggleFullScreen();
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers != ModifierKeys.Control) return;

            switch (e.Key)
            {
                case Key.OemPlus:
                case Key.Add:
                    ZoomIn();
                    e.Handled = true;
                    break;
                case Key.OemMinus:
                case Key.Subtract:
                    ZoomOut();
                    e.Handled = true;
                    break;
                case Key.D0:
                case Key.NumPad0:
                    ZoomReset();
                    e.Handled = true;
                    break;
            }
        }

        private void MainWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if (e.Delta > 0) ZoomIn(); else ZoomOut();
            e.Handled = true;
        }

        // ============================= Window chrome (custom title bar) =============================

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                MaximizeButton_Click(sender, e);
                return;
            }
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            MaximizeIconPath.Data = (Geometry)FindResource(WindowState == WindowState.Maximized ? "Icon.Nav.Restore" : "Icon.Nav.Maximize");
        }

        // Session save, settings persistence, and "clear on close" all happen in
        // MainWindow_Closing (wired in the constructor) so they run no matter
        // which path closes the window — this button just triggers Close().
        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
