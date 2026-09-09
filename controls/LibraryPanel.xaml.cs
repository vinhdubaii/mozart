using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MozartBrowser.Models;

namespace MozartBrowser.Controls
{
    public partial class LibraryPanel : UserControl
    {
        public event EventHandler<string>? OpenUrlRequested;
        public event EventHandler? CloseRequested;

        public LibraryPanel()
        {
            InitializeComponent();
        }

        private bool _isPrivateMode;

        /// <summary>
        /// Private windows never record history (see MainWindow.WireTabEvents,
        /// which skips App.History.AddVisitAsync whenever tab.IsPrivate is
        /// true), so showing a History tab here would always just be an empty,
        /// misleading list. Setting this hides that tab entirely and falls
        /// back to Bookmarks if History happened to be the active tab already.
        /// </summary>
        public bool IsPrivateMode
        {
            get => _isPrivateMode;
            set
            {
                _isPrivateMode = value;
                HistoryTabButton.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
                if (value && HistoryTabButton.IsChecked == true)
                    BookmarksTabButton.IsChecked = true;
            }
        }

        public async System.Threading.Tasks.Task RefreshAsync()
        {
            ListItems.Items.Clear();

            if (BookmarksTabButton.IsChecked == true)
            {
                foreach (var bookmark in await App.Bookmarks.GetAllAsync())
                    ListItems.Items.Add(BuildRow(bookmark.Title, bookmark.Url, () => OpenUrlRequested?.Invoke(this, bookmark.Url)));
            }
            else if (HistoryTabButton.IsChecked == true)
            {
                foreach (var item in await App.History.GetRecentAsync())
                    ListItems.Items.Add(BuildRow(item.Title, item.Url, () => OpenUrlRequested?.Invoke(this, item.Url)));
            }
            else if (DownloadsTabButton.IsChecked == true)
            {
                foreach (var download in App.Downloads.Downloads)
                    ListItems.Items.Add(BuildDownloadRow(download));
            }
        }

        /// <summary>
        /// Downloads need their own row (not the shared BuildRow used by
        /// Bookmarks/History) because each entry needs two actions — "Open"
        /// launches the file itself, "Show in folder" opens Explorer with it
        /// selected — plus double-click-to-open on the row itself, matching
        /// Chrome/Edge's downloads shelf.
        /// </summary>
        private Border BuildDownloadRow(DownloadItem download)
        {
            var status = download.State switch
            {
                DownloadState.Completed => "Completed",
                DownloadState.Failed => "Failed",
                _ => $"{download.ProgressPercent:0}%"
            };

            bool CanTouchFile() =>
                download.State == DownloadState.Completed && System.IO.File.Exists(download.FilePath);

            void OpenFile()
            {
                if (!CanTouchFile()) return;
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(download.FilePath)
                    {
                        UseShellExecute = true
                    });
                }
                catch
                {
                    // Best-effort: e.g. no app associated with this file type,
                    // or the file was moved/deleted after completion.
                }
            }

            void ShowInFolder()
            {
                if (!CanTouchFile()) return;
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{download.FilePath}\"");
                }
                catch
                {
                    // Best-effort, same as OpenFile above.
                }
            }

            var border = new Border { Padding = new Thickness(8, 6, 8, 6) };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textStack = new StackPanel { Cursor = Cursors.Hand };
            textStack.Children.Add(new TextBlock
            {
                Text = download.FileName,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            textStack.Children.Add(new TextBlock
            {
                Text = $"{status} · {download.Url}",
                FontSize = 10,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            textStack.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2) OpenFile();
            };
            Grid.SetColumn(textStack, 0);
            grid.Children.Add(textStack);

            var buttonsStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };

            var openButton = new Button
            {
                Content = "Open",
                FontSize = 10,
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 4, 0),
                IsEnabled = CanTouchFile()
            };
            openButton.Click += (_, _) => OpenFile();
            buttonsStack.Children.Add(openButton);

            var showInFolderButton = new Button
            {
                Content = "Show in folder",
                FontSize = 10,
                Padding = new Thickness(8, 3, 8, 3),
                IsEnabled = CanTouchFile()
            };
            showInFolderButton.Click += (_, _) => ShowInFolder();
            buttonsStack.Children.Add(showInFolderButton);

            Grid.SetColumn(buttonsStack, 1);
            grid.Children.Add(buttonsStack);

            border.Child = grid;
            return border;
        }

        private Border BuildRow(string title, string subtitle, Action? onClick)
        {
            var border = new Border
            {
                Padding = new Thickness(8, 6, 8, 6),
                Cursor = onClick != null ? Cursors.Hand : Cursors.Arrow
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(title) ? subtitle : title,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            stack.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 10,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            border.Child = stack;
            if (onClick != null)
                border.MouseLeftButtonDown += (_, _) => onClick();

            return border;
        }

        private async void Tab_Checked(object sender, RoutedEventArgs e)
        {
            // BookmarksTabButton has IsChecked="True" in XAML, so WPF raises this
            // Checked event once synchronously *during* InitializeComponent() —
            // before ListItems (declared further down in the same XAML) has been
            // wired up yet, which would NullReferenceException in RefreshAsync().
            // IsLoaded is still false at that point, so this guard skips only that
            // one spurious early call; MainWindow already calls RefreshAsync()
            // explicitly whenever the panel is actually opened (LibraryButton_Click),
            // and every real user click on these tabs happens after the panel is
            // loaded, so real tab switches are unaffected.
            if (!IsLoaded) return;
            await RefreshAsync();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
