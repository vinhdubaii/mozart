using System;
using System.IO;
using System.Windows;
using MozartBrowser.Interop;
using MozartBrowser.Models;
using Microsoft.Win32;

namespace MozartBrowser.Dialogs
{
    public partial class BackgroundPickerDialog : Window
    {
        private string? _selectedCustomImagePath;

        public BackgroundPickerDialog()
        {
            InitializeComponent();
            Interop.WindowMaximizeFix.Apply(this);

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
                SelectedFileText.Text = Path.GetFileName(dialog.FileName);
                CustomOption.IsChecked = true;
            }
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
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
                // Copy into AppData so the background survives even if the
                // original file is later moved or deleted by the user.
                var backgroundsFolder = Path.Combine(App.AppDataFolder, "Backgrounds");
                Directory.CreateDirectory(backgroundsFolder);

                var destination = Path.Combine(backgroundsFolder, Path.GetFileName(_selectedCustomImagePath));
                File.Copy(_selectedCustomImagePath, destination, overwrite: true);

                bg.Type = NewTabBackgroundType.Custom;
                bg.Value = destination;
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
