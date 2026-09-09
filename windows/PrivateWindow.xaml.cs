using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Web.WebView2.Core;
using MozartBrowser.Interop;
using MozartBrowser.Models;
using MozartBrowser.Services.Browser;

namespace MozartBrowser.Windows
{
    /// <summary>
    /// Shell window for private/incognito browsing. Mirrors MainWindow's
    /// multi-tab architecture (own ObservableCollection&lt;BrowserTab&gt;, its
    /// own tab strip, its own CreateNewTabAsync/WireTabEvents/etc.) rather than
    /// sharing code with it, so a bug fix here can never accidentally touch
    /// the normal-browsing window and vice versa.
    ///
    /// Two deliberate differences from MainWindow, both privacy-motivated:
    ///   - Every tab uses App.WebViewEnvironments.PrivateEnvironment (a
    ///     temp profile wiped on window close), never the persistent one.
    ///   - There's no "New Tab Page" here — MainWindow's NewTabPage shows
    ///     Recently Visited / Pinned tiles sourced from normal browsing
    ///     history (HistoryService), which would leak normal-session
    ///     browsing into a private window. New tabs navigate straight to
    ///     the configured homepage instead, and the Library panel's History
    ///     tab is hidden entirely (LibraryPanelControl.IsPrivateMode).
    /// </summary>
    public partial class PrivateWindow : Window
    {
        public ObservableCollection<BrowserTab> Tabs { get; } = new();
        private BrowserTab? _activeTab;

        public PrivateWindow()
        {
            InitializeComponent();
            WindowMaximizeFix.Apply(this);

            LibraryPanelControl.IsPrivateMode = true;
            LibraryPanelControl.OpenUrlRequested += (_, url) => NavigateActiveTab(url);
            LibraryPanelControl.CloseRequested += (_, _) => LibraryPanelControl.Visibility = Visibility.Collapsed;

            _ = CreateNewTabAsync();
        }

        // ============================= Tab management =============================

        private static string DefaultHomeUrl =>
            App.Settings.Current.HomepageUrl == "about:newtab"
                ? "https://duckduckgo.com"
                : App.Settings.Current.HomepageUrl;

        private async System.Threading.Tasks.Task CreateNewTabAsync(string? initialUrl = null)
        {
            var tab = new BrowserTab { IsPrivate = true };
            Tabs.Add(tab);

            TabContentHost.Children.Add(tab.WebView);
            tab.WebView.Visibility = Visibility.Collapsed;

            var env = await App.WebViewEnvironments.GetOrCreatePrivateEnvironmentAsync();
            await tab.WebView.EnsureCoreWebView2Async(env);
            WireTabEvents(tab);

            tab.WebView.CoreWebView2.Settings.IsZoomControlEnabled = false;

            // Same reasoning as MainWindow.CreateNewTabAsync: Mozart's own vault
            // replaces WebView2's built-in autosave/autofill entirely. Doubly
            // relevant for a private tab, which should never be offered a
            // password save prompt at all — PasswordCaptureService is
            // intentionally never attached below.
            tab.WebView.CoreWebView2.Profile.IsPasswordAutosaveEnabled = false;
            tab.WebView.CoreWebView2.Profile.IsGeneralAutofillEnabled = false;

            App.Downloads.Attach(tab.WebView.CoreWebView2);
            App.Bridge.Attach(tab.WebView.CoreWebView2);

            tab.IsNewTabPage = false;
            MozartBrowser.InternalPages.TryResolveScheme(initialUrl ?? DefaultHomeUrl, out var resolvedUrl);
            tab.WebView.CoreWebView2.Navigate(resolvedUrl);

            RebuildTabStrip();
            SetActiveTab(tab);
        }

        private void WireTabEvents(BrowserTab tab)
        {
            tab.WebView.CoreWebView2.NavigationStarting += (_, _) => tab.IsLoading = true;

            tab.WebView.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                tab.IsLoading = false;
                tab.Url = tab.WebView.Source?.ToString() ?? tab.Url;
                tab.CanGoBack = tab.WebView.CoreWebView2.CanGoBack;
                tab.CanGoForward = tab.WebView.CoreWebView2.CanGoForward;

                // Intentionally no App.History.AddVisitAsync call anywhere in
                // this window — private tabs never record history.

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

            // Same NewWindowRequested fix as MainWindow: without this,
            // target="_blank"/window.open() links spawn a bare native
            // WebView2 window instead of opening as a tab here. Opens into
            // another private tab (never a normal one) to keep everything
            // in this window on the private profile.
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

            // Same full-replacement custom context menu as MainWindow (guide
            // Part 2) - reuses the same ContextMenuBuilder/ContextMenuHost.
            // OpenInNewTab points at this window's own CreateNewTabAsync, so
            // "Open link in new window" / "Search with..." land in another
            // private tab rather than leaking into a normal-browsing tab.
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

                    var menu = await MozartBrowser.ContextMenuBuilder.BuildMenuAsync(
                        host, e.ContextMenuTarget, new Point(e.Location.X, e.Location.Y));

                    menu.PlacementTarget = tab.WebView;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.RelativePoint;
                    menu.HorizontalOffset = e.Location.X;
                    menu.VerticalOffset = e.Location.Y;
                    menu.IsOpen = true;
                }
                catch
                {
                    // See MainWindow's identical handler for why this is an
                    // intentional silent catch, not an oversight.
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
                _activeTab.WebView.Visibility = Visibility.Collapsed;

            _activeTab = tab;
            tab.WebView.Visibility = Visibility.Visible;

            UpdateAddressBarAndButtons(tab);
            RebuildTabStrip();
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

        /// <summary>Same Epiphany-style "only show the strip when >= 2 tabs" as MainWindow, built directly (no DataTemplate).</summary>
        private void RebuildTabStrip()
        {
            TabStripItems.Items.Clear();
            TabStripBorder.Visibility = Tabs.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;

            var activeBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            var inactiveBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));

            foreach (var tab in Tabs)
            {
                var isActive = ReferenceEquals(tab, _activeTab);

                var border = new Border
                {
                    Height = 28,
                    Margin = new Thickness(2, 3, 2, 3),
                    Padding = new Thickness(10, 0, 4, 0),
                    CornerRadius = new CornerRadius(6),
                    Background = isActive ? activeBrush : inactiveBrush,
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
                    Foreground = Brushes.White
                });

                var closeIcon = new Path
                {
                    Width = 10,
                    Height = 10,
                    Stretch = Stretch.Uniform,
                    Stroke = Brushes.White,
                    StrokeThickness = 1.5,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Data = Geometry.Parse("M6,6L18,18M18,6L6,18")
                };

                var closeButton = new Button
                {
                    Content = closeIcon,
                    Width = 20,
                    Height = 20,
                    Margin = new Thickness(6, 0, 0, 0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand
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
            AddressBarTextBox.Text = tab.Url;
            BackButton.IsEnabled = tab.CanGoBack;
            ForwardButton.IsEnabled = tab.CanGoForward;
            LockIcon.Visibility = tab.Url.StartsWith("https://") ? Visibility.Visible : Visibility.Collapsed;
            _ = UpdateBookmarkStarAsync(tab.Url);
        }

        private async System.Threading.Tasks.Task UpdateBookmarkStarAsync(string url)
        {
            if (string.IsNullOrEmpty(url)) { BookmarkStarPath.Fill = Brushes.Transparent; return; }
            var bookmarked = await App.Bookmarks.IsBookmarkedAsync(url);
            BookmarkStarPath.Fill = bookmarked ? Brushes.White : Brushes.Transparent;
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
            MozartBrowser.InternalPages.TryResolveScheme(url, out var resolvedUrl);
            _activeTab.WebView.CoreWebView2.Navigate(resolvedUrl);
        }

        private async void BookmarkStarButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeTab == null || string.IsNullOrEmpty(_activeTab.Url)) return;

            var isBookmarked = await App.Bookmarks.IsBookmarkedAsync(_activeTab.Url);
            if (isBookmarked)
                await App.Bookmarks.RemoveAsync(_activeTab.Url);
            else
                await App.Bookmarks.AddAsync(_activeTab.Url, _activeTab.Title, _activeTab.FaviconUrl);

            await UpdateBookmarkStarAsync(_activeTab.Url);
        }

        // ============================= Toolbar button handlers =============================

        private void BackButton_Click(object sender, RoutedEventArgs e) => _activeTab?.WebView.CoreWebView2.GoBack();
        private void ForwardButton_Click(object sender, RoutedEventArgs e) => _activeTab?.WebView.CoreWebView2.GoForward();
        private void ReloadButton_Click(object sender, RoutedEventArgs e) => _activeTab?.WebView.CoreWebView2.Reload();

        // Unlike MainWindow's Home (which goes to the tile-based New Tab
        // Page), Home here just re-navigates the current tab to the
        // configured homepage directly — see the class doc comment for why
        // this window has no New Tab Page at all.
        private void HomeButton_Click(object sender, RoutedEventArgs e) => NavigateActiveTab(DefaultHomeUrl);
        private async void NewTabButton_Click(object sender, RoutedEventArgs e) => await CreateNewTabAsync();

        private void LibraryButton_Click(object sender, RoutedEventArgs e)
        {
            LibraryPanelControl.Visibility = LibraryPanelControl.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;

            if (LibraryPanelControl.Visibility == Visibility.Visible)
                _ = LibraryPanelControl.RefreshAsync();
        }

        // ============================= Hamburger menu (☰) =============================
        // Deliberately smaller than MainWindow's: no "New Private Window" (this
        // already is one), no Settings, no zoom row — kept to the handful of
        // actions that make sense inside an already-private window.

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();

            menu.Items.Add(MenuItem("New Private Tab", "Ctrl+T", async (_, _) => await CreateNewTabAsync()));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("Find in Page...", "Ctrl+F", (_, _) => _activeTab?.WebView.CoreWebView2.ExecuteScriptAsync("undefined")));
            menu.Items.Add(MenuItem("Print...", "Ctrl+P", (_, _) => _activeTab?.WebView.CoreWebView2.ShowPrintUI()));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("Close Private Window", null, (_, _) => Close()));

            menu.PlacementTarget = MenuButton;
            menu.IsOpen = true;
        }

        private static MenuItem MenuItem(string header, string? gesture, RoutedEventHandler handler)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? string.Empty };
            item.Click += handler;
            return item;
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
            MaximizeIconPath.Data = Geometry.Parse(WindowState == WindowState.Maximized
                ? "M6,9 L15,9 L15,18 L6,18 Z M9,6 L18,6 L18,15 L15,15 L15,9 L9,9 Z"   // restore: two overlapping squares
                : "M6,6 L18,6 L18,18 L6,18 Z");                                       // maximize: single square
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void PrivateWindow_Closed(object? sender, EventArgs e)
        {
            foreach (var tab in Tabs)
            {
                try
                {
                    TabContentHost.Children.Remove(tab.WebView);
                    tab.WebView.Dispose();
                }
                catch
                {
                    // Best-effort per tab, same as MainWindow_Closing.
                }
            }
            Tabs.Clear();

            // NOTE: pre-existing limitation carried over unchanged from before
            // this rewrite — this wipes the one shared PrivateEnvironment for
            // the whole app, so opening two Private windows and closing only
            // one currently also tears down the other's profile. Multi-window
            // private-profile lifetime (e.g. ref-counting) is a separate,
            // larger change outside this fix's scope.
            App.WebViewEnvironments.CleanupPrivateEnvironment();
        }
    }
}
