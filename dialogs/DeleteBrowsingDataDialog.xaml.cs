using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using MozartBrowser.Models;
using MozartBrowser.Services.Data;

namespace MozartBrowser.Dialogs
{
    /// <summary>
    /// Chromium-style "Delete browsing data" dialog. Operates on the shared
    /// normal-profile CoreWebView2Profile passed in from MainWindow (any tab's
    /// .CoreWebView2.Profile works — they all point at the same underlying
    /// profile store) — never on the private-window profile, which is a
    /// throwaway temp folder that gets deleted on its own anyway.
    /// </summary>
    public partial class DeleteBrowsingDataDialog : Window
    {
        private readonly CoreWebView2Profile _profile;

        public DeleteBrowsingDataDialog(CoreWebView2Profile profile)
        {
            InitializeComponent();
            Interop.WindowMaximizeFix.Apply(this);
            _profile = profile;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var types = new ClearBrowsingDataTypes
            {
                History = HistoryCheck.IsChecked == true,
                Cookies = CookiesCheck.IsChecked == true,
                Cache = CacheCheck.IsChecked == true,
                DownloadHistory = DownloadHistoryCheck.IsChecked == true,
                AutofillData = AutofillCheck.IsChecked == true,
                Passwords = PasswordsCheck.IsChecked == true
            };

            var kinds = BrowsingDataService.BuildKinds(types);
            if (kinds == 0)
            {
                StatusText.Text = "Select at least one type of data to delete.";
                return;
            }

            var range = (BrowsingDataService.TimeRange)TimeRangeCombo.SelectedIndex;

            DeleteButton.IsEnabled = false;
            StatusText.Text = "Deleting...";

            try
            {
                await BrowsingDataService.ClearAsync(_profile, kinds, range);
                await BrowsingDataService.ClearVaultIfSelectedAsync(types);
                StatusText.Text = "Done.";
                await System.Threading.Tasks.Task.Delay(400);
                Close();
            }
            catch (System.Exception ex)
            {
                StatusText.Text = $"Couldn't delete some data: {ex.Message}";
                DeleteButton.IsEnabled = true;
            }
        }
    }
}
