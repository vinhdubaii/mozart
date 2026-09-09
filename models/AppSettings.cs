using System;
using System.Collections.Generic;
using System.IO;

namespace MozartBrowser.Models
{
    public enum SecureDnsMode
    {
        Off,
        Automatic,
        Custom
    }

    public enum AppTheme
    {
        System,
        Light,
        Dark
    }

    public enum NewTabBackgroundType
    {
        None,
        Color,
        Preset,
        Custom
    }

    /// <summary>Chromium-style "On startup" behavior, chosen in General settings.</summary>
    public enum StartupMode
    {
        /// <summary>Always open a single New Tab page.</summary>
        NewTab,

        /// <summary>Reopen every tab that was open when the browser last closed.</summary>
        Continue,

        /// <summary>Open exactly the URLs in StartupSettings.Pages.</summary>
        SpecificPages,

        /// <summary>Reopen the last session's tabs, plus one extra New Tab page.</summary>
        ContinueAndNewTab
    }

    public class SecureDnsSettings
    {
        public SecureDnsMode Mode { get; set; } = SecureDnsMode.Off;

        /// <summary>One of: cloudflare, google, quad9, cleanbrowsing, custom.</summary>
        public string Provider { get; set; } = "cloudflare";

        /// <summary>Only used when Provider == "custom". Must be a DoH template URL.</summary>
        public string? CustomTemplate { get; set; }

        public static readonly Dictionary<string, string> BuiltInProviders = new()
        {
            ["cloudflare"] = "https://dns.cloudflare.com/dns-query",
            ["google"] = "https://dns.google/dns-query",
            ["quad9"] = "https://dns.quad9.net/dns-query",
            ["cleanbrowsing"] = "https://doh.cleanbrowsing.org/doh/family-filter/",
        };
    }

    public class DownloadSettings
    {
        public string Location { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        public bool AskWhereToSaveEachFile { get; set; } = false;
        public bool ShowDownloadsWhenDone { get; set; } = true;
    }

    public class NewTabBackgroundSettings
    {
        public NewTabBackgroundType Type { get; set; } = NewTabBackgroundType.None;

        /// <summary>Hex color, preset filename, or path under Backgrounds/ depending on Type.</summary>
        public string? Value { get; set; }

        public double OverlayOpacity { get; set; } = 0.3;
    }

    /// <summary>New Tab Page grid layout: which sections show, and column count.</summary>
    public class NewTabLayoutSettings
    {
        public bool ShowPinnedSites { get; set; } = true;
        public bool ShowRecentlyVisited { get; set; } = true;

        /// <summary>2–8, clamped again defensively in NewTabPage.ApplyColumnWidth.</summary>
        public int Columns { get; set; } = 4;
    }

    public class WindowSettings
    {
        public double Width { get; set; } = 1280;
        public double Height { get; set; } = 800;
        public bool IsMaximized { get; set; } = false;
    }

    /// <summary>"On startup" section of General settings, Chromium-style.</summary>
    public class StartupSettings
    {
        public StartupMode Mode { get; set; } = StartupMode.NewTab;

        /// <summary>Only used when Mode == SpecificPages. One URL per entry, in open order.</summary>
        public List<string> Pages { get; set; } = new();
    }

    /// <summary>
    /// Which categories of data an operation (manual "Delete browsing data" or
    /// automatic "Clear on close") should wipe. Maps 1:1 onto WebView2's
    /// CoreWebView2BrowsingDataKinds flags — see Services/BrowsingDataService.
    /// </summary>
    public class ClearBrowsingDataTypes
    {
        public bool History { get; set; } = true;
        public bool Cookies { get; set; } = true;
        public bool Cache { get; set; } = true;
        public bool DownloadHistory { get; set; } = false;
        public bool AutofillData { get; set; } = false;
        public bool Passwords { get; set; } = false;
    }

    /// <summary>"Clear browsing data when closing Mozart" section of Privacy &amp; Security.</summary>
    public class ClearOnCloseSettings
    {
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Defaults mirror Cromite's own "clear on exit" defaults: cookies and
        /// cache go, history/passwords/autofill are left alone unless the user
        /// opts in explicitly — clearing those every session is more likely to
        /// annoy someone into disabling the feature than to help them.
        /// </summary>
        public ClearBrowsingDataTypes Types { get; set; } = new()
        {
            History = false,
            Cookies = true,
            Cache = true,
            DownloadHistory = false,
            AutofillData = false,
            Passwords = false
        };
    }

    /// <summary>
    /// "Passwords and autofill" section of Privacy &amp; Security. Controls
    /// Mozart's own password vault (PasswordVaultService/PasswordCaptureService)
    /// — WebView2/Chromium's native autosave is always forced off (see
    /// MainWindow.CreateNewTabAsync) since it has no public API to list, view,
    /// or delete individual saved entries, which is exactly what this app's
    /// own vault + Password Manager UI now provide instead.
    /// </summary>
    public class PasswordManagerSettings
    {
        /// <summary>Whether a detected login shows the "Save password?" prompt.</summary>
        public bool OfferToSavePasswords { get; set; } = true;

        /// <summary>Whether a saved match shows the click-to-fill key icon in password fields.</summary>
        public bool AutofillEnabled { get; set; } = true;
    }

    /// <summary>One draggable color stop on the Custom Themes gradient canvas.</summary>
    public class GradientColorStop
    {
        /// <summary>0.0–1.0, relative to canvas width.</summary>
        public double X { get; set; }

        /// <summary>0.0–1.0, relative to canvas height.</summary>
        public double Y { get; set; }

        public string Hex { get; set; } = "#000000";
    }

    /// <summary>
    /// Zen Browser-inspired custom toolbar/tab-strip gradient. Default OFF.
    /// Max 3 stops — see GradientThemeService for how 1/2/3 stops render.
    /// </summary>
    public class CustomThemeSettings
    {
        public bool IsEnabled { get; set; } = false;

        public List<GradientColorStop> ColorStops { get; set; } = new();
    }

    /// <summary>
    /// Root of settings.json. Persisted via SettingsService. Keep this a plain
    /// data object (no logic) so it serializes cleanly with System.Text.Json.
    /// </summary>
    public class AppSettings
    {
        // General
        public string HomepageUrl { get; set; } = "about:newtab";
        public string DefaultSearchEngineName { get; set; } = "Google";
        public List<SearchEngine> SearchEngines { get; set; } = new(SearchEngine.Defaults);
        public StartupSettings Startup { get; set; } = new();

        // Appearance
        public bool ShowBookmarkBar { get; set; } = false;
        public AppTheme Theme { get; set; } = AppTheme.System;
        public NewTabBackgroundSettings NewTabBackground { get; set; } = new();
        public NewTabLayoutSettings NewTabLayout { get; set; } = new();
        public CustomThemeSettings CustomTheme { get; set; } = new();

        // Privacy & Security
        public SecureDnsSettings SecureDns { get; set; } = new();
        public PasswordManagerSettings PasswordManager { get; set; } = new();
        public ClearOnCloseSettings ClearOnClose { get; set; } = new();

        // Downloads
        public DownloadSettings Downloads { get; set; } = new();

        // Window state (restored on next launch)
        public WindowSettings Window { get; set; } = new();

        /// <summary>
        /// Extension IDs the user has pinned to the toolbar, in pin order
        /// (most-recently-pinned last, rendered left-to-right nearest the
        /// puzzle icon). Everything else about an extension (name, icon,
        /// enabled state) is derived live from ExtensionService at render
        /// time, not duplicated into settings.
        /// </summary>
        public List<string> PinnedExtensionIds { get; set; } = new();

        /// <summary>
        /// URLs of every normal-window tab open at last shutdown (in order),
        /// saved by MainWindow just before closing, consumed by
        /// StartupMode.Continue / ContinueAndNewTab on next launch. "about:newtab"
        /// entries represent a tab that was showing the New Tab page. Never
        /// touched by private windows.
        /// </summary>
        public List<string> LastSessionTabs { get; set; } = new();
    }
}
