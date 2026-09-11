using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using MozartBrowser.Models;

namespace MozartBrowser.Services.Data
{
    /// <summary>
    /// Loads/saves AppSettings to a JSON file in %AppData%\Mozart Browser\settings.json.
    /// Kept deliberately simple (no SQLite) since this is a single small object,
    /// not a table that needs querying.
    /// </summary>
    public class SettingsService
    {
        private readonly string _filePath;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public AppSettings Current { get; private set; } = new();

        /// <summary>
        /// Raised after every successful SaveAsync. Lets UI that doesn't own
        /// the settings object (MainWindow reacting to changes made from
        /// settings.html via the bridge, rather than its own native dialogs)
        /// know it should re-apply theme/bookmark-bar/etc. Fires on every
        /// save, including unrelated ones (window size, last-session tabs),
        /// so subscribers should be cheap and idempotent.
        /// </summary>
        public event Action? Saved;

        public SettingsService(string filePath)
        {
            _filePath = filePath;
        }

        /// <summary>
        /// Wholesale-replaces Current, then saves. Used by the settings.save
        /// bridge action: settings.html always sends back the full settings
        /// object (not a partial patch), so there's nothing to merge field by
        /// field - this just swaps the in-memory object being read everywhere
        /// else (App.Settings.Current) and persists it the normal way.
        /// </summary>
        public async Task ReplaceCurrentAsync(AppSettings updated)
        {
            Current = updated;
            await SaveAsync();
        }

        public async Task LoadAsync()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = await File.ReadAllTextAsync(_filePath).ConfigureAwait(false);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                    if (loaded != null)
                    {
                        Current = loaded;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                // Corrupt settings file should never crash startup; fall back to defaults.
                System.Diagnostics.Debug.WriteLine($"[SettingsService] Failed to load settings: {ex.Message}");
            }

            Current = new AppSettings();
            await SaveAsync();
        }

        public async Task SaveAsync()
        {
            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var json = JsonSerializer.Serialize(Current, JsonOptions);
                await File.WriteAllTextAsync(_filePath, json).ConfigureAwait(false);
                Saved?.Invoke();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SettingsService] Failed to save settings: {ex.Message}");
            }
        }
    }
}
