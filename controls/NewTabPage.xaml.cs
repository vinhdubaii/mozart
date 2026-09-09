using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MozartBrowser.Models;
using MozartBrowser.Dialogs;
using MozartBrowser.Windows;

namespace MozartBrowser.Controls
{
    /// <summary>
    /// The "about:newtab" page: a Pinned row + a Recently-visited (Top Sites) row,
    /// each built from HistoryService, plus an optional custom background
    /// (color / preset / custom image) and a configurable column count —
    /// editable via the Customize button (NewTabSettingsWindow).
    /// </summary>
    public partial class NewTabPage : UserControl
    {
        public event EventHandler<string>? NavigateRequested;

        // Tile geometry — the circle itself plus the label under it. Column
        // count only changes ItemWidth (WrapPanel handles the actual wrapping),
        // so this is an approximation of "N columns" rather than an exact grid,
        // same tradeoff Chrome/Edge make with their top-sites row.
        private const double TileDiameter = 64;
        private const double TileCellHeight = 96;

        public NewTabPage()
        {
            InitializeComponent();
            ApplyBackground();
        }

        public async System.Threading.Tasks.Task RefreshAsync()
        {
            ApplyBackground();

            var layout = App.Settings.Current.NewTabLayout;
            ApplyColumnWidth();

            PinnedSection.Visibility = layout.ShowPinnedSites ? Visibility.Visible : Visibility.Collapsed;
            RecentSection.Visibility = layout.ShowRecentlyVisited ? Visibility.Visible : Visibility.Collapsed;

            var pinned = layout.ShowPinnedSites
                ? await App.History.GetPinnedSitesAsync()
                : new System.Collections.Generic.List<TopSiteItem>();
            var pinnedUrls = pinned.Select(p => p.Url).ToHashSet();

            var recent = layout.ShowRecentlyVisited
                ? (await App.History.GetTopSitesAsync(12)).Where(s => !pinnedUrls.Contains(s.Url)).ToList()
                : new System.Collections.Generic.List<TopSiteItem>();

            PinnedGrid.Items.Clear();
            if (layout.ShowPinnedSites)
                PinnedGrid.Items.Add(BuildAddTile());
            foreach (var site in pinned)
                PinnedGrid.Items.Add(BuildTile(site));

            RecentGrid.Items.Clear();
            foreach (var site in recent)
                RecentGrid.Items.Add(BuildTile(site));

            EmptyStateText.Visibility =
                pinned.Count == 0 && recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Recomputes tile cell width from the user's chosen column count.
        /// WrapPanel wraps automatically once tiles no longer fit a row, so
        /// setting a fixed per-tile width is enough to get an "N columns" grid
        /// without hand-rolling a UniformGrid with row logic.
        /// </summary>
        private void ApplyColumnWidth()
        {
            var columns = Math.Clamp(App.Settings.Current.NewTabLayout.Columns, 2, 8);
            _cellWidth = 900.0 / columns;
        }

        private double _cellWidth = 130;

        /// <summary>
        /// A tile: circular, blurred glass background, favicon centered inside,
        /// label underneath. Replaces the old flat white rounded-rectangle tile.
        /// </summary>
        private Border BuildTile(TopSiteItem site)
        {
            var cell = new Border
            {
                Width = _cellWidth,
                Height = TileCellHeight,
                Cursor = Cursors.Hand
            };

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var circle = new Ellipse
            {
                Width = TileDiameter,
                Height = TileDiameter,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            // Frosted-glass look: semi-transparent surface color + a blur behind it,
            // instead of the old hard-edged solid-white square.
            var glass = (Brush)FindResource("SurfaceAltBrush");
            var glassClone = glass.Clone();
            glassClone.Opacity = 0.55;
            circle.Fill = glassClone;
            circle.Effect = new BlurEffect { Radius = 6, KernelType = KernelType.Gaussian };

            var iconHost = new Grid
            {
                Width = TileDiameter,
                Height = TileDiameter,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            iconHost.Children.Add(circle);

            var favicon = new Image
            {
                Width = 28,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            TrySetFavicon(favicon, site.FaviconUrl, site.Domain);
            iconHost.Children.Add(favicon);

            stack.Children.Add(iconHost);

            stack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(site.Title) ? site.Domain : site.Title,
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 0),
                MaxWidth = _cellWidth - 8,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)FindResource("TextPrimaryBrush")
            });

            cell.Child = stack;
            cell.MouseLeftButtonDown += (_, _) => NavigateRequested?.Invoke(this, site.Url);

            var contextMenu = new ContextMenu();
            if (site.IsPinned)
            {
                var unpin = new MenuItem { Header = "Unpin" };
                unpin.Click += async (_, _) =>
                {
                    await App.History.UnpinSiteAsync(site.Url);
                    await RefreshAsync();
                };
                contextMenu.Items.Add(unpin);
            }
            else
            {
                var pin = new MenuItem { Header = "Pin" };
                pin.Click += async (_, _) =>
                {
                    await App.History.PinSiteAsync(site.Url, site.Title, site.FaviconUrl);
                    await RefreshAsync();
                };
                contextMenu.Items.Add(pin);
            }
            cell.ContextMenu = contextMenu;

            return cell;
        }

        /// <summary>The "+" tile at the end of Recently visited — opens a small
        /// popup to pin an arbitrary name + URL, for sites with no visit history.</summary>
        private Border BuildAddTile()
        {
            var cell = new Border
            {
                Width = _cellWidth,
                Height = TileCellHeight,
                Cursor = Cursors.Hand
            };

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var circle = new Ellipse
            {
                Width = TileDiameter,
                Height = TileDiameter,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var glass = (Brush)FindResource("SurfaceAltBrush");
            var glassClone = glass.Clone();
            glassClone.Opacity = 0.35;
            circle.Fill = glassClone;
            circle.Effect = new BlurEffect { Radius = 6, KernelType = KernelType.Gaussian };

            var iconHost = new Grid { Width = TileDiameter, Height = TileDiameter, HorizontalAlignment = HorizontalAlignment.Center };
            iconHost.Children.Add(circle);
            iconHost.Children.Add(new TextBlock
            {
                Text = "+",
                FontSize = 24,
                FontWeight = FontWeights.Light,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("TextPrimaryBrush")
            });
            stack.Children.Add(iconHost);

            stack.Children.Add(new TextBlock
            {
                Text = "Add site",
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 0),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)FindResource("TextSecondaryBrush")
            });

            cell.Child = stack;
            cell.MouseLeftButtonDown += async (_, _) =>
            {
                var popup = new AddPinnedSiteDialog { Owner = Window.GetWindow(this) };
                if (popup.ShowDialog() == true && !string.IsNullOrWhiteSpace(popup.ResultUrl))
                {
                    await App.History.PinSiteAsync(popup.ResultUrl!, popup.ResultTitle ?? string.Empty, null);
                    await RefreshAsync();
                }
            };

            return cell;
        }

        /// <summary>
        /// Loads the favicon: prefers the URL WebView2/history already recorded;
        /// falls back to Google's public favicon service (handles both "no
        /// favicon_url on record yet" and manually-pinned sites that were never
        /// actually visited, so have no history-derived favicon at all).
        /// </summary>
        private static void TrySetFavicon(Image target, string? faviconUrl, string domain)
        {
            var url = !string.IsNullOrWhiteSpace(faviconUrl)
                ? faviconUrl
                : $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(domain)}&sz=64";

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(url, UriKind.Absolute);
                bitmap.EndInit();
                target.Source = bitmap;
            }
            catch
            {
                // Swallow — a missing favicon just leaves the tile with an empty
                // circle rather than crashing the New Tab Page.
            }
        }

        private void ApplyBackground()
        {
            var bg = App.Settings.Current.NewTabBackground;

            switch (bg.Type)
            {
                case NewTabBackgroundType.Color when !string.IsNullOrEmpty(bg.Value):
                    RootGrid.Background = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(bg.Value));
                    OverlayBorder.Opacity = 0;
                    break;

                case NewTabBackgroundType.Preset or NewTabBackgroundType.Custom when !string.IsNullOrEmpty(bg.Value):
                    var path = bg.Type == NewTabBackgroundType.Preset
                        ? System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Backgrounds", bg.Value)
                        : bg.Value;

                    if (File.Exists(path))
                    {
                        var backgroundBitmap = new BitmapImage();
                        backgroundBitmap.BeginInit();
                        backgroundBitmap.CacheOption = BitmapCacheOption.OnLoad;
                        backgroundBitmap.UriSource = new Uri(path);
                        backgroundBitmap.EndInit();
                        backgroundBitmap.Freeze();

                        RootGrid.Background = new ImageBrush(backgroundBitmap)
                        {
                            Stretch = Stretch.UniformToFill
                        };
                        OverlayBorder.Opacity = bg.OverlayOpacity;
                    }
                    break;

                default:
                    RootGrid.Background = Brushes.Transparent;
                    OverlayBorder.Opacity = 0;
                    break;
            }
        }

        private async void CustomizeButton_Click(object sender, RoutedEventArgs e)
        {
            var popup = new NewTabSettingsWindow { Owner = Window.GetWindow(this) };
            if (popup.ShowDialog() == true)
            {
                await App.Settings.SaveAsync();
                await RefreshAsync();
            }
        }
    }
}
