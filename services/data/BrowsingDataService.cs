using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using MozartBrowser.Models;

namespace MozartBrowser.Services.Data
{
    /// <summary>
    /// Thin wrapper around CoreWebView2Profile.ClearBrowsingDataAsync, shared by
    /// the manual "Delete browsing data" dialog and the automatic "Clear on
    /// close" setting. Deletion happens at the WebView2/Chromium layer itself
    /// (the same store Edge/Chrome use), not via ad-hoc file deletion in this
    /// app — so it actually removes the data, not just a copy of it.
    /// </summary>
    public static class BrowsingDataService
    {
        /// <summary>Named time ranges shown in the Delete Browsing Data dialog, Chromium-style.</summary>
        public enum TimeRange
        {
            LastHour,
            Last24Hours,
            Last7Days,
            Last4Weeks,
            AllTime
        }

        public static CoreWebView2BrowsingDataKinds BuildKinds(ClearBrowsingDataTypes types)
        {
            CoreWebView2BrowsingDataKinds kinds = 0;

            if (types.History) kinds |= CoreWebView2BrowsingDataKinds.BrowsingHistory;
            if (types.Cookies) kinds |= CoreWebView2BrowsingDataKinds.Cookies;

            // "Cached images and files": DiskCache covers the HTTP cache;
            // CacheStorage covers the Cache Storage API used by service workers.
            // Chrome's own "Cached images and files" clears both.
            if (types.Cache) kinds |= CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage;

            if (types.DownloadHistory) kinds |= CoreWebView2BrowsingDataKinds.DownloadHistory;
            if (types.AutofillData) kinds |= CoreWebView2BrowsingDataKinds.GeneralAutofill;
            if (types.Passwords) kinds |= CoreWebView2BrowsingDataKinds.PasswordAutosave;

            return kinds;
        }

        /// <summary>
        /// Clears the given kinds from the given profile, honoring the time range.
        /// AllTime uses the no-range overload (clears everything of that kind,
        /// matching Chrome's "All time" — the ranged overload would otherwise
        /// need an arbitrarily old start date).
        /// </summary>
        public static Task ClearAsync(CoreWebView2Profile profile, CoreWebView2BrowsingDataKinds kinds, TimeRange range)
        {
            if (kinds == 0)
                return Task.CompletedTask;

            if (range == TimeRange.AllTime)
                return profile.ClearBrowsingDataAsync(kinds);

            var start = range switch
            {
                TimeRange.LastHour => DateTime.Now.AddHours(-1),
                TimeRange.Last24Hours => DateTime.Now.AddDays(-1),
                TimeRange.Last7Days => DateTime.Now.AddDays(-7),
                TimeRange.Last4Weeks => DateTime.Now.AddDays(-28),
                _ => DateTime.MinValue
            };

            return profile.ClearBrowsingDataAsync(kinds, start, DateTime.Now);
        }

        /// <summary>
        /// Wipes Mozart's own password vault when the "Passwords" checkbox is
        /// selected. Separate from ClearAsync's WebView2-layer clear above:
        /// PasswordAutosave in BuildKinds targets Chromium's native store,
        /// which this app no longer uses (see MainWindow.CreateNewTabAsync —
        /// native autosave is always forced off, PasswordVaultService is the
        /// real store now). Call this alongside ClearAsync, not instead of it,
        /// so both checkboxes' full intent is honored.
        /// </summary>
        public static Task ClearVaultIfSelectedAsync(ClearBrowsingDataTypes types)
        {
            return types.Passwords ? App.Passwords.ClearAllAsync() : Task.CompletedTask;
        }
    }
}
