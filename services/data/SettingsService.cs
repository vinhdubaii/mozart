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

        public SettingsService(string filePath)
        {
            _filePath = filePath;
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
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SettingsService] Failed to save settings: {ex.Message}");
            }
        }
    }
}
