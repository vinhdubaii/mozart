using System;
using System.Windows;
using System.Windows.Input;

namespace MozartBrowser.Dialogs
{
    /// <summary>
    /// Tiny "Name + Address" popup opened from the New Tab Page's "+" tile,
    /// for pinning a site that has no visit history yet.
    /// </summary>
    public partial class AddPinnedSiteDialog : Window
    {
        public string? ResultTitle { get; private set; }
        public string? ResultUrl { get; private set; }

        public AddPinnedSiteDialog()
        {
            InitializeComponent();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var url = UrlBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                UrlBox.Focus();
                return;
            }

            if (!url.Contains("://"))
                url = "https://" + url;

            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                UrlBox.Focus();
                UrlBox.SelectAll();
                return;
            }

            ResultUrl = url;
            ResultTitle = string.IsNullOrWhiteSpace(NameBox.Text) ? null : NameBox.Text.Trim();
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
