using System;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using MozartBrowser.Models;

namespace MozartBrowser.Services.Theme
{
    /// <summary>
    /// Applies AppTheme (System/Light/Dark) by swapping the "Themes/*.xaml"
    /// entry inside Application.Resources.MergedDictionaries. Every color in
    /// the app is looked up as DynamicResource (or via FindResource at the
    /// moment a control is built), so this takes effect immediately — no
    /// window recreation or restart needed, unlike the Secure DNS setting.
    /// </summary>
    public static class ThemeService
    {
        private const string LightUri = "Themes/Light.xaml";
        private const string DarkUri = "Themes/Dark.xaml";

        public static void Apply(AppTheme theme)
        {
            var resolvedIsDark = theme switch
            {
                AppTheme.Dark => true,
                AppTheme.Light => false,
                AppTheme.System => IsWindowsInDarkMode(),
                _ => false
            };

            var targetUri = resolvedIsDark ? DarkUri : LightUri;

            var appResources = System.Windows.Application.Current.Resources;
            var existingThemeDict = appResources.MergedDictionaries.FirstOrDefault(d =>
                d.Source != null &&
                (d.Source.OriginalString.EndsWith(LightUri, StringComparison.OrdinalIgnoreCase) ||
                 d.Source.OriginalString.EndsWith(DarkUri, StringComparison.OrdinalIgnoreCase)));

            var newDict = new ResourceDictionary { Source = new Uri(targetUri, UriKind.Relative) };

            if (existingThemeDict != null)
            {
                var index = appResources.MergedDictionaries.IndexOf(existingThemeDict);
                appResources.MergedDictionaries[index] = newDict;
            }
            else
            {
                appResources.MergedDictionaries.Add(newDict);
            }
        }

        /// <summary>Reads the same registry value Windows itself uses for "Choose your mode".</summary>
        private static bool IsWindowsInDarkMode()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var value = key?.GetValue("AppsUseLightTheme");
                // 0 = dark mode, 1 (or missing key) = light mode.
                return value is int intValue && intValue == 0;
            }
            catch
            {
                return false; // Registry read failing is not worth crashing over; default to light.
            }
        }
    }
}
