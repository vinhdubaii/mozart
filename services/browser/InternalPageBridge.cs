using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace MozartBrowser.Services.Browser
{
    /// <summary>
    /// The one bridge between Mozart's internal HTML pages (New Tab, History,
    /// Downloads, Settings — see InternalPages) and the C# services that
    /// actually own the data (HistoryService, DownloadService, SettingsService,
    /// BookmarkService...).
    ///
    /// Also owns serving the pages themselves: every "mozart://<host>/..."
    /// request is answered here (see OnWebResourceRequested) straight from
    /// assets/InternalPages on disk, now that internal pages are a real
    /// registered custom scheme (see WebViewEnvironmentService) instead of an
    /// https virtual-host mapping.
    ///
    /// Protocol, page → host (request):
    ///   window.chrome.webview.postMessage(JSON.stringify({ id, action, payload }))
    ///   Handled here in OnWebMessageReceived, dispatched to whatever handler
    ///   was registered for `action`, and replied to with:
    ///   { id, ok: true,  result }   on success
    ///   { id, ok: false, error }    on failure
    ///
    /// Protocol, host → page (unsolicited push, e.g. download progress):
    ///   { event, payload }  — no id, so the page's bridge client can tell
    ///   replies and pushed events apart.
    ///
    /// Both directions are delivered as JSON over the same channel
    /// (CoreWebView2.PostWebMessageAsJson / chrome.webview.postMessage), and
    /// the matching JS client lives at Assets/InternalPages/shared/mozart-bridge.js.
    ///
    /// SECURITY: WebMessageReceived fires for messages from *any* page loaded
    /// in this CoreWebView2, not just Mozart's own — a malicious website could
    /// otherwise call postMessage itself and trigger privileged actions (read
    /// history, delete downloads, change settings). Every request is checked
    /// against e.Source and silently ignored unless it originates from the
    /// "mozart://" scheme.
    /// </summary>
    public class InternalPageBridge
    {
        /// <summary>
        /// isPrivate reflects which CoreWebView2 the request came from (see
        /// Attach), so e.g. "downloads.getAll" can answer with the calling
        /// window's own download list — Normal or Private — without either
        /// side needing a separate URL/page.
        /// </summary>
        public delegate Task<object?> HandlerFunc(JsonElement payload, bool isPrivate);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            [".html"] = "text/html",
            [".css"] = "text/css",
            [".js"] = "text/javascript"
        };

        private readonly Dictionary<string, HandlerFunc> _handlers = new();
        private readonly List<CoreWebView2> _attached = new();
        private readonly Dictionary<CoreWebView2, bool> _isPrivate = new();
        private readonly object _lock = new();

        /// <summary>Registers (or replaces) the handler for a given action name, e.g. "history.getRecent".</summary>
        public void RegisterHandler(string action, HandlerFunc handler) => _handlers[action] = handler;

        /// <summary>Convenience overload for handlers that don't care whether the caller is a Normal or Private tab.</summary>
        public void RegisterHandler(string action, Func<JsonElement, Task<object?>> handler) =>
            _handlers[action] = (payload, _) => handler(payload);

        /// <summary>
        /// Wires this bridge onto a tab's CoreWebView2: registers the
        /// mozart:// resource filter so its internal pages actually resolve,
        /// and starts listening for bridge requests. Safe to call once per
        /// tab, right after EnsureCoreWebView2Async — every tab needs this,
        /// not just ones currently showing an internal page, since the user
        /// can navigate any tab to mozart://history etc. at any time.
        /// isPrivate must reflect whether this tab belongs to a Private
        /// window, so per-window bridge actions (downloads.*) answer with
        /// the right data set.
        /// </summary>
        public void Attach(CoreWebView2 coreWebView2, bool isPrivate)
        {
            coreWebView2.AddWebResourceRequestedFilter($"{InternalPages.Scheme}://*", CoreWebView2WebResourceContext.All);
            coreWebView2.WebResourceRequested += OnWebResourceRequested;
            coreWebView2.WebMessageReceived += OnWebMessageReceived;

            lock (_lock)
            {
                _attached.Add(coreWebView2);
                _isPrivate[coreWebView2] = isPrivate;
            }
        }

        /// <summary>Call when a tab is closing, so BroadcastEventAsync stops trying to reach a disposed CoreWebView2.</summary>
        public void Detach(CoreWebView2 coreWebView2)
        {
            try { coreWebView2.WebMessageReceived -= OnWebMessageReceived; }
            catch { /* already disposed — nothing left to unsubscribe from */ }
            try { coreWebView2.WebResourceRequested -= OnWebResourceRequested; }
            catch { /* already disposed */ }

            lock (_lock)
            {
                _attached.Remove(coreWebView2);
                _isPrivate.Remove(coreWebView2);
            }
        }

        /// <summary>
        /// Pushes an unsolicited event to every currently-attached tab that is
        /// actually showing one of Mozart's internal pages right now (checked
        /// via each CoreWebView2's live Source) — e.g. download progress
        /// ticks, or "history changed" after a new visit is recorded.
        /// </summary>
        public void BroadcastEvent(string eventName, object? payload = null)
        {
            var json = JsonSerializer.Serialize(new { @event = eventName, payload }, JsonOptions);

            CoreWebView2[] snapshot;
            lock (_lock) snapshot = _attached.ToArray();

            foreach (var coreWebView2 in snapshot)
            {
                try
                {
                    if (coreWebView2.Source != null && InternalPages.IsInternalUrl(coreWebView2.Source))
                    {
                        coreWebView2.PostWebMessageAsJson(json);
                    }
                }
                catch
                {
                    // Best-effort: the tab may have navigated away or been
                    // disposed between the snapshot above and this call.
                }
            }
        }

        /// <summary>
        /// Serves every "mozart://<host>/<path>" request straight from
        /// assets/InternalPages — <host>.html for a bare page request (e.g.
        /// "mozart://settings" → settings.html) and any deeper path (e.g.
        /// "mozart://settings/shared/base.css") resolved relative to that
        /// same folder, matching how the old SetVirtualHostNameToFolderMapping
        /// resolved paths under the single shared InternalPages folder.
        /// </summary>
        private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (sender is not CoreWebView2 coreWebView2) return;

            Uri uri;
            try { uri = new Uri(e.Request.Uri); }
            catch { return; }

            if (!string.Equals(uri.Scheme, InternalPages.Scheme, StringComparison.OrdinalIgnoreCase)) return;

            var relativePath = uri.AbsolutePath.Trim('/');
            var filePath = string.IsNullOrEmpty(relativePath)
                ? Path.Combine(InternalPages.FolderPath, $"{uri.Host}.html")
                : Path.Combine(InternalPages.FolderPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

            // Any request straight at a host with no path segments (e.g.
            // "mozart://settings", "mozart://extensions") is the page itself.
            if (string.IsNullOrEmpty(relativePath))
                filePath = Path.Combine(InternalPages.FolderPath, $"{uri.Host}.html");

            var environment = coreWebView2.Environment;
            if (!File.Exists(filePath))
            {
                e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", string.Empty);
                return;
            }

            var extension = Path.GetExtension(filePath);
            var contentType = ContentTypes.TryGetValue(extension, out var ct) ? ct : "application/octet-stream";

            var bytes = File.ReadAllBytes(filePath);
            var stream = new MemoryStream(bytes);
            e.Response = environment.CreateWebResourceResponse(
                stream, 200, "OK", $"Content-Type: {contentType}");
        }

        private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (sender is not CoreWebView2 coreWebView2) return;

            // See class remarks: never process a bridge call from anything
            // other than Mozart's own internal pages.
            if (string.IsNullOrEmpty(e.Source) || !InternalPages.IsInternalUrl(e.Source))
            {
                return;
            }

            bool isPrivate;
            lock (_lock) isPrivate = _isPrivate.TryGetValue(coreWebView2, out var p) && p;

            string? raw;
            try { raw = e.TryGetWebMessageAsString(); }
            catch { return; } // wasn't posted as a plain JSON string — not one of ours

            if (string.IsNullOrEmpty(raw)) return;

            string? id = null;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                var action = root.TryGetProperty("action", out var actionProp) ? actionProp.GetString() : null;
                var payload = root.TryGetProperty("payload", out var payloadProp)
                    ? payloadProp.Clone()
                    : default;

                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(action))
                    return;

                if (!_handlers.TryGetValue(action, out var handler))
                {
                    await ReplyAsync(coreWebView2, id, ok: false, result: null, error: $"Unknown bridge action '{action}'.");
                    return;
                }

                try
                {
                    var result = await handler(payload, isPrivate);
                    await ReplyAsync(coreWebView2, id, ok: true, result: result, error: null);
                }
                catch (Exception ex)
                {
                    await ReplyAsync(coreWebView2, id, ok: false, result: null, error: ex.Message);
                }
            }
            catch (Exception ex)
            {
                // Malformed JSON from our own pages shouldn't happen, but if it
                // ever does, still try to unblock whatever's awaiting on the
                // JS side rather than leaving it hanging forever.
                if (!string.IsNullOrEmpty(id))
                    await ReplyAsync(coreWebView2, id, ok: false, result: null, error: ex.Message);
            }
        }

        private static Task ReplyAsync(CoreWebView2 coreWebView2, string id, bool ok, object? result, string? error)
        {
            var json = JsonSerializer.Serialize(new { id, ok, result, error }, JsonOptions);
            try { coreWebView2.PostWebMessageAsJson(json); }
            catch { /* tab may have navigated away/closed between request and reply */ }
            return Task.CompletedTask;
        }
    }
}
