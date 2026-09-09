using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MozartBrowser.Chrome;
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

        /// <summary>
        /// Registers every action Mozart's internal HTML pages (New Tab,
        /// History, Downloads, Settings) can call through App.Bridge. Real
        /// history/downloads/settings actions are added as each of those
        /// pages gets built; for now this just proves the request/response
        /// round trip works end to end.
        /// </summary>
        private static void RegisterBridgeHandlers()
        {
            Bridge.RegisterHandler("system.ping", _ => Task.FromResult<object?>(new
            {
                message = "pong",
                time = DateTime.UtcNow,
                version = Updates.CurrentVersion
            }));
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
