using System;

namespace MozartBrowser.Services.Browser
{
    /// <summary>
    /// Central registry of Mozart's own "chrome://"-style internal pages — New
    /// Tab, History, Downloads, Settings — which are plain HTML/CSS/JS shipped
    /// under Assets/InternalPages and served to WebView2 through a virtual
    /// host mapping (see InternalPageBridge.Attach) rather than file:// URLs.
    /// That gives them a real https origin, so they can use fetch, relative
    /// paths, and other CORS-sensitive APIs normally — exactly how chrome://
    /// pages work inside real Chromium, and why "mozart.internal" (not
    /// "localhost" or a file path) is the host name used everywhere below.
    /// </summary>
    public static class InternalPages
    {
        public const string VirtualHost = "mozart.internal";
        private const string FolderName = "InternalPages";

        public static string BaseUrl => $"https://{VirtualHost}/";

        public static string NewTabUrl => BaseUrl + "newtab.html";
        public static string HistoryUrl => BaseUrl + "history.html";
        public static string DownloadsUrl => BaseUrl + "downloads.html";
        public static string SettingsUrl => BaseUrl + "settings.html";
        public static string PasswordsUrl => BaseUrl + "passwords.html";
        public static string ExtensionsUrl => BaseUrl + "extensions.html";

        /// <summary>Deep-links extensions.html straight to one extension's card — see OnPinnedExtensionClicked.</summary>
        public static string ExtensionsUrlFor(string extensionId) => $"{ExtensionsUrl}#{extensionId}";

        /// <summary>
        /// Folder on disk mapped to VirtualHost. MozartBrowser.csproj copies
        /// Assets/InternalPages/** to the output directory as Content, so this
        /// resolves correctly both in dev (bin/Debug/...) and after publish.
        /// </summary>
        public static string FolderPath =>
            System.IO.Path.Combine(AppContext.BaseDirectory, "assets", FolderName);

        /// <summary>
        /// Resolves Mozart's own address-bar shortcuts — "mozart://history" and
        /// the legacy "about:newtab" — to the real virtual-host URL that
        /// should actually be navigated to. Returns false (leaving
        /// resolvedUrl equal to the input) for anything that isn't one of
        /// Mozart's internal pages, so callers can fall through to normal
        /// navigation/search-engine resolution unchanged.
        /// </summary>
        public static bool TryResolveScheme(string url, out string resolvedUrl)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                resolvedUrl = url;
                return false;
            }

            if (string.Equals(url, "about:newtab", StringComparison.OrdinalIgnoreCase))
            {
                resolvedUrl = NewTabUrl;
                return true;
            }

            const string schemePrefix = "mozart://";
            if (url.StartsWith(schemePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var page = url[schemePrefix.Length..].Trim('/').ToLowerInvariant();
                resolvedUrl = page switch
                {
                    "" or "newtab" => NewTabUrl,
                    "history" => HistoryUrl,
                    "downloads" => DownloadsUrl,
                    "settings" => SettingsUrl,
                    "passwords" => PasswordsUrl,
                    "extensions" => ExtensionsUrl,
                    _ => NewTabUrl
                };
                return true;
            }

            resolvedUrl = url;
            return false;
        }
    }
}
