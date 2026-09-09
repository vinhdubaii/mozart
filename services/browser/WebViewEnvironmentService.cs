using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using MozartBrowser.Models;

namespace MozartBrowser.Services.Browser
{
    /// <summary>
    /// Owns the two CoreWebView2Environment instances used by the app:
    ///   - Normal: fixed UserDataFolder under %AppData%, cookies/session persist across restarts.
    ///   - Private: a fresh temp folder created per app session, deleted on exit.
    /// Every WebView2 control in a normal tab must be initialized against
    /// NormalEnvironment, and every private-window tab against PrivateEnvironment,
    /// so that private browsing never touches the persistent profile.
    ///
    /// RUNTIME RESOLUTION: release builds ship a Fixed Version WebView2 Runtime
    /// bundled in a "WebView2" folder next to MozartBrowser.exe. That folder is
    /// produced automatically by the WebView2.Runtime.X64 NuGet package (see
    /// MozartBrowser.csproj) on every build/publish — no manual download step,
    /// no CI script to maintain, just bump that package's version to move to a
    /// newer Chromium build. This avoids depending on the system-wide Evergreen
    /// Runtime, whose dependency resolution can fail on trimmed-down Windows
    /// images (observed: WebView2RuntimeNotFoundException / "Package dependency
    /// criteria could not be resolved" on a Windows 11 build where AppX/framework
    /// packages such as Microsoft.VCLibs had been removed, e.g. by aggressive
    /// debloat tooling).
    /// If that "WebView2" folder isn't present for some reason, we fall back to
    /// browserExecutableFolder: null, which uses whatever Evergreen Runtime is
    /// installed on the machine (useful as a dev-time safety net).
    ///
    /// NOTE: Secure DNS is applied via AdditionalBrowserArguments, which only
    /// take effect when an environment is created. Changing the DNS setting at
    /// runtime therefore requires an app restart to apply — surfaced in
    /// SettingsWindow as a "Restart to apply" prompt rather than attempted live.
    /// </summary>
    public class WebViewEnvironmentService
    {
        private readonly string _appDataFolder;
        private readonly SettingsService _settings;
        private string? _privateTempFolder;

        public CoreWebView2Environment? NormalEnvironment { get; private set; }
        public CoreWebView2Environment? PrivateEnvironment { get; private set; }

        /// <summary>
        /// Folder containing a Fixed Version WebView2 Runtime (msedgewebview2.exe
        /// and friends), copied next to the app's own executable by the
        /// WebView2.Runtime.X64 NuGet package. Null/absent means "use the
        /// system Evergreen Runtime instead" (see class remarks).
        /// </summary>
        public static string? FixedRuntimeFolder
        {
            get
            {
                var candidate = Path.Combine(AppContext.BaseDirectory, "WebView2");
                return Directory.Exists(candidate) ? candidate : null;
            }
        }

        public WebViewEnvironmentService(string appDataFolder, SettingsService settings)
        {
            _appDataFolder = appDataFolder;
            _settings = settings;
        }

        public async Task InitializeNormalEnvironmentAsync()
        {
            var userDataFolder = Path.Combine(_appDataFolder, "WebView2Profile");
            Directory.CreateDirectory(userDataFolder);

            var options = new CoreWebView2EnvironmentOptions(BuildBrowserArguments())
            {
                // Required for CoreWebView2Profile.AddBrowserExtensionAsync and
                // friends to work at all — must be set before the environment is
                // created, can't be toggled afterward. Normal profile only: never
                // set this on PrivateEnvironment below, matching Chromium's
                // default of no extensions in Incognito.
                AreBrowserExtensionsEnabled = true
            };
            NormalEnvironment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: FixedRuntimeFolder,
                userDataFolder: userDataFolder,
                options: options);
        }

        public async Task<CoreWebView2Environment> GetOrCreatePrivateEnvironmentAsync()
        {
            if (PrivateEnvironment != null)
                return PrivateEnvironment;

            _privateTempFolder = Path.Combine(Path.GetTempPath(), "MozartBrowserPrivate_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_privateTempFolder);

            var options = new CoreWebView2EnvironmentOptions(BuildBrowserArguments());
            PrivateEnvironment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: FixedRuntimeFolder,
                userDataFolder: _privateTempFolder,
                options: options);

            return PrivateEnvironment;
        }

        /// <summary>Deletes the temp profile used for private browsing. Call when the last private window closes.</summary>
        public void CleanupPrivateEnvironment()
        {
            PrivateEnvironment = null;

            if (_privateTempFolder != null && Directory.Exists(_privateTempFolder))
            {
                try { Directory.Delete(_privateTempFolder, recursive: true); }
                catch { /* best-effort cleanup; OS will reclaim temp eventually */ }
            }

            _privateTempFolder = null;
        }

        private string BuildBrowserArguments()
        {
            var dns = _settings.Current.SecureDns;
            if (dns.Mode == SecureDnsMode.Off)
                return string.Empty;

            var template = dns.Provider == "custom"
                ? dns.CustomTemplate
                : SecureDnsSettings.BuiltInProviders.GetValueOrDefault(dns.Provider);

            if (string.IsNullOrWhiteSpace(template))
                return string.Empty;

            var mode = dns.Mode == SecureDnsMode.Automatic ? "automatic" : "secure";

            return $"--enable-features=DnsOverHttps --dns-over-https-mode={mode} --dns-over-https-templates={template}";
        }
    }
}
