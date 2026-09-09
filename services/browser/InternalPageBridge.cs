using System;
using System.Collections.Generic;
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
    /// against e.Source and silently ignored unless it originates from
    /// InternalPages.BaseUrl.
    /// </summary>
    public class InternalPageBridge
    {
        public delegate Task<object?> HandlerFunc(JsonElement payload);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly Dictionary<string, HandlerFunc> _handlers = new();
        private readonly List<CoreWebView2> _attached = new();
        private readonly object _lock = new();

        /// <summary>Registers (or replaces) the handler for a given action name, e.g. "history.getRecent".</summary>
        public void RegisterHandler(string action, HandlerFunc handler) => _handlers[action] = handler;

        /// <summary>
        /// Wires this bridge onto a tab's CoreWebView2: sets up the virtual
        /// host mapping so its internal pages resolve at all, and starts
        /// listening for bridge requests. Safe to call once per tab, right
        /// after EnsureCoreWebView2Async — every tab needs this, not just
        /// ones currently showing an internal page, since the user can
        /// navigate any tab to mozart://history etc. at any time.
        /// </summary>
        public void Attach(CoreWebView2 coreWebView2)
        {
            coreWebView2.SetVirtualHostNameToFolderMapping(
                InternalPages.VirtualHost,
                InternalPages.FolderPath,
                CoreWebView2HostResourceAccessKind.Allow);

            coreWebView2.WebMessageReceived += OnWebMessageReceived;

            lock (_lock) _attached.Add(coreWebView2);
        }

        /// <summary>Call when a tab is closing, so BroadcastEventAsync stops trying to reach a disposed CoreWebView2.</summary>
        public void Detach(CoreWebView2 coreWebView2)
        {
            try { coreWebView2.WebMessageReceived -= OnWebMessageReceived; }
            catch { /* already disposed — nothing left to unsubscribe from */ }

            lock (_lock) _attached.Remove(coreWebView2);
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
                    if (coreWebView2.Source != null &&
                        coreWebView2.Source.StartsWith(InternalPages.BaseUrl, StringComparison.OrdinalIgnoreCase))
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

        private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (sender is not CoreWebView2 coreWebView2) return;

            // See class remarks: never process a bridge call from anything
            // other than Mozart's own internal pages.
            if (string.IsNullOrEmpty(e.Source) ||
                !e.Source.StartsWith(InternalPages.BaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

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
                    var result = await handler(payload);
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
