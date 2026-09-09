using System;
using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using MozartBrowser.Models;

namespace MozartBrowser.Services.Browser
{
    /// <summary>
    /// Tracks in-flight and completed downloads across all tabs/windows.
    /// Call Attach(coreWebView2) once per WebView2 instance (normal or private)
    /// right after CoreWebView2InitializationCompleted fires.
    /// </summary>
    public class DownloadService
    {
        private readonly SettingsService _settings;

        public ObservableCollection<DownloadItem> Downloads { get; } = new();

        public event EventHandler<DownloadItem>? DownloadCompleted;

        public DownloadService(SettingsService settings)
        {
            _settings = settings;
        }

        public void Attach(CoreWebView2 coreWebView2)
        {
            coreWebView2.DownloadStarting += OnDownloadStarting;
        }

        private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
        {
            var settings = _settings.Current.Downloads;

            if (settings.AskWhereToSaveEachFile)
            {
                // WebView2 does NOT show its own "where to save" picker by
                // default — left alone (ResultFilePath untouched, Handled
                // false) it silently saves straight into its own default
                // Downloads folder with no prompt at all, which is exactly
                // the "images/videos auto-download without asking" bug. To
                // actually ask, the app has to show its own SaveFileDialog
                // here and hand the chosen path back via ResultFilePath.
                // A Deferral is required because showing UI takes this
                // handler outside the single synchronous tick WebView2
                // otherwise expects it to finish in.
                var deferral = e.GetDeferral();

                var suggestedName = Path.GetFileName(e.ResultFilePath);
                var suggestedDirectory = Path.GetDirectoryName(e.ResultFilePath);

                var dialog = new SaveFileDialog
                {
                    FileName = suggestedName,
                    InitialDirectory = !string.IsNullOrEmpty(suggestedDirectory) && Directory.Exists(suggestedDirectory)
                        ? suggestedDirectory
                        : settings.Location,
                    Filter = "All Files (*.*)|*.*"
                };

                // DownloadStarting fires on the UI thread that owns this
                // CoreWebView2 in practice, but routing the dialog through
                // the Dispatcher keeps this safe even if that ever changes.
                App.Current.Dispatcher.Invoke(() =>
                {
                    if (dialog.ShowDialog() == true)
                        e.ResultFilePath = dialog.FileName;
                    else
                        e.Cancel = true;
                });

                // We already handled the "where to save" decision ourselves,
                // so suppress WebView2's own default download flyout too.
                e.Handled = true;
                deferral.Complete();

                if (e.Cancel)
                    return;
            }
            else
            {
                var fileName = Path.GetFileName(e.ResultFilePath);
                Directory.CreateDirectory(settings.Location);
                e.ResultFilePath = Path.Combine(settings.Location, fileName);
            }

            var item = new DownloadItem
            {
                FileName = Path.GetFileName(e.ResultFilePath),
                FilePath = e.ResultFilePath,
                Url = e.DownloadOperation.Uri,
                TotalBytes = (ulong)Math.Max(0, e.DownloadOperation.TotalBytesToReceive ?? 0)
            };

            App.Current.Dispatcher.Invoke(() => Downloads.Insert(0, item));

            e.DownloadOperation.BytesReceivedChanged += (_, _) =>
            {
                App.Current.Dispatcher.Invoke(() =>
                    item.ReceivedBytes = (ulong)e.DownloadOperation.BytesReceived);
            };

            e.DownloadOperation.StateChanged += (_, _) =>
            {
                var state = e.DownloadOperation.State switch
                {
                    CoreWebView2DownloadState.InProgress => DownloadState.InProgress,
                    CoreWebView2DownloadState.Interrupted => DownloadState.Failed,
                    CoreWebView2DownloadState.Completed => DownloadState.Completed,
                    _ => DownloadState.InProgress
                };

                App.Current.Dispatcher.Invoke(() =>
                {
                    item.State = state;
                    if (state == DownloadState.Completed)
                        DownloadCompleted?.Invoke(this, item);
                });
            };
        }
    }
}
