using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using MozartBrowser.Models;

namespace MozartBrowser.Services.Browser
{
    /// <summary>
    /// Implements every action a ContextMenuEntry can be wired to (see
    /// guide section 2.5's group tables). ContextMenuBuilder only decides
    /// *which* items appear; this class decides what actually happens when
    /// one is clicked.
    ///
    /// Every public method here is a thin synchronous wrapper around an
    /// ...Async implementation, routed through RunSafe. That's not just
    /// tidiness - before this, every action was bare `async void` wired
    /// directly to MenuItem.Click, so any failure (a JS exception, a locked
    /// output file, an unexpected null) had nowhere to go but the app's
    /// global unhandled-exception handler, which is what put the raw
    /// stack-trace dialog in front of the user for the "Copy image" crash.
    /// RunSafe catches at the one place all of these funnel through, so a
    /// failed menu action degrades to a small message box instead.
    /// </summary>
    public static class ContextMenuActionHandler
    {
        // ============================= Link group =============================

        public static void OpenLinkInNewWindow(ContextMenuHost host, string linkUri) =>
            RunSafe(() =>
            {
                host.OpenInNewTab(linkUri);
                return Task.CompletedTask;
            }, host, "Open link in new window");

        public static void CopyLink(ContextMenuHost host, string linkUri) =>
            RunSafe(() =>
            {
                Clipboard.SetText(linkUri);
                return Task.CompletedTask;
            }, host, "Copy link");

        public static void SaveLinkAs(ContextMenuHost host, string linkUri) =>
            RunSafe(() => TriggerBrowserDownloadAsync(host.CoreWebView2, linkUri), host, "Save link as");

        // ============================= Image group =============================

        public static void SaveImageAs(ContextMenuHost host, string sourceUri) =>
            RunSafe(() => TriggerBrowserDownloadAsync(host.CoreWebView2, sourceUri), host, "Save image as");

        public static void CopyImageLink(ContextMenuHost host, string sourceUri) =>
            RunSafe(() =>
            {
                Clipboard.SetText(sourceUri);
                return Task.CompletedTask;
            }, host, "Copy image link");

        public static void CopyImage(ContextMenuHost host, Point targetLocation) =>
            RunSafe(() => CopyImageToClipboardAsync(host, targetLocation), host, "Copy image");

        // ============================= Media group =============================

        public static void ToggleLoop(ContextMenuHost host, Point targetLocation, bool newLoopState) =>
            RunSafe(async () =>
            {
                var (x, y) = ToCssPixels(host, targetLocation);
                var js = $$"""
                    (function() {
                        var el = document.elementFromPoint({{x}}, {{y}});
                        while (el && el.tagName !== 'VIDEO' && el.tagName !== 'AUDIO') el = el.parentElement;
                        if (el) el.loop = {{(newLoopState ? "true" : "false")}};
                    })();
                    """;
                await host.CoreWebView2.ExecuteScriptAsync(js);
            }, host, "Loop");

        public static void ShowAllControls(ContextMenuHost host, Point targetLocation) =>
            RunSafe(async () =>
            {
                var (x, y) = ToCssPixels(host, targetLocation);
                var js = $$"""
                    (function() {
                        var el = document.elementFromPoint({{x}}, {{y}});
                        while (el && el.tagName !== 'VIDEO' && el.tagName !== 'AUDIO') el = el.parentElement;
                        if (el) el.controls = true;
                    })();
                    """;
                await host.CoreWebView2.ExecuteScriptAsync(js);
            }, host, "Show all controls");

        public static void SaveVideoAs(ContextMenuHost host, string sourceUri) =>
            RunSafe(() => TriggerBrowserDownloadAsync(host.CoreWebView2, sourceUri), host, "Save video as");

        public static void SaveVideoFrameAs(ContextMenuHost host, Point targetLocation) =>
            RunSafe(() => CaptureVideoFrameAsync(host, targetLocation, saveToDisk: true), host, "Save video frame as");

        public static void CopyVideoFrame(ContextMenuHost host, Point targetLocation) =>
            RunSafe(() => CaptureVideoFrameAsync(host, targetLocation, saveToDisk: false), host, "Copy video frame");

        public static void TogglePictureInPicture(ContextMenuHost host, Point targetLocation) =>
            RunSafe(async () =>
            {
                var (x, y) = ToCssPixels(host, targetLocation);
                var js = $$"""
                    (function() {
                        var el = document.elementFromPoint({{x}}, {{y}});
                        while (el && el.tagName !== 'VIDEO') el = el.parentElement;
                        if (el) { el.requestPictureInPicture().catch(function() {}); }
                    })();
                    """;
                await host.CoreWebView2.ExecuteScriptAsync(js);
            }, host, "Picture in picture");

        // ============================= Selection / Editable group =============================

        public static void CopySelection(ContextMenuHost host) =>
            RunSafe(() => host.CoreWebView2.ExecuteScriptAsync("document.execCommand('copy')"), host, "Copy");

        public static void SearchWith(ContextMenuHost host, string selectionText) =>
            RunSafe(() =>
            {
                var engine = App.SearchEngines.DefaultEngine;
                var url = engine.UrlTemplate.Replace("%s", System.Net.WebUtility.UrlEncode(selectionText));
                host.OpenInNewTab(url);
                return Task.CompletedTask;
            }, host, "Search with");

        public static void Cut(ContextMenuHost host) =>
            RunSafe(() => host.CoreWebView2.ExecuteScriptAsync("document.execCommand('cut')"), host, "Cut");

        public static void Paste(ContextMenuHost host) =>
            RunSafe(async () =>
            {
                // document.execCommand('paste') reads the system clipboard
                // directly from page script, and Chromium has blocked that
                // for ordinary pages for years now (security: a page
                // shouldn't be able to silently read whatever the user last
                // copied) - it just no-ops, which is why Paste did nothing
                // at all before this fix. The clipboard read happens here in
                // the host app instead (Clipboard.GetText(), which *is*
                // allowed - this is a native WPF app, not page script), and
                // only the resulting plain text is handed to the page via
                // execCommand('insertText', ...), which inserts into
                // whatever's focused without itself touching the clipboard.
                //
                // Scope: text only for now. Pasting an image from the
                // clipboard would need a different path (e.g. writing it
                // into a hidden <input type="file"> via a DataTransfer,
                // which has its own browser-support caveats) - not
                // implemented yet.
                if (!Clipboard.ContainsText())
                    return;

                var text = Clipboard.GetText();
                var escapedText = JsonSerializer.Serialize(text);
                var js = $$"""
                    document.execCommand('insertText', false, {{escapedText}});
                    """;
                await host.CoreWebView2.ExecuteScriptAsync(js);
            }, host, "Paste");

        public static void SelectAll(ContextMenuHost host) =>
            RunSafe(() => host.CoreWebView2.ExecuteScriptAsync("document.execCommand('selectAll')"), host, "Select all");

        // ============================= Page group =============================

        public static void Back(ContextMenuHost host) =>
            RunSafe(() =>
            {
                host.CoreWebView2.GoBack();
                return Task.CompletedTask;
            }, host, "Back");

        public static void Forward(ContextMenuHost host) =>
            RunSafe(() =>
            {
                host.CoreWebView2.GoForward();
                return Task.CompletedTask;
            }, host, "Forward");

        public static void Refresh(ContextMenuHost host) =>
            RunSafe(() =>
            {
                host.CoreWebView2.Reload();
                return Task.CompletedTask;
            }, host, "Refresh");

        public static void SaveAs(ContextMenuHost host) =>
            RunSafe(async () =>
            {
                var dialog = new SaveFileDialog
                {
                    FileName = SuggestFileName(host.CoreWebView2.DocumentTitle, "mhtml"),
                    Filter = "Web Page, single file (*.mhtml)|*.mhtml"
                };
                if (dialog.ShowDialog(host.OwnerWindow) != true)
                    return;

                // WebView2 doesn't expose page-save (HTML/MHTML) as a
                // first-class API in this SDK, but the DevTools Protocol's
                // Page.captureSnapshot does exactly what a real browser's
                // "Save page as > Webpage, single file" does: one
                // self-contained .mhtml with every sub-resource inlined.
                // Not yet verified on a real build whether this specific CDP
                // method is on WebView2's allowed list for
                // CallDevToolsProtocolMethodAsync - if it throws, that now
                // surfaces as a normal "Save as failed: ..." message instead
                // of crashing, at least.
                var resultJson = await host.CoreWebView2.CallDevToolsProtocolMethodAsync(
                    "Page.captureSnapshot", "{\"format\":\"mhtml\"}");
                using var doc = JsonDocument.Parse(resultJson);
                var mhtml = doc.RootElement.GetProperty("data").GetString() ?? string.Empty;
                await File.WriteAllTextAsync(dialog.FileName, mhtml);
            }, host, "Save as");

        public static void Print(ContextMenuHost host) =>
            RunSafe(() =>
            {
                host.CoreWebView2.ShowPrintUI();
                return Task.CompletedTask;
            }, host, "Print");

        public static void ViewPageSource(ContextMenuHost host) =>
            RunSafe(async () =>
            {
                var resultJson = await host.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML");
                var html = JsonSerializer.Deserialize<string>(resultJson) ?? string.Empty;

                // TODO: a real syntax-highlighted source-viewer window/tab is
                // a separate, bigger piece of UI (guide 2.5) - not blocking
                // the rest of the context menu, so this is a minimal
                // stand-in until that view exists.
                Clipboard.SetText(html);
                MessageBox.Show(host.OwnerWindow,
                    "Page source copied to clipboard. A dedicated source viewer isn't built yet.",
                    "View page source", MessageBoxButton.OK, MessageBoxImage.Information);
            }, host, "View page source");

        // ============================= Extension group =============================

        public static void AddToMozart(ContextMenuHost host, string pageUri) =>
            RunSafe(async () =>
            {
                var extensionId = ExtractExtensionId(pageUri);
                if (extensionId == null)
                {
                    MessageBox.Show(host.OwnerWindow,
                        "Couldn't determine the extension ID from this page.",
                        "Add to Mozart", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var store = pageUri.Contains("chromewebstore.google.com", StringComparison.OrdinalIgnoreCase)
                    ? ExtensionStoreKind.ChromeWebStore
                    : ExtensionStoreKind.EdgeAddons;

                var installed = await App.Extensions.InstallFromStoreAsync(extensionId, store);

                MessageBox.Show(host.OwnerWindow,
                    $"\"{installed.Name}\" was installed.",
                    "Add to Mozart", MessageBoxButton.OK, MessageBoxImage.Information);
            }, host, "Add to Mozart");

        // Same 32-char-id capture group already proven out in
        // ContextMenuBuilder's ChromeWebStorePattern/EdgeAddonsPattern -
        // duplicated here rather than re-deriving the ID logic from scratch.
        private static string? ExtractExtensionId(string pageUri)
        {
            var match = System.Text.RegularExpressions.Regex.Match(pageUri,
                @"/detail/[^/]+/([a-zA-Z0-9]{32})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        // ============================= Trailing group =============================

        public static void Inspect(ContextMenuHost host) =>
            RunSafe(() =>
            {
                host.CoreWebView2.OpenDevToolsWindow();
                return Task.CompletedTask;
            }, host, "Inspect");

        // ============================= Shared helpers =============================

        /// <summary>
        /// Every public action above funnels through here instead of being
        /// bare `async void`. If the action throws, the user sees a small
        /// "X couldn't complete" message box scoped to that one action,
        /// rather than the app's global unhandled-exception dialog with a
        /// raw .NET stack trace (which is what happened for the "Copy
        /// image" Promise bug before this fix).
        /// </summary>
        private static async void RunSafe(Func<Task> action, ContextMenuHost host, string actionTitle)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                MessageBox.Show(host.OwnerWindow,
                    $"{actionTitle} couldn't complete:\n{ex.Message}",
                    actionTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// args.Location (from CoreWebView2ContextMenuRequestedEventArgs) is
        /// reported relative to the WebView2 control's own bounds. Page-side
        /// JS coordinate APIs like elementFromPoint work in CSS pixels of the
        /// *visual* viewport at the page's current zoom, so the app's own
        /// ZoomFactor has to be divided out here. This hasn't been verified
        /// against a running app (no Windows/.NET runtime in this dev
        /// environment) - worth double-checking against a real right-click
        /// once this builds, especially at non-100% Windows display scaling.
        /// </summary>
        private static (double x, double y) ToCssPixels(ContextMenuHost host, Point location)
        {
            var zoom = host.WebView.ZoomFactor;
            if (zoom <= 0) zoom = 1.0;
            return (location.X / zoom, location.Y / zoom);
        }

        private static async Task TriggerBrowserDownloadAsync(CoreWebView2 coreWebView2, string url)
        {
            // WebView2 has no direct "download this URL" API in this SDK, so
            // this recreates what a real browser's "Save link/image/video as"
            // does under the hood: synthesize a temporary <a download>
            // element and .click() it from the page's own script context.
            // That's a real download request made *as the page*, so it
            // carries cookies/referrer correctly (unlike a bare out-of-band
            // HTTP fetch would) - it fires CoreWebView2.DownloadStarting,
            // which is already wired up app-wide via App.Downloads.Attach
            // (see DownloadService.cs / MainWindow.CreateNewTabAsync), so
            // the normal Save-As dialog / download list just works with no
            // extra plumbing here. This script is plain and synchronous (no
            // Promise), so it isn't subject to the ExecuteScriptAsync +
            // Promise bug described on CaptureImageAsDataUrlAsync below.
            var escapedUrl = JsonSerializer.Serialize(url);
            var js = $$"""
                (function() {
                    var a = document.createElement('a');
                    a.href = {{escapedUrl}};
                    a.download = '';
                    document.body.appendChild(a);
                    a.click();
                    a.remove();
                })();
                """;
            await coreWebView2.ExecuteScriptAsync(js);
        }

        private static async Task CopyImageToClipboardAsync(ContextMenuHost host, Point targetLocation)
        {
            var dataUrl = await CaptureImageAsDataUrlAsync(host, targetLocation);
            if (string.IsNullOrEmpty(dataUrl))
            {
                ShowCanvasTaintedMessage(host.OwnerWindow, "Copy image");
                return;
            }

            SetClipboardImageFromDataUrl(dataUrl);
        }

        /// <summary>
        /// Fixed 2026-08-22: this used to load the image a *second* time
        /// via `new Image(); img.src = sourceUri` and wait for `img.onload`
        /// inside a `new Promise(...)`. CoreWebView2.ExecuteScriptAsync does
        /// not reliably await a returned Promise - in practice it can just
        /// JSON-serialize the Promise object itself, which has no own
        /// enumerable properties and so serializes to `{}`. Deserializing
        /// that `{}` as a C# string threw
        /// "Cannot get the value of a token type 'StartObject' as a string",
        /// which is the crash the app showed on every "Copy image" click.
        /// This is a long-standing, still-open WebView2 limitation
        /// (MicrosoftEdge/WebView2Feedback#950, #2295) - it isn't fixed by
        /// the runtime version bump.
        ///
        /// The fix here sidesteps the problem rather than working around
        /// ExecuteScriptAsync's Promise handling: the target is already an
        /// &lt;img&gt; sitting in the page (that's what right-clicking Kind
        /// == Image means), already loaded, so there's no need to fetch it
        /// again at all. elementFromPoint grabs that live element and draws
        /// it straight to a canvas synchronously - no Promise, no second
        /// network request, no async gap for ExecuteScriptAsync to mishandle.
        /// </summary>
        private static async Task<string?> CaptureImageAsDataUrlAsync(ContextMenuHost host, Point targetLocation)
        {
            var (x, y) = ToCssPixels(host, targetLocation);
            var js = $$"""
                (function() {
                    var el = document.elementFromPoint({{x}}, {{y}});
                    while (el && el.tagName !== 'IMG') el = el.parentElement;
                    if (!el) return null;
                    var canvas = document.createElement('canvas');
                    canvas.width = el.naturalWidth || el.width;
                    canvas.height = el.naturalHeight || el.height;
                    canvas.getContext('2d').drawImage(el, 0, 0);
                    try {
                        return canvas.toDataURL('image/png');
                    } catch (e) {
                        return null;
                    }
                })();
                """;
            var resultJson = await host.CoreWebView2.ExecuteScriptAsync(js);
            return JsonSerializer.Deserialize<string>(resultJson);
        }

        private static async Task CaptureVideoFrameAsync(ContextMenuHost host, Point targetLocation, bool saveToDisk)
        {
            var (x, y) = ToCssPixels(host, targetLocation);
            var js = $$"""
                (function() {
                    var el = document.elementFromPoint({{x}}, {{y}});
                    while (el && el.tagName !== 'VIDEO') el = el.parentElement;
                    if (!el) return null;
                    var canvas = document.createElement('canvas');
                    canvas.width = el.videoWidth;
                    canvas.height = el.videoHeight;
                    canvas.getContext('2d').drawImage(el, 0, 0);
                    try {
                        return canvas.toDataURL('image/png');
                    } catch (e) {
                        return null;
                    }
                })();
                """;
            var resultJson = await host.CoreWebView2.ExecuteScriptAsync(js);
            var dataUrl = JsonSerializer.Deserialize<string>(resultJson);

            if (string.IsNullOrEmpty(dataUrl))
            {
                ShowCanvasTaintedMessage(host.OwnerWindow, saveToDisk ? "Save video frame as" : "Copy video frame");
                return;
            }

            if (saveToDisk)
            {
                var dialog = new SaveFileDialog { FileName = "frame.png", Filter = "PNG image (*.png)|*.png" };
                if (dialog.ShowDialog(host.OwnerWindow) != true)
                    return;

                var base64 = dataUrl[(dataUrl.IndexOf(',') + 1)..];
                await File.WriteAllBytesAsync(dialog.FileName, Convert.FromBase64String(base64));
            }
            else
            {
                SetClipboardImageFromDataUrl(dataUrl);
            }
        }

        private static void SetClipboardImageFromDataUrl(string dataUrl)
        {
            var base64 = dataUrl[(dataUrl.IndexOf(',') + 1)..];
            var bytes = Convert.FromBase64String(base64);

            using var stream = new MemoryStream(bytes);
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Clipboard.SetImage(decoder.Frames[0]);
        }

        private static void ShowCanvasTaintedMessage(Window owner, string actionTitle) =>
            MessageBox.Show(owner,
                "This couldn't be captured - the source doesn't allow reading its pixel data " +
                "cross-origin (no Access-Control-Allow-Origin header), or it's DRM-protected.",
                actionTitle, MessageBoxButton.OK, MessageBoxImage.Information);

        private static string SuggestFileName(string? title, string extension)
        {
            var name = string.IsNullOrWhiteSpace(title) ? "page" : title;
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return $"{name}.{extension}";
        }
    }
}
