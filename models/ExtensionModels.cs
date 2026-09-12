using System.Collections.Generic;

namespace MozartBrowser.Models
{
    /// <summary>
    /// Which store an extension ID was resolved from — determines which CRX
    /// "update check" endpoint ExtensionService downloads from.
    /// </summary>
    public enum ExtensionStoreKind
    {
        ChromeWebStore,
        EdgeAddons
    }

    /// <summary>
    /// One installed extension, as shown in the toolbar pin popup and in
    /// Settings → Extensions. Combines data read from the extension's own
    /// extracted manifest.json (Name/Version/Description/Icon/Permissions/
    /// SiteAccess/OptionsPageUrl) with live state from WebView2's
    /// CoreWebView2BrowserExtension (Id/IsEnabled).
    /// </summary>
    public class InstalledExtension
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Version { get; init; }
        public string? Description { get; init; }

        /// <summary>Absolute path to the extracted extension folder on disk.</summary>
        public required string FolderPath { get; init; }

        /// <summary>Absolute path to the best-available icon file, or null if the manifest declared none.</summary>
        public string? IconPath { get; init; }

        public bool IsEnabled { get; set; }

        /// <summary>Chrome-style "Allow in Incognito" toggle — whether this extension is also registered against the Private profile. See ExtensionService.SetAllowedInIncognitoAsync.</summary>
        public bool AllowedInIncognito { get; set; }

        /// <summary>Plain permission strings from the manifest (e.g. "storage", "tabs") — excludes URL match patterns.</summary>
        public List<string> Permissions { get; init; } = new();

        /// <summary>
        /// URL match patterns the extension can access — host_permissions (MV3)
        /// or the URL-pattern-shaped entries pulled out of "permissions" (MV2).
        /// </summary>
        public List<string> SiteAccess { get; init; } = new();

        public string? OptionsPageUrl { get; init; }

        public bool HasOptionsPage => !string.IsNullOrEmpty(OptionsPageUrl);
    }
}
