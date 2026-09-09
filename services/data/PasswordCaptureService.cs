using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using MozartBrowser.Dialogs;

namespace MozartBrowser.Services.Data
{
    /// <summary>
    /// Detects logins and offers autofill for Mozart's own password vault
    /// (PasswordVaultService) — entirely separate from WebView2's built-in
    /// autosave, which this app forces off wherever the vault is active (see
    /// MainWindow.CreateNewTabAsync).
    ///
    /// How capture works (heuristic, best-effort by nature — every site's
    /// HTML/JS is different, this cannot be 100% reliable for every site):
    ///   1. A content script is injected into every document via
    ///      AddScriptToExecuteOnDocumentCreatedAsync, which — per the WebView2
    ///      docs — runs in the top frame AND every iframe of the page, so
    ///      logins embedded in third-party widgets/OAuth iframes are covered
    ///      too, not just top-level forms.
    ///   2. The script watches password fields' input events, and fires a
    ///      "capture" message on form submit, on a click that looks like a
    ///      login/submit button, and on beforeunload as a last-resort net —
    ///      this catches classic form posts AND most JS/SPA-driven logins
    ///      that never emit a real "submit" event.
    ///   3. Every inbound message is checked against CoreWebView2's own
    ///      e.Source (the frame's real origin, supplied by the engine itself
    ///      — not something page JS can spoof) before being trusted, so a
    ///      malicious page can't fake a "capture"/"fillRequest" for a domain
    ///      it doesn't actually run on.
    ///
    /// Autofill is click-to-fill only (a small key icon next to a password
    /// field with a saved match) — never silent-on-load — matching how every
    /// major browser behaves today, since silently populating credentials the
    /// instant a page loads lets that page's own JS read them back out
    /// immediately, before the user did anything.
    /// </summary>
    public static class PasswordCaptureService
    {
        /// <summary>
        /// Injected once per document (main frame + every iframe). Kept
        /// dependency-free (no libraries) and defensive — wrapped so a
        /// throwing page script or an unusual DOM never breaks the page
        /// itself; every risky call sits behind try/catch.
        /// </summary>
        private const string CaptureScript = """
            (function() {
                if (window.__mozartPwInjected) return;
                window.__mozartPwInjected = true;
                if (location.protocol !== 'http:' && location.protocol !== 'https:') return;

                function post(msg) {
                    try {
                        msg.mozartPasswordMsg = true;
                        window.chrome.webview.postMessage(JSON.stringify(msg));
                    } catch (e) {}
                }

                function findUsernameField(passwordField) {
                    try {
                        var form = passwordField.closest('form');
                        var scope = form || document;
                        var inputs = Array.prototype.slice.call(scope.querySelectorAll('input'));
                        var pwIndex = inputs.indexOf(passwordField);
                        for (var i = pwIndex - 1; i >= 0; i--) {
                            var t = (inputs[i].type || 'text').toLowerCase();
                            if (t === 'text' || t === 'email' || t === 'tel') return inputs[i];
                        }
                    } catch (e) {}
                    return null;
                }

                var lastCapture = null;

                function tryCapture(pw) {
                    try {
                        if (!pw || !pw.value) return;
                        var userField = findUsernameField(pw);
                        var username = userField ? userField.value : '';
                        if (!username) return;
                        lastCapture = { username: username, password: pw.value };
                    } catch (e) {}
                }

                function fireCapture() {
                    if (!lastCapture) return;
                    post({ type: 'capture', username: lastCapture.username, password: lastCapture.password });
                    lastCapture = null;
                }

                var savedUsernames = [];

                function addIcon(pw) {
                    try {
                        if (!savedUsernames.length || pw.__mozartIconAdded || pw.offsetParent === null) return;
                        pw.__mozartIconAdded = true;
                        var rect = pw.getBoundingClientRect();
                        var icon = document.createElement('div');
                        icon.textContent = '\u{1F511}';
                        icon.title = 'Fill saved password (Mozart)';
                        icon.style.cssText = 'position:absolute;cursor:pointer;z-index:2147483647;' +
                            'font-size:14px;line-height:1;user-select:none;' +
                            'top:' + (window.scrollY + rect.top + rect.height / 2 - 8) + 'px;' +
                            'left:' + (window.scrollX + rect.right - 22) + 'px;';
                        icon.addEventListener('click', function(ev) {
                            ev.preventDefault();
                            ev.stopPropagation();
                            showPicker(icon, pw);
                        });
                        document.body.appendChild(icon);
                    } catch (e) {}
                }

                function showPicker(anchor, pw) {
                    var existing = document.getElementById('__mozartPicker');
                    if (existing) existing.remove();
                    var menu = document.createElement('div');
                    menu.id = '__mozartPicker';
                    menu.style.cssText = 'position:absolute;z-index:2147483647;background:#fff;color:#111;' +
                        'border:1px solid #ccc;border-radius:6px;box-shadow:0 2px 10px rgba(0,0,0,.25);' +
                        'font:13px/1.4 sans-serif;min-width:170px;overflow:hidden;' +
                        'top:' + (parseFloat(anchor.style.top) + 20) + 'px;left:' + anchor.style.left + ';';
                    savedUsernames.forEach(function(u) {
                        var item = document.createElement('div');
                        item.textContent = u;
                        item.style.cssText = 'padding:7px 12px;cursor:pointer;';
                        item.addEventListener('mouseenter', function() { item.style.background = '#eee'; });
                        item.addEventListener('mouseleave', function() { item.style.background = ''; });
                        item.addEventListener('click', function() {
                            window.__mozartPendingFillField = pw;
                            post({ type: 'fillRequest', username: u });
                            menu.remove();
                        });
                        menu.appendChild(item);
                    });
                    document.body.appendChild(menu);
                    setTimeout(function() {
                        document.addEventListener('click', function closeOnce() {
                            menu.remove();
                            document.removeEventListener('click', closeOnce);
                        }, { once: true });
                    }, 0);
                }

                function wireField(pw) {
                    if (pw.__mozartWired) return;
                    pw.__mozartWired = true;
                    pw.addEventListener('input', function() { tryCapture(pw); });
                    addIcon(pw);
                }

                function scan() {
                    try { document.querySelectorAll('input[type="password"]').forEach(wireField); } catch (e) {}
                }

                document.addEventListener('submit', function() {
                    scan();
                    document.querySelectorAll('input[type="password"]').forEach(tryCapture);
                    fireCapture();
                }, true);

                document.addEventListener('click', function(ev) {
                    var el = ev.target.closest && ev.target.closest('button, input[type="submit"], [role="button"]');
                    if (!el) return;
                    var label = ((el.textContent || el.value || '') + '').toLowerCase();
                    if (/log ?in|sign ?in|submit|continue|next/.test(label)) {
                        document.querySelectorAll('input[type="password"]').forEach(tryCapture);
                        fireCapture();
                    }
                }, true);

                window.addEventListener('beforeunload', fireCapture);

                try {
                    var mo = new MutationObserver(scan);
                    mo.observe(document.documentElement, { childList: true, subtree: true });
                } catch (e) {}
                document.addEventListener('DOMContentLoaded', scan);
                scan();

                // ---- host -> page bridge (click-to-fill) ----
                window.__mozartEnableFill = function(usernames) {
                    savedUsernames = usernames || [];
                    document.querySelectorAll('input[type="password"]').forEach(addIcon);
                };

                window.__mozartDoFill = function(username, password) {
                    var pw = window.__mozartPendingFillField || document.querySelector('input[type="password"]');
                    if (!pw) return;
                    var userField = findUsernameField(pw);
                    if (userField) {
                        userField.value = username;
                        userField.dispatchEvent(new Event('input', { bubbles: true }));
                        userField.dispatchEvent(new Event('change', { bubbles: true }));
                    }
                    pw.value = password;
                    pw.dispatchEvent(new Event('input', { bubbles: true }));
                    pw.dispatchEvent(new Event('change', { bubbles: true }));
                };
            })();
            """;

        /// <summary>
        /// Wires capture + autofill into one tab's CoreWebView2. Call once
        /// right after EnsureCoreWebView2Async, for normal tabs only — never
        /// for private/incognito tabs (checked at the call site in
        /// MainWindow, same convention as HistoryService).
        /// </summary>
        public static async Task AttachAsync(CoreWebView2 coreWebView2, Window ownerWindow)
        {
            await coreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(CaptureScript);

            coreWebView2.WebMessageReceived += (_, e) =>
                _ = HandleMessageAsync(coreWebView2, null, e.Source, TryGetJson(e), ownerWindow);

            coreWebView2.FrameCreated += (_, frameArgs) =>
            {
                // CoreWebView2Frame.WebMessageReceived reuses the same event args
                // type as the top-level CoreWebView2.WebMessageReceived — there is
                // no separate "frame" variant of the args type.
                frameArgs.Frame.WebMessageReceived += (_, e) =>
                    _ = HandleMessageAsync(coreWebView2, frameArgs.Frame, e.Source, TryGetJson(e), ownerWindow);
            };

            coreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (!e.IsSuccess) return;
                await PushSavedUsernamesAsync(coreWebView2);
            };
        }

        private static string? TryGetJson(CoreWebView2WebMessageReceivedEventArgs e)
        {
            try { return e.TryGetWebMessageAsString(); }
            catch { return null; }
        }

        /// <summary>
        /// After every successful navigation, tells the page's own script
        /// which usernames (for the exact current host) have a saved
        /// password, so it can light up the click-to-fill icon. Main frame
        /// only for now — a known, deliberate v1 limitation: forms embedded
        /// in a cross-origin iframe still get captured fine (the content
        /// script runs there too), they just won't show the icon
        /// automatically since the host doesn't proactively push into every
        /// iframe on every navigation.
        /// </summary>
        private static async Task PushSavedUsernamesAsync(CoreWebView2 coreWebView2)
        {
            if (!App.Settings.Current.PasswordManager.AutofillEnabled) return;

            var domain = TryGetHost(coreWebView2.Source);
            if (domain == null) return;

            var saved = await App.Passwords.GetForDomainAsync(domain);
            if (saved.Count == 0) return;

            var usernamesJson = JsonSerializer.Serialize(saved.Select(p => p.Username).Distinct());
            try
            {
                await coreWebView2.ExecuteScriptAsync($"window.__mozartEnableFill && window.__mozartEnableFill({usernamesJson});");
            }
            catch
            {
                // Best-effort: a page that navigated away again immediately can throw here; harmless.
            }
        }

        private static async Task HandleMessageAsync(
            CoreWebView2 coreWebView2, CoreWebView2Frame? frame, string sourceOrigin, string? json, Window ownerWindow)
        {
            if (string.IsNullOrEmpty(json)) return;

            // The message itself claims a domain (via the page's own location.hostname
            // for "fillRequest", or implicitly via which frame sent it for "capture").
            // Cross-check against e.Source — supplied by the WebView2 engine from the
            // real frame origin, not something page JS can spoof — before trusting it.
            var actualDomain = TryGetHost(sourceOrigin);
            if (actualDomain == null) return;

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(json);
                root = doc.RootElement.Clone();
            }
            catch
            {
                return;
            }

            if (!root.TryGetProperty("mozartPasswordMsg", out _)) return;
            if (!root.TryGetProperty("type", out var typeProp)) return;

            switch (typeProp.GetString())
            {
                case "capture":
                {
                    if (!App.Settings.Current.PasswordManager.OfferToSavePasswords) return;

                    var username = root.TryGetProperty("username", out var u) ? u.GetString() : null;
                    var password = root.TryGetProperty("password", out var p) ? p.GetString() : null;
                    if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return;

                    var alreadySaved = await App.Passwords.ExistsAsync(actualDomain, username);

                    // Fire on the UI thread — WebMessageReceived callbacks are already
                    // dispatched there by WebView2, but ownerWindow.Dispatcher keeps this
                    // safe even if that ever changes.
                    ownerWindow.Dispatcher.Invoke(() =>
                    {
                        var prompt = new SavePasswordPromptDialog(actualDomain, username!, password!, alreadySaved)
                        {
                            Owner = ownerWindow
                        };
                        prompt.Show();
                    });
                    break;
                }

                case "fillRequest":
                {
                    if (!App.Settings.Current.PasswordManager.AutofillEnabled) return;

                    var username = root.TryGetProperty("username", out var u) ? u.GetString() : null;
                    if (string.IsNullOrEmpty(username)) return;

                    var matches = await App.Passwords.GetForDomainAsync(actualDomain);
                    var match = matches.FirstOrDefault(m => m.Username == username);
                    if (match == null) return;

                    string plaintext;
                    try { plaintext = PasswordVaultService.Decrypt(match.EncryptedPassword); }
                    catch { return; }

                    var fillCall = $"window.__mozartDoFill && window.__mozartDoFill({JsonSerializer.Serialize(username)}, {JsonSerializer.Serialize(plaintext)});";
                    try
                    {
                        if (frame != null)
                            await frame.ExecuteScriptAsync(fillCall);
                        else
                            await coreWebView2.ExecuteScriptAsync(fillCall);
                    }
                    catch
                    {
                        // Best-effort: frame may have navigated away between click and here.
                    }
                    break;
                }
            }
        }

        private static string? TryGetHost(string? url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https")
                ? uri.Host
                : null;
        }
    }
}
