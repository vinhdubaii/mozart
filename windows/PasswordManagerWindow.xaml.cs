using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using MozartBrowser.Interop;
using MozartBrowser.Services.Data;

namespace MozartBrowser.Windows
{
    /// <summary>
    /// Mozart's own password manager UI — lists everything saved in
    /// PasswordVaultService (grouped by domain, expand a site to see its
    /// accounts), with search, delete, and reveal-after-reauthentication.
    ///
    /// This replaces the previous version, which just pointed a WebView2 at
    /// edge://settings/passwords: that only worked for Chromium's own
    /// built-in autosave store, which this app no longer uses now that it has
    /// its own vault (PasswordVaultService) — there would be nothing to show
    /// there anymore.
    /// </summary>
    public partial class PasswordManagerWindow : Window
    {
        private ObservableCollection<DomainGroup> _allGroups = new();

        public PasswordManagerWindow()
        {
            InitializeComponent();
            Interop.WindowMaximizeFix.Apply(this);
            _ = LoadAsync();
        }

        private async System.Threading.Tasks.Task LoadAsync()
        {
            var all = await App.Passwords.GetAllAsync();

            _allGroups = new ObservableCollection<DomainGroup>(
                all.GroupBy(p => p.Domain, StringComparer.OrdinalIgnoreCase)
                   .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                   .Select(g => new DomainGroup
                   {
                       Domain = g.Key,
                       Accounts = new ObservableCollection<PasswordRow>(
                           g.OrderBy(p => p.Username, StringComparer.OrdinalIgnoreCase)
                            .Select(p => new PasswordRow
                            {
                                Id = p.Id,
                                Username = p.Username,
                                EncryptedPassword = p.EncryptedPassword
                            }))
                   }));

            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var query = SearchBox.Text.Trim();

            var filtered = string.IsNullOrEmpty(query)
                ? _allGroups
                : new ObservableCollection<DomainGroup>(
                    _allGroups
                        .Select(g => new DomainGroup
                        {
                            Domain = g.Domain,
                            Accounts = new ObservableCollection<PasswordRow>(
                                g.Accounts.Where(a =>
                                    g.Domain.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                    a.Username.Contains(query, StringComparison.OrdinalIgnoreCase)))
                        })
                        .Where(g => g.Accounts.Count > 0));

            GroupsList.ItemsSource = filtered;
            EmptyStateText.Visibility = filtered.Any() ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyFilter();

        private void RevealButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button { Tag: PasswordRow row }) return;

            if (row.IsRevealed)
            {
                row.RevealedPassword = "••••••••";
                row.RevealButtonLabel = "Show";
                row.IsRevealed = false;
                return;
            }

            try
            {
                row.RevealedPassword = PasswordVaultService.Decrypt(row.EncryptedPassword);
                row.RevealButtonLabel = "Hide";
                row.IsRevealed = true;
            }
            catch
            {
                MessageBox.Show(
                    "This entry couldn't be decrypted — it may have been saved under a different Windows account.",
                    "Password Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button { Tag: PasswordRow row }) return;

            var result = MessageBox.Show(
                "Delete this saved password?", "Password Manager",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            await App.Passwords.DeleteAsync(row.Id);
            await LoadAsync();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }

    /// <summary>UI-only grouping of saved logins by domain — never persisted, rebuilt from PasswordVaultService on load/search.</summary>
    public class DomainGroup
    {
        public string Domain { get; set; } = string.Empty;
        public ObservableCollection<PasswordRow> Accounts { get; set; } = new();
    }

    /// <summary>One row in the expanded account list — wraps a SavedPassword with the reveal/hide UI state.</summary>
    public class PasswordRow : INotifyPropertyChanged
    {
        public int Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public byte[] EncryptedPassword { get; set; } = Array.Empty<byte>();
        public bool IsRevealed { get; set; }

        private string _revealedPassword = "••••••••";
        public string RevealedPassword
        {
            get => _revealedPassword;
            set { _revealedPassword = value; OnPropertyChanged(); }
        }

        private string _revealButtonLabel = "Show";
        public string RevealButtonLabel
        {
            get => _revealButtonLabel;
            set { _revealButtonLabel = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
