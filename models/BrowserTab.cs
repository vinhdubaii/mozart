using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Wpf;

namespace MozartBrowser.Models
{
    /// <summary>
    /// Represents a single browser tab. Each tab owns its own WebView2 control
    /// instance; tabs share one CoreWebView2Environment (normal or private)
    /// so cookies/session are shared like a real browser profile.
    /// </summary>
    public class BrowserTab : INotifyPropertyChanged
    {
        public Guid Id { get; } = Guid.NewGuid();

        public WebView2 WebView { get; } = new WebView2();

        private string _title = "New Tab";
        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        private string _url = string.Empty;
        public string Url
        {
            get => _url;
            set { _url = value; OnPropertyChanged(); }
        }

        private string? _faviconUrl;
        public string? FaviconUrl
        {
            get => _faviconUrl;
            set { _faviconUrl = value; OnPropertyChanged(); }
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        private bool _canGoBack;
        public bool CanGoBack
        {
            get => _canGoBack;
            set { _canGoBack = value; OnPropertyChanged(); }
        }

        private bool _canGoForward;
        public bool CanGoForward
        {
            get => _canGoForward;
            set { _canGoForward = value; OnPropertyChanged(); }
        }

        /// <summary>True when this tab lives inside a private/incognito window.</summary>
        public bool IsPrivate { get; init; }

        /// <summary>True when this tab is currently showing the New Tab Page (no navigation yet).</summary>
        private bool _isNewTabPage = true;
        public bool IsNewTabPage
        {
            get => _isNewTabPage;
            set { _isNewTabPage = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
