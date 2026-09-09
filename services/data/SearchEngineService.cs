using System;
using System.Linq;
using System.Net;
using MozartBrowser.Models;

namespace MozartBrowser.Services.Data
{
    /// <summary>
    /// Resolves whatever the user typed in the address bar into a navigable URL:
    /// either the URL itself (if it looks like one) or a search-engine query URL.
    /// Also supports Firefox-style quick-switch prefixes, e.g. "!b cats" forces Bing
    /// for that one query regardless of the default engine.
    /// </summary>
    public class SearchEngineService
    {
        private readonly SettingsService _settings;

        public SearchEngineService(SettingsService settings)
        {
            _settings = settings;
        }

        public SearchEngine DefaultEngine =>
            _settings.Current.SearchEngines.FirstOrDefault(e => e.Name == _settings.Current.DefaultSearchEngineName)
            ?? _settings.Current.SearchEngines.FirstOrDefault()
            ?? SearchEngine.Defaults[0];

        /// <summary>Turns raw address-bar text into a navigable URL.</summary>
        public string Resolve(string input)
        {
            input = input.Trim();
            if (string.IsNullOrEmpty(input))
                return "about:newtab";

            // Quick-switch: "!g some query" -> force that engine for this search only.
            if (input.StartsWith('!'))
            {
                var spaceIndex = input.IndexOf(' ');
                if (spaceIndex > 1)
                {
                    var shortcut = input[1..spaceIndex];
                    var query = input[(spaceIndex + 1)..];
                    var engine = _settings.Current.SearchEngines.FirstOrDefault(
                        e => string.Equals(e.Shortcut, shortcut, StringComparison.OrdinalIgnoreCase));
                    if (engine != null)
                        return BuildSearchUrl(engine, query);
                }
            }

            if (LooksLikeUrl(input))
                return NormalizeUrl(input);

            return BuildSearchUrl(DefaultEngine, input);
        }

        private static string BuildSearchUrl(SearchEngine engine, string query)
        {
            var encoded = WebUtility.UrlEncode(query);
            return engine.UrlTemplate.Replace("%s", encoded);
        }

        /// <summary>
        /// Heuristic used by every browser's address bar: has a scheme, or looks like
        /// "word.word" / "word.word/path" with no spaces, or is a bare localhost/IP.
        /// </summary>
        private static bool LooksLikeUrl(string input)
        {
            if (input.Contains(' '))
                return false;

            if (input.StartsWith("http://") || input.StartsWith("https://") ||
                input.StartsWith("about:") || input.StartsWith("file://"))
                return true;

            if (input.StartsWith("localhost") || input.StartsWith("127.0.0.1"))
                return true;

            // e.g. "google.com", "sub.example.co.uk/path?x=1"
            var hostPart = input.Split('/')[0];
            return hostPart.Contains('.') && !hostPart.EndsWith('.');
        }

        private static string NormalizeUrl(string input)
        {
            if (input.StartsWith("http://") || input.StartsWith("https://") ||
                input.StartsWith("about:") || input.StartsWith("file://"))
                return input;

            return "https://" + input;
        }
    }
}
