using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MozartBrowser.Models;
using Microsoft.Win32;

namespace MozartBrowser.Windows
{
    /// <summary>
    /// Customize popup for the New Tab Page: grid layout (pinned/recent
    /// visibility, column count) plus the background picker that used to be
    /// this window's whole job back when it was BackgroundPickerDialog.
    /// </summary>
    public partial class NewTabSettingsWindow : Window
    {
        private string? _selectedCustomImagePath;
        private bool _userPickedNewFile;

        public NewTabSettingsWindow()
        {
            InitializeComponent();
            Interop.WindowMaximizeFix.Apply(this);

            for (int i = 2; i <= 8; i++)
                ColumnsCombo.Items.Add(i);

            var layout = App.Settings.Current.NewTabLayout;
            ShowPinnedCheck.IsChecked = layout.ShowPinnedSites;
            ShowRecentCheck.IsChecked = layout.ShowRecentlyVisited;
            ColumnsCombo.SelectedItem = layout.Columns;
            if (ColumnsCombo.SelectedItem == null)
                ColumnsCombo.SelectedIndex = 2; // falls back to 4 columns (index 2 == "4")

            var current = App.Settings.Current.NewTabBackground;
            switch (current.Type)
            {
                case NewTabBackgroundType.Color:
                    ColorOption.IsChecked = true;
                    if (!string.IsNullOrEmpty(current.Value)) ColorHexBox.Text = current.Value;
                    break;
                case NewTabBackgroundType.Custom:
                    CustomOption.IsChecked = true;
                    _selectedCustomImagePath = current.Value;
                    SelectedFileText.Text = current.Value != null ? Path.GetFileName(current.Value) : "";
                    break;
                default:
                    NoneOption.IsChecked = true;
                    break;
            }
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Images (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp"
            };

            if (dialog.ShowDialog() == true)
            {
                _selectedCustomImagePath = dialog.FileName;
                _userPickedNewFile = true;
                SelectedFileText.Text = Path.GetFileName(dialog.FileName);
                CustomOption.IsChecked = true;
            }
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            var layout = App.Settings.Current.NewTabLayout;
            layout.ShowPinnedSites = ShowPinnedCheck.IsChecked == true;
            layout.ShowRecentlyVisited = ShowRecentCheck.IsChecked == true;
            layout.Columns = ColumnsCombo.SelectedItem is int cols ? cols : 4;

            var bg = App.Settings.Current.NewTabBackground;

            if (NoneOption.IsChecked == true)
            {
                bg.Type = NewTabBackgroundType.None;
                bg.Value = null;
            }
            else if (ColorOption.IsChecked == true)
            {
                bg.Type = NewTabBackgroundType.Color;
                bg.Value = ColorHexBox.Text.Trim();
            }
            else if (CustomOption.IsChecked == true && _selectedCustomImagePath != null)
            {
                if (_userPickedNewFile)
                {
                    // Copy into AppData so the background survives even if the
                    // original file is later moved or deleted by the user.
                    var backgroundsFolder = Path.Combine(App.AppDataFolder, "Backgrounds");
                    Directory.CreateDirectory(backgroundsFolder);

                    var destination = Path.Combine(backgroundsFolder, Path.GetFileName(_selectedCustomImagePath));
                    File.Copy(_selectedCustomImagePath, destination, overwrite: true);

                    bg.Type = NewTabBackgroundType.Custom;
                    bg.Value = destination;
                }
                else
                {
                    // No new file was picked (Custom was already the saved
                    // background) — keep the existing saved path as-is instead
                    // of re-copying it onto itself.
                    bg.Type = NewTabBackgroundType.Custom;
                    bg.Value = _selectedCustomImagePath;
                }
            }

            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }
    }
}
