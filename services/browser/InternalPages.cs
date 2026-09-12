using System;

namespace MozartBrowser.Services.Browser
{
    /// <summary>
    /// Central registry of Mozart's own "chrome://"-style internal pages — New
    /// Tab, History, Downloads, Settings — which are plain HTML/CSS/JS shipped
    /// under Assets/InternalPages and served to WebView2 through a real custom
    /// scheme ("mozart://") rather than a virtual https host or file:// URLs.
    /// See InternalPageBridge.Attach/OnWebResourceRequested for how requests
    /// against this scheme are actually served, and
    /// WebViewEnvironmentService for where the scheme is registered with
    /// CoreWebView2CustomSchemeRegistration before each CoreWebView2Environment
    /// is created (required for the scheme to be recognized at all).
    ///
    /// Sub-pages/deep-links use HASH routing (e.g. "mozart://settings#appearance",
    /// "mozart://extensions#<id>") rather than path routing, so the page never
    /// actually navigates away from "mozart://<host>/<host>.html" — only the
    /// hash changes — which means the existing relative asset links inside
    /// each page (shared/base.css, shared/mozart-bridge.js, ...) keep working
    /// unchanged.
    /// </summary>
    public static class InternalPages
    {
        public const string Scheme = "mozart";
        private const string FolderName = "InternalPages";

        // .NET's generic Uri parsing generally treats "scheme://host/path" as
        // hierarchical (with Host/AbsolutePath parsed normally) for unknown
        // schemes out of the box on modern .NET — this registration is just
        // a defensive belt-and-suspenders so InternalPageBridge's
        // OnWebResourceRequested (new Uri(e.Request.Uri).Host/.AbsolutePath)
        // behaves identically regardless of runtime quirks.
        static InternalPages()
        {
            try
            {
                UriParser.Register(new GenericUriParser(GenericUriParserOptions.GenericAuthority), Scheme, -1);
            }
            catch (InvalidOperationException)
            {
                // Already registered (e.g. re-entrant static init) — fine, ignore.
            }
        }

        public static string NewTabUrl => $"{Scheme}://newtab";
        public static string HistoryUrl => $"{Scheme}://history";
        public static string DownloadsUrl => $"{Scheme}://downloads";
        public static string SettingsUrl => $"{Scheme}://settings";
        public static string PasswordsUrl => $"{Scheme}://passwords";
        public static string ExtensionsUrl => $"{Scheme}://extensions";

        /// <summary>Deep-links extensions.html straight to one extension's card — see OnPinnedExtensionClicked.</summary>
        public static string ExtensionsUrlFor(string extensionId) => $"{ExtensionsUrl}#{extensionId}";

        /// <summary>
        /// Folder on disk containing every internal page's HTML/CSS/JS.
        /// MozartBrowser.csproj copies Assets/InternalPages/** to the output
        /// directory as Content, so this resolves correctly both in dev
        /// (bin/Debug/...) and after publish.
        /// </summary>
        public static string FolderPath =>
            System.IO.Path.Combine(AppContext.BaseDirectory, "assets", FolderName);

        /// <summary>True for any "mozart://..." URL — used to exclude internal pages from history recording, the bookmark star, and the address bar's lock icon.</summary>
        public static bool IsInternalUrl(string? url) =>
            !string.IsNullOrEmpty(url) && url.StartsWith($"{Scheme}://", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Resolves Mozart's own address-bar shortcuts — bare page names like
        /// "mozart://history" (normalized/validated) and the legacy
        /// "about:newtab" — to the canonical "mozart://<page>" URL that should
        /// actually be navigated to. Returns false (leaving resolvedUrl equal
        /// to the input) for anything that isn't one of Mozart's internal
        /// pages, so callers can fall through to normal navigation/search
        /// engine resolution unchanged.
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

            var schemePrefix = $"{Scheme}://";
            if (url.StartsWith(schemePrefix, StringComparison.OrdinalIgnoreCase))
            {
                // Keep everything after the host as-is (hash deep links like
                // "extensions#<id>" or "settings#appearance") — only the host
                // itself is validated/normalized against the known pages.
                var rest = url[schemePrefix.Length..];
                var hashIndex = rest.IndexOf('#');
                var host = (hashIndex >= 0 ? rest[..hashIndex] : rest).Trim('/').ToLowerInvariant();
                var suffix = hashIndex >= 0 ? rest[hashIndex..] : string.Empty;

                var page = host switch
                {
                    "" or "newtab" => "newtab",
                    "history" => "history",
                    "downloads" => "downloads",
                    "settings" => "settings",
                    "passwords" => "passwords",
                    "extensions" => "extensions",
                    _ => "newtab"
                };
                resolvedUrl = $"{schemePrefix}{page}{suffix}";
                return true;
            }

            resolvedUrl = url;
            return false;
        }
    }
}
