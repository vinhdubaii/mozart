/**
 * MozartBridge — JS-side client for InternalPageBridge.cs.
 *
 * Usage from any Mozart internal page:
 *   const result = await MozartBridge.call('history.getRecent', { limit: 50 });
 *   MozartBridge.on('download.progress', (payload) => { ... });
 *
 * Protocol (mirrors InternalPageBridge.cs):
 *   request:  postMessage(JSON.stringify({ id, action, payload }))
 *   response: { id, ok: true, result } | { id, ok: false, error }
 *   push:     { event, payload }   (no id — how a reply is told apart from a push)
 */
(function (global) {
    "use strict";

    const pending = new Map();
    const listeners = new Map();
    let nextId = 1;

    function hostAvailable() {
        return !!(global.chrome && global.chrome.webview);
    }

    function call(action, payload) {
        return new Promise((resolve, reject) => {
            if (!hostAvailable()) {
                reject(new Error("MozartBridge: host not available (page not running inside Mozart Browser?)."));
                return;
            }

            const id = String(nextId++);
            pending.set(id, { resolve, reject });

            try {
                global.chrome.webview.postMessage(JSON.stringify({ id, action, payload }));
            } catch (err) {
                pending.delete(id);
                reject(err);
            }
        });
    }

    /** Subscribes to a pushed event (e.g. "download.progress"). Returns an unsubscribe function. */
    function on(eventName, callback) {
        if (!listeners.has(eventName)) listeners.set(eventName, new Set());
        listeners.get(eventName).add(callback);
        return () => listeners.get(eventName)?.delete(callback);
    }

    // Called for every message the host posts back — both replies to our own
    // requests and unsolicited pushed events. Exposed on window so the
    // chrome.webview listener below (and, in principle, the host itself) can
    // reach it directly.
    global.__mozartDispatchEvent = function (message) {
        if (!message || typeof message !== "object") return;

        if (message.id !== undefined && message.id !== null) {
            const waiter = pending.get(message.id);
            if (!waiter) return; // reply for a request we no longer care about
            pending.delete(message.id);

            if (message.ok) waiter.resolve(message.result);
            else waiter.reject(new Error(message.error || "Unknown Mozart bridge error."));
            return;
        }

        if (message.event) {
            const set = listeners.get(message.event);
            if (!set) return;
            for (const cb of set) {
                try { cb(message.payload); }
                catch (err) { console.error("MozartBridge listener error:", err); }
            }
        }
    };

    if (hostAvailable()) {
        global.chrome.webview.addEventListener("message", (e) => {
            // e.data is already the parsed object for JSON messages posted
            // via CoreWebView2.PostWebMessageAsJson on the C# side.
            global.__mozartDispatchEvent(e.data);
        });
    }

    global.MozartBridge = { call, on };
})(window);
