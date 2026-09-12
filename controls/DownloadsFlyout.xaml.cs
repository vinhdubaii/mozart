using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MozartBrowser.Models;
using MozartBrowser.Services.Browser;

namespace MozartBrowser.Controls
{
    /// <summary>
    /// Chromium-style "recent downloads" popup, replacing the old
    /// LibraryPanel flyout's Downloads tab. Purely a quick-glance surface —
    /// the full sortable/searchable list still lives at mozart://downloads
    /// (see FullHistoryRequested / the hamburger menu's "Downloads" item).
    ///
    /// Bound to a specific DownloadService resolved fresh on every Open()
    /// via SetServiceResolver, so the same control works for both
    /// MainWindow (App.Downloads) and PrivateWindow (App.DownloadsFor(true)) —
    /// each window owns its own instance of this control.
    /// </summary>
    public partial class DownloadsFlyout : UserControl
    {
        private Func<DownloadService?>? _resolveService;
        private DispatcherTimer? _refreshTimer;

        public DownloadsFlyout()
        {
            InitializeComponent();
        }

        /// <summary>Raised when the user clicks "Full download history" — the owner window should navigate a tab to InternalPages.DownloadsUrl.</summary>
        public event EventHandler? FullHistoryRequested;

        public void SetServiceResolver(Func<DownloadService?> resolver) => _resolveService = resolver;

        public void Toggle(UIElement placementTarget)
        {
            if (FlyoutPopup.IsOpen)
            {
                FlyoutPopup.IsOpen = false;
                return;
            }
            Open(placementTarget);
        }

        private void Open(UIElement placementTarget)
        {
            FlyoutPopup.PlacementTarget = placementTarget;
            FlyoutPopup.IsOpen = true;
            Refresh();

            // Simple 1s poll while the popup is open, same spirit as
            // downloads.html's own polling — good enough for a quick-glance
            // panel without wiring per-item PropertyChanged handlers.
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _refreshTimer.Tick += (_, _) => Refresh();
            _refreshTimer.Start();
        }

        private void FlyoutPopup_Closed(object? sender, EventArgs e)
        {
            _refreshTimer?.Stop();
            _refreshTimer = null;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => FlyoutPopup.IsOpen = false;

        private void FullHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            FlyoutPopup.IsOpen = false;
            FullHistoryRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Refresh()
        {
            var service = _resolveService?.Invoke();
            RowsPanel.Children.Clear();

            var items = service?.Downloads.OrderByDescending(d => d.StartTime).Take(8).ToList()
                ?? new List<DownloadItem>();

            if (items.Count == 0)
            {
                RowsPanel.Children.Add(new TextBlock
                {
                    Text = "No downloads yet.",
                    Margin = new Thickness(10, 20, 10, 20),
                    Foreground = (Brush)FindResource("TextSecondaryBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                return;
            }

            foreach (var item in items)
                RowsPanel.Children.Add(BuildRow(item, service!));
        }

        private Border BuildRow(DownloadItem item, DownloadService service)
        {
            // A completed download whose file no longer exists on disk shows
            // "Removed", matching Chrome's own recent-downloads flyout —
            // computed here rather than stored, since the file can disappear
            // at any time after the download itself finished.
            var fileExists = File.Exists(item.FilePath);
            var statusText = item.State switch
            {
                DownloadState.InProgress => $"{item.ProgressPercent:0}% · {FormatBytes(item.ReceivedBytes)}",
                DownloadState.Failed => "Failed",
                DownloadState.Cancelled => "Cancelled",
                DownloadState.Completed when !fileExists => "Removed",
                _ => $"{FormatBytes(item.TotalBytes)} · {TimeAgo(item.StartTime)}"
            };

            var grid = new Grid { Margin = new Thickness(8, 6, 8, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var extensionLabel = Path.GetExtension(item.FileName).TrimStart('.').ToUpperInvariant();
            if (extensionLabel.Length > 3) extensionLabel = extensionLabel[..3];
            if (extensionLabel.Length == 0) extensionLabel = "?";

            var icon = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(6),
                Background = (Brush)FindResource("AccentBrush"),
                Margin = new Thickness(0, 0, 10, 0)
            };
            icon.Child = new TextBlock
            {
                Text = extensionLabel,
                Foreground = Brushes.White,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(icon, 0);

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock
            {
                Text = item.FileName,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource("TextPrimaryBrush")
            });
            textStack.Children.Add(new TextBlock
            {
                Text = statusText,
                FontSize = 11,
                Foreground = (Brush)FindResource("TextSecondaryBrush")
            });
            Grid.SetColumn(textStack, 1);

            var removeButton = new Button
            {
                Content = "\u2715",
                Width = 22,
                Height = 22,
                FontSize = 10,
                Style = (Style)FindResource("ChromeIconButton"),
                ToolTip = "Remove from list"
            };
            removeButton.Click += (_, _) =>
            {
                service.Remove(item.Id);
                Refresh();
            };
            Grid.SetColumn(removeButton, 2);

            grid.Children.Add(icon);
            grid.Children.Add(textStack);
            grid.Children.Add(removeButton);

            var row = new Border
            {
                Background = Brushes.Transparent,
                Cursor = item.State == DownloadState.Completed && fileExists ? Cursors.Hand : Cursors.Arrow,
                Child = grid
            };

            if (item.State == DownloadState.Completed && fileExists)
            {
                row.MouseLeftButtonUp += (_, _) =>
                {
                    try { Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true }); }
                    catch { /* best-effort, same as the bridge's downloads.open handler */ }
                };
            }

            return row;
        }

        private static string FormatBytes(ulong bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double size = bytes;
            var unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return $"{size:0.#} {units[unit]}";
        }

        private static string TimeAgo(DateTime time)
        {
            var span = DateTime.Now - time;
            if (span.TotalSeconds < 60) return "Just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} minutes ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours} hours ago";
            return $"{(int)span.TotalDays} days ago";
        }
    }
}
