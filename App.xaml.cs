using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MozartBrowser.Chrome;
using MozartBrowser.Models;
using MozartBrowser.Services.Data;
using MozartBrowser.Services.Browser;
using MozartBrowser.Services.Theme;
using MozartBrowser.Services.Update;

namespace MozartBrowser
{
    /// <summary>
    /// Application entry point. Responsible for creating the shared, app-lifetime
    /// singletons (settings, history, bookmarks, WebView2 environments) *before*
    /// any window is shown, since MainWindow and PrivateWindow both depend on them.
    /// </summary>
    public partial class App : Application
    {
        public static SettingsService Settings { get; private set; } = null!;
        public static HistoryService History { get; private set; } = null!;
        public static BookmarkService Bookmarks { get; private set; } = null!;
        public static PasswordVaultService Passwords { get; private set; } = null!;
        public static DownloadService Downloads { get; private set; } = null!;
        public static SearchEngineService SearchEngines { get; private set; } = null!;
        public static WebViewEnvironmentService WebViewEnvironments { get; private set; } = null!;
        public static UpdateService Updates { get; private set; } = null!;
        public static ExtensionService Extensions { get; private set; } = null!;

        /// <summary>
        /// The single bridge every tab's CoreWebView2 attaches to, connecting
        /// Mozart's internal HTML pages (New Tab, History, Downloads, Settings)
        /// to the services above. See InternalPageBridge for the protocol.
        /// </summary>
        public static InternalPageBridge Bridge { get; private set; } = null!;

        public static string AppDataFolder { get; private set; } = string.Empty;

        public App()
        {
            // App-wide safety net: without these, ANY unhandled exception anywhere
            // (a background Task, an "async void" event handler from WebView2
            // events, etc.) kills the whole process with an opaque native error
            // dialog (0xe0434352) and no indication of what actually went wrong.
            // These handlers turn that into a readable message box instead, and
            // for DispatcherUnhandledException (the common case — anything that
            // eventually resumes on the UI thread) they let the app keep running.
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(
                $"Something went wrong, but Mozart Browser will keep running.\n\n{e.Exception}",
                "Mozart Browser - Unexpected error", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }

        private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            MessageBox.Show(
                $"Mozart Browser hit a fatal error and needs to close.\n\n{e.ExceptionObject}",
                "Mozart Browser - Fatal error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            e.SetObserved();
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // App.xaml intentionally has no StartupUri: with StartupUri, WPF creates
            // and shows MainWindow as soon as this method first yields (at the first
            // await below), while Settings/History/etc. are still null — MainWindow's
            // constructor reads App.Settings.Current immediately and would crash with
            // a NullReferenceException. Instead we create MainWindow ourselves, after
            // every async init step below has actually finished.
            try
            {
                AppDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Mozart Browser");
                Directory.CreateDirectory(AppDataFolder);

                Settings = new SettingsService(Path.Combine(AppDataFolder, "settings.json"));
                await Settings.LoadAsync();

                ThemeService.Apply(Settings.Current.Theme);

                History = new HistoryService(Path.Combine(AppDataFolder, "browser.db"));
                await History.InitializeAsync();

                Bookmarks = new BookmarkService(Path.Combine(AppDataFolder, "browser.db"));
                await Bookmarks.InitializeAsync();

                Passwords = new PasswordVaultService(Path.Combine(AppDataFolder, "browser.db"));
                await Passwords.InitializeAsync();

                SearchEngines = new SearchEngineService(Settings);

                WebViewEnvironments = new WebViewEnvironmentService(AppDataFolder, Settings);
                await WebViewEnvironments.InitializeNormalEnvironmentAsync();

                Extensions = new ExtensionService(AppDataFolder);

                Downloads = new DownloadService(Settings);

                Updates = new UpdateService("vinhdubaii", "mozart-browser");

                // Fire-and-forget background update check; never blocks startup.
                _ = Updates.CheckForUpdateAsync();

                Bridge = new InternalPageBridge();
                RegisterBridgeHandlers();

                var mainWindow = new MainWindow();
                MainWindow = mainWindow;
                mainWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Mozart Browser failed to start.\n\n{ex}",
                    "Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(-1);
            }
        }

        /// <summary>Case-insensitive so payload.save can hand back camelCase JSON keys and land on this app's PascalCase AppSettings properties.</summary>
        private static readonly JsonSerializerOptions BridgeDeserializeOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Registers every action Mozart's internal HTML pages (New Tab,
        /// History, Downloads, Settings, Extensions) can call through
        /// App.Bridge. Window-specific actions that need a live MainWindow
        /// (e.g. anchoring a native dialog) are registered separately by
        /// MainWindow itself — see MainWindow.RegisterWindowBridgeHandlers.
        /// </summary>
        private static void RegisterBridgeHandlers()
        {
            Bridge.RegisterHandler("system.ping", _ => Task.FromResult<object?>(new
            {
                message = "pong",
                time = DateTime.UtcNow,
                version = Updates.CurrentVersion.ToString()
            }));

            // ---- History ----
            Bridge.RegisterHandler("history.getRecent", async payload =>
            {
                var limit = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("limit", out var l) ? l.GetInt32() : 200;
                return (object?)await History.GetRecentAsync(limit);
            });
            Bridge.RegisterHandler("history.search", async payload =>
            {
                var query = payload.GetProperty("query").GetString() ?? string.Empty;
                var limit = payload.TryGetProperty("limit", out var l) ? l.GetInt32() : 200;
                return (object?)await History.SearchAsync(query, limit);
            });
            Bridge.RegisterHandler("history.delete", async payload =>
            {
                await History.DeleteAsync(payload.GetProperty("id").GetInt32());
                return null;
            });
            Bridge.RegisterHandler("history.clear", async _ =>
            {
                await History.ClearAsync();
                return null;
            });

            // ---- Downloads ----
            Bridge.RegisterHandler("downloads.getAll", _ =>
                Task.FromResult<object?>(Downloads.Downloads.ToList()));
            Bridge.RegisterHandler("downloads.open", payload =>
            {
                var item = Downloads.Downloads.FirstOrDefault(d => d.Id == payload.GetProperty("id").GetString());
                if (item != null && File.Exists(item.FilePath))
                    Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
                return Task.FromResult<object?>(null);
            });
            Bridge.RegisterHandler("downloads.showInFolder", payload =>
            {
                var item = Downloads.Downloads.FirstOrDefault(d => d.Id == payload.GetProperty("id").GetString());
                if (item != null && File.Exists(item.FilePath))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FilePath}\"") { UseShellExecute = true });
                return Task.FromResult<object?>(null);
            });
            Bridge.RegisterHandler("downloads.remove", payload =>
            {
                Downloads.Remove(payload.GetProperty("id").GetString() ?? string.Empty);
                return Task.FromResult<object?>(null);
            });
            Bridge.RegisterHandler("downloads.clearCompleted", _ =>
            {
                Downloads.ClearCompleted();
                return Task.FromResult<object?>(null);
            });

            // ---- Extensions ----
            Bridge.RegisterHandler("extensions.getAll", async _ => (object?)await Extensions.GetInstalledAsync());
            Bridge.RegisterHandler("extensions.setEnabled", async payload =>
            {
                await Extensions.SetEnabledAsync(
                    payload.GetProperty("id").GetString() ?? string.Empty,
                    payload.GetProperty("enabled").GetBoolean());
                return null;
            });
            Bridge.RegisterHandler("extensions.remove", async payload =>
            {
                await Extensions.RemoveAsync(payload.GetProperty("id").GetString() ?? string.Empty);
                return null;
            });
            Bridge.RegisterHandler("extensions.loadUnpacked", async _ =>
            {
                string? folder = null;
                Current.Dispatcher.Invoke(() =>
                {
                    using var dialog = new System.Windows.Forms.FolderBrowserDialog
                    {
                        Description = "Select the unpacked extension's folder (containing manifest.json)"
                    };
                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                        folder = dialog.SelectedPath;
                });
                // User cancelled the picker — not an error, just nothing to install.
                if (folder == null) return null;
                return (object?)await Extensions.LoadUnpackedAsync(folder);
            });

            // ---- Settings ----
            Bridge.RegisterHandler("settings.get", _ => Task.FromResult<object?>(Settings.Current));
            Bridge.RegisterHandler("settings.save", async payload =>
            {
                var updated = JsonSerializer.Deserialize<AppSettings>(payload.GetRawText(), BridgeDeserializeOptions)
                    ?? throw new InvalidOperationException("settings.save received an empty/invalid settings object.");
                await Settings.ReplaceCurrentAsync(updated);

                // Previously only App startup ever called ThemeService.Apply, so a
                // theme change made in settings.html sat persisted-but-unapplied
                // until the next launch. Apply it to the native WPF chrome right
                // now, and push the resolved value to every open internal HTML
                // page (they can't see AppTheme.System resolve on their own).
                var isDark = ThemeService.ResolveIsDark(updated.Theme);
                Current.Dispatcher.Invoke(() => ThemeService.Apply(updated.Theme));
                Bridge.BroadcastEvent("theme.changed", new { isDark });

                return null;
            });
            Bridge.RegisterHandler("system.getTheme", _ =>
                Task.FromResult<object?>(new { isDark = ThemeService.ResolveIsDark(Settings.Current.Theme) }));
            Bridge.RegisterHandler("settings.pickDownloadsFolder", _ =>
            {
                string? folder = null;
                Current.Dispatcher.Invoke(() =>
                {
                    using var dialog = new System.Windows.Forms.FolderBrowserDialog
                    {
                        Description = "Choose where downloads are saved",
                        SelectedPath = Settings.Current.Downloads.Location
                    };
                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                        folder = dialog.SelectedPath;
                });
                return Task.FromResult<object?>(folder == null ? null : new { path = folder });
            });
            Bridge.RegisterHandler("settings.checkDefaultBrowser", _ =>
                Task.FromResult<object?>(new { isDefault = DefaultBrowserService.IsDefaultBrowser() }));
            Bridge.RegisterHandler("settings.openDefaultBrowserSettings", _ =>
            {
                // Windows doesn't allow programmatically setting the default
                // browser (deliberately, to stop silent hijacking) — this is
                // the same "open the OS picker" fallback SettingsWindow used.
                DefaultBrowserService.OpenDefaultAppsSettings();
                return Task.FromResult<object?>(null);
            });
            Bridge.RegisterHandler("settings.checkForUpdate", async _ => (object?)await Updates.CheckForUpdateAsync());

            // ---- Passwords ----
            Bridge.RegisterHandler("passwords.getAll", async _ =>
            {
                var all = await Passwords.GetAllAsync();
                // Never send encrypted_password blobs to the page at all -- only
                // passwords.reveal (below) touches ciphertext, and only for the
                // one row the user explicitly clicked "Show" on.
                return (object?)all.Select(p => new
                {
                    id = p.Id,
                    domain = p.Domain,
                    username = p.Username,
                    updatedAt = p.UpdatedAt
                }).ToList();
            });
            Bridge.RegisterHandler("passwords.reveal", async payload =>
            {
                var id = payload.GetProperty("id").GetInt32();
                var entry = await Passwords.GetByIdAsync(id)
                    ?? throw new InvalidOperationException("That saved password no longer exists.");
                return (object?)new { password = PasswordVaultService.Decrypt(entry.EncryptedPassword) };
            });
            Bridge.RegisterHandler("passwords.delete", async payload =>
            {
                await Passwords.DeleteAsync(payload.GetProperty("id").GetInt32());
                return null;
            });
        }

        /// <summary>
        /// Settings are already saved properly (via await) in MainWindow_Closing
        /// before Close() is called, so there's nothing left to do here. This is
        /// intentionally empty rather than re-saving — a blocking
        /// .GetAwaiter().GetResult() call here previously deadlocked the process
        /// on exit (the awaited file write wants to resume on this same UI
        /// thread, which is busy blocking on it).
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            base.OnExit(e);
        }
    }
}
