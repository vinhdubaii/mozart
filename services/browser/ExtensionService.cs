using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using MozartBrowser.Models;

namespace MozartBrowser.Services.Browser
{
    /// <summary>
    /// Downloads, unpacks, installs, and manages browser extensions on top
    /// of WebView2's (experimental) extension APIs. Extensions only ever
    /// load into the Normal CoreWebView2Profile — see
    /// WebViewEnvironmentService, which only sets AreBrowserExtensionsEnabled
    /// on NormalEnvironment. A singleton owned by App, mirroring the shape
    /// of BookmarkService/DownloadService.
    /// </summary>
    public class ExtensionService
    {
        private readonly string _extensionsRootFolder;
        private readonly string _idMapPath;
        private readonly HttpClient _httpClient = new();

        // Resolved fresh on every call instead of cached once - a
        // CoreWebView2Profile .NET wrapper is tied to the specific tab whose
        // CoreWebView2 handed it out. The old approach (MainWindow calling
        // AttachProfile once per new tab, "last tab created wins") went
        // stale the moment that particular tab got closed, even with other
        // Normal tabs still open and perfectly usable - every method below
        // would then throw ObjectDisposedException/COMException 0x8007139F
        // from a disposed CoreWebView2, and pinned-icon rebuilds would go
        // silently empty. MainWindow supplies a resolver once (see
        // SetProfileResolver) that always reads from whichever tabs are
        // live right now instead of a field frozen at attach time.
        private Func<CoreWebView2Profile?>? _profileResolver;

        /// <summary>
        /// Raised after RemoveAsync actually removes an extension, so
        /// MainWindow can drop its pinned toolbar icon (if any) without
        /// SettingsWindow having to reach into MainWindow directly.
        /// </summary>
        public event EventHandler<string>? ExtensionRemoved;

        public ExtensionService(string appDataFolder)
        {
            _extensionsRootFolder = Path.Combine(appDataFolder, "Extensions");
            _idMapPath = Path.Combine(_extensionsRootFolder, "extension-id-map.json");
            Directory.CreateDirectory(_extensionsRootFolder);
        }

        /// <summary>
        /// Called once (from MainWindow's constructor), not per-tab. The
        /// resolver itself is invoked fresh on every install/list/enable/
        /// remove call, so it always reflects whichever tabs are open at
        /// that moment rather than whichever tab happened to exist when
        /// this was wired up.
        /// </summary>
        public void SetProfileResolver(Func<CoreWebView2Profile?> resolver) => _profileResolver = resolver;

        private CoreWebView2Profile? ResolveProfile() => _profileResolver?.Invoke();

        /// <summary>
        /// Full store-install pipeline: download the .crx by ID, unpack it,
        /// register it with WebView2, and return the parsed metadata. Throws
        /// on any failure — callers (ContextMenuActionHandler.AddToMozart) are
        /// expected to catch via the existing RunSafe wrapper and show a
        /// message box, not this service.
        /// </summary>
        public async Task<InstalledExtension> InstallFromStoreAsync(string storeExtensionId, ExtensionStoreKind store)
        {
            var profile = ResolveProfile()
                ?? throw new InvalidOperationException("No active WebView2 profile to install into yet.");

            var crxBytes = await DownloadCrxAsync(storeExtensionId, store);

            // Extract straight to a PERMANENT folder, named after the store
            // ID (the one piece of identity known before install), and never
            // move it afterward. Per AddBrowserExtensionAsync's own docs,
            // "the content of the extension is not copied to the user data
            // folder" - WebView2 only remembers the folder path we hand it
            // here and re-reads the extension from that exact path on every
            // future launch. Renaming/moving it after the fact (an earlier
            // version of this method did that, to reconcile the store ID
            // against the ID WebView2 assigns) orphans WebView2's stored
            // path, so the extension silently disappears the next time the
            // browser restarts. The store ID and WebView2's own ID can
            // differ, so the ID -> folder link that install-time ID map
            // (see LoadIdMap/SaveIdMap below) is what makes it possible to
            // look this extension back up later by the ID WebView2 reports
            // in GetBrowserExtensionsAsync.
            var extractFolder = Path.Combine(_extensionsRootFolder, storeExtensionId);
            UnpackCrx(crxBytes, extractFolder);

            var liveExtension = await profile.AddBrowserExtensionAsync(extractFolder);

            var map = LoadIdMap();
            map[liveExtension.Id] = storeExtensionId;
            SaveIdMap(map);

            var parsed = ParseManifest(extractFolder, liveExtension.Id, liveExtension.Name)
                ?? throw new InvalidOperationException("Installed extension has no readable manifest.json.");
            parsed.IsEnabled = liveExtension.IsEnabled;
            return parsed;
        }

        public async Task<List<InstalledExtension>> GetInstalledAsync()
        {
            // Cross-reference WebView2's live extension list (for
            // Id/Name/IsEnabled - Name comes back already localized, see
            // ParseManifest's notes) against each extension's own extracted
            // folder (for Description/Icon/Permissions/SiteAccess/OptionsPage,
            // none of which CoreWebView2BrowserExtension exposes itself).
            // The folder is found via the id map, not by assuming the folder
            // name equals the live extension Id (see InstallFromStoreAsync).
            var result = new List<InstalledExtension>();
            var profile = ResolveProfile();
            if (profile == null) return result;

            var map = LoadIdMap();
            var live = await profile.GetBrowserExtensionsAsync();
            foreach (var ext in live)
            {
                var folder = ResolveFolder(ext.Id, map);
                var parsed = folder != null ? ParseManifest(folder, ext.Id, ext.Name) : null;
                if (parsed == null) continue;

                parsed.IsEnabled = ext.IsEnabled;
                result.Add(parsed);
            }
            return result;
        }

        public async Task SetEnabledAsync(string extensionId, bool enabled)
        {
            var profile = ResolveProfile();
            if (profile == null) return;
            var live = await profile.GetBrowserExtensionsAsync();
            var match = live.FirstOrDefault(e => e.Id == extensionId);
            if (match != null)
                await match.EnableAsync(enabled);
        }

        public async Task RemoveAsync(string extensionId)
        {
            var profile = ResolveProfile();
            if (profile == null) return;
            var live = await profile.GetBrowserExtensionsAsync();
            var match = live.FirstOrDefault(e => e.Id == extensionId);
            if (match != null)
                await match.RemoveAsync();

            var map = LoadIdMap();
            var folder = ResolveFolder(extensionId, map);
            if (folder != null && Directory.Exists(folder))
            {
                try { Directory.Delete(folder, recursive: true); }
                catch { /* best-effort, matches CleanupPrivateEnvironment's style */ }
            }

            if (map.Remove(extensionId))
                SaveIdMap(map);

            ExtensionRemoved?.Invoke(this, extensionId);
        }

        // ============================= CRX download =============================

        private async Task<byte[]> DownloadCrxAsync(string extensionId, ExtensionStoreKind store)
        {
            // The store's own product version is baked into the update-check
            // URL (see below) — pulling it from the actual running engine
            // (same call ShowAbout already uses) means this never hardcodes
            // a prodversion that looks stale forever.
            var fullVersion = CoreWebView2Environment.GetAvailableBrowserVersionString(
                WebViewEnvironmentService.FixedRuntimeFolder);
            var majorVersion = fullVersion.Split('.').FirstOrDefault() is { Length: > 0 } major ? major : "120";

            // Unofficial "update check" endpoints — there is no documented
            // public API for downloading a .crx by extension ID. Best-effort;
            // may break without notice if either store changes its endpoint.
            var url = store == ExtensionStoreKind.ChromeWebStore
                ? $"https://clients2.google.com/service/update2/crx?response=redirect&acceptformat=crx2,crx3&prodversion={majorVersion}.0&x=id%3D{extensionId}%26uc"
                : $"https://edge.microsoft.com/extensionwebstorebase/v1/crx?response=redirect&prod=chromiumcrx&prodchannel=&prodversion={majorVersion}.0.0.0&lang=en&acceptformat=crx2,crx3&x=id%3D{extensionId}%26installsource%3Dondemand%26uc";

            byte[] bytes;
            try
            {
                bytes = await _httpClient.GetByteArrayAsync(url);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException(
                    $"Couldn't reach the extension store to download this extension: {ex.Message}", ex);
            }

            if (bytes.Length < 12 || bytes[0] != 'C' || bytes[1] != 'r' || bytes[2] != '2' || bytes[3] != '4')
                throw new InvalidDataException(
                    "Downloaded file is not a valid CRX package (bad magic header). The store's download endpoint may have changed.");

            return bytes;
        }

        // ============================= CRX3 unpack =============================

        private static void UnpackCrx(byte[] crx, string destinationFolder)
        {
            // Layout: 4 bytes magic "Cr24", 4 bytes version (uint32 LE),
            // 4 bytes header length N (uint32 LE), N bytes protobuf header
            // (skipped - no signature verification needed for this
            // personal-install flow; Chrome itself doesn't require it for
            // sideloaded extensions either), then a plain zip archive for
            // the rest of the file.
            var version = BitConverter.ToUInt32(crx, 4);
            if (version != 3)
                throw new NotSupportedException($"Unsupported CRX version {version} — only CRX3 is supported.");

            var headerLength = BitConverter.ToInt32(crx, 8);
            var zipStart = 12 + headerLength;

            if (Directory.Exists(destinationFolder))
                Directory.Delete(destinationFolder, recursive: true);
            Directory.CreateDirectory(destinationFolder);

            using var zipStream = new MemoryStream(crx, zipStart, crx.Length - zipStart, writable: false);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
            archive.ExtractToDirectory(destinationFolder);
        }

        // ============================= manifest.json parsing =============================

        /// <summary>
        /// Reads the metadata WebView2's own CoreWebView2BrowserExtension
        /// doesn't expose: description, icon, permissions/site access,
        /// options page. Id and Name are passed in from the caller's live
        /// CoreWebView2BrowserExtension rather than read from this manifest's
        /// raw "name" field, because:
        ///   - Id: WebView2 assigns its own chrome.runtime.id on install,
        ///     which may not match any ID this code guessed beforehand.
        ///   - Name: CoreWebView2BrowserExtension.Name already returns the
        ///     resolved/localized name. Reading manifest.json's "name" field
        ///     directly would instead surface the raw, unresolved
        ///     "__MSG_extName__"-style placeholder for any extension that
        ///     uses _locales/*.json for its display name (uBlock Origin
        ///     Lite among them) - re-implementing that _locales lookup here
        ///     would just duplicate what WebView2 already resolved for us.
        /// </summary>
        private InstalledExtension? ParseManifest(string extractFolder, string id, string name)
        {
            var manifestPath = Path.Combine(extractFolder, "manifest.json");
            if (!File.Exists(manifestPath)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = doc.RootElement;

            var version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            var description = root.TryGetProperty("description", out var d) ? d.GetString() : null;
            // Unlike Name, WebView2 doesn't resolve this one for us, so a
            // localized extension's raw manifest.json can still hand back
            // a "__MSG_extDescription__"-style placeholder here.
            description = ResolveMessagePlaceholder(description, extractFolder, root);
            var manifestVersion = root.TryGetProperty("manifest_version", out var mv) ? mv.GetInt32() : 2;

            string? iconPath = null;
            if (root.TryGetProperty("icons", out var icons) && icons.ValueKind == JsonValueKind.Object)
            {
                var best = icons.EnumerateObject()
                    .Select(p => (Size: int.TryParse(p.Name, out var s) ? s : 0, Value: p.Value.GetString()))
                    .Where(p => !string.IsNullOrEmpty(p.Value))
                    .OrderByDescending(p => p.Size)
                    .FirstOrDefault();
                if (best.Value != null)
                    iconPath = Path.Combine(extractFolder, best.Value.Replace('/', Path.DirectorySeparatorChar));
            }

            var permissions = new List<string>();
            var siteAccess = new List<string>();

            void ClassifyEntries(JsonElement array)
            {
                foreach (var entry in array.EnumerateArray())
                {
                    var text = entry.GetString();
                    if (string.IsNullOrEmpty(text)) continue;
                    // URL match patterns look like "scheme://host/path" (with *
                    // wildcards) or the special "<all_urls>" - anything else is
                    // a plain API permission name like "storage" or "tabs".
                    if (text.Contains("://") || text == "<all_urls>")
                        siteAccess.Add(text);
                    else
                        permissions.Add(text);
                }
            }

            if (root.TryGetProperty("permissions", out var perms) && perms.ValueKind == JsonValueKind.Array)
                ClassifyEntries(perms);
            if (manifestVersion == 3 && root.TryGetProperty("host_permissions", out var hostPerms) && hostPerms.ValueKind == JsonValueKind.Array)
                ClassifyEntries(hostPerms);

            string? optionsPageUrl = null;
            if (root.TryGetProperty("options_page", out var opV2))
                optionsPageUrl = ToChromeExtensionUrl(id, opV2.GetString());
            else if (root.TryGetProperty("options_ui", out var opV3) &&
                     opV3.TryGetProperty("page", out var opV3Page))
                optionsPageUrl = ToChromeExtensionUrl(id, opV3Page.GetString());

            return new InstalledExtension
            {
                Id = id,
                Name = name,
                Version = version,
                Description = description,
                FolderPath = extractFolder,
                IconPath = iconPath,
                Permissions = permissions,
                SiteAccess = siteAccess,
                OptionsPageUrl = optionsPageUrl
            };
        }

        // WebView2 serves an installed extension's own files under
        // chrome-extension://{id}/{relativePath} - same scheme Chrome uses.
        private static string? ToChromeExtensionUrl(string extensionId, string? relativePath) =>
            string.IsNullOrEmpty(relativePath) ? null : $"chrome-extension://{extensionId}/{relativePath.TrimStart('/')}";

        // ============================= live-id -> folder-name map =============================

        /// <summary>
        /// A live extension's Id (from CoreWebView2BrowserExtension, only
        /// known after AddBrowserExtensionAsync returns) is not necessarily
        /// the folder name it lives in on disk (the folder is named after
        /// the store ID, fixed at install time - see InstallFromStoreAsync
        /// for why it can never be renamed to match). This little JSON file
        /// is the only link between the two, so it has to survive restarts
        /// just like the extension folders themselves.
        /// </summary>
        private Dictionary<string, string> LoadIdMap()
        {
            if (!File.Exists(_idMapPath)) return new Dictionary<string, string>();
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_idMapPath))
                       ?? new Dictionary<string, string>();
            }
            catch
            {
                // Corrupt map file - treat as empty rather than crashing the
                // whole Extensions panel over it.
                return new Dictionary<string, string>();
            }
        }

        private void SaveIdMap(Dictionary<string, string> map) =>
            File.WriteAllText(_idMapPath, JsonSerializer.Serialize(map));

        /// <summary>
        /// Resolves a live extension Id to its folder on disk via the id
        /// map, falling back to a folder literally named after the live Id
        /// as a last resort (covers the case where the store ID and the
        /// live ID happen to be identical, or the map is missing/stale).
        /// </summary>
        private string? ResolveFolder(string liveId, Dictionary<string, string> map)
        {
            if (map.TryGetValue(liveId, out var storeId))
            {
                var mapped = Path.Combine(_extensionsRootFolder, storeId);
                if (Directory.Exists(mapped)) return mapped;
            }

            var fallback = Path.Combine(_extensionsRootFolder, liveId);
            return Directory.Exists(fallback) ? fallback : null;
        }

        // ============================= _locales/*.json placeholder resolution =============================

        /// <summary>
        /// Resolves a raw manifest string that may be a "__MSG_key__"
        /// placeholder (Chrome's i18n convention) by looking it up in
        /// _locales/{default_locale}/messages.json. Returns the input
        /// unchanged if it isn't a placeholder, or if resolution fails for
        /// any reason (missing default_locale, missing messages.json,
        /// missing key) - falling back to the raw placeholder text is
        /// better than throwing and losing the whole install.
        /// </summary>
        private static string? ResolveMessagePlaceholder(string? value, string extractFolder, JsonElement manifestRoot)
        {
            if (string.IsNullOrEmpty(value) || !value.StartsWith("__MSG_") || !value.EndsWith("__"))
                return value;

            try
            {
                if (!manifestRoot.TryGetProperty("default_locale", out var localeEl))
                    return value;

                var messagesPath = Path.Combine(extractFolder, "_locales", localeEl.GetString() ?? "", "messages.json");
                if (!File.Exists(messagesPath))
                    return value;

                var key = value[6..^2]; // strip "__MSG_" prefix and "__" suffix
                using var messagesDoc = JsonDocument.Parse(File.ReadAllText(messagesPath));
                if (messagesDoc.RootElement.TryGetProperty(key, out var entry) &&
                    entry.TryGetProperty("message", out var messageEl))
                    return messageEl.GetString() ?? value;
            }
            catch
            {
                // Malformed _locales/messages.json - fall through to the raw placeholder.
            }

            return value;
        }
    }
}
