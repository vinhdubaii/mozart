using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MozartBrowser.Chrome;

namespace MozartBrowser.Dialogs
{
    /// <summary>
    /// Non-modal toast asking "Save password for this site?", shown by
    /// PasswordCaptureService right after it heuristically detects a login.
    /// Not modal (Show(), not ShowDialog()) so it never blocks browsing —
    /// same UX as Chrome/Edge's own save-password bubble.
    /// </summary>
    public partial class SavePasswordPromptDialog : Window
    {
        private readonly string _domain;
        private readonly string _username;
        private readonly string _password;
        private readonly DispatcherTimer _autoCloseTimer;

        public SavePasswordPromptDialog(string domain, string username, string password, bool isUpdate)
        {
            InitializeComponent();

            _domain = domain;
            _username = username;
            _password = password;

            HeaderText.Text = isUpdate ? "Update password?" : "Save password?";
            SaveButton.Content = isUpdate ? "Update" : "Save";
            DomainText.Text = domain;
            UsernameText.Text = username;

            Loaded += (_, _) => PositionNearOwner();

            // Matches real browsers: the bubble doesn't nag forever if ignored.
            _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _autoCloseTimer.Tick += (_, _) => { _autoCloseTimer.Stop(); Close(); };
            _autoCloseTimer.Start();

            Closed += (_, _) => _autoCloseTimer.Stop();
        }

        private void PositionNearOwner()
        {
            if (Owner == null) return;

            const double margin = 20;
            const double gapBelowChrome = 6;

            // Anchored top-right, just under the toolbar/tab strip/bookmark bar,
            // matching where Chrome/Edge's own save-password bubble drops down
            // from the address bar's lock icon. The previous version anchored to
            // Owner.ActualHeight (the window's bottom edge) instead, which put it
            // near the bottom of the screen when maximized, often partly off
            // screen with its buttons clipped.
            var chromeHeight = Owner is MainWindow mainWindow ? mainWindow.ChromeHeight : 0;

            Left = Owner.Left + Owner.ActualWidth - ActualWidth - margin;
            Top = Owner.Top + chromeHeight + gapBelowChrome;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void RevealButton_Click(object sender, RoutedEventArgs e)
        {
            var showing = PasswordText.Text == _password;
            PasswordText.Text = showing ? "••••••••" : _password;
            RevealButton.Content = showing ? "Show" : "Hide";
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            await App.Passwords.SaveAsync(_domain, _username, _password);
            Close();
        }

        private void NeverButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
