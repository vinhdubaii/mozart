using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace MozartBrowser.Services.Browser
{
    /// <summary>
    /// Reads/sets Mozart's status as the Windows default browser. Since Windows 8,
    /// apps cannot silently set themselves as the default browser (that was
    /// abused too often) — the only sanctioned path is to send the user to the
    /// Settings app's "Default apps" page and let them pick Mozart from the list
    /// themselves, same as Chrome/Edge/Firefox all do.
    ///
    /// IMPORTANT: for Mozart to actually appear in that list, the installer must
    /// register it as a browser candidate first (ProgId, Capabilities key,
    /// UrlAssociations, RegisteredApplications) — see installer/setup.iss.
    /// Until that registration exists, IsDefaultBrowser() will simply always
    /// report false, which is the honest state (Windows has no candidate to
    /// pick).
    /// </summary>
    public static class DefaultBrowserService
    {
        /// <summary>Must exactly match the ProgId registered by the installer.</summary>
        public const string ProgId = "MozartBrowserHTML";

        public static bool IsDefaultBrowser()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");

                var currentProgId = key?.GetValue("ProgId") as string;
                return string.Equals(currentProgId, ProgId, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Missing key, access denied, or unexpected shape — treat as "not default"
                // rather than surfacing a registry-read error to the user.
                return false;
            }
        }

        /// <summary>
        /// Opens Windows' own "Choose default apps" page so the user can pick
        /// Mozart themselves. Falls back to the legacy Control Panel applet on
        /// older Windows builds where ms-settings: isn't registered.
        /// </summary>
        public static void OpenDefaultAppsSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = "ms-settings:defaultapps", UseShellExecute = true });
            }
            catch
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "control.exe",
                        Arguments = "/name Microsoft.DefaultPrograms",
                        UseShellExecute = true
                    });
                }
                catch
                {
                    // Best-effort only; nothing sensible left to fall back to.
                }
            }
        }
    }
}
